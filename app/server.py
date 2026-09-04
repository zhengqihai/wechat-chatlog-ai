# -*- coding: utf-8 -*-
"""基于 WeChatDataAnalysis 与 WeQ 的微信/QQ 实时聊天分析服务。"""

import os
import json
import asyncio
import secrets
import threading
import requests
import time
from collections import deque
from datetime import datetime
from typing import List, Dict, Optional
from pathlib import Path

from fastapi import FastAPI, HTTPException, Body, Query
from fastapi.staticfiles import StaticFiles
from fastapi.responses import FileResponse, JSONResponse
from fastapi.middleware.cors import CORSMiddleware
import uvicorn

from adapters import WeChatAdapter, WeQAdapter

try:
    import automation
    AUTOMATION_AVAILABLE = True
except Exception:
    AUTOMATION_AVAILABLE = False

BASE_DIR = Path(__file__).parent.resolve()
ROOT_DIR = Path(os.environ.get("CHATLOG_ROOT_DIR") or BASE_DIR.parent).resolve()
CONFIG_FILE = ROOT_DIR / "config.json"
RUNTIME_FILE = ROOT_DIR / "data" / "runtime.json"
STATIC_DIR = ROOT_DIR / "app" / "static"

app = FastAPI(title="Chatlog AI Summarizer", version="5.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

def load_config() -> dict:
    default_config = {
        "port": 18765,
        "wechat_api": "http://127.0.0.1:10392",
        "wechat_account": "",
        "weq_url": "http://127.0.0.1:7690",
        "qq_uin": "",
        "active_provider": "",
        "providers": [],
    }
    if CONFIG_FILE.exists():
        try:
            with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)
                default_config.update(data)
        except Exception:
            pass
    return default_config


def save_config(config: dict) -> None:
    CONFIG_FILE.parent.mkdir(parents=True, exist_ok=True)
    temporary = CONFIG_FILE.with_suffix(CONFIG_FILE.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as file:
        json.dump(config, file, ensure_ascii=False, indent=2)
        file.write("\n")
    os.replace(str(temporary), str(CONFIG_FILE))


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
    config = load_config()
    providers = config.get("providers") or []
    return [item for item in providers if isinstance(item, dict)]


def get_active_provider() -> dict:
    config = load_config()
    providers = config.get("providers") or []
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


def load_runtime() -> dict:
    if not RUNTIME_FILE.exists():
        return {}
    try:
        with RUNTIME_FILE.open("r", encoding="utf-8") as file:
            data = json.load(file)
            return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def save_runtime(data: dict) -> None:
    RUNTIME_FILE.parent.mkdir(parents=True, exist_ok=True)
    temporary = RUNTIME_FILE.with_suffix(RUNTIME_FILE.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as file:
        json.dump(data, file, ensure_ascii=False, indent=2)
        file.write("\n")
    os.replace(str(temporary), str(RUNTIME_FILE))


AUTO_REPLY_DEFAULTS = {
    "enabled": False,
    "guard_targets": [],
    "context_count": 80,
    "reply_prompt": "",
    "hotkeys": {
        "open_wechat": "ctrl+alt+w",
        "search": "ctrl+f",
        "paste": "ctrl+v",
        "send": "enter",
    },
}

# 自动回复配置按微信账号隔离存放：每个账号一个独立 JSON 文件，
# 与根目录 config.json 分开，避免多账号切换时 prompt/守护对象串号。
AUTO_REPLY_DIR = ROOT_DIR / "data" / "auto_reply"


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


def _load_auto_reply_raw() -> dict:
    """读取当前账号的自动回复配置，并迁移旧版扁平数据（仅归入 default 账号）。"""
    account = _auto_reply_account_key()
    path = _auto_reply_file_path(account)
    if path.exists():
        return _read_json_file(path)

    # 旧版把配置放在 runtime.json 的 auto_reply 字段里，且无法判断属于哪个账号。
    # 仅在“未选择账号”的 default 场景迁移，避免把旧 prompt 错误套到某个多账号上。
    if account == "default":
        runtime = load_runtime()
        legacy = runtime.get("auto_reply")
        if isinstance(legacy, dict) and legacy:
            _write_json_file(path, legacy)
            runtime.pop("auto_reply", None)
            save_runtime(runtime)
            return legacy
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
        self._adapter = None
        self._adapter_key = None
        self._baselined = set()
        self._signatures = {}
        self._fingerprints = {}
        self.active_chat_name = None
        self.log = deque(maxlen=200)

    def _log(self, message: str, level: str = "info") -> None:
        self.log.append({
            "time": datetime.now().strftime("%H:%M:%S"),
            "level": level,
            "message": message,
        })

    def _get_adapter(self) -> WeChatAdapter:
        config = load_config()
        key = (config.get("wechat_api", ""), config.get("wechat_account", ""))
        if self._adapter is None or self._adapter_key != key:
            self._adapter = WeChatAdapter(*key)
            self._adapter_key = key
        return self._adapter

    def is_running(self) -> bool:
        return bool(self._thread and self._thread.is_alive())

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

        incoming = [m for m in messages if m.get("sender") and m.get("sender") != "我"]
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
            self._reply_to(target, target_name, messages, config)

    def _reply_to(self, target: Dict, target_name: str, messages: List[Dict], config: Dict) -> None:
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
            self.active_chat_name = automation.send_wechat_message(
                target_name, reply, hotkeys, active_chat_name=self.active_chat_name
            )
        except Exception as exc:
            self._log("向「%s」发送失败：%s" % (target_name, exc), level="error")
            return
        self._log("已回复「%s」" % target_name)


_auto_reply_engine = AutoReplyEngine()


_WECHAT_ADAPTER = None
_WECHAT_ADAPTER_KEY = None
_QQ_ADAPTER = None
_QQ_ADAPTER_KEY = None


# ==================== 数据源状态与消息窗口 ====================

def get_wechat_adapter() -> WeChatAdapter:
    """按当前配置复用微信本地 API 会话。"""
    global _WECHAT_ADAPTER, _WECHAT_ADAPTER_KEY
    config = load_config()
    key = (config.get("wechat_api", ""), config.get("wechat_account", ""))
    if _WECHAT_ADAPTER is None or _WECHAT_ADAPTER_KEY != key:
        _WECHAT_ADAPTER = WeChatAdapter(*key)
        _WECHAT_ADAPTER_KEY = key
    return _WECHAT_ADAPTER


def get_qq_adapter() -> WeQAdapter:
    """按当前配置复用 WeQ 登录态和已打开账号。"""
    global _QQ_ADAPTER, _QQ_ADAPTER_KEY
    config = load_config()
    runtime = load_runtime()
    key = (
        config.get("weq_url", ""),
        runtime.get("weq_token", ""),
        config.get("qq_uin", ""),
    )
    if _QQ_ADAPTER is None or _QQ_ADAPTER_KEY != key:
        _QQ_ADAPTER = WeQAdapter(*key)
        _QQ_ADAPTER_KEY = key
    return _QQ_ADAPTER


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
    result = get_wechat_adapter().list_chats()
    return {"status_info": result["status_info"], "chats": result["chats"]}


def get_real_wechat_messages(target_id: str, limit: int = 50) -> List[Dict]:
    return get_wechat_adapter().list_messages(target_id, limit)


def get_real_qq_chats() -> Dict:
    result = get_qq_adapter().list_chats()
    return {"status_info": result["status_info"], "chats": result["chats"]}


def get_real_qq_messages(target_id: str, is_group: bool = True, limit: int = 50) -> List[Dict]:
    kind = "group" if is_group else "c2c"
    return get_qq_adapter().list_messages(target_id, kind, limit)


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

    config = load_config()
    config["providers"] = providers
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
    config = load_config()
    config["providers"] = remaining
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
async def get_chats_api(platform: str = Query("wechat")):
    try:
        if platform == "qq":
            res = get_real_qq_chats()
        else:
            res = get_real_wechat_chats()
        return {"status": "success", "platform": platform, "status_info": res["status_info"], "chats": res["chats"]}
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))


@app.get("/api/messages")
async def get_messages_api(
    target_id: str,
    platform: str = Query("wechat"),
    is_group: bool = Query(True),
    limit: int = Query(500, ge=1, le=2000),
    recent_hours: Optional[float] = Query(None),
    recent_count: Optional[int] = Query(None)
):
    try:
        if platform == "qq":
            msgs = get_real_qq_messages(target_id, is_group, limit)
        else:
            msgs = get_real_wechat_messages(target_id, limit)
        msgs = filter_messages(msgs, recent_hours, recent_count)
        return {
            "status": "success",
            "messages": msgs,
            "source_valid": True,
            "message": "" if msgs else "当前会话没有读取到真实明文消息"
        }
    except Exception as e:
        raise HTTPException(status_code=503, detail=str(e))


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
        system_prompt = "你是一名高效的高级会议秘书。请对以下聊天记录整理为标准的【会议纪要与决策】，包含：1. 讨论背景 2. 关键决策 3. 责任人及后续动作。"
    elif summary_type == "todo":
        system_prompt = "你是一个严谨的项目经理。请从以下聊天记录中精确提取【待办事项与任务列表 (Action Items)】，包含：任务描述、责任人、预期交付时间。"
    elif summary_type == "topics":
        system_prompt = "你是一个信息分析专家。请提炼以下聊天记录中的【核心讨论热点与话题】，按关注度从高到低排列，并给出简要总结。"
    else:
        system_prompt = "你是一个智能聊天助手。请对以下聊天记录提供简洁清晰的 Markdown 格式结构化总结，包含核心议题、关键观点及待办项。"

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
    runtime = load_runtime()
    history = runtime.get("summaries", {}).get(target_id, [])
    return {"status": "success", "history": history}


@app.post("/api/history")
async def save_history_api(data: dict = Body(...)):
    target_id = data.get("target_id")
    history = data.get("history")
    if not target_id or history is None:
        raise HTTPException(status_code=400, detail="缺少 target_id 或 history")
    runtime = load_runtime()
    if "summaries" not in runtime:
        runtime["summaries"] = {}
    runtime["summaries"][target_id] = history
    save_runtime(runtime)
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

    default_outer = "你是聊天记录中的“我”本人。"
    default_inner = (
        "请根据最近的对话上下文，生成一条自然、简洁、符合“我”语气和风格的下一条回复。"
        "只输出回复正文本身，不要任何解释、引号、“回复：”等前缀或额外说明。"
    )
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
    allowed = {"enabled", "guard_targets", "context_count", "reply_prompt", "hotkeys"}
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
            automation.send_wechat_message(target_name, text, hotkeys)
            return {"status": "success", "message": "完整发送流程已执行"}
    except HTTPException:
        raise
    except Exception as exc:
        raise HTTPException(status_code=500, detail="测试失败：%s" % exc) from exc
    raise HTTPException(status_code=400, detail="未知测试动作：%s" % action)


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


if STATIC_DIR.exists():
    app.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")

@app.get("/")
async def read_index():
    index_path = STATIC_DIR / "index.html"
    if index_path.exists():
        return FileResponse(index_path)
    return JSONResponse({"message": "Chatlog AI 后端服务正常运行。"})


def main():
    config = load_config()
    port = config.get("port", 18765)
    print(f"==================================================")
    print(f" Chatlog AI 统一服务 v5.0（微信 WeChatDataAnalysis / QQ WeQ）已启动")
    print(f" 监听地址: http://127.0.0.1:{port}")
    print(f"==================================================")
    _auto_reply_engine.start()
    uvicorn.run(app, host="127.0.0.1", port=port, log_level="warning")


if __name__ == "__main__":
    main()
