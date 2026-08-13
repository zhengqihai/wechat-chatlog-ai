# -*- coding: utf-8 -*-
"""微信 WeChatDataAnalysis 与 QQ WeQ 的本地数据源适配器。"""

import json
from datetime import datetime
from typing import Dict, List, Optional

import requests


class DataSourceError(RuntimeError):
    """本地成熟数据源不可用或返回了无效结果。"""


def _format_time(value) -> str:
    """将秒或毫秒时间戳统一格式化为本地时间。"""
    try:
        number = float(value)
        if number > 100000000000:
            number = number / 1000.0
        return datetime.fromtimestamp(number).strftime("%Y-%m-%d %H:%M:%S")
    except (TypeError, ValueError, OSError, OverflowError):
        return str(value or "")


def _wechat_content(message: Dict) -> str:
    """优先使用解析后的正文，没有正文时保留消息类型语义。"""
    content = str(message.get("content") or "").strip()
    if content:
        return content
    render_type = str(message.get("renderType") or "message")
    labels = {
        "image": "[图片]",
        "emoji": "[动画表情]",
        "video": "[视频]",
        "voice": "[语音]",
        "file": "[文件]",
        "link": "[链接]",
        "quote": "[引用消息]",
        "chatHistory": "[聊天记录]",
        "transfer": "[转账]",
        "redPacket": "[红包]",
        "location": "[位置]",
        "voip": "[通话]",
        "system": "[系统消息]",
    }
    return labels.get(render_type, "[消息]")


def _qq_element_text(element: Dict) -> str:
    """把 WeQ 的结构化消息元素转换为可供总结的纯文本。"""
    element_type = str(element.get("type") or "")
    data = element.get("data") if isinstance(element.get("data"), dict) else {}
    text = data.get("textContent")
    if isinstance(text, str) and text.strip():
        return text
    if element_type == "face":
        return str(data.get("faceText") or "[表情]")
    labels = {
        "pic": "[图片]",
        "video": "[视频]",
        "file": "[文件]",
        "ptt": "[语音]",
        "mface": "[动画表情]",
        "onlineFile": "[在线文件]",
        "onlineFolder": "[在线文件夹]",
        "reply": "",
        "markdown": "[卡片消息]",
        "ark": "[卡片消息]",
        "wallet": "[红包或转账]",
        "call": "[通话]",
        "location": "[位置]",
    }
    file_name = data.get("fileName")
    if isinstance(file_name, str) and file_name.strip():
        return "[文件] %s" % file_name
    return labels.get(element_type, "[消息]")


def _qq_content(message: Dict) -> str:
    parts = [_qq_element_text(item) for item in message.get("elements") or [] if isinstance(item, dict)]
    content = "".join(parts).strip()
    return content or "[消息]"


class WeChatAdapter:
    """通过 WeChatDataAnalysis HTTP API 读取微信实时数据库。"""

    def __init__(self, base_url: str, account: str = ""):
        self.base_url = base_url.rstrip("/")
        self.account = account.strip()
        self.session = requests.Session()

    def _get(self, path: str, params: Optional[Dict] = None, timeout: int = 60) -> Dict:
        try:
            response = self.session.get(self.base_url + path, params=params, timeout=timeout)
            response.raise_for_status()
            data = response.json()
        except (requests.RequestException, ValueError) as exc:
            raise DataSourceError("WeChatDataAnalysis 请求失败：%s" % exc) from exc
        if data.get("status") not in ("success", "completed", 0, None):
            raise DataSourceError(str(data.get("message") or data.get("errmsg") or data))
        return data

    def _resolve_account(self) -> str:
        if self.account:
            return self.account
        data = self._get("/api/chat/accounts")
        account = str(data.get("default_account") or "").strip()
        if not account:
            raise DataSourceError("微信数据已发现，但尚未完成密钥验证和数据库解锁")
        self.account = account
        return account

    def list_chats(self, limit: int = 400) -> Dict:
        account = self._resolve_account()
        data = self._get(
            "/api/chat/sessions",
            {
                "account": account,
                "limit": limit,
                "include_hidden": "false",
                "include_official": "false",
                "preview": "latest",
                "source": "realtime",
            },
        )
        chats = []
        for item in data.get("sessions") or []:
            username = str(item.get("username") or item.get("id") or "").strip()
            if not username:
                continue
            chats.append(
                {
                    "id": username,
                    "name": str(item.get("name") or username),
                    "type": "group" if item.get("isGroup") else "friend",
                    "source": "wechat-realtime",
                    "last_message": str(item.get("lastMessage") or ""),
                    "last_time": str(item.get("lastMessageTime") or ""),
                    "unread": int(item.get("unreadCount") or 0),
                }
            )
        return {
            "chats": chats,
            "status_info": {
                "ready": True,
                "msg": "微信实时数据库已连接，共 %d 个会话" % len(chats),
                "source": str(data.get("source") or "realtime"),
                "account": account,
            },
        }

    def list_messages(self, target_id: str, limit: int = 500) -> List[Dict]:
        account = self._resolve_account()
        requested_limit = min(max(int(limit), 1), 2000)
        rows = []
        offset = 0
        while len(rows) < requested_limit:
            page_limit = min(500, requested_limit - len(rows))
            data = self._get(
                "/api/chat/messages",
                {
                    "username": target_id,
                    "account": account,
                    "limit": page_limit,
                    "offset": offset,
                    "order": "desc",
                    "source": "realtime",
                },
                timeout=90,
            )
            page = data.get("messages") or []
            rows.extend(page)
            if len(page) < page_limit:
                break
            offset += len(page)
        messages = []
        seen_ids = set()
        for item in reversed(rows[:requested_limit]):
            message_id = str(item.get("id") or "")
            if message_id and message_id in seen_ids:
                continue
            if message_id:
                seen_ids.add(message_id)
            sender = "我" if item.get("isSent") else str(
                item.get("senderDisplayName") or item.get("senderUsername") or "未知成员"
            )
            timestamp = item.get("createTime")
            messages.append(
                {
                    "id": str(item.get("id") or ""),
                    "sender": sender,
                    "time": _format_time(timestamp),
                    "timestamp": timestamp,
                    "content": _wechat_content(item),
                    "message_type": str(item.get("renderType") or "message"),
                    "source": "wechat-realtime",
                }
            )
        return messages


class WeQAdapter:
    """通过 WeQ Web 的 tRPC API 读取 QQ NT 数据库。"""

    def __init__(self, base_url: str, token: str, preferred_uin: str = ""):
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.preferred_uin = preferred_uin.strip()
        self.session = requests.Session()
        self.opened_uin = ""
        self.self_uid = ""

    def _login(self) -> None:
        if not self.token.strip():
            raise DataSourceError("WeQ 访问令牌未配置")
        try:
            response = self.session.post(
                self.base_url + "/_auth/login",
                json={"token": self.token},
                timeout=15,
            )
            response.raise_for_status()
        except requests.RequestException as exc:
            raise DataSourceError("WeQ 登录失败：%s" % exc) from exc

    def _query(self, procedure: str, payload: Optional[Dict] = None, timeout: int = 60):
        if not self.session.cookies:
            self._login()
        params = None if payload is None else {"input": json.dumps({"json": payload}, ensure_ascii=False)}
        try:
            response = self.session.get(
                self.base_url + "/trpc/" + procedure,
                params=params,
                timeout=timeout,
            )
            if response.status_code == 401:
                self._login()
                response = self.session.get(
                    self.base_url + "/trpc/" + procedure,
                    params=params,
                    timeout=timeout,
                )
            response.raise_for_status()
            data = response.json()
            if "error" in data:
                raise DataSourceError(str(data["error"]))
            return data.get("result", {}).get("data", {}).get("json")
        except (requests.RequestException, ValueError) as exc:
            raise DataSourceError("WeQ 接口 %s 请求失败：%s" % (procedure, exc)) from exc

    def _mutation(self, procedure: str, payload: Dict, timeout: int = 120):
        if not self.session.cookies:
            self._login()
        try:
            response = self.session.post(
                self.base_url + "/trpc/" + procedure,
                json={"json": payload},
                timeout=timeout,
            )
            response.raise_for_status()
            data = response.json()
            if "error" in data:
                raise DataSourceError(str(data["error"]))
            return data.get("result", {}).get("data", {}).get("json")
        except (requests.RequestException, ValueError) as exc:
            raise DataSourceError("WeQ 接口 %s 请求失败：%s" % (procedure, exc)) from exc

    def ensure_account(self) -> Dict:
        """复用当前会话；WeQ 重启后用其已保存密钥重新打开账号。"""
        try:
            profile = self._query("account.getSelfProfile", timeout=20)
            if profile:
                self.opened_uin = str(profile.get("uin") or "")
                self.self_uid = str(profile.get("uid") or "")
                return profile
        except DataSourceError:
            pass

        configs = self._query("bootstrap.listAccountConfigs", timeout=30) or []
        if isinstance(configs, dict):
            configs = [configs]
        candidates = [item for item in configs if item.get("dbKey")]
        if self.preferred_uin:
            candidates.sort(key=lambda item: str(item.get("uin")) != self.preferred_uin)
        if not candidates:
            raise DataSourceError("WeQ 尚未保存可用的 QQ 数据库密钥")

        chosen = candidates[0]
        payload = {
            "uin": str(chosen.get("uin") or ""),
            "dbKey": str(chosen.get("dbKey") or ""),
            "algo": chosen.get("algo"),
            "displayName": str(chosen.get("displayName") or ""),
            "avatarUrl": str(chosen.get("avatarUrl") or ""),
            "dataDir": str(chosen.get("dataDir") or ""),
        }
        payload = {key: value for key, value in payload.items() if value not in (None, "")}
        self._mutation("bootstrap.openAccount", payload, timeout=180)
        profile = self._query("account.getSelfProfile", timeout=30) or {}
        self.opened_uin = str(profile.get("uin") or chosen.get("uin") or "")
        self.self_uid = str(profile.get("uid") or "")
        return profile

    @staticmethod
    def _chat_kind(chat_type) -> str:
        value = str(chat_type or "").upper()
        return "group" if "GROUP" in value or value == "2" else "c2c"

    def list_chats(self, limit: int = 200) -> Dict:
        profile = self.ensure_account()
        contacts = self._query("account.listRecentContacts", timeout=60) or []
        chats = []
        for item in contacts[:limit]:
            kind = self._chat_kind(item.get("chatType"))
            target_id = str(item.get("targetUid") or "").strip()
            if not target_id:
                continue
            name = str(
                item.get("targetRemark")
                or item.get("targetDisplayName")
                or item.get("targetGroupNick")
                or item.get("targetUin")
                or target_id
            )
            preview = item.get("preview") if isinstance(item.get("preview"), dict) else {}
            chats.append(
                {
                    "id": target_id,
                    "name": name,
                    "type": "group" if kind == "group" else "friend",
                    "qq_kind": kind,
                    "source": "qq-realtime",
                    "last_message": str(preview.get("displayText") or preview.get("textContent") or ""),
                    "last_time": _format_time(item.get("sendTime")),
                    "unread": int(item.get("unreadCount") or 0),
                }
            )
        return {
            "chats": chats,
            "status_info": {
                "ready": True,
                "msg": "QQ 实时数据库已连接，共 %d 个会话" % len(chats),
                "source": "weq-live",
                "account": self.opened_uin,
                "name": str(profile.get("nick") or self.opened_uin),
            },
        }

    def _resolve_group_names(self, group_code: str, rows: List[Dict]) -> Dict[str, str]:
        uids = list(dict.fromkeys(str(row.get("senderUid") or "") for row in rows))
        uids = [uid for uid in uids if uid and uid != self.self_uid]
        if not uids:
            return {}
        names = {}
        # WeQ 单次最多适合查询 200 个成员，长会话按批次补全昵称。
        for offset in range(0, len(uids), 200):
            try:
                members = self._query(
                    "account.getGroupMembersByUids",
                    {"groupCode": group_code, "uids": uids[offset:offset + 200]},
                    timeout=60,
                ) or []
                names.update({
                    str(item.get("uid") or ""): str(
                        item.get("card") or item.get("nick") or item.get("uin") or ""
                    )
                    for item in members
                })
            except DataSourceError:
                continue
        return names

    def list_messages(self, target_id: str, kind: str, limit: int = 500) -> List[Dict]:
        profile = self.ensure_account()
        actual_kind = "group" if kind == "group" else "c2c"
        requested_limit = min(max(int(limit), 1), 2000)
        page_size = min(requested_limit, 200)
        rows = self._query(
            "account.listLatest",
            {"kind": actual_kind, "conv": target_id, "limit": page_size},
            timeout=90,
        ) or []

        # listLatest/listBefore 都按新到旧返回，持续以当前最旧 msgSeq 向前翻页。
        seen_ids = {str(item.get("msgId") or "") for item in rows}
        while len(rows) < requested_limit and len(rows) > 0:
            oldest_seq = str(rows[-1].get("msgSeq") or "").strip()
            if not oldest_seq:
                break
            remaining = requested_limit - len(rows)
            older = self._query(
                "account.listBefore",
                {
                    "kind": actual_kind,
                    "conv": target_id,
                    "beforeSeq": oldest_seq,
                    "limit": min(remaining, 200),
                },
                timeout=90,
            ) or []
            fresh = []
            for item in older:
                message_id = str(item.get("msgId") or "")
                if message_id and message_id in seen_ids:
                    continue
                if message_id:
                    seen_ids.add(message_id)
                fresh.append(item)
            if not fresh:
                break
            rows.extend(fresh)
            if len(older) < min(remaining, 200):
                break

        rows = rows[:requested_limit]
        group_names = self._resolve_group_names(target_id, rows) if actual_kind == "group" else {}
        self_name = str(profile.get("nick") or "我")
        messages = []
        for item in reversed(rows):
            sender_uid = str(item.get("senderUid") or "")
            if sender_uid and sender_uid == self.self_uid:
                sender = "我"
            elif actual_kind == "group":
                sender = group_names.get(sender_uid) or str(item.get("senderUin") or sender_uid or "未知成员")
            else:
                sender = str(item.get("senderUin") or sender_uid or self_name)
            timestamp = item.get("sendTime")
            messages.append(
                {
                    "id": str(item.get("msgId") or ""),
                    "sender": sender,
                    "time": _format_time(timestamp),
                    "timestamp": timestamp,
                    "content": _qq_content(item),
                    "message_type": "message",
                    "source": "qq-realtime",
                }
            )
        return messages
