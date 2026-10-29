# -*- coding: utf-8 -*-
"""微信 WeChatDataAnalysis 的本地数据源适配器。"""

import urllib.parse
from datetime import datetime
from typing import Dict, List, Optional, Tuple

import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry


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


# ==================== 通讯录归一 ====================

# 上游 counts 的分类键（§14.7.1 实测），逐字对齐，不做改名。
CONTACT_COUNT_KEYS = (
    "friends",
    "groups",
    "officials",
    "officialSubscriptions",
    "officialServices",
    "officialEnterprises",
    "officialUnknown",
    "formerFriends",
    "blocked",
    "total",
)

# 联系人文本字段（§14.7.1 实测清单）。gender / addTime / sourceScene 上游未标注类型，
# 统一归一为文本，避免客户端反序列化时因"数字还是字符串"整条退化。
CONTACT_TEXT_FIELDS = (
    "username", "displayName", "remark", "nickname", "alias", "gender", "signature",
    "type", "country", "province", "city", "region", "sourceScene", "source",
    "addTime", "addTimeText", "avatarLink", "pinyinKey", "pinyinInitial",
)


def _to_text(value) -> str:
    return "" if value is None else str(value)


def _to_int(value) -> int:
    try:
        return int(float(value))
    except (TypeError, ValueError):
        return 0


def normalize_contact(item: Dict) -> Dict:
    """把上游联系人裁剪成 §14.7.1 的字段白名单，丢弃上游多给的字段。

    avatar 恒置空：上游给的是 http://127.0.0.1:10392/api/chat/avatar?...，
    带着上游端口，必须由 server.py 改写成 /api/contacts/avatar 后再下发。
    """
    contact = {name: _to_text(item.get(name)) for name in CONTACT_TEXT_FIELDS}
    contact["commonChatroomCount"] = _to_int(item.get("commonChatroomCount"))
    contact["avatar"] = ""
    return contact


def normalize_contact_counts(raw) -> Dict:
    source = raw if isinstance(raw, dict) else {}
    return {key: _to_int(source.get(key)) for key in CONTACT_COUNT_KEYS}


def normalize_usernames(value) -> str:
    """usernames 归一为逗号分隔字符串：不传 / 空串 / [""] 一律得到空串（= 全部）。"""
    if value is None:
        parts = []
    elif isinstance(value, (list, tuple, set)):
        parts = [_to_text(item).strip() for item in value]
    else:
        parts = [_to_text(value).strip()]
    result = []
    for part in parts:
        for piece in part.split(","):
            piece = piece.strip()
            if piece:
                result.append(piece)
    return ",".join(result)


# ==================== 朋友圈归一 ====================

# media 定位参数的键（§14.8.1.1 实测）。客户端整套回传，这里只做键白名单，值原样保留。
SNS_ATTR_KEYS = ("type", "md5", "key", "enc_idx", "token")


def normalize_sns_attrs(raw):
    if not isinstance(raw, dict):
        return None
    attrs = {key: raw[key] for key in SNS_ATTR_KEYS if raw.get(key) not in (None, "")}
    return attrs or None


def normalize_sns_size(raw) -> Dict:
    source = raw if isinstance(raw, dict) else {}
    return {
        "width": _to_int(source.get("width")),
        "height": _to_int(source.get("height")),
        "totalSize": _to_int(source.get("totalSize")),
    }


def normalize_sns_media(item) -> Optional[Dict]:
    if not isinstance(item, dict):
        return None
    return {
        "type": _to_text(item.get("type")),
        "id": _to_text(item.get("id")),
        "url": _to_text(item.get("url")),
        "thumb": _to_text(item.get("thumb")),
        "urlAttrs": normalize_sns_attrs(item.get("urlAttrs")),
        "thumbAttrs": normalize_sns_attrs(item.get("thumbAttrs")),
        "size": normalize_sns_size(item.get("size")),
    }


def normalize_sns_post(item: Dict) -> Dict:
    media = item.get("media")
    return {
        "id": _to_text(item.get("id")),
        "tid": _to_text(item.get("tid")),
        "username": _to_text(item.get("username")),
        "displayName": _to_text(item.get("displayName")),
        # createTime 转文本原样保留（客户端要把它当 create_time 回传），另给一份可读时间。
        "createTime": _to_text(item.get("createTime")),
        "createTimeText": _format_time(item.get("createTime")),
        "contentDesc": _to_text(item.get("contentDesc")),
        "location": _to_text(item.get("location")),
        "sourceName": _to_text(item.get("sourceName")),
        "media": [m for m in (normalize_sns_media(x) for x in media) if m]
        if isinstance(media, list) else [],
        # likes / comments / finderFeed / official 结构未实测，原样透传，不做字段猜测。
        "likes": item.get("likes"),
        "comments": item.get("comments"),
        "type": _to_text(item.get("type")),
        "title": _to_text(item.get("title")),
        "contentUrl": _to_text(item.get("contentUrl")),
        "finderFeed": item.get("finderFeed"),
        "official": item.get("official"),
    }


# ==================== 上游响应脱敏 ====================

# 落盘路径一律不下发：ZIP 产物只通过 /api/sns/export/{id}/download 回传。
# zip_path / zippath / zip_dir / zipdir 是 2026-09-11 实测补上的：上游导出任务对象里
# 有一条 zipPath（本机绝对路径），只覆盖 output_dir / path 这几种写法是挡不住的。
_PATH_KEY_NAMES = {
    "output_dir", "outputdir", "dir", "directory",
    "path", "file_path", "filepath", "save_path", "savepath",
    "zip_path", "zippath", "zip_dir", "zipdir",
}


def sanitize_upstream_payload(value, base_url: str = ""):
    """递归剔除落盘路径与指向上游自身的地址，避免把 10392 暴露给客户端。"""
    if isinstance(value, dict):
        return {
            key: sanitize_upstream_payload(item, base_url)
            for key, item in value.items()
            if _to_text(key).lower() not in _PATH_KEY_NAMES
        }
    if isinstance(value, list):
        return [sanitize_upstream_payload(item, base_url) for item in value]
    if isinstance(value, str) and base_url and base_url in value:
        return ""
    return value


class WeChatAdapter:
    """通过 WeChatDataAnalysis HTTP API 读取微信实时数据库。"""

    def __init__(self, base_url: str, account: str = ""):
        self.base_url = base_url.rstrip("/")
        self.account = account.strip()
        self.session = requests.Session()
        # 上游 uvicorn 的 keep-alive 空闲超时默认 5 秒，而自动回复引擎的 POLL_INTERVAL 恰好
        # 也是 5 秒：Session 复用连接时大概率抓到上游"刚好关闭"的垂死连接，报
        # RemoteDisconnected / ConnectionResetError(10054)。故障是**稀疏**的（中间大段轮询成功），
        # 说明上游没挂，纯粹是连接复用撞车 —— 这段重试不是在兜"上游不稳"，不要当成冗余删掉。
        retry = Retry(
            total=2,
            # 只在连接层重试：连接还没建立成功，重发不会造成任何副作用。
            connect=2,
            # read / status 必须为 0。重试"读超时"或"非成功状态码"会把上游**已经处理完**的
            # POST 再执行一遍（本工具的上游端点暂时幂等，但不要埋下这个习惯）。
            read=0,
            status=0,
            backoff_factor=0.2,
            # 因为只在连接层重试，所有方法重试都是安全的；设为 None 兼容 POST/PUT
            # （默认的方法白名单会漏掉它们）。
            allowed_methods=None,
        )
        adapter = HTTPAdapter(max_retries=retry, pool_maxsize=8)
        self.session.mount("http://", adapter)
        self.session.mount("https://", adapter)

    def _get(self, path: str, params: Optional[Dict] = None, timeout: int = 60) -> Dict:
        try:
            response = self.session.get(self.base_url + path, params=params, timeout=timeout)
            response.raise_for_status()
            data = response.json()
        except (requests.RequestException, ValueError) as exc:
            raise DataSourceError("WeChatDataAnalysis 请求失败：%s" % exc) from exc
        if data.get("status") not in ("success", "completed", 0, None):
            # 优先取上游自带的中文 message/errmsg（能定位到具体问题），兜底才报状态；
            # 不 dump 整个上游 payload（logs/ 禁止外发，§10 R10）。
            raise DataSourceError(
                str(data.get("message") or data.get("errmsg")
                    or ("上游返回非成功状态：%s" % data.get("status")))
            )
        return data

    def _post(self, path: str, payload: Optional[Dict] = None, timeout: int = 60,
              ok_statuses: Tuple[str, ...] = ()) -> Dict:
        """_get 的 POST 版本（上游的导出 / 增量同步都是 POST）。

        ``ok_statuses`` 是本端点**额外**认可的 status 取值，由调用方显式传入。
        刻意只加在 _post 上：``noop``（空操作）只有同步类接口才有这种幂等语义，
        放进全局白名单会让 contacts / timeline / export 上的 noop 也被算成成功 ——
        那些端点上的 noop 是真异常。
        """
        try:
            response = self.session.post(self.base_url + path, json=payload or {}, timeout=timeout)
            response.raise_for_status()
            data = response.json()
        except (requests.RequestException, ValueError) as exc:
            raise DataSourceError("WeChatDataAnalysis 请求失败：%s" % exc) from exc
        if data.get("status") not in ("success", "completed", 0, None, *ok_statuses):
            # 优先取上游自带的中文 message/errmsg（能定位到具体问题），兜底才报状态；
            # 不 dump 整个上游 payload：logs/ 已被 §10 R10 列为禁止外发，
            # 把整段响应写进去是没有必要的风险。
            raise DataSourceError(
                str(data.get("message") or data.get("errmsg")
                    or ("上游返回非成功状态：%s" % data.get("status")))
            )
        return data

    def _fetch_bytes(self, url: str, params: Optional[Dict] = None,
                     timeout: int = 60) -> Tuple[bytes, str, Dict]:
        """拉取上游二进制（头像 / 朋友圈图片 / 视频 / 导出 ZIP）。

        URL 由本类自行拼装、不接受调用方传来的完整地址，因此不存在"传入任意 URL 被转发"的 SSRF 风险。
        """
        try:
            response = self.session.get(url, params=params, timeout=timeout)
            response.raise_for_status()
        except requests.RequestException as exc:
            raise DataSourceError("WeChatDataAnalysis 媒体请求失败：%s" % exc) from exc
        content_type = response.headers.get("Content-Type") or "application/octet-stream"
        return response.content, content_type, dict(response.headers)

    def _resolve_account(self) -> str:
        if self.account:
            return self.account
        data = self._get("/api/chat/accounts")
        account = str(data.get("default_account") or "").strip()
        if not account:
            raise DataSourceError("微信数据已发现，但尚未完成密钥验证和数据库解锁")
        self.account = account
        return account

    def media_url(self, kind: str, target_id: str, md5: str = "", file_id: str = "") -> str:
        """拼装上游媒体接口地址（图片 / 表情 / 视频等共用 /api/chat/media/{kind}）。

        参数由本方法自行拼装、不接收调用方传来的完整 URL，避免把上游地址当成外部输入。
        """
        account = self._resolve_account()
        params = {"account": account, "username": target_id}
        if md5:
            params["md5"] = md5
        if file_id:
            params["file_id"] = file_id
        return self.base_url + "/api/chat/media/%s?%s" % (kind, urllib.parse.urlencode(params))

    def fetch_media(self, kind: str, target_id: str, md5: str = "",
                    file_id: str = "", timeout: int = 60) -> Tuple[bytes, str]:
        """拉取媒体原始字节，返回 (内容, Content-Type)。"""
        url = self.media_url(kind, target_id, md5, file_id)
        try:
            response = self.session.get(url, timeout=timeout)
            response.raise_for_status()
        except requests.RequestException as exc:
            raise DataSourceError("WeChatDataAnalysis 媒体请求失败：%s" % exc) from exc
        content_type = response.headers.get("Content-Type") or "application/octet-stream"
        return response.content, content_type

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
                    # 图片消息的定位信息：上游不保证每次都下发可直接使用的 imageUrl，
                    # 因此这里只保留 md5 / file_id 原始事实，由服务端按需拼装代理地址。
                    "image_md5": str(item.get("imageMd5") or ""),
                    "image_file_id": str(item.get("imageFileId") or ""),
                }
            )
        return messages

    # ==================== 通讯录 ====================

    def list_contacts(self, include: Optional[Dict] = None, keyword: str = "",
                      account: str = "") -> Dict:
        """GET /api/chat/contacts：**一次返回全部**（§14.7.1 实测结论 1：上游没有分页参数）。"""
        params = {"account": (account or "").strip() or self._resolve_account()}
        # include_* 一律按上游的 "true"/"false" 文本下发，不做二次语义映射。
        for name, value in (include or {}).items():
            params[name] = "true" if value else "false"
        text = (keyword or "").strip()
        if text:
            params["keyword"] = text
        data = self._get("/api/chat/contacts", params, timeout=90)
        contacts = [
            normalize_contact(item)
            for item in (data.get("contacts") or [])
            if isinstance(item, dict)
        ]
        return {
            "total": _to_int(data.get("total")) or len(contacts),
            "counts": normalize_contact_counts(data.get("counts")),
            "contacts": contacts,
            "source": _to_text(data.get("source")),
            "sourceRequested": _to_text(data.get("sourceRequested")),
            "sourceFallback": bool(data.get("sourceFallback")),
            "sourceFallbackReason": _to_text(data.get("sourceFallbackReason")),
            "sourceFallbackMessage": _to_text(data.get("sourceFallbackMessage")),
            "sourceFallbackRetryAfterSeconds": _to_int(
                data.get("sourceFallbackRetryAfterSeconds")
            ),
        }

    def fetch_avatar(self, username: str, timeout: int = 60) -> Tuple[bytes, str]:
        """GET /api/chat/avatar：按 username 取头像字节，上游地址不外发。"""
        content, content_type, _ = self._fetch_bytes(
            self.base_url + "/api/chat/avatar",
            {"account": self._resolve_account(), "username": username},
            timeout,
        )
        return content, content_type

    # ==================== 朋友圈 ====================

    def sns_timeline(self, limit: int = 20, offset: int = 0,
                     usernames="", keyword: str = "") -> Dict:
        """GET /api/sns/timeline。usernames 是逗号分隔字符串，不传即全部。"""
        params = {"account": self._resolve_account(), "limit": int(limit), "offset": int(offset)}
        names = normalize_usernames(usernames)
        if names:
            params["usernames"] = names
        text = (keyword or "").strip()
        if text:
            params["keyword"] = text
        data = self._get("/api/sns/timeline", params, timeout=90)
        timeline = [
            normalize_sns_post(item)
            for item in (data.get("timeline") or [])
            if isinstance(item, dict)
        ]
        raw_offset = data.get("offset")
        return {
            "timeline": timeline,
            "hasMore": bool(data.get("hasMore")),
            "limit": _to_int(data.get("limit")) or int(limit),
            "offset": int(offset) if raw_offset is None else _to_int(raw_offset),
            "source": _to_text(data.get("source")),
            "cover": data.get("cover"),
            "covers": data.get("covers"),
            "sourceRequested": _to_text(data.get("sourceRequested")),
            "sourceFallback": bool(data.get("sourceFallback")),
            "sourceFallbackReason": _to_text(data.get("sourceFallbackReason")),
            "sourceFallbackMessage": _to_text(data.get("sourceFallbackMessage")),
            "sourceFallbackRetryAfterSeconds": _to_int(
                data.get("sourceFallbackRetryAfterSeconds")
            ),
        }

    def sns_users(self, keyword: str = "", limit: int = 5000) -> Dict:
        params = {"account": self._resolve_account(), "limit": int(limit)}
        text = (keyword or "").strip()
        if text:
            params["keyword"] = text
        data = self._get("/api/sns/users", params, timeout=90)
        items = []
        for item in (data.get("items") or []):
            if not isinstance(item, dict):
                continue
            items.append({
                "username": _to_text(item.get("username")),
                "displayName": _to_text(item.get("displayName")),
                "postCount": _to_int(item.get("postCount")),
            })
        return {
            "items": items,
            "count": _to_int(data.get("count")) or len(items),
            "limit": _to_int(data.get("limit")) or int(limit),
        }

    def sns_sync_latest(self, max_scan: int = 200, force: bool = False) -> Dict:
        """POST /api/sns/realtime/sync_latest：把实时朋友圈增量拉进解密库，可能较慢。"""
        payload = {
            "account": self._resolve_account(),
            "max_scan": int(max_scan),
            "force": 1 if force else 0,
        }
        # 上游在"没有新数据"时返回 status=noop（幂等空操作，实测 scanned=200 / upserted=0），
        # 那是成功语义，不是失败。只有这条端点放行它。
        data = self._post("/api/sns/realtime/sync_latest", payload, timeout=180,
                          ok_statuses=("noop",))
        return sanitize_upstream_payload(data, self.base_url)

    def fetch_sns_media(self, params: Dict, timeout: int = 90) -> Tuple[bytes, str]:
        """GET /api/sns/media。定位参数由调用方（server.py）白名单收敛后传入。"""
        query = {"account": self._resolve_account()}
        query.update(params or {})
        content, content_type, _ = self._fetch_bytes(
            self.base_url + "/api/sns/media", query, timeout
        )
        return content, content_type

    # ==================== 朋友圈导出（异步任务） ====================

    def sns_export_create(self, payload: Dict, timeout: int = 180) -> Dict:
        body = {"account": self._resolve_account()}
        body.update(payload or {})
        data = self._post("/api/sns/exports", body, timeout=timeout)
        return sanitize_upstream_payload(data, self.base_url)

    def sns_export_status(self, export_id: str, timeout: int = 60) -> Dict:
        path = "/api/sns/exports/%s" % urllib.parse.quote(str(export_id), safe="")
        data = self._get(path, timeout=timeout)
        return sanitize_upstream_payload(data, self.base_url)

    def sns_export_download(self, export_id: str,
                            timeout: int = 300) -> Tuple[bytes, str, Dict]:
        path = "/api/sns/exports/%s/download" % urllib.parse.quote(str(export_id), safe="")
        return self._fetch_bytes(
            self.base_url + path,
            {"account": self._resolve_account()},
            timeout,
        )

