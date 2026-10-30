# -*- coding: utf-8 -*-
"""微信原始数据 → 契约字段的公共转换。

这里放**跨模块共享**的转换规则（时间格式、群判定、local_type 位域、压缩解压）。
单个模块私有的解析逻辑留在各自文件里，不要往这里塞。

契约锚点：`chats[].last_time` / `messages[].time` 必须是 `YYYY-MM-DD HH:MM:SS`——
客户端 `ChatItemViewModel.FormatShortTime` 用 `(\\d{4}-\\d{2}-\\d{2})\\s+(\\d{2}:\\d{2})` 去匹配，
格式不对会让「今天只显示 HH:mm」退化成原样输出。
"""

from __future__ import annotations

from datetime import datetime
from typing import Optional

# ---------- 时间 ----------

def format_time(value, fallback: str = "") -> str:
    """秒/毫秒时间戳 → `YYYY-MM-DD HH:MM:SS`（与 adapters._format_time 行为一致）。"""
    try:
        number = float(value)
        if number > 100000000000:      # 毫秒
            number /= 1000.0
        return datetime.fromtimestamp(number).strftime("%Y-%m-%d %H:%M:%S")
    except (TypeError, ValueError, OSError, OverflowError):
        return fallback or ("" if value is None else str(value))


# ---------- 会话类型 ----------

GROUP_SUFFIX = "@chatroom"


def is_group(username: str) -> bool:
    """群判定只用用户名后缀。

    **不要用 contact.local_type 判群**：实测它同时出现在群和非群上
    （local_type=1 里群 3 个 / 非群 147 个，local_type=2 里群 74 个 / 非群 22 个），
    而 `@chatroom` 后缀与本机 77 张 Msg_ 表严格一一对应。
    """
    return str(username or "").endswith(GROUP_SUFFIX)


def session_type(username: str) -> str:
    return "group" if is_group(username) else "friend"


# ---------- local_type 位域 ----------

BASE_TYPE_MASK = 0xFFFFFFFF


def base_type(local_type) -> int:
    """低位 32 位是基础消息类型。实测：`244813135921 = (0x39 << 32) | 49`。"""
    try:
        return int(local_type) & BASE_TYPE_MASK
    except (TypeError, ValueError):
        return 0


def sub_type(local_type) -> int:
    try:
        return int(local_type) >> 32
    except (TypeError, ValueError):
        return 0


# 基础类型 → 契约里的 message_type（上游叫 renderType）。
# 只收录**实测出现过**的取值，未出现的宁可不猜（见 docs/SELFHOST_DATA_PLAN.md §4 阶段 C）。
BASE_TYPE_NAMES = {
    1: "text",
    3: "image",
    34: "voice",
    42: "card",
    43: "video",
    47: "emoji",
    48: "location",
    49: "link",        # appmsg 家族，按子类型再细分
    10000: "system",
}

# message_type → 非文本消息的展示标签（对齐 docs/API_REFERENCE.md §4.2.1）。
TYPE_LABELS = {
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
    "card": "[名片]",
    "system": "[系统消息]",
}


def type_label(message_type: str) -> str:
    return TYPE_LABELS.get(message_type, "[消息]")


# ---------- WCDB 列压缩 ----------

ZSTD_MAGIC = bytes.fromhex("28B52FFD")
# WCDB_CT_message_content：0 = 原样，4 = zstd 压缩。
# 实测零例外：CT=0 的 150769 条全部非压缩，CT=4 的 44050 条全部是 zstd。
WCDB_CT_ZSTD = 4

_decompressor = None


def _zstd():
    global _decompressor
    if _decompressor is None:
        import zstandard
        _decompressor = zstandard.ZstdDecompressor()
    return _decompressor


def decode_content(raw, column_type: Optional[int] = None) -> str:
    """把 message_content 还原成文本。

    优先信任 `column_type`（WCDB 的压缩标志，实测零例外）；没有标志时才退回看 zstd 魔数，
    这样即使上游改了 CT 取值，也不至于直接吐乱码。
    """
    if raw is None:
        return ""
    if isinstance(raw, str):
        return raw
    data = bytes(raw)
    if not data:
        return ""
    compressed = (column_type == WCDB_CT_ZSTD) if column_type is not None else False
    if not compressed and data[:4] != ZSTD_MAGIC:
        return data.decode("utf-8", "replace")
    try:
        return _zstd().decompress(data).decode("utf-8", "replace")
    except Exception:
        # 解压失败时不要把二进制喂给上层（它会流进 AI 提示词）。
        return ""
