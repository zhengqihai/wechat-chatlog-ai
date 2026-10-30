# -*- coding: utf-8 -*-
"""C1：会话列表（对应 `WeChatAdapter.list_chats`）。

数据来源：`session.db` 的 `SessionTable` + `contact.db` 的 `contact`（取显示名）。
输出的 dict 形状必须与 `docs/API_REFERENCE.md` §4.1 完全一致 —— 前端不感知数据从哪来。

两个实现要点：
  - **显示名**：`remark`（备注）优先，其次 `nick_name`，再退回 username。实测：
    群「44536055599@chatroom」remark=摸鱼群 / nick=摸鱼吹牛技术群，用户看到的是备注。
  - **过滤**：对齐上游查询参数 `include_hidden=false, include_official=false`。
    上游日志里同一时刻是 `rawCount: 108, filteredCount: 92`，这里的过滤结果要对得上。
"""

from __future__ import annotations

from typing import Dict, List

from . import convert
from .store import LocalStore

SOURCE = "wechat-realtime"

# 官方/系统账号：`include_official=false` 时要排除。
# `weixin`（微信团队）算官方；**`filehelper`（文件传输助手）保留** ——
# `docs/API_REFERENCE.md` §4.1 的示例数据里就带着「文件传输助手」，说明它本来就在列表里。
OFFICIAL_USERS = frozenset({"weixin"})
OFFICIAL_PREFIX = "gh_"

# 「真人会话」的通讯录类型。实测：1 = 好友/部分群，2 = 群，3 = 好友；
# 0 = 服务号占位（服务通知等），4/5/6 = openim 等非真人会话。
HUMAN_CONTACT_TYPES = frozenset({1, 2, 3})


def _visible(username: str, hidden, contact_types: dict,
             include_hidden: bool, include_official: bool) -> bool:
    """会话可见性 —— 对齐上游的 `include_hidden=false, include_official=false`。

    这条规则不是拍脑袋：按它过滤本机 108 条会话得到 **92 条**，与上游日志里
    同一时刻的 `rawCount: 108 → filteredCount: 92` 精确吻合。排除的 16 条构成：
    不在通讯录 4 + local_type 0/5/6 共 6 + 公众号 5 + 微信团队 1。
    """
    if hidden and not include_hidden:
        return False
    if contact_types.get(username) not in HUMAN_CONTACT_TYPES:
        return False
    if not include_official and (username.startswith(OFFICIAL_PREFIX) or username in OFFICIAL_USERS):
        return False
    return True


def _load_sessions(store: LocalStore) -> List[dict]:
    """读 SessionTable，并按最新消息时间倒序（对齐上游"最近优先"的返回顺序）。"""
    with store.read("session.db") as con:
        rows = con.execute(
            "select username, unread_count, is_hidden, summary, last_timestamp "
            "from SessionTable"
        ).fetchall()
    out = []
    for username, unread, hidden, summary, last_ts in rows:
        username = str(username or "").strip()
        if not username:
            continue
        out.append({
            "id": username,
            "unread": int(unread or 0),
            "hidden": bool(hidden),
            "last_message": str(summary or ""),
            "last_timestamp": last_ts or 0,
            "last_time": convert.format_time(last_ts),
        })
    out.sort(key=lambda item: item["last_timestamp"], reverse=True)
    return out


def _load_contacts(store: LocalStore, usernames: List[str]) -> Dict[str, dict]:
    """一次取回显示名与通讯录类型，避免 N+1 查询。

    显示名 = `remark`（备注）优先 → `nick_name` → 退回 username。实测：
    群「44536055599@chatroom」remark=摸鱼群 / nick=摸鱼吹牛技术群，用户看到的是备注。
    """
    if not usernames:
        return {}
    result: Dict[str, dict] = {}
    marks = ",".join("?" * len(usernames))
    with store.read("contact.db") as con:
        for username, local_type, remark, nick_name in con.execute(
            "select username, local_type, remark, nick_name from contact where username in (%s)" % marks,
            usernames,
        ):
            name = str(remark or "").strip() or str(nick_name or "").strip()
            result[str(username)] = {"local_type": local_type, "name": name}
    return result


def list_chats(store: LocalStore, account: str = "", include_hidden: bool = False,
               include_official: bool = False) -> Dict:
    """返回 `{"chats": [...], "status_info": {...}}`，字段与契约一致。"""
    sessions = _load_sessions(store)
    contacts = _load_contacts(store, [item["id"] for item in sessions])
    contact_types = {username: info["local_type"] for username, info in contacts.items()}

    chats = []
    for item in sessions:
        username = item["id"]
        if not _visible(username, item["hidden"], contact_types, include_hidden, include_official):
            continue
        chats.append({
            "id": username,
            "name": contacts.get(username, {}).get("name") or username,
            "type": convert.session_type(username),
            "source": SOURCE,
            "last_message": item["last_message"],
            "last_time": item["last_time"],
            "unread": item["unread"],
        })

    return {
        "chats": chats,
        "status_info": {
            "ready": True,
            "msg": "本地解密库已连接，共 %d 个会话" % len(chats),
            "source": "local",
            "account": account,
        },
    }
