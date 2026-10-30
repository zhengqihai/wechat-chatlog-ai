# -*- coding: utf-8 -*-
"""发言统计（C8）：按时间窗聚合每个发送者的消息数，支持游标增量。

为什么要单独一层，而不是复用 `list_messages`：

  - `list_messages` 有 **2000 条硬上限**。活跃群 2 小时就可能超过 2000 条，
    在它之上做统计会静默失真（前端拿到的是被截断的样本）。
  - 统计只需要 `(sender, create_time, sort_seq)`，不需要正文；
    短信道可以把非文本消息的解压省掉（群消息仍需解压以取发送者前缀）。

计数口径（与产品确认）：
  - 只统计 `local_type` 基础类型 `< 10000` 的真实消息，系统消息 / 撤回提示一律不计；
  - 按**发送者 wxid** 聚合，不按昵称（重名会合并成同一个人）；
  - 包含自己，显示名为「我」。
"""

from __future__ import annotations

import hashlib
import re
from typing import Dict, List, Optional, Tuple

from . import convert
from .store import LocalStore

# 与 messages.py 同一口径：群消息正文以 `wxid:\n` 开头，实测并非 100% 存在。
# 这里刻意复制一份而不是引用私有名：两处用途不同（一处取正文、一处只定发送者），
# 将来若格式变化，可以分别调整，不会互相牵连。
_SENDER_PREFIX_RE = re.compile(r"^([A-Za-z0-9_@\-\.]{3,64}):\n")

UNKNOWN_KEY = "__unknown__"


def _locate(store: LocalStore, target_id: str) -> Tuple[List[str], str]:
    table = "Msg_" + hashlib.md5(target_id.encode("utf-8")).hexdigest()
    return store.shards_for_table(table), table


def _sender_key(sender_wxid: str) -> str:
    return str(sender_wxid or "").strip() or UNKNOWN_KEY


def collect(store: LocalStore, target_id: str, since_ts: float,
            until_ts: Optional[float] = None,
            cursor: Optional[Tuple[int, int]] = None) -> Dict:
    """统计 `since_ts`（含）之后的消息。

    `cursor=(create_time, sort_seq)` 时只处理严格晚于该游标的行，用于每分钟增量。
    返回 `{"counts": {wxid: n}, "total": n, "scanned": n, "cursor": [ct, seq]}`。
    """
    target_id = str(target_id or "").strip()
    if not target_id:
        return {"counts": {}, "total": 0, "scanned": 0, "cursor": list(cursor or [])}

    db_names, table = _locate(store, target_id)
    if not table or not db_names:
        return {"counts": {}, "total": 0, "scanned": 0, "cursor": list(cursor or [])}

    is_group = convert.is_group(target_id)
    since = int(float(since_ts or 0))
    conditions = ["create_time >= ?"]
    params: List[object] = [since]
    if until_ts:
        conditions.append("create_time <= ?")
        params.append(int(float(until_ts)))
    if cursor and len(cursor) == 2:
        conditions.append("(create_time > ? or (create_time = ? and sort_seq > ?))")
        params.extend([int(cursor[0]), int(cursor[0]), int(cursor[1])])

    counts: Dict[str, int] = {}
    total = 0
    scanned = 0
    newest: Tuple[int, int] = (
        (int(cursor[0]), int(cursor[1])) if cursor and len(cursor) == 2 else (0, 0)
    )
    # 同一张表可能同时存在于多个分片（旧分片冻结 + 新分片续写），
    # 必须按"同一条消息"的键去重，否则统计值会被成倍放大。
    seen = set()

    for db_name in db_names:
        with store.read(db_name) as con:
            rows = con.execute(
                'select local_id, local_type, create_time, sort_seq, real_sender_id, '
                'WCDB_CT_message_content, message_content from "%s" where %s '
                'order by create_time asc, sort_seq asc' % (table, " and ".join(conditions)),
                params,
            ).fetchall()
            id_map = {rowid: user_name for rowid, user_name in
                      con.execute("select rowid, user_name from Name2Id")}

        for local_id, local_type, create_time, sort_seq, sender_id, column_type, raw in rows:
            stamp = (int(create_time or 0), int(sort_seq or 0))
            if stamp > newest:
                newest = stamp

            dedupe_key = (local_id, stamp[0], stamp[1])
            if dedupe_key in seen:
                continue
            seen.add(dedupe_key)
            scanned += 1

            base = convert.base_type(local_type)
            if base >= 10000:                 # 系统消息 / 撤回提示等事件，不算发言
                continue

            sender_wxid = str(id_map.get(sender_id) or "")
            if is_group:
                # 群消息的发送者前缀比 real_sender_id 更贴近"界面上显示的人"。
                text = convert.decode_content(raw, column_type)
                match = _SENDER_PREFIX_RE.match(text)
                if match:
                    sender_wxid = match.group(1)
            elif not sender_wxid:
                # 单聊只有两个参与者：不是自己，就只能是会话对方。
                # 这比落到"未知成员"更接近事实，也让好友会话的"我 / 对方"计数可用。
                sender_wxid = target_id

            key = _sender_key(sender_wxid)
            counts[key] = counts.get(key, 0) + 1
            total += 1

    return {
        "counts": counts,
        "total": total,
        "scanned": scanned,
        "cursor": [newest[0], newest[1]],
    }


def resolve_display_names(store: LocalStore, keys: List[str]) -> Dict[str, str]:
    """wxid → 显示名。自己固定为「我」，未知发送者为「未知成员」。"""
    self_wxid = str(store.self_wxid or "")
    wanted = sorted({key for key in keys if key and key != UNKNOWN_KEY})
    result: Dict[str, str] = {}
    if wanted:
        marks = ",".join("?" * len(wanted))
        with store.read("contact.db") as con:
            for username, remark, nick_name in con.execute(
                "select username, remark, nick_name from contact where username in (%s)" % marks,
                wanted,
            ):
                display = str(remark or "").strip() or str(nick_name or "").strip()
                if display:
                    result[str(username)] = display

    names = {}
    for key in keys:
        if key == UNKNOWN_KEY:
            names[key] = "未知成员"
        elif key == self_wxid:
            names[key] = "我"
        else:
            names[key] = result.get(key) or key
    return names
