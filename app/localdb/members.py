# -*- coding: utf-8 -*-
"""群成员读取（C7）：当前名单 + 快照差分（入群 / 退群）。

设计口径（与产品确认过，不要擅自扩大）：

  1. **只做快照差分**，不解析群系统消息。因此事件时间 = "检测到的时间"，
     不是精确的入群/退群时刻。首次扫描只建基线，不产生任何事件，
     否则第一次上线会把整群成员报成"新入群"。
  2. 快照**必须全量群**扫描：只有连续采样才可能发现退群（微信不给退群提示）。
     `contact.db` 一次查询就能拿全部成员，成本是毫秒级，按群拆开反而会断档。
  3. 列名自适应但**不猜**：不同微信 4.0 小版本的 `chatroom_member` 列名有差异，
     这里先读 `pragma table_info`，按候选名匹配；一个都匹配不上就抛 `StoreError`
     并把实际列名带出来，绝不静默返回空名单（那会表现为"所有人同时退群"）。
  4. 群名 / 成员显示名：备注优先 → 昵称 → 用户名。群昵称（如果表里有）优先于通讯录备注，
     因为群里看到的就是群昵称。
"""

from __future__ import annotations

import json
import os
import re
import threading
import time
from typing import Dict, List

from .store import LocalStore, StoreError

GROUP_SUFFIX = "@chatroom"

# 列名候选。命中顺序即优先级；都匹配不上时报错，不做模糊猜测。
_GROUP_COLUMNS = ("chatroom", "chat_room", "room_name")
_MEMBER_COLUMNS = ("member", "member_id", "member_username", "user_name", "username", "wxid")
_MEMBER_NAME_COLUMNS = (
    "display_name", "displayname", "nick_name", "nickname", "remark", "group_nickname",
)

# 实测（2026-10-20，微信 4.0 本机库）：真实结构是**数字外键**，不是字符串用户名——
#   chatroom_member(room_id, member_id)
#   room_id  → chat_room.id      → chat_room.username 才是 `xxx@chatroom`
#   member_id → contact.id       → contact.username 才是 `wxid_xxx`
# 按字符串口径读会一行都匹配不上（表现为"扫描完成，但没有读到群"），所以两种结构都要认。
_ROOM_ID_COLUMNS = ("room_id", "roomid", "chat_room_id", "chatroom_id")
_MEMBER_ID_COLUMNS = ("member_id", "memberid", "user_id", "contact_id")

# 事件列表上限：卡片只需要"最近发生了什么"，无上限会让快照文件无限增长。
MAX_CHANGES = 200

_LOCK = threading.RLock()


def _safe_name(value: str) -> str:
    """群 id → 文件名。只保留字母数字与少量符号，其余一律替换。"""
    text = re.sub(r"[^0-9A-Za-z_@.\-]", "_", str(value or ""))
    return text or "unknown"


def _table_columns(con, table: str) -> List[str]:
    return [str(row[1]) for row in con.execute("pragma table_info(%s)" % table)]


def _pick(columns: List[str], candidates) -> str:
    """按候选名匹配（大小写不敏感）。返回空串表示没找到。"""
    lookup = {name.lower(): name for name in columns}
    for name in candidates:
        if name in lookup:
            return lookup[name]
    return ""


def _row_value(row, columns: List[str], name: str, default=""):
    if not name:
        return default
    try:
        index = columns.index(name)
    except ValueError:
        return default
    value = row[index]
    return default if value is None else value


def load_roster(store: LocalStore) -> Dict:
    """读全量群成员。

    返回 `{"groups": {群id: {成员id: 群昵称}}, "group_names": {群id: 显示名}}`。
    成员值为空串表示表里没有群昵称列或该行为空，由上层用通讯录兜底。
    """
    with store.read("contact.db") as con:
        tables = {str(row[0]) for row in con.execute(
            "select name from sqlite_master where type='table'")}
        if "chatroom_member" not in tables:
            raise StoreError(
                "contact.db 里没有 chatroom_member 表（实际表：%s）"
                % ", ".join(sorted(tables)))
        columns = _table_columns(con, "chatroom_member")
        groups: Dict[str, Dict[str, str]] = {}

        room_id_col = _pick(columns, _ROOM_ID_COLUMNS)
        member_id_col = _pick(columns, _MEMBER_ID_COLUMNS)
        if room_id_col and member_id_col:
            # 结构 B（本机实测）：room_id → chat_room.id，member_id → contact.id。
            # name2id 只在部分版本存在，且 rowid 与 contact.id 同源；有就作为兜底。
            if "chat_room" not in tables:
                raise StoreError(
                    "chatroom_member 是数字外键结构，但 contact.db 里没有 chat_room 表（实际表：%s）"
                    % ", ".join(sorted(tables)))
            has_name2id = "name2id" in tables
            fallback_join = ('left join name2id n on n.rowid = cm."%s"' % member_id_col
                             if has_name2id else "")
            fallback_select = ", n.username" if has_name2id else ", null"
            rows = con.execute(
                'select cr.username, c.username, c.remark, c.nick_name%s '
                'from chatroom_member cm '
                'join chat_room cr on cr.id = cm."%s" '
                'left join contact c on c.id = cm."%s" %s'
                % (fallback_select, room_id_col, member_id_col, fallback_join)
            ).fetchall()
            for group, member, remark, nick_name, fallback in rows:
                group = str(group or "").strip()
                member = str(member or fallback or "").strip()
                if not group.endswith(GROUP_SUFFIX) or not member:
                    continue
                display = str(remark or "").strip() or str(nick_name or "").strip()
                groups.setdefault(group, {})[member] = display
        else:
            # 结构 A：表里直接存字符串用户名（部分版本）。
            group_col = _pick(columns, _GROUP_COLUMNS)
            member_col = _pick(columns, _MEMBER_COLUMNS)
            if not group_col or not member_col:
                raise StoreError(
                    "chatroom_member 列名无法识别：实际列 %s；期望数字外键 %s 或字符串 %s"
                    % (columns, list(_ROOM_ID_COLUMNS) + list(_MEMBER_ID_COLUMNS),
                       list(_GROUP_COLUMNS) + list(_MEMBER_COLUMNS)))
            name_col = _pick(columns, _MEMBER_NAME_COLUMNS)
            select_cols = [group_col, member_col] + ([name_col] if name_col else [])
            rows = con.execute(
                'select %s from chatroom_member'
                % ", ".join('"%s"' % c for c in select_cols)
            ).fetchall()
            for row in rows:
                group = str(_row_value(row, select_cols, group_col) or "").strip()
                member = str(_row_value(row, select_cols, member_col) or "").strip()
                if not group.endswith(GROUP_SUFFIX) or not member:
                    continue
                groups.setdefault(group, {})[member] = str(
                    _row_value(row, select_cols, name_col) or "").strip()

        group_names = {}
        for username, remark, nick_name in con.execute(
                "select username, remark, nick_name from contact"):
            username = str(username or "").strip()
            if not username.endswith(GROUP_SUFFIX):
                continue
            group_names[username] = (
                str(remark or "").strip() or str(nick_name or "").strip())

    return {"groups": groups, "group_names": group_names}


def load_contact_names(store: LocalStore) -> Dict[str, str]:
    """一次性读全量通讯录显示名（3974 行）。比按成员 id 分批 IN 查询更简单也更快。"""
    names: Dict[str, str] = {}
    with store.read("contact.db") as con:
        for username, remark, nick_name in con.execute(
                "select username, remark, nick_name from contact"):
            username = str(username or "").strip()
            if not username:
                continue
            names[username] = str(remark or "").strip() or str(nick_name or "").strip()
    return names


def display_name(username: str, group_nick: str, contact_names: Dict[str, str]) -> str:
    """群昵称优先 → 通讯录备注/昵称 → 用户名。"""
    return (str(group_nick or "").strip()
            or str(contact_names.get(username) or "").strip()
            or username)


def _snapshot_path(snapshot_dir: str, chatroom: str) -> str:
    return os.path.join(snapshot_dir, _safe_name(chatroom) + ".json")


def _read_snapshot(path: str) -> Dict:
    try:
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
            return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def _write_snapshot(path: str, data: Dict) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    temporary = path + ".tmp"
    with open(temporary, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(data, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    os.replace(temporary, path)


def scan_all(store: LocalStore, snapshot_dir: str) -> Dict:
    """全量扫描 + 差分 + 落盘。

    对每个群：
      - 没有历史快照 → 只建基线（事件为空）；
      - 有快照 → 与上次成员集合比较，产出 join / leave 事件；
      - 群已不在名单里（被踢/解散）→ 只标记 `missing`，**不为整群成员伪造退群事件**。
    """
    with _LOCK:
        roster = load_roster(store)
        contact_names = load_contact_names(store)
        now = time.time()
        summary = {
            "scanned_at": now,
            "groups": 0,
            "baselines": 0,
            "joined": 0,
            "left": 0,
            "changed": 0,
            "errors": [],
        }

        for chatroom, members in roster["groups"].items():
            try:
                path = _snapshot_path(snapshot_dir, chatroom)
                previous = _read_snapshot(path)
                prior_members = previous.get("members")
                has_baseline = isinstance(prior_members, dict) and bool(prior_members)

                current = {}
                for username, group_nick in members.items():
                    old = (prior_members or {}).get(username) if isinstance(prior_members, dict) else None
                    current[username] = {
                        "name": display_name(username, group_nick, contact_names),
                        "first_seen": float(old.get("first_seen")) if isinstance(old, dict)
                        and isinstance(old.get("first_seen"), (int, float)) else now,
                        "last_seen": now,
                    }

                changes = previous.get("changes")
                if not isinstance(changes, list):
                    changes = []
                if has_baseline:
                    joined = [name for name in current if name not in prior_members]
                    left = [name for name in prior_members if name not in current]
                    for username in joined:
                        changes.append({
                            "type": "join",
                            "username": username,
                            "name": current[username]["name"],
                            "detected_at": now,
                        })
                    for username in left:
                        old = prior_members.get(username) or {}
                        changes.append({
                            "type": "leave",
                            "username": username,
                            "name": str(old.get("name") or contact_names.get(username) or username),
                            "detected_at": now,
                        })
                    summary["joined"] += len(joined)
                    summary["left"] += len(left)
                    if joined or left:
                        summary["changed"] += 1
                else:
                    summary["baselines"] += 1

                _write_snapshot(path, {
                    "chatroom": chatroom,
                    "name": roster["group_names"].get(chatroom) or chatroom,
                    "updated_at": now,
                    "has_baseline": has_baseline,
                    "missing": False,
                    "members": current,
                    "changes": changes[-MAX_CHANGES:],
                })
                summary["groups"] += 1
            except Exception as exc:                     # 单群失败不阻断其它群
                summary["errors"].append("%s: %s" % (chatroom, exc))

        # 快照里存在、但本次名单里没有的群：只标记 missing，不产生成员级事件。
        try:
            for filename in os.listdir(snapshot_dir):
                if not filename.endswith(".json"):
                    continue
                path = os.path.join(snapshot_dir, filename)
                data = _read_snapshot(path)
                chatroom = str(data.get("chatroom") or "")
                if not chatroom or chatroom in roster["groups"]:
                    continue
                if not data.get("missing"):
                    data["missing"] = True
                    data["updated_at"] = now
                    _write_snapshot(path, data)
        except OSError as exc:
            summary["errors"].append("扫描快照目录失败：%s" % exc)

        return summary


def list_members(store: LocalStore, snapshot_dir: str, chatroom: str) -> Dict:
    """当前群成员（实时名单 + 快照里的 first_seen / last_seen）。"""
    chatroom = str(chatroom or "").strip()
    roster = load_roster(store)
    members = roster["groups"].get(chatroom)
    if members is None:
        return {
            "chatroom": chatroom,
            "name": roster["group_names"].get(chatroom) or chatroom,
            "count": 0,
            "last_scan_at": None,
            "members": [],
        }

    contact_names = load_contact_names(store)
    snapshot = _read_snapshot(_snapshot_path(snapshot_dir, chatroom))
    prior = snapshot.get("members") if isinstance(snapshot.get("members"), dict) else {}

    items = []
    for username, group_nick in members.items():
        old = prior.get(username) if isinstance(prior.get(username), dict) else {}
        items.append({
            "username": username,
            "displayName": display_name(username, group_nick, contact_names),
            "first_seen": old.get("first_seen"),
            "last_seen": old.get("last_seen"),
            "avatar": "",       # 由 server 改写成 /api/contacts/avatar?username=...
        })
    items.sort(key=lambda item: item["displayName"])

    return {
        "chatroom": chatroom,
        "name": roster["group_names"].get(chatroom) or chatroom,
        "count": len(items),
        "last_scan_at": snapshot.get("updated_at"),
        "members": items,
    }


def list_changes(snapshot_dir: str, chatroom: str, limit: int = 50) -> List[Dict]:
    """最近入群 / 退群事件，新的在前。"""
    data = _read_snapshot(_snapshot_path(snapshot_dir, str(chatroom or "")))
    changes = data.get("changes")
    if not isinstance(changes, list):
        return []
    limit = max(1, min(int(limit or 50), MAX_CHANGES))
    ordered = sorted(changes, key=lambda item: float(item.get("detected_at") or 0), reverse=True)
    return ordered[:limit]
