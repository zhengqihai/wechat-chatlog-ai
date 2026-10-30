# -*- coding: utf-8 -*-
"""C3：通讯录 + 头像（对应 `WeChatAdapter.list_contacts` / `fetch_avatar`）。

输出形状必须与 `docs/API_REFERENCE.md` §14.7.1 的字段白名单一致，
且**已归一化**（与 `adapters.normalize_contact` 同形）—— 这样 server 与客户端两层一行都不用改。

关键的实测口径（见 §2.7）：

  - **分类只看 username 形态，绝不看 `contact.local_type`**。实测 lt=0 里有 224 个真实好友
    （ohh、Nu、恭喜發財…），lt=3 里也有 473 个自定义账号好友，lt 与"好友/群/公众号"没有对应关系。
  - **本地拿不到的字段一律留空，不编造**：`gender / signature / country / province / city / region /
    sourceScene / source / addTime / addTimeText` —— `contact` 表里**根本没有这些列**。
    （踩过的坑：`select "gender" from contact` 不报错，因为 SQLite 把未知的双引号标识符
     当字符串字面量，会返回 `'gender'` 这个假值，看着像"有数据"。）
  - **"已删除好友"可靠判定**：`delete_flag=1`（实测仅 2 个，均 local_type=0、昵称备注皆空，
    是好友形态 `wxid_` 但已删除）。`include_former_friends` 开关据此生效。
  - **"黑名单"本地无依据**：contact 表没有黑名单字段，`include_blocked` 开关本地不生效。
  - **公众号细分本地无依据**：`verify_flag` 在公众号里全是 24，订阅号/服务号无法区分。
  - **头像来自 `head_image.db` 的 `image_buffer`**，不是 `contact.big_head_url`
    （那串 `https://wx.qlogo.cn/...` 是外网地址，本机不该去取，离线也取不到）。
    覆盖约 38%（1516/3976），缺图时客户端用文字头像兜底。
"""

from __future__ import annotations

from typing import Dict, List, Optional, Tuple

from .store import LocalStore

GROUP_SUFFIX = "@chatroom"
OFFICIAL_PREFIX = "gh_"
ENTERPRISE_SUFFIX = "@openim"

# 契约字段白名单（与 adapters.CONTACT_TEXT_FIELDS 逐字一致）。
# 本地拿不到的字段照样出现在结果里，但值为空串 —— 保持键集合完整，前端不用做兼容分支。
TEXT_FIELDS = (
    "username", "displayName", "remark", "nickname", "alias", "gender", "signature",
    "type", "country", "province", "city", "region", "sourceScene", "source",
    "addTime", "addTimeText", "avatarLink", "pinyinKey", "pinyinInitial",
)

COUNT_KEYS = (
    "friends", "groups", "officials", "officialSubscriptions", "officialServices",
    "officialEnterprises", "officialUnknown", "formerFriends", "blocked", "total",
)


def _kind(username: str) -> str:
    """按用户名形态分类。这是本机唯一可靠的分类依据（见模块 docstring）。"""
    if username.endswith(GROUP_SUFFIX):
        return "group"
    if username.startswith(OFFICIAL_PREFIX):
        return "official"
    if username.endswith(ENTERPRISE_SUFFIX):
        return "enterprise"
    return "friend"


def _flag(include: Optional[Dict], name: str, default: bool = True) -> bool:
    """取 include_* 开关。缺省沿用上游默认（好友 / 群聊 / 公众号默认开）。"""
    value = (include or {}).get(name)
    return default if value is None else bool(value)


def _want_officials(include: Optional[Dict]) -> bool:
    """公众号是否要。

    本地**无法区分**订阅号 / 服务号，所以三个开关只能合并看待，但不能简单写成
    `A or B or C`：B、C 在"未传参"时按默认 True，会把显式传入的 `include_officials=false`
    重新救回来（这个 bug 实测出现过 —— 关掉公众号后总数一点没变）。

    正确语义：只看**显式传了值**的那几个开关，任一为真即要；一个都没传则按上游默认（要）。
    """
    source = include or {}
    names = ("include_officials", "include_official_subscriptions", "include_official_services")
    given = [bool(source[name]) for name in names if name in source]
    return any(given) if given else True


def _load_rows(store: LocalStore) -> List[dict]:
    """一次读全量通讯录。3976 行、只取需要的列，约 10ms。

    拼音列有两套、**不要弄混**（实测踩过）：
      `quan_pin` / `pin_yin_initial`            → **昵称**的拼音（chunjidejingse / CJDJS）
      `remark_quan_pin` / `remark_pin_yin_initial` → **备注**的拼音（biaoyi / BY）

    契约里的 pinyinKey/pinyinInitial 跟着**显示名**走（显示名是"备注优先"），
    否则会出现"界面显示备注、拼音却是昵称的读音"，按备注搜反而搜不到。
    """
    with store.read("contact.db") as con:
        cursor = con.execute(
            "select username, local_type, alias, remark, nick_name, delete_flag, "
            "quan_pin, pin_yin_initial, remark_quan_pin, remark_pin_yin_initial, "
            "big_head_url, small_head_url from contact"
        )
        rows = []
        for (username, local_type, alias, remark, nick, delete_flag, quan_pin, initial,
             remark_pinyin, remark_initial, big_url, small_url) in cursor:
            username = str(username or "").strip()
            if not username:
                continue
            remark = str(remark or "")
            nickname = str(nick or "")
            has_remark = bool(remark.strip())
            rows.append({
                "username": username,
                # delete_flag=1 是「已删除好友」的可靠判定（实测仅 2 个，均 local_type=0、昵称备注皆空）。
                "kind": "former_friend" if int(delete_flag or 0) else _kind(username),
                "alias": str(alias or ""),
                "remark": remark,
                "nickname": nickname,
                "pinyinKey": str(remark_pinyin or "") if has_remark else str(quan_pin or ""),
                "pinyinInitial": str(remark_initial or "") if has_remark else str(initial or ""),
                # 搜索要覆盖两套拼音：用户既可能按备注读音搜，也可能按昵称读音搜。
                "searchPinyin": "|".join(str(x or "") for x in
                                         (quan_pin, initial, remark_pinyin, remark_initial)),
                # 优先小图：客户端只用于列表/详情的小圆头像，取小图更省。
                "avatarLink": str(small_url or "").strip() or str(big_url or "").strip(),
            })
    return rows


def _to_contract(row: dict) -> Dict:
    display = row["remark"].strip() or row["nickname"].strip() or row["username"]
    item = {name: "" for name in TEXT_FIELDS}
    item.update({
        "username": row["username"],
        "displayName": display,
        "remark": row["remark"],
        "nickname": row["nickname"],
        "alias": row["alias"],
        "type": "group" if row["kind"] == "group" else "friend",
        "avatarLink": row["avatarLink"],
        "pinyinKey": row["pinyinKey"],
        "pinyinInitial": row["pinyinInitial"],
    })
    # commonChatroomCount 恒 0：算它需要解析 chat_room.ext_buffer 的成员 protobuf，
    # 而客户端**未使用**该字段（只在 DTO 里占位）。等真需要时再实现，不做投机。
    item["commonChatroomCount"] = 0
    item["avatar"] = ""      # 由 server 改写成 /api/contacts/avatar?username=...
    return item


def _matches(row: dict, keyword: str) -> bool:
    """关键词匹配：用户名 / 备注 / 昵称 / 微信号 / 全拼 / 拼音首字母（客户端按拼音搜索要用）。"""
    if not keyword:
        return True
    needle = keyword.lower()
    for field in ("username", "remark", "nickname", "alias", "searchPinyin"):
        if needle in row[field].lower():
            return True
    return False


def _counts(rows: List[dict]) -> Dict:
    """按分类汇总。

    公众号细分（订阅号 / 服务号 / 企业号）本机**无法区分**（`verify_flag` 全是 24），
    所以公众号一律计入 `officialUnknown`；`@openim` 的归 `officialEnterprises`。
    `formerFriends` 按 `delete_flag=1` 统计；`blocked`（黑名单）无依据，恒 0。
    """
    result = {key: 0 for key in COUNT_KEYS}
    for row in rows:
        kind = row["kind"]
        if kind == "group":
            result["groups"] += 1
        elif kind == "official":
            result["officials"] += 1
            result["officialUnknown"] += 1
        elif kind == "enterprise":
            result["officials"] += 1
            result["officialEnterprises"] += 1
        elif kind == "former_friend":
            result["formerFriends"] += 1
        else:
            result["friends"] += 1
    result["total"] = result["friends"] + result["groups"] + result["officials"]
    return result


def list_contacts(store: LocalStore, include: Optional[Dict] = None, keyword: str = "",
                  account: str = "") -> Dict:
    """返回与上游同形的通讯录响应。"""
    rows = _load_rows(store)
    counts = _counts(rows)          # counts 反映**全量**通讯录，不受当前筛选影响

    want_friends = _flag(include, "include_friends")
    want_groups = _flag(include, "include_groups")
    want_officials = _want_officials(include)
    # 已删除好友默认不显示（与上游一致）：只有显式勾选 include_former_friends 才列出。
    want_former = _flag(include, "include_former_friends", default=False)

    selected = []
    for row in rows:
        kind = row["kind"]
        if kind == "former_friend":
            if not want_former:
                continue
        elif kind == "group":
            if not want_groups:
                continue
        elif kind in ("official", "enterprise"):
            if not want_officials:
                continue
        elif not want_friends:
            continue
        if not _matches(row, (keyword or "").strip()):
            continue
        selected.append(_to_contract(row))

    return {
        "total": len(selected),
        "counts": counts,
        "contacts": selected,
        "source": "local",
        "sourceRequested": "local",
        "sourceFallback": False,
        "sourceFallbackReason": "",
        "sourceFallbackMessage": "",
        "sourceFallbackRetryAfterSeconds": 0,
    }


def _sniff_image(data: bytes) -> str:
    """按魔数判类型：不给客户端一个假的 content-type，否则浏览器/控件会解码失败。"""
    if data[:3] == b"\xff\xd8\xff":
        return "image/jpeg"
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return "image/png"
    if data[:6] in (b"GIF87a", b"GIF89a"):
        return "image/gif"
    if data[:2] == b"BM":
        return "image/bmp"
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "image/webp"
    return "application/octet-stream"


def avatar_bytes(store: LocalStore, username: str) -> Tuple[bytes, str]:
    """取某个联系人的头像字节。

    只查本机缓存，**不去外网**（`contact.small_head_url` 是 wx.qlogo.cn 的地址，
    离线或断网就取不到，也不该由本机代取）。取不到抛 `LookupError`，由上层转成明确的错误响应 ——
    不返回假图，避免"看着加载成功其实是空白"。
    """
    name = str(username or "").strip()
    if not name:
        raise LookupError("username 不能为空")
    with store.read("head_image.db") as con:
        row = con.execute("select image_buffer from head_image where username=?", (name,)).fetchone()
    if not row or row[0] is None:
        raise LookupError("本机没有 %s 的头像缓存" % name)
    raw = row[0]
    if isinstance(raw, str):        # 少数版本这里存的是 URL 文本，不是图片
        raise LookupError("本机缓存的 %s 头像不是图片字节" % name)
    data = bytes(raw)
    if not data:
        raise LookupError("本机缓存的 %s 头像为空" % name)
    return data, _sniff_image(data)
