# -*- coding: utf-8 -*-
"""C2：会话消息（对应 `WeChatAdapter.list_messages`）。

输出形状必须与 `docs/API_REFERENCE.md` §4.2 完全一致：
    [{id, sender, time, timestamp, content, message_type, source, image_md5, image_file_id}]

几个容易踩的点（都有实测依据，见 §2.6）：

  1. **表名**：`Msg_` + `md5(target_id)`；`session_last_message` 那张"映射表"是上游自建的缓存，
     **真实库里没有**，所以要自己算。表还可能落在别的 `message_*.db` 里，因此走 store 反查归属。
  2. **压缩**：靠 `WCDB_CT_message_content`（0 = 原样，4 = zstd）。实测零例外，
     比"看魔数"更可靠；解压失败时**返回空串而不是二进制**，避免脏数据流进 AI 提示词。
  3. **发送者**：群消息正文自带 `wxid_xxx:\n` 前缀，但实测 308 条里只有 303 条有 ——
     所以是"匹配才剥"，匹配不到就退回 `real_sender_id` → `Name2Id`。前缀只用来定发送者，
     剥离后的正文才是要展示/送模型的内容。
  4. **非文本消息**：正文是 XML（图片 `<img>`、位置 `<location>`…）。**绝不能把 XML 直接当 content**，
     否则会污染 AI 输入；按契约给标签（`[图片]` 等）。`message_content` 的原始 XML 仅用于判定子类型。
  5. **图片定位**：图片消息里没有完整 md5，`image_md5` / `image_file_id` 本阶段**留空**，
     待 C6 从 `message_resource.db` 取（docs §2.6 已记录）。
"""

from __future__ import annotations

import hashlib
import re
import threading
import time
from typing import Dict, List, Optional, Tuple

from . import convert
from . import images
from .store import LocalStore

SOURCE = "wechat-realtime"
MAX_LIMIT = 2000        # 与上游一致：单会话最多读 2000 条

# 群消息正文的发送者前缀。实测 308 条群文本里 303 条带前缀 —— 不能假设恒存在。
_SENDER_PREFIX_RE = re.compile(r"^([A-Za-z0-9_@\-\.]{3,64}):\n")

# appmsg(49) 子类型 → 契约里的 message_type。
# 位域高 32 位就是 appmsg 的 `<type>`（实测 sub 与 XML 里的 <type> 相等）。
_APPMSG_TYPES = {
    5: "link",           # 链接卡片
    6: "file",           # 文件
    19: "chatHistory",   # 合并转发的聊天记录
    2000: "transfer",    # 转账
    2001: "redPacket",   # 红包
}
_XML_TYPE_RE = re.compile(r"<type>(\d+)</type>")

# ---------- 防撤回（T-F5） ----------
# 撤回提示的两种形态都含这句中文：新格式是 `<sysmsg type="revokemsg">…<content>…</content>`，
# 旧格式直接是纯文本「"xxx" 撤回了一条消息」。用语义短语而不是 `revokemsg` 关键字做判定，
# 才能同时覆盖两种，不依赖微信某版格式。
_RECALL_MARK = "撤回了一条消息"
_SELF_RECALL_TEXT = "你撤回了一条消息"
_RECALL_WINDOW_SECONDS = 120
_RECALL_CONTENT_RE = re.compile(r"<content>(.*?)</content>", re.S)


def _locate(store: LocalStore, target_id: str) -> Tuple[List[str], Optional[str]]:
    """会话 id → (含该表的 message 分片列表, 消息表名)。找不到返回 ([], table)。

    为什么返回**列表**：同一张表可能出现在多个分片里（旧分片冻结 + 新分片续写），
    必须由 list_messages 跨分片合并；只看第一个分片会漏掉新消息（见 store.shards_for_table）。
    """
    table = "Msg_" + hashlib.md5(target_id.encode("utf-8")).hexdigest()
    return store.shards_for_table(table), table


def _appmsg_type(sub: int, xml: str) -> str:
    """appmsg 细分。`<refermsg>` 是"引用回复"的结构标记，优先判为引用。

    实测：本机 sub=57 的 70 条 appmsg **全部**带 `<refermsg>`，所以这条规则不是拍脑袋。
    未识别的子类型统一归 `link`（契约里有这个键，语义最中性），不做臆测。
    """
    if "<refermsg>" in xml:
        return "quote"
    if sub in _APPMSG_TYPES:
        return _APPMSG_TYPES[sub]
    match = _XML_TYPE_RE.search(xml)
    if match:
        try:
            return _APPMSG_TYPES.get(int(match.group(1)), "link")
        except ValueError:
            pass
    return "link"


_REFERMSG_RE = re.compile(r"<refermsg>(.*?)</refermsg>", re.S)


def _extract_tag(xml: str, name: str) -> str:
    """从 XML 片段里取第一个 `<name>…</name>` 的文本内容，取不到返回空串。"""
    match = re.search(r"<%s>(.*?)</%s>" % (name, name), xml, re.S)
    return match.group(1).strip() if match else ""


def _quote_content(xml: str) -> str:
    """解析引用消息（appmsg 带 `<refermsg>`），组合成可读内容。

    `<title>` 是回复正文；`<refermsg>` 内 `<displayname>` 是被引用者、
    `<content>` 是被引用原文。被引用原文只有 `type=1`（文本）才直读，
    其余（图片/视频/链接…）按 `convert.BASE_TYPE_NAMES` 给类型标签，不把 XML 当正文流出。
    """
    title = _extract_tag(xml, "title")
    ref = _REFERMSG_RE.search(xml)
    if not ref:
        return convert.type_label("quote")

    ref_xml = ref.group(0)
    displayname = _extract_tag(ref_xml, "displayname")
    ref_type = _extract_tag(ref_xml, "type")
    ref_content = _extract_tag(ref_xml, "content")

    if ref_type == "1":
        quoted = ref_content or convert.type_label("text")
    else:
        base = int(ref_type) if ref_type.isdigit() else 0
        quoted = convert.type_label(convert.BASE_TYPE_NAMES.get(base, "message"))

    lines = []
    if displayname or quoted:
        lines.append("引用「%s」：%s" % (displayname or "对方", quoted))
    if title:
        lines.append(title)
    return "\n——\n".join(lines) if lines else convert.type_label("quote")


def _resolve_display_names(store: LocalStore, usernames: List[str]) -> Dict[str, str]:
    """发送者 wxid → 显示名（备注优先，其次昵称），一次查完。"""
    wanted = sorted({name for name in usernames if name})
    if not wanted:
        return {}
    result: Dict[str, str] = {}
    marks = ",".join("?" * len(wanted))
    with store.read("contact.db") as con:
        for username, remark, nick_name in con.execute(
            "select username, remark, nick_name from contact where username in (%s)" % marks,
            wanted,
        ):
            display = str(remark or "").strip() or str(nick_name or "").strip()
            if display:
                result[str(username)] = display
    return result


def _extract_recall_text(raw: str) -> str:
    """从撤回提示正文里取出可读文案：新格式取 `<content>…</content>`，旧格式直接是纯文本。"""
    match = _RECALL_CONTENT_RE.search(raw or "")
    return (match.group(1) if match else raw).strip()


def _capture_content(text: str, local_type) -> str:
    """捕获原文时用的正文（与 list_messages 同口径：文本剥发送者前缀，非文本给类型标签）。"""
    base = convert.base_type(local_type)
    if base == 1:
        match = _SENDER_PREFIX_RE.match(text)
        if match:
            text = text[match.end():]
        return text.strip() or convert.type_label("text")
    return convert.type_label(convert.BASE_TYPE_NAMES.get(base, "message"))


class RecallBackup:
    """撤回前临时备份（T-F5）：持续把最近 120 秒内的消息原文抄一份，供撤回后恢复。

    为什么需要它：实测微信撤回会把原消息从库里删掉（或原位替换成撤回提示），
    原文不额外落一份就再也拿不回来。只留 120 秒 —— 微信超过 2 分钟就撤不回，
    活过 2 分钟的消息可安全丢弃（性能上全库扫最近 2 分钟约 45ms，2 秒一次可接受）。
    """

    def __init__(self):
        self._lock = threading.Lock()
        self._items = {}          # (table, local_id) -> {"content": str, "create_time": int}

    def capture(self, store: LocalStore) -> None:
        now = int(time.time())
        cutoff = now - _RECALL_WINDOW_SECONDS
        with self._lock:
            for db_name in store.message_shards():
                try:
                    with store.read(db_name) as con:
                        tables = [r[0] for r in con.execute(
                            "select name from sqlite_master where type='table' and name like 'Msg_%'")]
                        for table in tables:
                            rows = con.execute(
                                'select local_id, local_type, create_time, '
                                'WCDB_CT_message_content, message_content, packed_info_data from "%s" '
                                'where create_time >= ?' % table, (cutoff,)).fetchall()
                            for local_id, local_type, create_time, wct, raw, packed_info in rows:
                                base = convert.base_type(local_type)
                                if base >= 10000:
                                    continue
                                image_hash = ""
                                if base == 3:
                                    image_hash = images.extract_image_hash(packed_info)
                                self._items[(table, str(local_id))] = {
                                    "content": _capture_content(convert.decode_content(raw, wct), local_type),
                                    "message_type": convert.BASE_TYPE_NAMES.get(base, "message"),
                                    "image_hash": image_hash,
                                    "create_time": int(create_time or 0),
                                }
                except Exception:
                    continue
            self._items = {k: v for k, v in self._items.items() if v["create_time"] >= cutoff}

    def get(self, table: str, local_id):
        return self._items.get((table, str(local_id)))


def _annotate_recalls(ordered: List[dict], table: str, recall_backup: Optional[RecallBackup]) -> None:
    """给已按时间正序的消息打上撤回标注（T-F5），原地修改。

    撤回提示的 `local_id` 恰好等于被撤原消息的 `local_id`（微信是原位替换/复用行号），
    因此用「撤回提示 local_id → 备份」精确恢复原文，不再需要任何名字/时间就近匹配。

    只处理「别人撤回」（正文不是「你撤回了一条消息」）；本人撤回只把 [系统消息]
    换成可读文案，不恢复原文。
    """
    for msg in ordered:
        raw = msg.pop("_sysxml", "")
        if not raw or _RECALL_MARK not in raw:
            continue
        text = _extract_recall_text(raw)
        if not text:
            continue
        msg["content"] = text
        if text == _SELF_RECALL_TEXT:
            continue
        msg["recall_notice"] = True
        if recall_backup is not None:
            entry = recall_backup.get(table, msg.get("id"))
            if entry:
                if entry.get("image_hash"):
                    msg["recalled_image_hash"] = entry["image_hash"]
                    msg["recalled_image_time"] = entry["create_time"]
                else:
                    msg["recalled_content"] = entry["content"]


def list_messages(store: LocalStore, target_id: str, limit: int = 500,
                  annotate_recalls: bool = False,
                  recall_backup: Optional[RecallBackup] = None) -> List[dict]:
    """读取某会话最近 `limit` 条消息，**按时间正序**返回（与上游适配器一致）。

    同一张表可能同时存在于多个 message 分片里（旧分片冻结 + 新分片续写），
    因此**必须跨分片合并**：每个分片各取最新 `limit` 条，合并后按
    `(create_time, sort_seq)` 倒序截断到 `limit`，再翻转成正序。
    只读第一个分片会稳定地漏掉新消息，而且不会报错。
    """
    target_id = str(target_id or "").strip()
    if not target_id:
        return []
    db_names, table = _locate(store, target_id)
    if not table or not db_names:
        return []
    limit = min(max(int(limit), 1), MAX_LIMIT)
    is_group = convert.is_group(target_id)
    self_wxid = store.self_wxid

    # 先攒齐所有分片的候选，最后统一排序截断。
    # 排序键用 `create_time`，因为契约要求 `messages[].time` 单调递增（"始终按时间正序"）。
    # 为什么不用 `sort_seq`：它其实是**本机接收时间(ms)**，`create_time` 是**服务端时间(s)**，
    # 两者会有毫秒级错位。实测本机群聊 500 条里有 2 处逆序正是这个原因
    # （sort_seq 相差 1ms、create_time 差 1s 且方向相反）。
    # 同一秒内再按 sort_seq 兜底，保留微信自己的展示次序。
    staged: List[Tuple[int, int, dict]] = []
    seen = set()          # 跨分片去重键（见下方说明）
    for db_name in db_names:
        with store.read(db_name) as con:
            rows = con.execute(
                'select local_id, local_type, create_time, sort_seq, real_sender_id, '
                'WCDB_CT_message_content, message_content, packed_info_data from "%s" '
                'order by create_time desc, sort_seq desc limit ?' % table,
                (limit,),
            ).fetchall()
            id_map = {rowid: user_name for rowid, user_name in
                      con.execute("select rowid, user_name from Name2Id")}

        for local_id, local_type, create_time, sort_seq, sender_id, column_type, raw, packed_info in rows:
            # 若某个分片是把整张表复制过去的，同一行会在两个分片各出现一次。
            # 只对 (local_id, create_time, sort_seq) 三者全同的行去重 —— 那才是同一条消息；
            # "同一秒发的两条不同消息"靠 sort_seq（毫秒级接收时间）区分，不会被误伤。
            key = (local_id, create_time, sort_seq)
            if key in seen:
                continue
            seen.add(key)

            base = convert.base_type(local_type)
            sub = convert.sub_type(local_type)
            text = convert.decode_content(raw, column_type)

            sender_wxid = str(id_map.get(sender_id) or "")
            if is_group:
                match = _SENDER_PREFIX_RE.match(text)
                if match:
                    # 前缀里就是发送者 wxid，比 real_sender_id 更贴近"微信界面上显示的人"。
                    sender_wxid = match.group(1)
                    text = text[match.end():]

            image_hash = ""
            if base == 1:
                message_type = "text"
                content = text.strip() or convert.type_label("text")
            else:
                message_type = convert.BASE_TYPE_NAMES.get(base, "message")
                if base == 49:
                    message_type = _appmsg_type(sub, text)
                    # 引用消息要解析出「引用谁 + 原文 + 回复」，不能只留一个 [引用消息] 标签。
                    content = _quote_content(text) if message_type == "quote" else convert.type_label(message_type)
                else:
                    content = convert.type_label(message_type)
                if base == 3:
                    # 图片 hash 是本地定位 .dat 的键（来自 packed_info_data），见 C6 调研报告。
                    image_hash = images.extract_image_hash(packed_info)

            item = {
                "id": str(local_id),
                "sender_wxid": sender_wxid,
                "time": convert.format_time(create_time),
                "timestamp": create_time,
                "content": content,
                "message_type": message_type,
                "image_hash": image_hash,
            }
            if annotate_recalls and base >= 10000:
                # 撤回检测需要 system 消息的原始 XML（base>=10000），这里先留着，
                # 排序后在 _annotate_recalls 里消费掉，不会泄漏到最终输出。
                item["_sysxml"] = text
            staged.append((int(create_time or 0), int(sort_seq or 0), item))

    # 合并后取最新 limit 条（倒序截断），再翻转成正序。
    staged.sort(key=lambda item: (item[0], item[1]), reverse=True)
    ordered = [item[2] for item in reversed(staged[:limit])]

    # 先把 wxid 解析成显示名并挂到每条上，最终构建输出时直接用。
    names = _resolve_display_names(store, [item["sender_wxid"] for item in ordered])
    for item in ordered:
        wxid = item["sender_wxid"]
        item["_display"] = "我" if (wxid and wxid == self_wxid) else (names.get(wxid) or wxid or "未知成员")

    if annotate_recalls:
        _annotate_recalls(ordered, table, recall_backup)

    messages = []
    for item in ordered:
        sender = item.pop("_display", "未知成员")
        messages.append({
            "id": item["id"],
            "sender": sender,
            "time": item["time"],
            "timestamp": item["timestamp"],
            "content": item["content"],
            "message_type": item["message_type"],
            "source": SOURCE,
            # 图片定位信息来自 message_resource.db，归 C6；先留空占位，保持字段契约完整。
            "image_md5": "",
            "image_file_id": "",
            # 本地图片定位键（32 位 hex，来自 packed_info_data）；非图片消息为空串。
            "image_hash": item.get("image_hash") or "",
            # 防撤回（T-F5）：只在命中时写这些字段，避免给每条消息都塞进噪声。
            **({"recall_notice": True} if item.get("recall_notice") else {}),
            **({"recalled_content": item["recalled_content"]} if item.get("recalled_content") else {}),
            **({"recalled_image_hash": item["recalled_image_hash"]} if item.get("recalled_image_hash") else {}),
            **({"recalled_image_time": item["recalled_image_time"]} if item.get("recalled_image_time") else {}),
        })
    return messages
