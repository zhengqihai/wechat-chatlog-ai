# -*- coding: utf-8 -*-
"""基于 WeChatDataAnalysis 的微信实时聊天分析服务。"""

import os
import json
import random
import asyncio
import re
import secrets
import threading
import requests
import time
import urllib.parse
from collections import deque
from datetime import datetime
from typing import List, Dict, Optional
from pathlib import Path

from fastapi import FastAPI, HTTPException, Body, Query
from fastapi.responses import JSONResponse, Response
from fastapi.middleware.cors import CORSMiddleware
import uvicorn

from adapters import WeChatAdapter
from config_defaults import DEFAULT_CONFIG, DEFAULT_PROMPTS

try:
    import automation
    AUTOMATION_AVAILABLE = True
except Exception:
    AUTOMATION_AVAILABLE = False

# 自研本地数据链路（阶段 C）。用 try 包住的原因与 automation 一样：
# 它是可选能力，坏掉时服务必须能继续用上游跑，而不是整个 FastAPI 起不来。
try:
    from localdb import keys as localdb_keys
    from localdb import service as localdb_service
    LOCALDB_AVAILABLE = True
except Exception:
    localdb_keys = None
    localdb_service = None
    LOCALDB_AVAILABLE = False

BASE_DIR = Path(__file__).parent.resolve()
ROOT_DIR = Path(os.environ.get("CHATLOG_ROOT_DIR") or BASE_DIR.parent).resolve()
# 可写数据根目录：正式安装版由 .NET 客户端指向 ProgramData（程序目录只读，不可写）；
# 便携版未设置 CHATLOG_DATA_DIR 时回退到项目根目录，行为与改造前完全一致。
DATA_ROOT = Path(os.environ.get("CHATLOG_DATA_DIR") or ROOT_DIR).resolve()
# 全部配置集中在 config/ 根目录：主配置、API Key、提示词、按账号隔离的自动回复。
CONFIG_DIR = DATA_ROOT / "config"
CONFIG_FILE = CONFIG_DIR / "config.json"
API_KEYS_FILE = CONFIG_DIR / "api-keys.json"
PROMPTS_DIR = CONFIG_DIR / "prompts"
AUTO_REPLY_DIR = CONFIG_DIR / "auto-reply"
# 定时发送同样是"按账号隔离的配置"，与 auto-reply 平级，避免与主配置并发写冲突。
SCHEDULED_SEND_DIR = CONFIG_DIR / "scheduled-send"
# 运行时会话总结缓存属于数据而非配置，仍留在 data/ 下。
SUMMARIES_FILE = DATA_ROOT / "data" / "summaries.json"
# 定时发送的运行时状态（上次发送时刻、抖动后的待触发时刻）属于数据而非配置：
# 若写回配置文件，会与界面随时保存的配置互相覆盖，必然出现"丢更新"。
SCHEDULED_STATE_FILE = DATA_ROOT / "data" / "scheduled-send-state.json"
# 账号切换的"待确认"闸门标记（T19-A，§7.17）。刻意与 config.json 同放 config/：它是
# "数据源指向哪个账号"这组状态的一半，另一半就是 config.json 的 wechat_account。
PENDING_SWITCH_FILE = CONFIG_DIR / "pending_account_switch.json"

app = FastAPI(title="Chatlog AI Summarizer", version="5.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

def load_config() -> dict:
    # 默认值来自 config_defaults（原 launcher 生成 config.json 的职责，现由 .NET 客户端 Bootstrap 承担）。
    default_config = dict(DEFAULT_CONFIG)
    if CONFIG_FILE.exists():
        try:
            # 必须用 utf-8-sig：记事本等编辑器写出的 UTF-8 会带 BOM，
            # 用 "utf-8" 读取时 json 解析会失败，而下面的 except 会把**整份配置**
            # （端口 / 上游地址 / 账号 / data_source）静默换成默认值 —— 排查方向会被彻底带偏。
            with open(CONFIG_FILE, "r", encoding="utf-8-sig") as f:
                data = json.load(f)
                default_config.update(data)
        except Exception as exc:
            # 不再静默吞掉：配置读坏必须在控制台看得见。
            print("[config] 读取 %s 失败，本次使用默认配置：%s" % (CONFIG_FILE, exc))
    return default_config


def save_config(config: dict) -> None:
    CONFIG_FILE.parent.mkdir(parents=True, exist_ok=True)
    temporary = CONFIG_FILE.with_suffix(CONFIG_FILE.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as file:
        json.dump(config, file, ensure_ascii=False, indent=2)
        file.write("\n")
    os.replace(str(temporary), str(CONFIG_FILE))


def load_api_keys() -> dict:
    """读取 config/api-keys.json，始终返回 {"providers": [...]} 结构。"""
    providers = []
    if API_KEYS_FILE.exists():
        try:
            with API_KEYS_FILE.open("r", encoding="utf-8") as file:
                data = json.load(file)
            if isinstance(data, dict) and isinstance(data.get("providers"), list):
                providers = [item for item in data["providers"] if isinstance(item, dict)]
        except (OSError, ValueError):
            providers = []
    return {"providers": providers}


def save_api_keys(data: dict) -> None:
    providers = [item for item in (data.get("providers") or []) if isinstance(item, dict)]
    _write_json_file(API_KEYS_FILE, {"providers": providers})


def load_prompt(name: str, fallback: Optional[str] = None) -> str:
    """读取 config/prompts/<name>.txt；文件缺失或为空时回退到 config_defaults 的默认文案。"""
    if fallback is None:
        fallback = DEFAULT_PROMPTS.get(name, "")
    path = PROMPTS_DIR / (name + ".txt")
    try:
        text = path.read_text(encoding="utf-8").strip()
    except OSError:
        return fallback
    return text or fallback


# 国内主流模型供应商预设；余额接口仅在厂商提供标准 REST 接口时配置。
PROVIDER_PRESETS = {
    "deepseek": {
        "name": "DeepSeek",
        "base_url": "https://api.deepseek.com",
        "model": "deepseek-chat",
        "balance_path": "/user/balance",
        "balance_parser": "deepseek",
    },
    "kimi": {
        "name": "Kimi（Moonshot）",
        "base_url": "https://api.moonshot.cn/v1",
        "model": "moonshot-v1-8k",
        "balance_path": "/users/me/balance",
        "balance_parser": "moonshot",
    },
    "zhipu": {
        "name": "智谱 GLM",
        "base_url": "https://open.bigmodel.cn/api/paas/v4",
        "model": "glm-4-flash",
        "balance_path": None,
        "balance_parser": None,
    },
    "qwen": {
        "name": "通义千问（DashScope）",
        "base_url": "https://dashscope.aliyuncs.com/compatible-mode/v1",
        "model": "qwen-plus",
        "balance_path": None,
        "balance_parser": None,
    },
    "custom": {
        "name": "自定义（OpenAI 兼容）",
        "base_url": "",
        "model": "",
        "balance_path": None,
        "balance_parser": None,
    },
}


def list_providers() -> list:
    return load_api_keys()["providers"]


def get_active_provider() -> dict:
    config = load_config()
    providers = list_providers()
    active_id = str(config.get("active_provider") or "").strip()
    if active_id and any(str(item.get("id") or "") == active_id for item in providers):
        pass
    elif providers:
        active_id = str(providers[0].get("id") or "").strip()
    else:
        return {}
    for item in providers:
        if str(item.get("id") or "") == active_id:
            return item
    return {}


def _mask_api_key(api_key) -> str:
    key = str(api_key or "").strip()
    if not key:
        return ""
    if len(key) <= 8:
        return key[:2] + "***"
    return key[:4] + "****" + key[-4:]


def _extract_api_error(data) -> str:
    """从不同厂商的响应体中提取可读错误信息。"""
    if not isinstance(data, dict):
        return str(data)
    error = data.get("error")
    if isinstance(error, dict):
        return str(error.get("message") or error)
    if isinstance(error, str) and error:
        return error
    return str(data.get("message") or data)


def _chat_completion(base_url: str, model: str, api_key: str, messages: List[Dict],
                     temperature: float = 0.7, timeout: int = 120) -> str:
    """调用 OpenAI 兼容的 /chat/completions，返回助手正文。"""
    url = base_url.rstrip("/") + "/chat/completions"
    payload = {"model": model, "messages": messages, "temperature": temperature}
    headers = {"Authorization": "Bearer %s" % api_key, "Content-Type": "application/json"}
    try:
        response = requests.post(url, json=payload, headers=headers, timeout=timeout)
    except requests.RequestException as exc:
        raise HTTPException(status_code=502, detail="无法连接模型服务：%s" % exc) from exc
    if response.status_code >= 400:
        try:
            body = response.json()
        except ValueError:
            body = response.text
        raise HTTPException(
            status_code=502,
            detail="模型 API 错误（HTTP %s）：%s" % (response.status_code, _extract_api_error(body)),
        )
    try:
        res_json = response.json()
    except ValueError as exc:
        raise HTTPException(status_code=502, detail="模型 API 返回无效数据：%s" % exc) from exc
    if "choices" in res_json and res_json["choices"]:
        return str(res_json["choices"][0].get("message", {}).get("content") or "")
    raise HTTPException(status_code=502, detail="模型 API 返回异常：%s" % _extract_api_error(res_json))


def _query_balance(provider: dict) -> dict:
    """按供应商预设的余额接口解析账户余额。"""
    base_url = str(provider.get("base_url") or "").rstrip("/")
    api_key = str(provider.get("api_key") or "").strip()
    balance_path = str(provider.get("balance_path") or "").strip()
    parser = str(provider.get("balance_parser") or "").strip()
    if not base_url or not api_key or not balance_path:
        return {"available": False, "message": "该供应商未配置余额查询接口"}

    url = base_url + balance_path
    headers = {"Authorization": "Bearer %s" % api_key}
    try:
        response = requests.get(url, headers=headers, timeout=30)
    except requests.RequestException as exc:
        raise HTTPException(status_code=502, detail="余额接口请求失败：%s" % exc) from exc
    if response.status_code >= 400:
        try:
            body = response.json()
        except ValueError:
            body = response.text
        raise HTTPException(
            status_code=502,
            detail="余额接口错误（HTTP %s）：%s" % (response.status_code, _extract_api_error(body)),
        )
    try:
        data = response.json()
    except ValueError as exc:
        raise HTTPException(status_code=502, detail="余额接口返回无效数据：%s" % exc) from exc

    if parser == "deepseek":
        if not data.get("is_available"):
            return {"available": False, "message": str(data.get("message") or "账户不可用")}
        infos = data.get("balance_infos") or []
        parts = ["%s %s" % (info.get("total_balance", "0"), info.get("currency", "")) for info in infos]
        return {"available": True, "text": "；".join(parts) or "0"}
    if parser == "moonshot":
        if data.get("code") != 0:
            return {"available": False, "message": str(data.get("message") or data)}
        detail = data.get("data") or {}
        available = detail.get("available_balance")
        return {
            "available": True,
            "available_balance": available,
            "voucher_balance": detail.get("voucher_balance"),
            "cash_balance": detail.get("cash_balance"),
            "text": "可用余额 ¥%s" % available,
        }
    return {"available": True, "text": str(data)}


def load_summaries() -> dict:
    """读取 data/summaries.json（会话 ID → 历史总结列表）。"""
    data = _read_json_file(SUMMARIES_FILE)
    return data if isinstance(data, dict) else {}


def save_summaries(data: dict) -> None:
    _write_json_file(SUMMARIES_FILE, data)


AUTO_REPLY_DEFAULTS = {
    "enabled": False,
    "guard_targets": [],
    "context_count": 80,
    # 同一会话的最小回复间隔（秒），0 = 不限制。字段名与取值范围是对客户端冻结的契约，
    # 见 §7.16，不要改名或改语义。
    "reply_cooldown_seconds": 10,
    "reply_prompt": "",
    "hotkeys": {
        "open_wechat": "ctrl+alt+w",
        "search": "ctrl+f",
        "paste": "ctrl+v",
        "send": "enter",
        # 第 5 个可配键（§14.3.4）：「搜索后确认进入会话」用的键。
        # 旧配置里没有它，load_auto_reply 的合并与 automation 的兜底都会落到 enter，
        # 因此现有用户升级后行为不变。
        "confirm": "enter",
    },
}

# 自动回复配置按微信账号隔离存放：每个账号一个独立 JSON 文件，
# 与 config/config.json 分开，避免多账号切换时 prompt/守护对象串号。


def _auto_reply_account_key() -> str:
    """当前微信账号标识；未显式选择账号时归入 default。"""
    config = load_config()
    account = str(config.get("wechat_account") or "").strip()
    return account or "default"


def _auto_reply_file_path(account: str) -> Path:
    safe = "".join(ch for ch in account if ch.isalnum() or ch in "-_")
    if not safe:
        safe = "default"
    return AUTO_REPLY_DIR / (safe + ".json")


def _read_json_file(path: Path) -> dict:
    try:
        with path.open("r", encoding="utf-8") as file:
            data = json.load(file)
            return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def _write_json_file(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as file:
        json.dump(data, file, ensure_ascii=False, indent=2)
        file.write("\n")
    os.replace(str(temporary), str(path))


# ==================== 账号切换闸门（T19-A，见 docs/DOTNET_CLIENT_PLAN.md §7.17） ====================
#
# 为什么需要闸门：程序能改"数据源读哪个账号的数据"，却无法确认"微信客户端现在登录的是谁"
# （上游 /api/current-account 对个人微信恒返回 null，见 §14.4.1.1）。危险窗口 = 数据源已指向 B、
# 客户端还登录着 A，此时键盘模拟出的消息会落进 A 的会话，且不可撤销。所以切换后先暂停发送。
#
# 闸门只拦"发送动作"，不拦"检测"：检测照常跑，用户才能看到"有新消息但没回"。
#
# 状态载体是 PENDING_SWITCH_FILE 是否存在；**启动时读一次**缓存在内存，之后只由 switch / confirm
# 两个接口改写内存并按需要的时机落盘 —— 不要每轮去读盘（检测轮询是秒级的）。

_PENDING_SWITCH_LOCK = threading.Lock()
_pending_switch_state: Optional[Dict] = None


def _load_pending_switch_from_disk() -> Optional[Dict]:
    """启动时读一次标记文件；文件不存在或内容不完整一律视为"未暂停"。"""
    data = _read_json_file(PENDING_SWITCH_FILE)
    account = str(data.get("account") or "").strip()
    if not account:
        return None
    return {
        "account": account,
        "previousAccount": str(data.get("previousAccount") or "").strip(),
        "requestedAt": data.get("requestedAt"),
    }


def get_pending_switch() -> Optional[Dict]:
    with _PENDING_SWITCH_LOCK:
        return dict(_pending_switch_state) if _pending_switch_state else None


def pending_switch_payload() -> Dict:
    """`GET /api/wechat/account/pending` 与 accounts 里的 `pendingSwitch` 共用一份结构。

    两处各写一套字段迟早会漂，所以只保留这一个出口。
    """
    pending = get_pending_switch()
    if not pending:
        return {"pending": False, "account": "", "previousAccount": "", "requestedAt": None}
    return {
        "pending": True,
        "account": pending.get("account") or "",
        "previousAccount": pending.get("previousAccount") or "",
        "requestedAt": pending.get("requestedAt"),
    }


def _ensure_send_allowed() -> bool:
    """共享发送谓词：闸门关闭时返回 False。**所有"会真的做键盘模拟发送"的入口都必须先问它。**

    覆盖范围刻意包含手动发送：待确认状态的含义就是"程序不确定微信客户端登录的是谁"，
    此时**任何**键盘模拟都可能发错人——手动发送不是例外，它同样是在"登录着 A 的窗口里
    搜索 B 的会话名"。各调用点的差别只在"被拦之后怎么办"（两个引擎记日志并跳过、HTTP
    端点回 409），判据只有这一份，避免两处各写一套后逐渐漂移。
    """
    return get_pending_switch() is None


def send_paused_reason() -> str:
    """闸门关闭的原因（一句话）。引擎日志、status 端点、409 文案共用同一句，避免三处措辞漂移。"""
    pending = get_pending_switch()
    if not pending:
        return ""
    return "账号切换待确认：数据源已指向「%s」，请在微信客户端登录该账号并点「我已切换」，期间禁止发送" % (
        pending.get("account") or ""
    )


def _set_pending_switch_memory(pending: Optional[Dict]) -> None:
    """只改内存（switch 顺序里的第 2 步"先置闸门"）。"""
    global _pending_switch_state
    with _PENDING_SWITCH_LOCK:
        _pending_switch_state = dict(pending) if pending else None


def _persist_pending_switch(pending: Dict) -> None:
    """落盘（switch 顺序里的第 7 步），跨重启有效。"""
    _write_json_file(PENDING_SWITCH_FILE, dict(pending))


def _clear_pending_switch_file() -> None:
    try:
        PENDING_SWITCH_FILE.unlink()
    except OSError:
        pass


_pending_switch_state = _load_pending_switch_from_disk()


def _load_auto_reply_raw() -> dict:
    """读取当前账号的自动回复配置。"""
    account = _auto_reply_account_key()
    path = _auto_reply_file_path(account)
    if path.exists():
        return _read_json_file(path)
    return {}


def load_auto_reply() -> dict:
    stored = _load_auto_reply_raw()
    merged = json.loads(json.dumps(AUTO_REPLY_DEFAULTS))
    merged.update(stored)
    stored_hotkeys = stored.get("hotkeys") if isinstance(stored.get("hotkeys"), dict) else {}
    merged["hotkeys"] = {**AUTO_REPLY_DEFAULTS["hotkeys"], **stored_hotkeys}
    return merged


def save_auto_reply(settings: dict) -> dict:
    account = _auto_reply_account_key()
    path = _auto_reply_file_path(account)
    current = _load_auto_reply_raw()
    for key, value in settings.items():
        if value is None:
            continue
        current[key] = value
    _write_json_file(path, current)
    return load_auto_reply()


class AutoReplyEngine:
    """后台守护引擎：两级探测 → 生成 → 发送，与前端界面解耦。"""

    POLL_INTERVAL = 5.0
    DETECT_WINDOW = 30

    def __init__(self):
        self._thread = None
        self._stop_event = threading.Event()
        self._baselined = set()
        self._signatures = {}
        self._fingerprints = {}
        # 回复冷却：按会话分别记"上次回复成功的时刻"，给 A 回复过不压住 B 的回复。
        # 冷却值本身是按账号一份的配置项（reply_cooldown_seconds，§7.16），不在这里存。
        self._last_reply_at: Dict[str, float] = {}
        self.active_chat_name = None
        self.log = deque(maxlen=200)

    def _log(self, message: str, level: str = "info") -> None:
        self.log.append({
            "time": datetime.now().strftime("%H:%M:%S"),
            "level": level,
            "message": message,
        })

    def _get_adapter(self):
        """会话/消息的数据来源（upstream 或 local，见 `get_chat_source`）。

        这里不再自己缓存：缓存职责已收敛到 `get_chat_source()` 一处。
        两处各存一份必然出现"清了一个忘了另一个"（这正是原来 reset_adapter 存在的原因）。
        """
        return get_source("chat")

    def is_running(self) -> bool:
        return bool(self._thread and self._thread.is_alive())

    def reset_detection_state(self) -> None:
        """账号切换后必须调用：基线/签名/指纹都是**旧账号的会话**留下的。

        不重置的话，新账号的会话指纹与旧记录对不上 → 被判成"有新消息" → 刚切完账号就自动回复
        一条（§14.4.4 第 4 项）。冷却计时一并无意义：会话标识已不属于同一个账号。
        整体重新绑定空容器而不是 clear()：检测线程只做 get/set/`in`/add，不迭代这几个容器，
        换绑在 GIL 下是原子的，不需要额外加锁。

        `active_chat_name` 也一并清掉：它是"微信前台当前所处会话名"的记录，被作为参数传给
        `automation.send_wechat_message`，而那个函数在**目标会话名 == 这个记录**时会跳过搜索、
        直接粘贴发送。切换后这个记录属于旧账号，新账号若恰有同名会话（或前台恰停在该会话），
        就会把消息粘进别的会话——与"发错人"同类。清掉它同时修掉另一个现象：/api/auto-reply/status
        会把它发给客户端，不清的话界面会一直显示上一个账号的会话名。
        """
        self._baselined = set()
        self._signatures = {}
        self._fingerprints = {}
        self._last_reply_at = {}
        self.active_chat_name = None

    def start(self) -> None:
        if self.is_running():
            return
        self._stop_event.clear()
        self._thread = threading.Thread(target=self._run, name="auto-reply-engine", daemon=True)
        self._thread.start()
        self._log("守护引擎已启动")

    def stop(self) -> None:
        self._stop_event.set()

    def _run(self) -> None:
        while not self._stop_event.is_set():
            try:
                self._poll_once()
            except Exception as exc:
                self._log("守护引擎异常：%s" % exc, level="error")
            self._stop_event.wait(self.POLL_INTERVAL)

    def _poll_once(self) -> None:
        if not (AUTOMATION_AVAILABLE and os.name == "nt"):
            return
        config = load_auto_reply()
        if not config.get("enabled"):
            return
        targets = config.get("guard_targets") or []
        if not targets:
            return

        adapter = self._get_adapter()
        try:
            result = adapter.list_chats()
        except Exception as exc:
            self._log("读取会话列表失败：%s" % exc, level="error")
            return
        chats = result.get("chats") or []
        chat_by_id = {chat.get("id"): chat for chat in chats}

        for target in targets:
            target_id = str(target.get("id") or "").strip()
            if not target_id:
                continue
            chat = chat_by_id.get(target_id)
            if chat is None:
                name = target.get("name")
                chat = next((item for item in chats if item.get("name") == name), None)
            if chat is None:
                continue
            fingerprint = "%s|%s|%s" % (
                chat.get("last_message", ""),
                chat.get("last_time", ""),
                chat.get("unread", 0),
            )
            if self._fingerprints.get(target_id) == fingerprint:
                continue
            self._fingerprints[target_id] = fingerprint
            self._handle_changed(target, chat, config)

    def _handle_changed(self, target: Dict, chat: Dict, config: Dict) -> None:
        target_id = target["id"]
        target_name = target.get("name") or chat.get("name") or target_id
        try:
            messages = self._get_adapter().list_messages(target_id, limit=self.DETECT_WINDOW)
        except Exception as exc:
            self._log("读取「%s」消息失败：%s" % (target_name, exc), level="error")
            return

        # 排除系统消息（撤回 / 拍一拍 / 群公告 / 转账提示等）：上游对这类消息一律回
        # isSent=False，会被下面 "sender != 我" 的判据当成"对方发来的消息"。但系统消息不是
        # "对方说了话"，回复它没有意义，还会让用户看到"我撤回一条 → 机器人平白多回一条"。
        incoming = [
            m for m in messages
            if m.get("sender") and m.get("sender") != "我" and m.get("message_type") != "system"
        ]
        signature = None
        if incoming:
            latest = incoming[-1]
            signature = latest.get("id") or ("%s|%s" % (
                latest.get("timestamp", ""), latest.get("content", ""),
            ))

        if target_id not in self._baselined:
            self._baselined.add(target_id)
            self._signatures[target_id] = signature
            return
        previous = self._signatures.get(target_id)
        if signature is not None and signature != previous:
            self._signatures[target_id] = signature
            # 回复冷却（§7.16）：同一会话在冷却期内只回一次。对方连发多条时签名会逐条变化、
            # 每条各自触发一次生成，而实测第二次生成的输入与第一次完全相同（两条回复文本
            # 一字不差）→ 纯属刷屏。冷却期内的触发直接丢弃（不做"挂起合并"：那需要把待处理
            # 状态从 _handle_changed 挪到 _poll_once，而丢弃没有任何信息损失）。
            # 0 = 不限制；这里再夹一次是因为用户可以手改 config/auto-reply/{account}.json，
            # 只靠写入端清洗挡不住。日志必须记，否则用户会以为"漏回了"。
            cooldown = _clamp_int(config.get("reply_cooldown_seconds"), 0, 600, 10)
            if cooldown > 0 and time.monotonic() - self._last_reply_at.get(str(target_id), 0.0) < cooldown:
                self._log("「%s」在冷却期内，本条不单独回复" % target_name)
                return
            self._reply_to(target, target_name, messages, config)

    def _reply_to(self, target: Dict, target_name: str, messages: List[Dict], config: Dict) -> None:
        # 账号切换闸门（T19-A）：位置刻意选在"生成之前"而不是"发送之前"——_reply_to 是先调模型
        # 生成、再走键盘模拟发送的，拦在发送前的话，暂停期间每轮轮询都会白烧一次真实模型调用
        # （又慢又花钱）。拦下时**不记冷却时间**：消息没发出去，不该吃掉冷却窗口。
        # 注意语义：签名在 _handle_changed 里已经更新过了，所以这条消息视为"已消费"，确认后
        # **不补发**（补发会把十几分钟前的回复打在最危险的时刻，见 §7.17）。
        if not _ensure_send_allowed():
            self._log("账号切换待确认，暂不发送「%s」" % target_name)
            return
        context_count = max(1, int(config.get("context_count") or 80))
        reply_prompt = str(config.get("reply_prompt") or "").strip()
        inner_prompt = str(target.get("prompt") or "").strip()
        try:
            reply = generate_reply_text(target_name, messages, context_count, reply_prompt, inner_prompt)
        except HTTPException as exc:
            self._log("为「%s」生成回复失败：%s" % (target_name, exc.detail), level="error")
            return
        if not reply:
            self._log("为「%s」生成的回复为空" % target_name, level="error")
            return
        hotkeys = config.get("hotkeys") or {}
        try:
            # 与定时发送引擎共用一把锁：两者同时触发会互抢剪贴板与前台窗口，
            # 结果是一条消息被发到另一个会话（见 _AUTOMATION_LOCK 的说明）。
            with _AUTOMATION_LOCK:
                self.active_chat_name = automation.send_wechat_message(
                    target_name, reply, hotkeys, active_chat_name=self.active_chat_name
                )
        except Exception as exc:
            self._log("向「%s」发送失败：%s" % (target_name, exc), level="error")
            return
        # 只有发送成功才计时：生成失败 / 发送失败不该吃掉冷却窗口。
        self._last_reply_at[str(target.get("id") or "")] = time.monotonic()
        self._log("已回复「%s」" % target_name)


_auto_reply_engine = AutoReplyEngine()


# ==================== 定时发送（主动打招呼） ====================
#
# 与自动回复的区别：自动回复是"被动响应别人发来的消息"，定时发送是"到点主动开口"。
# 两者共用同一套键盘模拟能力，因此必须共用同一把 _AUTOMATION_LOCK（见下）。

SCHEDULED_SEND_DEFAULTS = {
    "enabled": False,
    "tasks": [],
}

SCHEDULED_MAX_TASKS = 50
SCHEDULED_MODES = ("daily", "weekly", "interval", "once")

# 键盘模拟是独占资源：自动回复与定时发送若同时触发，会互抢剪贴板与前台窗口，
# 结果是一条消息被发到另一个会话。两个引擎共用这一把锁把发送串行化。
_AUTOMATION_LOCK = threading.Lock()


def _scheduled_file_path(account: str) -> Path:
    safe = "".join(ch for ch in account if ch.isalnum() or ch in "-_")
    if not safe:
        safe = "default"
    return SCHEDULED_SEND_DIR / (safe + ".json")


def _clamp_int(value, low: int, high: int, default: int) -> int:
    try:
        number = int(value)
    except (TypeError, ValueError):
        return default
    return max(low, min(high, number))


def _normalize_time_of_day(value) -> str:
    """把任意输入折算成合法的 "HH:MM"；非法时回退 09:00。"""
    text = str(value or "").strip()
    try:
        hour_text, minute_text = text.split(":", 1)
        hour = int(hour_text)
        minute = int(minute_text)
    except (ValueError, AttributeError):
        return "09:00"
    if not (0 <= hour <= 23 and 0 <= minute <= 59):
        return "09:00"
    return "%02d:%02d" % (hour, minute)


def _normalize_run_at(value) -> str:
    """一次性任务的时间点，统一成 "YYYY-MM-DDTHH:MM"；无法解析时返回空串。"""
    text = str(value or "").strip()
    for fmt in ("%Y-%m-%dT%H:%M", "%Y-%m-%d %H:%M", "%Y-%m-%dT%H:%M:%S",
                "%Y/%m/%d %H:%M", "%Y/%m/%d %H:%M:%S"):
        try:
            return datetime.strptime(text, fmt).strftime("%Y-%m-%dT%H:%M")
        except ValueError:
            continue
    return ""


def _load_scheduled_raw() -> dict:
    path = _scheduled_file_path(_auto_reply_account_key())
    if path.exists():
        return _read_json_file(path)
    return {}


def load_scheduled_send() -> dict:
    stored = _load_scheduled_raw()
    merged = json.loads(json.dumps(SCHEDULED_SEND_DEFAULTS))
    merged.update(stored)
    if not isinstance(merged.get("tasks"), list):
        merged["tasks"] = []
    merged["enabled"] = bool(merged.get("enabled"))
    return merged


def save_scheduled_send(settings: dict) -> dict:
    path = _scheduled_file_path(_auto_reply_account_key())
    current = _load_scheduled_raw()
    for key, value in settings.items():
        if value is None:
            continue
        current[key] = value
    _write_json_file(path, current)
    return load_scheduled_send()


def load_scheduled_state() -> dict:
    return _read_json_file(SCHEDULED_STATE_FILE)


def save_scheduled_state(state: dict) -> None:
    _write_json_file(SCHEDULED_STATE_FILE, state)


def normalize_scheduled_tasks(value) -> List[Dict]:
    """校验并归一化定时任务；数量上限由 SCHEDULED_MAX_TASKS 强制。"""
    if not isinstance(value, list):
        raise HTTPException(status_code=400, detail="tasks 必须是数组")
    cleaned = []
    seen_ids = set()
    for item in value:
        if not isinstance(item, dict):
            continue
        target_id = str(item.get("target_id") or "").strip()
        target_name = str(item.get("target_name") or "").strip()
        if not target_id or not target_name:
            continue
        task_id = str(item.get("id") or "").strip() or ("sched-" + secrets.token_hex(5))
        if task_id in seen_ids:
            continue
        seen_ids.add(task_id)

        mode = str(item.get("mode") or "daily").strip()
        if mode not in SCHEDULED_MODES:
            mode = "daily"

        weekdays = []
        if isinstance(item.get("weekdays"), list):
            for day_value in item["weekdays"]:
                day = _clamp_int(day_value, 0, 7, 0)
                if 1 <= day <= 7 and day not in weekdays:
                    weekdays.append(day)
        if mode == "weekly" and not weekdays:
            weekdays = [1]

        run_at = _normalize_run_at(item.get("run_at"))
        if mode == "once" and not run_at:
            # 一次性任务没有有效时间点等于永远不会触发，直接拒收而不是留一条死任务。
            continue

        cleaned.append({
            "id": task_id,
            "target_id": target_id,
            "target_name": target_name,
            "target_type": "group" if str(item.get("target_type")) == "group" else "friend",
            "enabled": bool(item.get("enabled", True)),
            "mode": mode,
            "time_of_day": _normalize_time_of_day(item.get("time_of_day")),
            "weekdays": weekdays,
            "interval_minutes": _clamp_int(item.get("interval_minutes"), 1, 10080, 120),
            "run_at": run_at,
            "cooldown_minutes": _clamp_int(item.get("cooldown_minutes"), 0, 10080, 60),
            "jitter_minutes": _clamp_int(item.get("jitter_minutes"), 0, 1440, 15),
            "context_count": _clamp_int(item.get("context_count"), 1, 2000, 80),
            "prompt": str(item.get("prompt") or "").strip(),
        })

    if len(cleaned) > SCHEDULED_MAX_TASKS:
        raise HTTPException(status_code=400, detail="定时发送任务最多 %d 个" % SCHEDULED_MAX_TASKS)
    return cleaned


def generate_greeting_text(target_name: str, messages: List[Dict], context_count: int,
                           task_prompt: str = "") -> str:
    """生成一条主动打招呼的正文（定时发送与预览接口共用）。

    与 generate_reply_text 的差别只在"指令"：那边要的是"下一条回复"，
    这边要的是"重开话题的招呼"，因此用独立的 scheduled-greeting 提示词。
    允许没有历史消息——久未联系的人本来就没有可总结的上下文。
    """
    recent = messages[-context_count:] if messages else []
    text_lines = []
    for message in recent:
        text_lines.append("[%s] %s: %s" % (
            message.get("time", ""),
            message.get("sender", "未知"),
            message.get("content", ""),
        ))
    chat_text = "\n".join(text_lines)

    outer_prompt = load_prompt("scheduled-greeting")
    inner_prompt = str(task_prompt or "").strip()
    system_prompt = "%s\n\n%s" % (outer_prompt, inner_prompt) if inner_prompt else outer_prompt

    if recent:
        user_content = (
            "对话对象：%s\n最近 %d 条消息：\n%s\n\n请直接给出这条主动打招呼的消息："
            % (target_name, len(recent), chat_text)
        )
    else:
        user_content = (
            "对话对象：%s\n当前没有可用的历史消息。\n\n请直接给出这条主动打招呼的消息："
            % target_name
        )

    return _model_completion(
        [{"role": "system", "content": system_prompt},
         {"role": "user", "content": user_content}],
        temperature=0.8,
    ).strip()


def _scheduled_generate(task: Dict) -> str:
    """取上下文并生成打招呼正文；取数或调用模型失败时抛异常由调用方处理。"""
    target_id = str(task.get("target_id") or "").strip()
    target_name = task.get("target_name") or target_id
    context_count = _clamp_int(task.get("context_count"), 1, 2000, 80)
    messages = get_source("chat").list_messages(target_id, limit=context_count)
    return generate_greeting_text(target_name, messages, context_count, task.get("prompt") or "")


def _scheduled_base_due(task: Dict, last_sent: Optional[float], now: float) -> Optional[float]:
    """基础到期时刻（未加随机抖动）；未到期或本周期已发过则返回 None。"""
    mode = task.get("mode")
    now_dt = datetime.fromtimestamp(now)

    if mode == "interval":
        if last_sent is None:
            # 首次观测不补发：由调用方写入基线，等满一个间隔再说（与守护引擎同理）。
            return None
        due = last_sent + _clamp_int(task.get("interval_minutes"), 1, 10080, 120) * 60
        return due if due <= now else None

    if mode == "once":
        if last_sent is not None:
            return None
        try:
            due = datetime.strptime(str(task.get("run_at") or ""), "%Y-%m-%dT%H:%M").timestamp()
        except ValueError:
            return None
        return due if due <= now else None

    # daily / weekly：都按"当天该时刻"，且当天只发一次。
    time_text = _normalize_time_of_day(task.get("time_of_day"))
    due_dt = now_dt.replace(
        hour=int(time_text[:2]), minute=int(time_text[3:]), second=0, microsecond=0
    )
    if mode == "weekly" and now_dt.isoweekday() not in (task.get("weekdays") or []):
        return None
    if last_sent is not None and datetime.fromtimestamp(last_sent).date() == now_dt.date():
        return None
    return due_dt.timestamp() if due_dt.timestamp() <= now else None


def _scheduled_next_display(task: Dict, entry: Dict, now: float) -> Optional[float]:
    """仅供界面展示的"下次触发时间"（不含随机抖动）。"""
    mode = task.get("mode")

    if mode == "once":
        try:
            return datetime.strptime(str(task.get("run_at") or ""), "%Y-%m-%dT%H:%M").timestamp()
        except ValueError:
            return None

    if mode == "interval":
        last_sent = entry.get("last_sent_at")
        base = float(last_sent) if isinstance(last_sent, (int, float)) else now
        return base + _clamp_int(task.get("interval_minutes"), 1, 10080, 120) * 60

    time_text = _normalize_time_of_day(task.get("time_of_day"))
    weekdays = task.get("weekdays") or []
    for offset in range(0, 8):
        # 用 +86400 而不是 timedelta：省一次 import，且国内无夏令时，不影响结果。
        candidate = datetime.fromtimestamp(now + offset * 86400).replace(
            hour=int(time_text[:2]), minute=int(time_text[3:]), second=0, microsecond=0
        )
        if mode == "weekly" and candidate.isoweekday() not in weekdays:
            continue
        if candidate.timestamp() <= now:
            continue
        return candidate.timestamp()
    return None


class ScheduledSendEngine:
    """后台定时引擎：到点 → 取上下文 → AI 生成招呼 → 键盘模拟发送。

    触发模型是"基础到期时刻 + 随机抖动"：抖动区间内随机推迟，这是"像真人"的关键。
    已摇定的待触发时刻会落盘，进程重启不会重摇一次。
    """

    POLL_INTERVAL = 30.0

    def __init__(self):
        self._thread = None
        self._stop_event = threading.Event()
        self.log = deque(maxlen=200)

    def _log(self, message: str, level: str = "info") -> None:
        self.log.append({
            "time": datetime.now().strftime("%H:%M:%S"),
            "level": level,
            "message": message,
        })

    def is_running(self) -> bool:
        return bool(self._thread and self._thread.is_alive())

    def start(self) -> None:
        if self.is_running():
            return
        self._stop_event.clear()
        self._thread = threading.Thread(target=self._run, name="scheduled-send-engine", daemon=True)
        self._thread.start()
        self._log("定时发送引擎已启动")

    def stop(self) -> None:
        self._stop_event.set()

    def _run(self) -> None:
        while not self._stop_event.is_set():
            try:
                self._poll_once()
            except Exception as exc:
                self._log("定时发送引擎异常：%s" % exc, level="error")
            self._stop_event.wait(self.POLL_INTERVAL)

    def _poll_once(self) -> None:
        if not (AUTOMATION_AVAILABLE and os.name == "nt"):
            return
        config = load_scheduled_send()
        if not config.get("enabled"):
            return
        tasks = [task for task in (config.get("tasks") or []) if bool(task.get("enabled"))]
        if not tasks:
            return

        state = load_scheduled_state()
        now = time.time()
        dirty = False
        for task in tasks:
            target_name = task.get("target_name") or task.get("id")
            try:
                if self._process_task(task, state, now):
                    dirty = True
            except HTTPException as exc:
                self._log("为「%s」发送失败：%s" % (target_name, exc.detail), level="error")
            except Exception as exc:
                self._log("为「%s」发送失败：%s" % (target_name, exc), level="error")

        if dirty:
            save_scheduled_state(state)

    def _process_task(self, task: Dict, state: Dict, now: float) -> bool:
        """推进单个任务，返回是否改动了状态。"""
        task_id = task.get("id")
        entry = state.get(task_id)
        if not isinstance(entry, dict):
            entry = {}
            state[task_id] = entry

        last_sent = entry.get("last_sent_at")
        last_sent = float(last_sent) if isinstance(last_sent, (int, float)) else None

        # 固定间隔模式首次观测只做基线，避免"刚启用就立刻发一条"。
        if task.get("mode") == "interval" and last_sent is None:
            entry["last_sent_at"] = now
            return True

        # 冷却闸门：距上次发送未满冷却时间一律不发（对所有模式生效）。
        cooldown = _clamp_int(task.get("cooldown_minutes"), 0, 10080, 60) * 60
        if last_sent is not None and cooldown > 0 and now - last_sent < cooldown:
            return self._clear_pending(entry)

        base_due = _scheduled_base_due(task, last_sent, now)
        if base_due is None:
            return self._clear_pending(entry)

        pending = entry.get("pending_fire_at")
        if not isinstance(pending, (int, float)):
            jitter = _clamp_int(task.get("jitter_minutes"), 0, 1440, 15) * 60
            pending = base_due + (random.uniform(0, jitter) if jitter > 0 else 0.0)
            entry["pending_fire_at"] = pending
            # 抖动后仍在未来：本轮只登记待触发时刻，不发送。
            if now < pending:
                return True

        if now < pending:
            return False

        # 账号切换闸门（T19-A）：到点要发时被拦下 → 记日志 + **跳过这一次、不补发**。
        # 位置同样在生成之前（_send_greeting 里才调模型），避免暂停期间白烧模型调用。
        # 这里把 entry 按"已发送"同样的方式推进（清 pending、写本次时刻），是为了让这一次
        # 真正被消费掉：若不推进，每轮轮询都会重复触发（日志刷屏），而且用户确认后会把这条
        # 十几分钟前的招呼立刻补发出去 —— 万一登录失败/登错账号，补发正好打在最危险的时刻。
        if not _ensure_send_allowed():
            self._log("账号切换待确认，跳过本次定时发送「%s」" % (task.get("target_name") or task_id))
            entry["last_sent_at"] = now
            entry["pending_fire_at"] = None
            return True

        self._send_greeting(task, entry)
        return True

    @staticmethod
    def _clear_pending(entry: Dict) -> bool:
        if entry.get("pending_fire_at") is None:
            return False
        entry["pending_fire_at"] = None
        return True

    def _send_greeting(self, task: Dict, entry: Dict) -> None:
        target_name = task.get("target_name") or task.get("target_id")
        try:
            text = _scheduled_generate(task)
        except HTTPException as exc:
            self._log("为「%s」生成打招呼消息失败：%s" % (target_name, exc.detail), level="error")
            return
        except Exception as exc:
            self._log("读取「%s」上下文失败：%s" % (target_name, exc), level="error")
            return

        if not text:
            self._log("为「%s」生成的打招呼消息为空" % target_name, level="error")
            return

        hotkeys = load_auto_reply().get("hotkeys") or {}
        try:
            with _AUTOMATION_LOCK:
                automation.send_wechat_message(target_name, text, hotkeys)
        except Exception as exc:
            self._log("向「%s」发送失败：%s" % (target_name, exc), level="error")
            return

        entry["last_sent_at"] = time.time()
        entry["pending_fire_at"] = None
        self._log("已定时向「%s」发送消息" % target_name)


_scheduled_send_engine = ScheduledSendEngine()


_WECHAT_ADAPTER = None
_WECHAT_ADAPTER_KEY = None


# ==================== 数据源状态与消息窗口 ====================

def get_wechat_adapter() -> WeChatAdapter:
    """按当前配置复用微信本地 API 会话（**上游**数据源）。"""
    global _WECHAT_ADAPTER, _WECHAT_ADAPTER_KEY
    config = load_config()
    key = (config.get("wechat_api", ""), config.get("wechat_account", ""))
    if _WECHAT_ADAPTER is None or _WECHAT_ADAPTER_KEY != key:
        _WECHAT_ADAPTER = WeChatAdapter(*key)
        _WECHAT_ADAPTER_KEY = key
    return _WECHAT_ADAPTER


# ---------- 数据源切换（阶段 D1）----------
#
# 自研实现已覆盖：会话 / 消息（C1/C2）、通讯录 + 头像（C3）、朋友圈时间线 + 用户（C4）、
# 朋友圈导出（C5）。**仍走上游的只剩「媒体（图片/语音/视频本体）」与账号枚举**：
# 前者阻塞于 C6（`.dat` 命名键与加密算法未破），后者是上游自己的账号清单。
# 因此这里做的是**能力级路由**，而不是把上游整个换掉。

_LOCAL_SERVICE = None
_LOCAL_SERVICE_KEY = None


def data_source_mode() -> str:
    """`config.json` 的 `data_source`：`upstream`（默认）/ `local`。"""
    return str(load_config().get("data_source") or "upstream").strip().lower()


def get_local_service():
    """取本地数据服务（按账号缓存）。不可用时返回 None —— **不抛**。"""
    global _LOCAL_SERVICE, _LOCAL_SERVICE_KEY
    if not LOCALDB_AVAILABLE:
        return None
    config = load_config()
    key = str(config.get("wechat_account") or "")
    if _LOCAL_SERVICE is None or _LOCAL_SERVICE_KEY != key:
        if _LOCAL_SERVICE is not None:
            _LOCAL_SERVICE.stop()
        _LOCAL_SERVICE = localdb_service.LocalDataService(CONFIG_DIR, DATA_ROOT, key)
        _LOCAL_SERVICE_KEY = key
    _LOCAL_SERVICE.start()
    return _LOCAL_SERVICE


# 本地链路**已实现**的能力（阶段 C 逐个补齐）。不在表里的能力恒走上游 ——
# 这是"能力级路由"的全部依据：没实现的能力不可能被误切到本地。
LOCAL_CAPABILITIES = {
    "chat": ("list_chats", "list_messages"),              # C1 / C2
    "contacts": ("list_contacts", "fetch_avatar"),        # C3
    "sns": ("sns_timeline", "sns_users", "sns_sync_latest"),  # C4（媒体仍走上游，见 C6）
    "export": ("sns_export_create", "sns_export_status", "sns_export_download"),  # C5
}


def get_source(capability: str):
    """按能力取数据源。`WeChatAdapter` 与 `LocalDataService` 同名同形，可直接互换。

    `local` 模式下若该能力未实现、或本地链路没就绪（无密钥 / 镜像失败），**回退上游**，
    原因通过 `/api/data-source/status` 暴露 —— 绝不静默降级，也不让接口 500。
    """
    if capability in LOCAL_CAPABILITIES and data_source_mode() == "local":
        service = get_local_service()
        if service is not None and service.ready:
            return service
    return get_wechat_adapter()


def _reset_wechat_adapters() -> None:
    """清掉全部数据源缓存：模块级上游适配器、自动回复引擎的引用、本地服务。

    缓存键都是 `(wechat_api, wechat_account)`，key 变了本来也会重建；显式清一次是为了
    不让"下一个调用方一定会读到新配置"成为隐含前提（切换的后续步骤/落盘都可能失败）。
    """
    global _WECHAT_ADAPTER, _WECHAT_ADAPTER_KEY, _LOCAL_SERVICE, _LOCAL_SERVICE_KEY
    _WECHAT_ADAPTER = None
    _WECHAT_ADAPTER_KEY = None
    if _LOCAL_SERVICE is not None:
        _LOCAL_SERVICE.stop()
    _LOCAL_SERVICE = None
    _LOCAL_SERVICE_KEY = None


def _fetch_upstream_accounts() -> Dict:
    """读上游账号清单的原样响应（结构见 docs §14.4.1.1）。

    走 adapter 的 `_get` 而不是另起一条 requests.get：那里有 keep-alive 重试（T25 修的坑）与
    统一的上游错误文案，绕开它等于把这两件已有的事重做一遍，还少一次故障复盘。
    """
    return get_wechat_adapter()._get("/api/chat/accounts", timeout=15)


def _normalize_upstream_account(item: Dict, active: str) -> Dict:
    """把上游 `accountInfos[]` 单项归一成本服务的契约字段（§7.17）。

    上游的 `accounts` 是**字符串数组**，不能当对象解析——`accountInfos[]` 才是权威来源。
    字段名只认 §14.4.1.1 实测到的 `keyReady` / `hasDecryptedDbs`，**不做别名兜底**：
    上游那些"数量 / 列表"类的候选字段语义并不等价——`bool(3) == True` 会把"有 3 个库"
    报成"解密库已就绪"，属于"看起来正常但是错的"、最难查的一类 bug。
    字段缺失即按"未知"处理（保守显示为未就绪）；真机验证若发现字段名对不上，再按实际结构改。
    """
    account = str(item.get("account") or "").strip()
    return {
        "account": account,
        "name": str(item.get("name") or account),
        "keyReady": bool(item.get("keyReady")),
        "switchable": bool(item.get("switchable")),
        "hasDecryptedDbs": bool(item.get("hasDecryptedDbs")),
        "isActive": bool(account) and account == active,
    }


def _upstream_account_entries(data: Dict, active: str = "") -> List[Dict]:
    """`accountInfos[]` → 归一后的账号列表；为空的条目直接丢弃。"""
    entries = []
    for item in data.get("accountInfos") or []:
        if not isinstance(item, dict):
            continue
        entry = _normalize_upstream_account(item, active)
        if entry["account"]:
            entries.append(entry)
    return entries


def message_epoch(message: Dict) -> Optional[float]:
    """兼容秒、毫秒时间戳及常见文本时间。"""
    value = message.get("timestamp", message.get("time"))
    if isinstance(value, (int, float)):
        number = float(value)
        return number / 1000.0 if number > 100000000000 else number
    text_value = str(value or "").strip()
    try:
        number = float(text_value)
        return number / 1000.0 if number > 100000000000 else number
    except ValueError:
        pass
    for fmt in ("%Y-%m-%d %H:%M:%S", "%Y/%m/%d %H:%M:%S"):
        try:
            return datetime.strptime(text_value, fmt).timestamp()
        except ValueError:
            continue
    return None


def filter_messages(messages: List[Dict], recent_hours: Optional[float] = None,
                    recent_count: Optional[int] = None) -> List[Dict]:
    """按最近小时和最近条数过滤，始终保持原始时间顺序。"""
    result = list(messages or [])
    if recent_hours is not None and recent_hours > 0:
        cutoff = time.time() - (recent_hours * 3600)
        result = [item for item in result if message_epoch(item) is None or message_epoch(item) >= cutoff]
    if recent_count is not None and recent_count > 0:
        result = result[-recent_count:]
    return result


def get_real_wechat_chats() -> Dict:
    result = get_source("chat").list_chats()
    return {"status_info": result["status_info"], "chats": result["chats"]}


def get_real_wechat_messages(target_id: str, limit: int = 50) -> List[Dict]:
    return get_source("chat").list_messages(target_id, limit)


# 只放行图片：本轮修复范围就是图片，其余媒体类型（表情/视频/语音/文件）不在内。


def image_proxy_path(target_id: str, md5: str = "", file_id: str = "") -> str:
    """本服务图片代理的相对路径。参数由服务端拼装，不接受任意 URL。"""
    params = {"target_id": target_id}
    if md5:
        params["md5"] = md5
    if file_id:
        params["file_id"] = file_id
    return "/api/media/image?%s" % urllib.parse.urlencode(params)


def absolute_self_url(path: str) -> str:
    """把相对路径补成指向本服务自身的绝对地址（供客户端直接绑定/下载）。"""
    port = int(load_config().get("port") or 18765)
    return "http://127.0.0.1:%d%s" % (port, path)


def with_image_url(message: Dict, target_id: str) -> Dict:
    """给图片消息补上可用的 image_url；其余消息原样返回。

    刻意不改动 content：正文仍保留 [图] 之类的标签，因为 content 会流进 /api/summarize
    送给大模型，塞进图片 URL 会污染 AI 总结的输入。
    """
    if str(message.get("message_type") or "") != "image":
        return message
    md5 = str(message.get("image_md5") or "")
    file_id = str(message.get("image_file_id") or "")
    if not md5 and not file_id:
        return message
    item = dict(message)
    item["image_url"] = absolute_self_url(image_proxy_path(target_id, md5, file_id))
    return item


# ==================== REST API 端点 ====================

@app.get("/api/config")
async def get_config_api():
    provider = get_active_provider()
    return {
        "has_key": bool(provider and str(provider.get("api_key") or "").strip()),
        "model": provider.get("model", ""),
        "provider_name": provider.get("name", ""),
    }


@app.get("/api/providers")
async def list_providers_api():
    config = load_config()
    active_id = str(get_active_provider().get("id") or "").strip()
    providers = []
    for item in list_providers():
        providers.append({
            "id": item.get("id"),
            "name": item.get("name"),
            "provider_type": item.get("provider_type"),
            "base_url": item.get("base_url"),
            "model": item.get("model"),
            "api_key_masked": _mask_api_key(item.get("api_key")),
            "balance_supported": bool(item.get("balance_path")),
            "active": str(item.get("id") or "") == active_id,
        })
    return {"status": "success", "providers": providers, "active_id": active_id, "presets": PROVIDER_PRESETS}


@app.post("/api/providers/validate")
async def validate_provider_api(data: dict = Body(...)):
    base_url = str(data.get("base_url") or "").strip().rstrip("/")
    model = str(data.get("model") or "").strip()
    api_key = str(data.get("api_key") or "").strip()
    if not base_url or not model or not api_key:
        raise HTTPException(status_code=400, detail="base_url、model、api_key 均不能为空")
    _chat_completion(
        base_url, model, api_key,
        [{"role": "user", "content": "你好，请回复“ok”。"}],
        temperature=0.0, timeout=60,
    )
    return {"status": "success", "message": "API Key 验证通过"}


@app.post("/api/providers")
async def save_provider_api(data: dict = Body(...)):
    provider_type = str(data.get("provider_type") or "custom").strip()
    base_url = str(data.get("base_url") or "").strip().rstrip("/")
    model = str(data.get("model") or "").strip()
    name = str(data.get("name") or "").strip()
    api_key = str(data.get("api_key") or "").strip()
    if not base_url or not model:
        raise HTTPException(status_code=400, detail="base_url 与 model 不能为空")

    preset = PROVIDER_PRESETS.get(provider_type) or {}
    balance_path = preset.get("balance_path")
    balance_parser = preset.get("balance_parser")
    if data.get("balance_path") is not None:
        balance_path = str(data.get("balance_path") or "").strip() or None
    if data.get("balance_parser") is not None:
        balance_parser = str(data.get("balance_parser") or "").strip() or None
    if not name:
        name = str(preset.get("name") or provider_type)

    providers = list_providers()
    provider_id = str(data.get("id") or "").strip()
    if provider_id:
        existing = next((item for item in providers if str(item.get("id") or "") == provider_id), None)
        if existing is None:
            raise HTTPException(status_code=404, detail="供应商不存在")
        if not api_key:
            api_key = str(existing.get("api_key") or "").strip()
        if not api_key:
            raise HTTPException(status_code=400, detail="api_key 不能为空")
        existing.update({
            "provider_type": provider_type,
            "name": name,
            "base_url": base_url,
            "model": model,
            "api_key": api_key,
            "balance_path": balance_path,
            "balance_parser": balance_parser,
        })
    else:
        if not api_key:
            raise HTTPException(status_code=400, detail="api_key 不能为空")
        provider_id = "prov-" + secrets.token_hex(5)
        providers.append({
            "id": provider_id,
            "provider_type": provider_type,
            "name": name,
            "base_url": base_url,
            "model": model,
            "api_key": api_key,
            "balance_path": balance_path,
            "balance_parser": balance_parser,
        })

    save_api_keys({"providers": providers})
    config = load_config()
    if not str(config.get("active_provider") or "").strip() or not any(
        str(item.get("id") or "") == str(config.get("active_provider") or "") for item in providers
    ):
        config["active_provider"] = provider_id
        save_config(config)
    return {"status": "success", "id": provider_id}


@app.post("/api/providers/activate")
async def activate_provider_api(data: dict = Body(...)):
    provider_id = str(data.get("id") or "").strip()
    if not provider_id:
        raise HTTPException(status_code=400, detail="缺少 id")
    providers = list_providers()
    if not any(str(item.get("id") or "") == provider_id for item in providers):
        raise HTTPException(status_code=404, detail="供应商不存在")
    config = load_config()
    config["active_provider"] = provider_id
    save_config(config)
    return {"status": "success"}


@app.delete("/api/providers/{provider_id}")
async def delete_provider_api(provider_id: str):
    providers = list_providers()
    remaining = [item for item in providers if str(item.get("id") or "") != provider_id]
    if len(remaining) == len(providers):
        raise HTTPException(status_code=404, detail="供应商不存在")
    save_api_keys({"providers": remaining})
    config = load_config()
    if str(config.get("active_provider") or "") == provider_id:
        config["active_provider"] = str(remaining[0].get("id") or "") if remaining else ""
        save_config(config)
    return {"status": "success"}


@app.get("/api/providers/balance")
async def provider_balance_api():
    provider = get_active_provider()
    if not provider:
        raise HTTPException(status_code=400, detail="尚未配置任何模型供应商")
    if not provider.get("balance_path"):
        return {"status": "success", "supported": False, "message": "该供应商暂不支持余额查询"}
    balance = _query_balance(provider)
    return {"status": "success", "supported": True, "balance": balance}


@app.get("/api/chats")
async def get_chats_api():
    try:
        res = get_real_wechat_chats()
        return {"status": "success", "status_info": res["status_info"], "chats": res["chats"]}
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))


@app.get("/api/messages")
async def get_messages_api(
    target_id: str,
    limit: int = Query(500, ge=1, le=2000),
    recent_hours: Optional[float] = Query(None),
    recent_count: Optional[int] = Query(None)
):
    try:
        msgs = get_real_wechat_messages(target_id, limit)
        msgs = filter_messages(msgs, recent_hours, recent_count)
        msgs = [with_image_url(item, target_id) for item in msgs]
        return {
            "status": "success",
            "messages": msgs,
            "source_valid": True,
            "message": "" if msgs else "当前会话没有读取到真实明文消息"
        }
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))


@app.get("/api/data-source/status")
async def data_source_status_api():
    """数据源状态：当前模式、本地链路是否就绪、降级原因、最近一次同步统计。

    这个接口存在的唯一目的就是**不让降级静默发生** ——
    上游那套"实时通道失败 → 读静态快照 → 照样返回 200"正是最初那次故障最难排查的地方。
    """
    config = load_config()
    mode = data_source_mode()
    info = {
        "status": "success",
        "mode": mode,
        "localdbAvailable": LOCALDB_AVAILABLE,
        "account": str(config.get("wechat_account") or ""),
    }
    if not LOCALDB_AVAILABLE:
        info["active"] = "upstream"
        info["detail"] = "自研数据链路不可用（依赖缺失或导入失败）"
        return info

    # 上游模式下不去创建服务（避免"看一眼状态就触发一次全量镜像构建"）；
    # 已经建过的话就把现状报出来。
    service = _LOCAL_SERVICE if mode != "local" else get_local_service()
    local = service.status() if service is not None else None
    info["local"] = local
    info["active"] = "local" if (mode == "local" and service is not None and service.ready) else "upstream"
    # 逐能力列出来源：一眼看出哪些已切到本地、哪些还在上游（后者= 阶段 C 还没补的能力）。
    local_active = info["active"] == "local"
    info["capabilities"] = {
        name: ("local" if local_active else "upstream") for name in LOCAL_CAPABILITIES
    }
    if mode == "local" and info["active"] != "local":
        info["fallbackReason"] = (
            (local or {}).get("initError") or (local or {}).get("syncError") or "本地链路尚未就绪"
        )
    return info


# ==================== 微信数据库密钥（阶段 D3：手动导入） ====================
#
# 为什么需要这一对端点：上游的授权页（10392）是**唯一**能"解析出 db_key"的东西，
# 而阶段 D3 把上游移除了。此后 `config/wechat-keys.json` 就是唯一的密钥来源，
# 而它此前是由人手工从上游产物复制过去的 —— 换账号 / 微信升级轮换密钥 /
# 数据目录被清空，都会让整条自研链路报废且无从补救。
#
# 这一对端点**不走能力级路由**：它们做的是"配置密钥"，与 data_source 是 local 还是
# upstream 无关 —— 密钥是两者共同的前提。

@app.get("/api/keys/status")
async def keys_status_api():
    """密钥现状：可以从哪些文件拿、每一把**实测**能不能用、本地链路为什么没起来。

    `verified` 是"真的解开了本机 session.db 第 1 页"，不是格式检查 —— 只看格式的话，
    一把合法但错误的密钥会让界面显示"已就绪"，而实际链路全 503。
    """
    if not LOCALDB_AVAILABLE:
        return {
            "status": "success",
            "localdbAvailable": False,
            "keys": [],
            "sources": {},
            "detail": "自研数据链路不可用（依赖缺失或导入失败）",
        }

    local_file = localdb_keys.backup_path(str(CONFIG_DIR))
    upstream_file = localdb_keys.upstream_keys_path(str(DATA_ROOT))
    keys, missing_reason = localdb_keys.describe_accounts(str(CONFIG_DIR), str(DATA_ROOT))

    service = _LOCAL_SERVICE
    info = service.status() if service is not None else {}
    return {
        "status": "success",
        "localdbAvailable": True,
        "keys": keys,
        "activeAccount": str(load_config().get("wechat_account") or ""),
        "usableCount": sum(1 for item in keys if item["verified"]),
        "missingReason": missing_reason,
        "sources": {
            "localKeysFile": local_file,
            "localKeysFileExists": os.path.exists(local_file),
            "upstreamKeysFile": upstream_file,
            "upstreamKeysFileExists": os.path.exists(upstream_file),
        },
        "initError": (info or {}).get("initError", ""),
    }


@app.post("/api/keys/import")
async def keys_import_api(data: dict = Body(...)):
    """导入微信数据库密钥：支持"密钥文件全文"或"裸 db_key"两种粘贴形态。

    **每一条都必须实测解密通过才写盘**。只校验格式就把内容存下来，等于把"导入成功"
    变成一句谎话 —— 用户会以为修好了，而链路其实还是坏的。
    一条都没通过时返回 400 且**不写盘**，让"失败"必须被看见。
    """
    if not LOCALDB_AVAILABLE:
        raise HTTPException(status_code=503, detail="自研数据链路不可用（依赖缺失或导入失败）")

    content = data.get("content")
    if not isinstance(content, str) or not content.strip():
        raise HTTPException(status_code=400, detail="缺少 content：请粘贴密钥文件全文或 db_key")

    try:
        entries = localdb_keys.parse_import(
            content,
            account=str(data.get("account") or ""),
            db_storage_path=str(data.get("db_storage_path") or ""),
        )
    except localdb_keys.KeyError_ as exc:
        raise HTTPException(status_code=400, detail=str(exc))

    accepted = []
    rejected = []
    for entry in entries:
        entry = localdb_keys.infer_account(entry)
        ok, message = localdb_keys.verify_entry(entry)
        record = {
            "account": entry.get("account") or "",
            "dbStoragePath": entry.get("db_storage_path") or "",
            "verified": ok,
            "message": message,
        }
        (accepted if ok else rejected).append((entry, record))

    if not accepted:
        raise HTTPException(
            status_code=400,
            detail="密钥校验未通过，未写入任何内容：" + "；".join(
                "%s %s" % (record["account"] or "（未识别账号）", record["message"])
                for _, record in rejected),
        )

    saved_to = localdb_keys.save_accounts(str(CONFIG_DIR), [entry for entry, _ in accepted])
    # 让新密钥立刻生效：本地服务是按账号缓存的（`_ensure_store` 只建一次），
    # 不重置的话要等重启进程才认。
    _reset_wechat_adapters()

    message = "已导入 %d 条密钥并校验通过" % len(accepted)
    if rejected:
        message += "；%d 条未通过已忽略" % len(rejected)
    return {
        "status": "success",
        "savedTo": saved_to,
        "imported": [record for _, record in accepted],
        "rejected": [record for _, record in rejected],
        "message": message,
    }


@app.get("/api/media/image")
async def get_media_image_api(
    target_id: str,
    md5: str = "",
    file_id: str = ""
):
    """图片代理：把上游 /api/chat/media/image 的字节流转发给本服务的调用方。

    只接受 target_id / md5 / file_id 三个定位参数，上游 URL 完全由服务端拼装，
    因此不存在"传入任意 URL 被转发"的 SSRF 风险。
    """
    if not md5 and not file_id:
        raise HTTPException(status_code=400, detail="缺少 md5 或 file_id")
    try:
        content, content_type = get_wechat_adapter().fetch_media(
            "image", target_id, md5, file_id
        )
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))
    return Response(
        content=content,
        media_type=content_type,
        # 图片按 md5 寻址、内容不可变，可放心让调用方缓存一天。
        headers={"Cache-Control": "private, max-age=86400"},
    )


# ==================== 通讯录代理（T21 需求 5） ====================

def contact_avatar_path(username: str) -> str:
    """本服务头像代理的相对路径；上游 10392 的地址永不下发。"""
    return "/api/contacts/avatar?%s" % urllib.parse.urlencode({"username": username})


@app.get("/api/contacts")
async def get_contacts_api(
    include_friends: Optional[bool] = Query(None),
    include_groups: Optional[bool] = Query(None),
    include_officials: Optional[bool] = Query(None),
    include_official_subscriptions: Optional[bool] = Query(None),
    include_official_services: Optional[bool] = Query(None),
    include_former_friends: Optional[bool] = Query(None),
    include_blocked: Optional[bool] = Query(None),
    keyword: str = Query(""),
    account: str = Query(""),
):
    """通讯录列表：入参逐字对齐上游 include_* 开关（§14.7.2），不额外造 scope 之类语法。

    未显式传入的开关一律不下发，交由上游默认值决定（friends / groups / officials 默认开）。
    """
    include = {}
    for name, value in (
        ("include_friends", include_friends),
        ("include_groups", include_groups),
        ("include_officials", include_officials),
        ("include_official_subscriptions", include_official_subscriptions),
        ("include_official_services", include_official_services),
        ("include_former_friends", include_former_friends),
        ("include_blocked", include_blocked),
    ):
        if value is not None:
            include[name] = value
    try:
        result = get_source("contacts").list_contacts(
            include=include, keyword=keyword, account=account
        )
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))
    for contact in result["contacts"]:
        username = str(contact.get("username") or "")
        contact["avatar"] = absolute_self_url(contact_avatar_path(username)) if username else ""
    return {"status": "success", **result}


@app.get("/api/contacts/avatar")
async def get_contact_avatar_api(username: str = Query(...)):
    """头像代理：与 /api/media/image 同一模式，只接受 username，不接受任意 URL。"""
    try:
        content, content_type = get_source("contacts").fetch_avatar(username)
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))
    return Response(
        content=content,
        media_type=content_type,
        headers={"Cache-Control": "private, max-age=86400"},
    )


# ==================== 朋友圈代理（T21 需求 6 / 7） ====================

@app.get("/api/sns/timeline")
async def get_sns_timeline_api(
    limit: int = Query(20, ge=1, le=200),
    offset: int = Query(0, ge=0),
    usernames: str = Query(""),
    keyword: str = Query(""),
):
    try:
        result = get_source("sns").sns_timeline(
            limit=limit, offset=offset, usernames=usernames, keyword=keyword
        )
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))
    return {"status": "success", **result}


@app.get("/api/sns/users")
async def get_sns_users_api(
    keyword: str = Query(""),
    limit: int = Query(5000, ge=1, le=20000),
):
    try:
        result = get_source("sns").sns_users(keyword=keyword, limit=limit)
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))
    return {"status": "success", **result}


@app.post("/api/sns/sync-latest")
async def sync_sns_latest_api(data: dict = Body(...)):
    try:
        max_scan = int(data.get("max_scan") or 200)
        force = bool(data.get("force"))
    except (TypeError, ValueError):
        raise HTTPException(status_code=400, detail="max_scan 必须是整数")
    try:
        result = get_source("sns").sns_sync_latest(max_scan=max_scan, force=force)
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))
    # 上游若也返回 status，先摘掉，避免把信封的 success 覆盖掉。
    result.pop("status", None)
    return {"status": "success", **result}


# /api/sns/media 的上游定位参数白名单（§14.8.1 实测清单）。
_SNS_MEDIA_TEXT_PARAMS = (
    "post_id", "post_type", "create_time", "idx", "media_id",
    "media_type", "url", "width", "height", "total_size", "use_cache", "variant",
)
# attrs 里只有这三个是上游 /api/sns/media 认可的（§14.8.1 实测参数表）；
# enc_idx / type 不在上游参数表内，不下发。
_SNS_MEDIA_ATTR_PARAMS = ("md5", "key", "token")
_SNS_MEDIA_MAX_LENGTH = 8192


def _sns_media_params(data: dict) -> dict:
    """把 POST body 收敛成上游 /api/sns/media 的白名单 query 参数。

    客户端把 media 的 urlAttrs / thumbAttrs **整套**放在 attrs 里回传，后端不按 variant
    重新挑参数——§14.8.1.1 实测：原图与缩略图是两个不同的 token，后端"替客户端挑"必错。
    """
    params = {}
    for name in _SNS_MEDIA_TEXT_PARAMS:
        value = data.get(name)
        if value in (None, ""):
            continue
        params[name] = str(value)[:_SNS_MEDIA_MAX_LENGTH]
    attrs = data.get("attrs")
    if isinstance(attrs, dict):
        for name in _SNS_MEDIA_ATTR_PARAMS:
            value = attrs.get(name)
            if value in (None, ""):
                continue
            params[name] = str(value)[:_SNS_MEDIA_MAX_LENGTH]
    return params


@app.post("/api/sns/media")
async def get_sns_media_api(data: dict = Body(...)):
    """朋友圈图片代理。

    **为什么必须是 POST 而不是 GET**：定位参数含 token / key（解密密钥），
    放 query string 会被 uvicorn 的 access log 写进 logs/chatlog.log，
    而 §10 R10 已把 logs/ 列为含密钥、禁止外发；走 body 则既不进日志也不进 URL。
    请勿"顺手简化"成 GET。
    """
    params = _sns_media_params(data)
    if not params.get("post_id") or not params.get("media_id"):
        raise HTTPException(status_code=400, detail="缺少 post_id 或 media_id")
    if not any(key in params for key in ("token", "key", "md5")):
        raise HTTPException(status_code=400, detail="缺少 attrs（token / key / md5），该媒体无法解密")
    try:
        content, content_type = get_wechat_adapter().fetch_sns_media(params)
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))
    return Response(content=content, media_type=content_type)


@app.get("/api/sns/video")
async def get_sns_video_api(
    post_id: str = Query(...),
    media_id: str = Query(...),
):
    """上游 /api/sns/video 只要 account / post_id / media_id，不含 token/key，故保持 GET。"""
    try:
        content, content_type = get_wechat_adapter().fetch_sns_video(post_id, media_id)
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))
    return Response(content=content, media_type=content_type)


_SNS_EXPORT_SCOPES = ("selected", "all")
_SNS_EXPORT_FORMATS = ("html", "json", "txt", "excel")


def _sns_export_payload(data: dict) -> dict:
    """收敛成上游 SnsExportCreateRequest 的白名单字段。

    刻意不接受 output_dir（落盘路径不下发，产物只走 /download 回传），
    也不接受 encrypt / content_key_base64（密钥不进客户端，第一版固定不加密）。
    """
    scope = str(data.get("scope") or "selected").strip().lower()
    if scope not in _SNS_EXPORT_SCOPES:
        raise HTTPException(status_code=400, detail="scope 只能是 selected 或 all")
    export_format = str(data.get("format") or "html").strip().lower()
    if export_format not in _SNS_EXPORT_FORMATS:
        raise HTTPException(status_code=400, detail="format 只能是 html / json / txt / excel")
    payload = {
        "scope": scope,
        "format": export_format,
        "use_cache": bool(data.get("use_cache", True)),
    }
    raw_names = data.get("usernames")
    if isinstance(raw_names, list):
        usernames = [str(item).strip() for item in raw_names if str(item).strip()]
        if usernames:
            payload["usernames"] = usernames
    file_name = str(data.get("file_name") or "").strip()
    if file_name:
        payload["file_name"] = file_name
    return payload


def _sns_export_id(payload: dict) -> str:
    """取导出任务 id。

    实测（2026-09-11 直接读上游 ``GET /api/sns/exports``）：任务对象的 id 字段是驼峰
    ``exportId``，列表响应把它包在 ``jobs[]`` 里，任务对象本身还带 ``status`` / ``zipReady`` /
    ``progress{postsTotal, postsExported, …}``。

    create / status 的**包装键**没有实测到（列表是 jobs，create 可能是 job / data / 直接平铺），
    所以这里刻意**不猜包装键名**，而是按已知的字段名做有限深度的查找：
    顶层先试三个老名字（保持既有行为），再递归找 ``exportId``。

    深度给到 6 是刻意的：create / status 的响应体只可能包含**刚创建的那一个**任务，
    多走几层不会撞上别人的 id，而"包装键名没实测"这件事正是深度不够时最容易踩的坑。
    """
    if isinstance(payload, dict):
        for name in ("export_id", "exportId", "id"):
            value = payload.get(name)
            if value not in (None, ""):
                return str(value)

    return _find_export_id(payload, depth=6)


def _find_export_id(value, depth: int) -> str:
    """在嵌套结构里找实测字段名 ``exportId``；取不到返回空串。"""
    if depth <= 0:
        return ""

    if isinstance(value, dict):
        for name in ("exportId", "export_id"):
            candidate = value.get(name)
            if candidate not in (None, ""):
                return str(candidate)
        children = value.values()
    elif isinstance(value, list):
        children = value
    else:
        return ""

    for child in children:
        found = _find_export_id(child, depth - 1)
        if found:
            return found

    return ""


@app.post("/api/sns/export")
async def create_sns_export_api(data: dict = Body(...)):
    """创建朋友圈导出任务；产物 ZIP 只通过 /api/sns/export/{id}/download 回传。"""
    try:
        result = get_source("export").sns_export_create(_sns_export_payload(data))
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))
    return {"status": "success", "export_id": _sns_export_id(result), "export": result}


@app.get("/api/sns/export/{export_id}")
async def get_sns_export_status_api(export_id: str):
    try:
        result = get_source("export").sns_export_status(export_id)
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))
    return {"status": "success", "export_id": _sns_export_id(result) or export_id, "export": result}


def _export_filename(headers: Dict, export_id: str):
    """从上游 Content-Disposition 取文件名，取不到就用导出 id 兜底。

    只保留文件名本体，防止目录穿越与响应头注入。
    """
    disposition = ""
    for key, value in (headers or {}).items():
        if str(key).lower() == "content-disposition":
            disposition = str(value)
            break
    name = ""
    if disposition:
        matched = re.search(r"filename\*=UTF-8''([^;]+)", disposition, re.IGNORECASE)
        if matched:
            name = urllib.parse.unquote(matched.group(1).strip())
        else:
            matched = re.search(r'filename="?([^";]+)"?', disposition, re.IGNORECASE)
            if matched:
                name = matched.group(1).strip()
    name = os.path.basename(name.replace("\\", "/")).replace('"', "").replace("\r", "").replace("\n", "")
    name = name or ("sns-export-%s.zip" % export_id)
    ascii_name = re.sub(r"[^A-Za-z0-9._-]", "_", name) or "sns-export.zip"
    return ascii_name, name


@app.get("/api/sns/export/{export_id}/download")
async def download_sns_export_api(export_id: str):
    """下载导出 ZIP：产物由本服务回传，客户端看不到上游输出目录。"""
    try:
        content, content_type, headers = get_source("export").sns_export_download(export_id)
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))
    ascii_name, utf8_name = _export_filename(headers, export_id)
    return Response(
        content=content,
        media_type=content_type or "application/zip",
        headers={
            "Content-Disposition": "attachment; filename=\"%s\"; filename*=UTF-8''%s"
            % (ascii_name, urllib.parse.quote(utf8_name))
        },
    )


@app.post("/api/summarize")
async def summarize_api(data: dict = Body(...)):
    provider = get_active_provider()
    api_key = str(provider.get("api_key") or "").strip()
    if not api_key:
        raise HTTPException(status_code=400, detail="请先在页面中配置模型供应商与 API Key。")
    base_url = str(provider.get("base_url") or "").strip().rstrip("/")
    model = str(provider.get("model") or "deepseek-chat").strip()
    if not base_url or not model:
        raise HTTPException(status_code=400, detail="当前模型供应商缺少 base_url 或 model，请重新配置。")

    target_name = data.get("target_name", "未知对话")
    messages = data.get("messages", [])
    custom_prompt = data.get("prompt", "").strip()
    summary_type = data.get("type", "general")
    try:
        recent_hours = float(data.get("recent_hours")) if data.get("recent_hours") not in (None, "") else None
    except (TypeError, ValueError):
        recent_hours = None
    try:
        recent_count = int(data.get("recent_count")) if data.get("recent_count") not in (None, "") else None
    except (TypeError, ValueError):
        recent_count = None
    messages = filter_messages(messages, recent_hours, recent_count)

    if not messages:
        raise HTTPException(status_code=400, detail="未收到有效的聊天记录，无法进行总结。")

    text_lines = []
    for m in messages:
        sender = m.get("sender", "未知")
        time_str = m.get("time", "")
        content = m.get("content", "")
        text_lines.append(f"[{time_str}] {sender}: {content}")
    chat_text = "\n".join(text_lines)

    if summary_type == "meeting":
        system_prompt = load_prompt("summary-meeting")
    elif summary_type == "todo":
        system_prompt = load_prompt("summary-todo")
    elif summary_type == "topics":
        system_prompt = load_prompt("summary-topics")
    else:
        system_prompt = load_prompt("summary-general")

    history = []
    for item in (data.get("history") or [])[-10:]:
        if not isinstance(item, dict) or item.get("role") not in ("user", "assistant"):
            continue
        content = str(item.get("content") or "").strip()
        if content:
            history.append({"role": item["role"], "content": content[:8000]})

    user_request = custom_prompt or "请按指定类型分析这段聊天记录。"
    user_content = (
        f"对话名称：{target_name}\n"
        f"本次提供 {len(messages)} 条真实聊天消息。\n\n"
        f"聊天记录：\n{chat_text}\n\n"
        f"本次要求：{user_request}"
    )

    summary_content = _chat_completion(
        base_url, model, api_key,
        [{"role": "system", "content": system_prompt}] + history + [{"role": "user", "content": user_content}],
        temperature=0.3,
    )
    return {"status": "success", "summary": summary_content, "analyzed_count": len(messages)}


@app.get("/api/history")
async def get_history_api(target_id: str = Query(...)):
    history = load_summaries().get(target_id, [])
    return {"status": "success", "history": history}


@app.post("/api/history")
async def save_history_api(data: dict = Body(...)):
    target_id = data.get("target_id")
    history = data.get("history")
    if not target_id or history is None:
        raise HTTPException(status_code=400, detail="缺少 target_id 或 history")
    summaries = load_summaries()
    summaries[target_id] = history
    save_summaries(summaries)
    return {"status": "success"}


def _model_completion(messages: List[Dict], temperature: float = 0.7) -> str:
    provider = get_active_provider()
    api_key = str(provider.get("api_key") or "").strip()
    if not api_key:
        raise HTTPException(status_code=400, detail="请先在页面中配置模型供应商与 API Key。")
    base_url = str(provider.get("base_url") or "").strip().rstrip("/")
    model = str(provider.get("model") or "deepseek-chat").strip()
    if not base_url or not model:
        raise HTTPException(status_code=400, detail="当前模型供应商缺少 base_url 或 model，请重新配置。")
    return _chat_completion(base_url, model, api_key, messages, temperature=temperature)


def generate_reply_text(target_name: str, messages: List[Dict], context_count: int,
                        reply_prompt: str, inner_prompt: str = "") -> str:
    """根据上下文生成下一条回复正文（自动回复引擎与测试接口共用）。

    回复提示词固定为两层：
    - 外层 reply_prompt：全局主体/身份，为空时用默认身份。
    - 内层 inner_prompt：单个会话的精细化口吻，为空时用默认指令。
    """
    recent = messages[-context_count:] if messages else []
    text_lines = []
    for message in recent:
        text_lines.append("[%s] %s: %s" % (
            message.get("time", ""),
            message.get("sender", "未知"),
            message.get("content", ""),
        ))
    chat_text = "\n".join(text_lines)

    default_outer = load_prompt("auto-reply-outer")
    default_inner = load_prompt("auto-reply-inner")
    outer_prompt = str(reply_prompt or "").strip() or default_outer
    inner_prompt = str(inner_prompt or "").strip() or default_inner
    system_prompt = "%s\n\n%s" % (outer_prompt, inner_prompt)

    user_content = (
        "对话对象：%s\n最近 %d 条消息：\n%s\n\n请直接给出下一条回复：" %
        (target_name, len(recent), chat_text)
    )
    return _model_completion(
        [{"role": "system", "content": system_prompt},
         {"role": "user", "content": user_content}],
        temperature=0.7,
    ).strip()


@app.post("/api/reply/generate")
async def reply_generate_api(data: dict = Body(...)):
    target_name = str(data.get("target_name") or "对方").strip()
    messages = data.get("messages") or []
    try:
        context_count = int(data.get("context_count") or 0)
    except (TypeError, ValueError):
        context_count = 0
    context_count = max(1, context_count) if context_count else 80
    custom_prompt = str(data.get("reply_prompt") or "").strip()
    inner_prompt = str(data.get("inner_prompt") or "").strip()
    reply = generate_reply_text(target_name, messages, context_count, custom_prompt, inner_prompt)
    return {"status": "success", "reply": reply}


@app.post("/api/reply/send")
async def reply_send_api(data: dict = Body(...)):
    if not AUTOMATION_AVAILABLE or os.name != "nt":
        raise HTTPException(status_code=400, detail="键盘模拟仅在 Windows 上可用")
    target_name = str(data.get("target_name") or "").strip()
    reply = str(data.get("reply") or "").strip()
    if not target_name or not reply:
        raise HTTPException(status_code=400, detail="缺少 target_name 或 reply")
    hotkeys = load_auto_reply()["hotkeys"]
    # 闸门（T19-A）：手动发送同样走键盘模拟，待确认期间一律拒绝（§7.17）。
    if not _ensure_send_allowed():
        raise HTTPException(status_code=409, detail=send_paused_reason())
    try:
        automation.send_wechat_message(target_name, reply, hotkeys)
    except Exception as exc:
        raise HTTPException(status_code=500, detail="键盘模拟发送失败：%s" % exc) from exc
    return {"status": "success", "message": "已发送"}


@app.get("/api/auto-reply/config")
async def get_auto_reply_config_api():
    return {
        "status": "success",
        "config": load_auto_reply(),
        "available": AUTOMATION_AVAILABLE and os.name == "nt",
    }


@app.get("/api/auto-reply/status")
async def get_auto_reply_status_api():
    config = load_auto_reply()
    return {
        "status": "success",
        "enabled": bool(config.get("enabled")),
        "running": _auto_reply_engine.is_running(),
        # 账号切换待确认时发送会被闸门拦下：界面只显示 running=true 会让用户以为"漏回了"，
        # 必须把暂停状态与原因一起给出（§7.17 闸门语义）。
        "sendPaused": not _ensure_send_allowed(),
        "sendPausedReason": send_paused_reason(),
        "active_chat_name": _auto_reply_engine.active_chat_name,
        "log": list(_auto_reply_engine.log),
    }


GUARD_MAX_FRIENDS = 8
GUARD_MAX_GROUPS = 3


def normalize_guard_targets(value) -> List[Dict]:
    """校验并去重守护会话，限制最多 8 个好友 + 3 个群聊。"""
    if not isinstance(value, list):
        raise HTTPException(status_code=400, detail="guard_targets 必须是数组")
    cleaned = []
    seen_ids = set()
    for item in value:
        if not isinstance(item, dict):
            continue
        target_id = str(item.get("id") or "").strip()
        name = str(item.get("name") or "").strip()
        kind = str(item.get("type") or "").strip()
        if not target_id or not name or kind not in ("friend", "group"):
            continue
        if target_id in seen_ids:
            continue
        seen_ids.add(target_id)
        prompt = str(item.get("prompt") or "").strip()
        cleaned.append({"id": target_id, "name": name, "type": kind, "prompt": prompt})
    friends = sum(1 for target in cleaned if target["type"] == "friend")
    groups = sum(1 for target in cleaned if target["type"] == "group")
    if friends > GUARD_MAX_FRIENDS or groups > GUARD_MAX_GROUPS:
        raise HTTPException(
            status_code=400,
            detail="守护范围最多 %d 个好友 + %d 个群聊" % (GUARD_MAX_FRIENDS, GUARD_MAX_GROUPS),
        )
    return cleaned


@app.post("/api/auto-reply/config")
async def set_auto_reply_config_api(data: dict = Body(...)):
    allowed = {"enabled", "guard_targets", "context_count", "reply_cooldown_seconds",
               "reply_prompt", "hotkeys"}
    settings = {}
    for key in allowed:
        if key not in data:
            continue
        value = data[key]
        if key == "context_count":
            try:
                value = max(1, min(2000, int(value)))
            except (TypeError, ValueError):
                continue
        elif key == "reply_cooldown_seconds":
            # 0 = 不限制（功能关闭）。上下界对齐 §7.16 的冻结契约。
            try:
                value = max(0, min(600, int(value)))
            except (TypeError, ValueError):
                continue
        elif key == "enabled":
            value = bool(value)
        elif key == "guard_targets":
            value = normalize_guard_targets(value)
        elif key == "hotkeys":
            if not isinstance(value, dict):
                continue
            value = {
                k: str(v).strip()
                for k, v in value.items()
                if k in AUTO_REPLY_DEFAULTS["hotkeys"] and str(v).strip()
            }
        elif key == "reply_prompt":
            value = str(value or "").strip()
        settings[key] = value
    saved = save_auto_reply(settings)
    return {"status": "success", "config": saved}


@app.post("/api/auto-reply/test")
async def auto_reply_test_api(data: dict = Body(...)):
    if not AUTOMATION_AVAILABLE or os.name != "nt":
        raise HTTPException(status_code=400, detail="键盘模拟仅在 Windows 上可用")
    action = str(data.get("action") or "").strip()
    hotkeys = load_auto_reply()["hotkeys"]
    try:
        if action in hotkeys:
            automation.press_hotkey(hotkeys[action])
            return {"status": "success", "message": "已触发快捷键 %s" % hotkeys[action]}
        if action == "clipboard":
            automation.set_clipboard_text(str(data.get("text") or "测试剪贴板"))
            return {"status": "success", "message": "已将测试文本写入剪贴板"}
        if action == "full":
            target_name = str(data.get("target_name") or "").strip()
            text = str(data.get("text") or "").strip()
            if not target_name or not text:
                raise HTTPException(status_code=400, detail="完整流程测试需要 target_name 和 text")
            # 闸门（T19-A）：只有 full 会真的发消息，快捷键 / 剪贴板不受影响。
            if not _ensure_send_allowed():
                raise HTTPException(status_code=409, detail=send_paused_reason())
            automation.send_wechat_message(target_name, text, hotkeys)
            return {"status": "success", "message": "完整发送流程已执行"}
    except HTTPException:
        raise
    except Exception as exc:
        raise HTTPException(status_code=500, detail="测试失败：%s" % exc) from exc
    raise HTTPException(status_code=400, detail="未知测试动作：%s" % action)


# 快捷键自动识别的探测源列表。
#
# **当前为空是刻意的**：§14.3.0 已真机取证——微信 4.1 的快捷键设置既不在注册表
# （键存在但无任何值），账号级配置也是加密二进制，没有任何可读通道。因此本接口
# 现在恒返回 found=false。
#
# 保留「依次尝试 N 个探测源 → 全部失败返回空」的结构：将来某版本微信把热键写回
# 注册表（或出现别的可读来源），只需往这个列表里加一个返回
# {"source": ..., "hotkeys": {...}} 的探测器，**接口与 UI 都不用改**（§14.3.2 第 4 条）。
#
# 明确不做"逐个候选键试按键"式探测（§14.3.3）：send 默认是 enter，一旦命中就是
# 真发一条消息；open_wechat 是显示/隐藏 toggle，连按两次状态不可预期。
HOTKEY_DETECTORS = []


@app.post("/api/auto-reply/detect-hotkeys")
async def auto_reply_detect_hotkeys_api():
    # 依次尝试所有探测源，任意一个返回非空即为命中；全部失败则返回空结果。
    for detector in HOTKEY_DETECTORS:
        try:
            hit = detector()
        except Exception:
            continue
        if hit:
            return {
                "status": "success",
                "found": True,
                "source": hit.get("source", ""),
                "hotkeys": hit.get("hotkeys", {}),
                "message": "已识别到快捷键，请核对后保存。",
            }

    # found=false 是**正常结果**，不是错误：客户端只更新提示区、不弹框（§14.3.2 第 3 条）。
    # message 必须写明"请手工填写"，不能只写"识别失败"——否则用户会一直以为自己用错了
    # （§14.3.6 风险 4）。
    return {
        "status": "success",
        "found": False,
        "source": "",
        "hotkeys": {},
        "message": "未能自动识别（微信 4.1 未提供可读的快捷键设置），"
                   "请手工填写微信「设置 → 快捷键」中的值",
    }


# ==================== 定时发送 ====================

@app.get("/api/scheduled-send/config")
async def get_scheduled_send_config_api():
    return {
        "status": "success",
        "config": load_scheduled_send(),
        "available": AUTOMATION_AVAILABLE and os.name == "nt",
    }


@app.post("/api/scheduled-send/config")
async def set_scheduled_send_config_api(data: dict = Body(...)):
    allowed = {"enabled", "tasks"}
    settings = {}
    for key in allowed:
        if key not in data:
            continue
        value = data[key]
        if key == "enabled":
            value = bool(value)
        elif key == "tasks":
            value = normalize_scheduled_tasks(value)
        settings[key] = value
    saved = save_scheduled_send(settings)
    return {"status": "success", "config": saved}


@app.get("/api/scheduled-send/status")
async def get_scheduled_send_status_api():
    config = load_scheduled_send()
    state = load_scheduled_state()
    now = time.time()
    tasks = []
    for task in config.get("tasks") or []:
        entry = state.get(task.get("id"))
        if not isinstance(entry, dict):
            entry = {}
        last_sent = entry.get("last_sent_at")
        tasks.append({
            "id": task.get("id"),
            "enabled": bool(task.get("enabled")),
            "next_fire_at": _scheduled_next_display(task, entry, now),
            "last_sent_at": last_sent if isinstance(last_sent, (int, float)) else None,
        })
    return {
        "status": "success",
        "enabled": bool(config.get("enabled")),
        "running": _scheduled_send_engine.is_running(),
        # 同上：闸门拦下时定时发送会静默跳过，必须让界面能解释"为什么到点了没发"。
        "sendPaused": not _ensure_send_allowed(),
        "sendPausedReason": send_paused_reason(),
        "tasks": tasks,
        "log": list(_scheduled_send_engine.log),
    }


@app.post("/api/scheduled-send/test")
async def scheduled_send_test_api(data: dict = Body(...)):
    if not AUTOMATION_AVAILABLE or os.name != "nt":
        raise HTTPException(status_code=400, detail="键盘模拟仅在 Windows 上可用")
    action = str(data.get("action") or "").strip()

    # 刻意收「完整任务」而不是 task_id：界面上还没保存的草稿也要能预览，
    # 否则新建任务必须先落盘才能试一次，交互很别扭。
    raw_task = data.get("task")
    if not isinstance(raw_task, dict):
        raise HTTPException(status_code=400, detail="缺少 task")
    normalized = normalize_scheduled_tasks([raw_task])
    if not normalized:
        raise HTTPException(status_code=400, detail="任务参数不完整（需要选择联系人，一次性任务还需要有效的时间点）")
    task = normalized[0]

    if action == "preview":
        try:
            return {"status": "success", "text": _scheduled_generate(task)}
        except HTTPException:
            raise
        except Exception as exc:
            raise HTTPException(status_code=500, detail="生成打招呼消息失败：%s" % exc) from exc

    if action == "send":
        # 闸门（T19-A）：放在生成之前——被拦下还去调模型就白烧一次调用。
        if not _ensure_send_allowed():
            raise HTTPException(status_code=409, detail=send_paused_reason())
        target_name = task.get("target_name") or task.get("target_id")
        try:
            text = _scheduled_generate(task)
            if not text:
                raise HTTPException(status_code=500, detail="生成的打招呼消息为空")
            hotkeys = load_auto_reply().get("hotkeys") or {}
            with _AUTOMATION_LOCK:
                automation.send_wechat_message(target_name, text, hotkeys)
        except HTTPException:
            raise
        except Exception as exc:
            raise HTTPException(status_code=500, detail="发送失败：%s" % exc) from exc

        # 手发也算一次真实发送。只对"已保存的任务"写状态：
        # 未保存草稿没有稳定 id，写进去只会留一堆永远用不到的状态项。
        saved_ids = {item.get("id") for item in (load_scheduled_send().get("tasks") or [])}
        if task.get("id") in saved_ids:
            state = load_scheduled_state()
            entry = state.get(task["id"])
            if not isinstance(entry, dict):
                entry = {}
                state[task["id"]] = entry
            entry["last_sent_at"] = time.time()
            entry["pending_fire_at"] = None
            save_scheduled_state(state)
        return {"status": "success", "text": text, "message": "已发送"}

    raise HTTPException(status_code=400, detail="未知测试动作：%s" % action)


# ==================== 微信账号切换（T19-A，契约见 docs/DOTNET_CLIENT_PLAN.md §7.17） ====================
#
# 切换本身不难：写配置 + 让几处缓存失效 + 重置引擎指纹 + 闸门。难的是安全——程序改得了"数据源读
# 哪个账号"，改不了也确认不了"微信客户端登录的是谁"（§14.4.3），所以切换后一律先暂停发送。
# 刻意不做"取消切换"接口：切错了就再切回上一个账号，走同一套 switch 流程。

@app.get("/api/wechat/accounts")
async def get_wechat_accounts_api():
    """上游账号清单（契约字段见 §7.17）+ 当前待确认状态，一次给全，客户端不必调两个接口。"""
    config = load_config()
    try:
        upstream = _fetch_upstream_accounts()
    except Exception as exc:
        raise HTTPException(status_code=503, detail=str(exc)) from exc
    default_account = str(upstream.get("default_account") or "").strip()
    active = str(config.get("wechat_account") or "").strip() or default_account
    return {
        "status": "success",
        "active": active,
        "defaultAccount": default_account,
        "accounts": _upstream_account_entries(upstream, active),
        "pendingSwitch": pending_switch_payload(),
    }


@app.post("/api/wechat/account/resolve")
async def resolve_wechat_account_api(data: dict = Body(...)):
    """返回"让用户去完成密钥解析"的授权页地址。

    **刻意不做 openapi 关键字探测**（前置取证结论）：授权页是给浏览器看的 UI 页面，它不会出现在
    `/openapi.json` 里（openapi 只描述 JSON 接口）；而上游也没有针对"账号授权/密钥解析"的专用
    JSON 端点（见 §14.7.1 的 openapi 校准清单）。若按 account / key 之类关键字去匹配，必然误命中
    `/api/chat/accounts`，浏览器打开的会是一个 JSON 页面——这种错误还很难被识别成 bug。
    因此直接返回上游 UI 根地址（沿用原 launcher.open_wechat_authorization_page 的行为；launcher 已退役）。
    """
    account = str((data or {}).get("account") or "").strip()
    if not account:
        raise HTTPException(status_code=400, detail="缺少 account")
    base = str(load_config().get("wechat_api") or "").strip().rstrip("/")
    if not base:
        raise HTTPException(status_code=400, detail="尚未配置数据源地址（wechat_api）")
    # 降级说明：未找到专用授权端点，降级为打开上游界面首页。
    return {"status": "success", "authorizeUrl": base + "/"}


@app.get("/api/wechat/account/resolve-status")
async def resolve_wechat_account_status_api(account: str = Query("")):
    """轮询用：该账号的密钥是否就绪 / 是否可切换（数据源同上，取 accountInfos[] 那一项）。"""
    account = (account or "").strip()
    if not account:
        raise HTTPException(status_code=400, detail="缺少 account")
    try:
        upstream = _fetch_upstream_accounts()
    except Exception as exc:
        raise HTTPException(status_code=503, detail=str(exc)) from exc
    entry = next((item for item in _upstream_account_entries(upstream) if item["account"] == account), None)
    if entry is None:
        raise HTTPException(status_code=404, detail="上游账号列表里没有账号「%s」" % account)
    key_ready = entry["keyReady"]
    switchable = entry["switchable"]
    if key_ready:
        message = "密钥已就绪，可以切换"
    elif not switchable:
        message = "该账号当前不可切换，请先在上游授权页完成授权"
    else:
        message = "尚未完成密钥解析，请在上游授权页完成授权"
    return {
        "status": "success",
        "account": account,
        "keyReady": key_ready,
        "switchable": switchable,
        "message": message,
    }


@app.post("/api/wechat/account/switch")
async def switch_wechat_account_api(data: dict = Body(...)):
    """切换数据源账号。**下面 8 个步骤的顺序就是安全设计本身，不许调换**（§7.17）。

    最容易被"顺手优化"掉的两处：②置闸门必须在④写配置之前（否则闸门还没关、数据源已经指向新
    账号，正是发错人的窗口）；⑤重置指纹不能省（省了就会出现"刚切完账号就自动回复一条"）。
    """
    account = str((data or {}).get("account") or "").strip()

    # ① 校验：账号非空 → 在上游账号列表里 → 密钥就绪 → 不是当前账号。
    if not account:
        raise HTTPException(status_code=400, detail="缺少 account")
    config = load_config()
    try:
        upstream = _fetch_upstream_accounts()
    except Exception as exc:
        raise HTTPException(status_code=503, detail=str(exc)) from exc
    default_account = str(upstream.get("default_account") or "").strip()
    active = str(config.get("wechat_account") or "").strip() or default_account
    entry = next((item for item in _upstream_account_entries(upstream) if item["account"] == account), None)
    if entry is None:
        raise HTTPException(status_code=400, detail="上游账号列表里没有账号「%s」" % account)
    if not entry["keyReady"]:
        raise HTTPException(
            status_code=400,
            detail="账号「%s」尚未完成密钥解析，请先在授权页完成授权再切换" % account,
        )
    if account == active:
        raise HTTPException(status_code=400, detail="当前已经是账号「%s」，无需切换" % account)

    previous = active
    pending = {
        "account": account,
        "previousAccount": previous,
        "requestedAt": datetime.now().isoformat(timespec="seconds"),
    }
    # ② 先置闸门（内存）——必须在④写配置之前。
    _set_pending_switch_memory(pending)

    try:
        # ③ 与键盘自动化互斥；同时互斥配置写入，避免与"保存定时任务"互相覆盖（§14.4.4 第 7 项）。
        with _AUTOMATION_LOCK:
            # ④ 原子写 config/config.json 的 wechat_account。
            #    在锁内重新 load 一次再改：①与③之间别人可能刚写过其他配置，用旧快照写回会覆盖它。
            latest = load_config()
            latest["wechat_account"] = account
            save_config(latest)
            # ⑤ 清空引擎的基线/签名/指纹/冷却计时。
            _auto_reply_engine.reset_detection_state()
            # ⑥ 清 adapter 缓存（模块级 + 引擎内，共两处）。
            _reset_wechat_adapters()
            # ⑦ 落盘标记文件（跨重启有效）。
            _persist_pending_switch(pending)
    except Exception as exc:
        # ④~⑦ 任一步失败：配置可能仍是旧账号，把闸门撤掉，免得留下"账号没换成、发送却一直停着"
        # 这种更难排查、也更影响用户的状态。
        _set_pending_switch_memory(None)
        raise HTTPException(status_code=500, detail="切换账号失败：%s" % exc) from exc

    # ⑧ 返回
    return {
        "status": "success",
        "previousAccount": previous,
        "active": account,
        "pendingConfirm": True,
        "message": "数据源已切换到「%s」，请把微信客户端的登录账号切到该账号，然后点「我已切换」" % account,
    }


@app.get("/api/wechat/account/pending")
async def get_pending_account_switch_api():
    """是否有"已切换、等待用户确认微信已登录新账号"的待确认状态（客户端启动时也读它）。"""
    return {"status": "success", **pending_switch_payload()}


@app.post("/api/wechat/account/confirm")
async def confirm_wechat_account_switch_api():
    """用户确认"微信客户端已登录新账号"：删标记文件 + 放开闸门。"""
    _set_pending_switch_memory(None)
    _clear_pending_switch_file()
    return {"status": "success", "message": "已确认账号切换，自动发送已恢复"}


@app.get("/api/health")
async def health_api():
    return {"status": "ok", "service": "chatlog"}

@app.post("/api/shutdown")
async def shutdown_api():
    def stop_server():
        os._exit(0)

    shutdown_file = str(os.environ.get("CHATLOG_SHUTDOWN_FILE") or "").strip()
    if shutdown_file:
        Path(shutdown_file).touch()
    loop = asyncio.get_event_loop()
    loop.call_later(0.5, stop_server)
    return {"status": "success", "message": "统一服务正在退出。"}


@app.get("/")
async def read_index():
    """根路径保留为「服务在跑吗」的探针。

    网页版前端（原 app/static/）已随 T14 退役，所以这里不再返回 index.html；
    保留这个端点是为了让浏览器误访问或端口探测时看到一句明确说明，而不是 404。
    """
    return JSONResponse({"message": "Chatlog AI 后端服务正常运行。"})


def main():
    config = load_config()
    port = config.get("port", 18765)
    print(f"==================================================")
    print(f" Chatlog AI 统一服务 v5.0（微信 WeChatDataAnalysis）已启动")
    print(f" 监听地址: http://127.0.0.1:{port}")
    # 冷启动预热：本地模式下镜像首次要全量构建（实测 message_0.db 约 1s），
    # 放到"开始监听"之前完成，否则第一批请求会落到「本地未就绪 → 回退上游 → 上游也不在」上，
    # 表现成一个看不出所以然的 503。
    source_note = "upstream（上游 HTTP）"
    if data_source_mode() == "local":
        service = get_local_service()
        if service is not None and service.wait_until_ready(10.0):
            source_note = "local（本地解密链路，已就绪）"
        else:
            detail = (service.status().get("initError") if service is not None else "") or "初始化超时"
            source_note = "local 未就绪 → 暂回退 upstream（%s）" % detail
    print(f" 数据来源: {source_note}")
    print(f"==================================================")
    _auto_reply_engine.start()
    _scheduled_send_engine.start()
    uvicorn.run(app, host="127.0.0.1", port=port, log_level="warning")


if __name__ == "__main__":
    main()
