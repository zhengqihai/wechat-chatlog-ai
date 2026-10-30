# -*- coding: utf-8 -*-
"""密钥来源（A2b）。

设计约定（docs/SELFHOST_DATA_PLAN.md §4 A2b）：**密钥来源必须可替换**。
实测结论是自研提取只剩 Hook 一条重路，所以当前策略是"先用上游产出的那一把"：

    1. `config/wechat-keys.json` —— 本项目自己的备份（首选，不依赖上游目录还在不在）
    2. `data/wechat/output/account_keys.json` —— 上游产出（回退）

两个文件的**结构完全不同**，这里统一归一成一套字段，上层不感知差异：

    {"account", "db_key", "image_xor_key", "image_aes_key",
     "wxid_dir", "db_storage_path", "source"}

换密钥来源时只改本模块，`cipher` / `wal` / `mirror` / 读取层一行都不用动。
"""

from __future__ import annotations

import json
import os
import re
from typing import Dict, List, Optional, Tuple

DB_STORAGE_DIRNAME = "db_storage"

# 本项目自己的密钥备份文件名（在 config/ 下）。
# 它不只是"备份"：上游被移除后（阶段 D3），它是**唯一**的密钥来源，
# 也是客户端「微信账号」窗口手动导入密钥的落点。
BACKUP_FILENAME = "wechat-keys.json"

# db_key 是 32 字节原始密钥的 hex，即 64 个 hex 字符。
DB_KEY_HEX_LENGTH = 64


class KeyError_(RuntimeError):
    """找不到可用的密钥。"""


def _norm_account(value: str) -> str:
    """把上游那种 `wxid_xxx_224d` 别名归一成 `wxid_xxx`，便于和账号目录对齐。"""
    text = str(value or "").strip()
    return text


def _entry(account: str, raw: Dict, source: str) -> Optional[Dict]:
    db_key = str(raw.get("db_key") or "").strip()
    if not db_key:
        return None
    wxid_dir = str(raw.get("db_key_source_wxid_dir") or raw.get("wxid_dir") or "").strip()
    storage = str(raw.get("db_key_source_db_storage_path")
                  or raw.get("db_storage_path") or "").strip()
    if not storage and wxid_dir:
        storage = os.path.join(wxid_dir, DB_STORAGE_DIRNAME)
    return {
        "account": account,
        "db_key": db_key,
        "image_xor_key": str(raw.get("image_xor_key") or ""),
        "image_aes_key": str(raw.get("image_aes_key") or ""),
        "wxid_dir": wxid_dir,
        "db_storage_path": storage,
        "source": source,
    }


def _from_backup(path: str) -> List[Dict]:
    with open(path, "r", encoding="utf-8") as fp:
        payload = json.load(fp)
    out = []
    for item in payload.get("accounts") or []:
        entry = _entry(str(item.get("account") or ""), item, "local-backup")
        if entry:
            out.append(entry)
    return out


def _from_upstream(path: str) -> List[Dict]:
    with open(path, "r", encoding="utf-8") as fp:
        payload = json.load(fp)
    out = []
    seen = set()
    for alias, item in (payload or {}).items():
        if not isinstance(item, dict):
            continue
        entry = _entry(_norm_account(alias), item, "upstream")
        if entry and entry["db_key"] not in seen:
            seen.add(entry["db_key"])
            out.append(entry)
    return out


def load_accounts(config_dir: str, data_root: str) -> List[Dict]:
    """按优先级汇总所有可用账号。两个来源都读，备份优先。"""
    candidates = [
        os.path.join(config_dir, BACKUP_FILENAME),
        os.path.join(data_root, "data", "wechat", "output", "account_keys.json"),
    ]
    entries: List[Dict] = []
    seen = set()
    for path in candidates:
        if not os.path.exists(path):
            continue
        try:
            found = _from_backup(path) if path.endswith("wechat-keys.json") else _from_upstream(path)
        except (OSError, ValueError):
            continue
        for entry in found:
            if entry["db_key"] in seen:
                continue
            seen.add(entry["db_key"])
            entries.append(entry)
    if not entries:
        raise KeyError_("未找到可用的微信数据库密钥：既没有 config/wechat-keys.json，"
                        "也没有 data/wechat/output/account_keys.json")
    return entries


def pick_account(config_dir: str, data_root: str, preferred: str = "") -> Dict:
    """选一个账号：优先配置里指定的，其次 `db_storage` 真实存在的。"""
    entries = load_accounts(config_dir, data_root)
    want = str(preferred or "").strip()
    if want:
        for entry in entries:
            if entry["account"] == want or entry["account"].startswith(want + "_"):
                return entry
    for entry in entries:
        if entry["db_storage_path"] and os.path.isdir(entry["db_storage_path"]):
            return entry
    return entries[0]


# ==================== 写入与校验（阶段 D3：手动导入密钥） ====================
#
# 为什么需要这一组：上游的授权页（10392）是**唯一**能"解析出 db_key"的东西。
# 上游一旦被移除，密钥就只剩 `config/wechat-keys.json` 这一个来源，而此前
# 它是由人手工从上游产物复制过去的（见 docs/SELFHOST_DATA_PLAN.md §4 A2b）——
# 换账号 / 微信升级轮换密钥 / 数据目录被清空，都会让整条自研链路报废且无从补救。
# 所以必须有一个"用户自己把密钥交给程序"的入口，并且**当场验证它能用**。

def backup_path(config_dir: str) -> str:
    """本项目自己的密钥文件路径（客户端「微信账号」窗口会把它显示给用户）。"""
    return os.path.join(config_dir, BACKUP_FILENAME)


def upstream_keys_path(data_root: str) -> str:
    """上游产出的密钥文件路径（上游被移除后不再产生，仅用于界面展示与兜底读取）。"""
    return os.path.join(data_root, "data", "wechat", "output", "account_keys.json")


def find_session_db(storage_path: str) -> Optional[str]:
    """在 `db_storage` 下定位 `session.db`。有它就够验证密钥，不必遍历全部库。"""
    for candidate in (os.path.join(storage_path, "session", "session.db"),
                      os.path.join(storage_path, "session.db")):
        if os.path.exists(candidate):
            return candidate
    if not os.path.isdir(storage_path):
        return None
    for dirpath, _dirnames, filenames in os.walk(storage_path):
        if "session.db" in filenames:
            return os.path.join(dirpath, "session.db")
    return None


def verify_entry(entry: Dict) -> Tuple[bool, str]:
    """**实测**这把密钥能不能解开本机库：解 `session.db` 第 1 页并校验页头。

    这是"导入成功"的唯一判据。只校验格式（64 位 hex）远远不够 ——
    一把格式合法但错误的密钥会让界面显示"已就绪"，而实际链路全 503，
    那正是最难排查的一类故障（上游就是这么坏的：静默降级 + 照样返回 HTTP 200）。

    代价：每次要跑一遍 PBKDF2(256000)，约几十毫秒。只在"导入"和"看状态"时调用，可接受。
    """
    from . import cipher as cipher_mod      # 局部导入：让 keys 模块本身不依赖 cryptography

    storage = str(entry.get("db_storage_path") or "").strip()
    if not storage:
        return False, "缺少数据库目录（db_storage_path）"
    if not os.path.isdir(storage):
        return False, "数据库目录不存在：%s" % storage

    session_db = find_session_db(storage)
    if not session_db:
        return False, "在 %s 下找不到 session.db" % storage

    raw_key = str(entry.get("db_key") or "").strip()
    try:
        key = bytes.fromhex(raw_key)
    except ValueError:
        return False, "db_key 不是合法的 hex 字符串"
    if len(key) != DB_KEY_HEX_LENGTH // 2:
        return False, "db_key 长度不对：应为 32 字节（%d 位 hex），当前 %d 字节" % (
            DB_KEY_HEX_LENGTH, len(key))

    try:
        with open(session_db, "rb") as handle:
            blob = handle.read(cipher_mod.PAGE_SIZE)
    except OSError as exc:
        return False, "读取 session.db 失败：%s" % exc

    try:
        cipher_mod.decrypt_first_page(blob, key)
    except Exception as exc:                # CipherError / 文件不足一页 / 已是明文
        return False, "密钥校验失败：%s" % exc
    return True, "密钥可用（%s 第 1 页解密通过）" % os.path.basename(session_db)


def _entries_from_payload(payload) -> List[Dict]:
    """从 JSON 里抽条目，同时认两种结构：

      ① 上游 `account_keys.json`：顶层就是 `"wxid_x_224d" → {db_key, db_key_source_db_storage_path, …}`
      ② 我们自己的 `wechat-keys.json`：`{"accounts": [{account, db_key, db_storage_path, …}]}`

    两种结构完全不同，但 `_entry` 已经把它们归一成同一套字段（`_entry` 会同时找
    `db_key_source_db_storage_path` 与 `db_storage_path`），所以这里只需正确分派。
    """
    if not isinstance(payload, dict):
        return []

    accounts = payload.get("accounts")
    if isinstance(accounts, list):          # ② 我们自己的格式
        out = []
        for item in accounts:
            if not isinstance(item, dict):
                continue
            entry = _entry(str(item.get("account") or ""), item, "import")
            if entry:
                out.append(entry)
        return out

    out = []                                # ① 上游格式
    for alias, item in payload.items():
        if not isinstance(item, dict):
            continue
        entry = _entry(_norm_account(alias), item, "import")
        if entry:
            out.append(entry)
    return out


def parse_import(content: str, account: str = "", db_storage_path: str = "") -> List[Dict]:
    """把用户粘贴的文本解析成条目列表。支持两种形态，**不做第三种猜测**：

      ① 密钥文件全文（上游 `account_keys.json` 或我们自己的 `wechat-keys.json`）；
      ② 裸 db_key：64 位 hex（允许空格 / 换行 / `0x` 前缀）。这种形态不含账号与库位置，
         必须另给 `db_storage_path`（`account` 可由目录名推断）。

    两者都不像就抛 `KeyError_` 并说明期望格式 —— 宁可让用户重贴一次，
    也不能"猜出一个大概像密钥的东西"写进配置。
    """
    text = str(content or "").strip().lstrip("\ufeff")
    if not text:
        raise KeyError_("粘贴内容为空")

    if text[:1] in ("{", "["):
        try:
            payload = json.loads(text)
        except ValueError as exc:
            raise KeyError_("看起来是 JSON，但解析失败：%s" % exc)
        entries = _entries_from_payload(payload)
        if not entries:
            raise KeyError_("JSON 里没有找到任何含 db_key 的账号记录")
        return entries

    # 先去掉 `0x` 前缀：`x` 不在 hex 字符集里，若出现在中间说明它本来就不是纯 hex。
    token = re.sub(r"[^0-9a-fA-F]", "", re.sub(r"0[xX]", "", text))
    if len(token) != DB_KEY_HEX_LENGTH:
        raise KeyError_(
            "无法识别粘贴内容：既不像密钥文件 JSON，也不是 %d 位 hex 的 db_key（当前解出 %d 位）"
            % (DB_KEY_HEX_LENGTH, len(token)))

    storage = str(db_storage_path or "").strip()
    if not storage:
        raise KeyError_("只粘贴 db_key 时，必须同时提供数据库目录（db_storage_path）")
    return [{
        "account": str(account or "").strip(),
        "db_key": token.lower(),
        "db_storage_path": storage,
        "wxid_dir": "",
        "image_xor_key": "",
        "image_aes_key": "",
        "source": "import",
    }]


def infer_account(entry: Dict) -> Dict:
    """账号名为空时从库目录推断：`…\\wxid_xxx_224d\\db_storage` → `wxid_xxx`。

    只粘贴裸 db_key 时账号就是空的，而账号是"这台机器读哪个账号"的键，不能留空。
    """
    if str(entry.get("account") or "").strip():
        return entry
    storage = str(entry.get("db_storage_path") or "").rstrip("\\/")
    folder = os.path.basename(os.path.dirname(storage))
    if not folder:
        return entry
    # 与 store.LocalStore.self_wxid 同一口径：账号目录名尾部是设备/登录相关的短哈希。
    inferred = re.sub(r"_[0-9a-f]{3,8}$", "", folder) or folder
    entry = dict(entry)
    entry["account"] = inferred
    return entry


def _to_backup(entry: Dict) -> Dict:
    """内部结构 → 文件字段名（读侧 `_entry` / `_from_backup` 认的就是这几个）。"""
    payload = {
        "account": str(entry.get("account") or ""),
        "db_key": str(entry.get("db_key") or ""),
        "db_storage_path": str(entry.get("db_storage_path") or ""),
    }
    for name in ("wxid_dir", "image_xor_key", "image_aes_key"):
        value = str(entry.get(name) or "")
        if value:
            payload[name] = value
    return payload


def save_accounts(config_dir: str, entries: List[Dict]) -> str:
    """合并写入 `config/wechat-keys.json`（临时文件 + 原子替换），返回文件路径。

    合并按 `db_key` 去重：新条目覆盖同 db_key 的旧条目，其余原样保留 ——
    导入一个账号不该把另一个账号的记录弄丢。
    """
    path = backup_path(config_dir)
    merged: Dict[str, Dict] = {}
    if os.path.exists(path):
        try:
            for item in _from_backup(path):
                merged[str(item["db_key"])] = item
        except (OSError, ValueError):
            # 旧文件坏了不该卡住这次导入 —— 用户这次提交的内容本来就是要覆盖它的。
            merged = {}
    for item in entries:
        key = str(item.get("db_key") or "")
        if key:
            merged[key] = item

    payload = {"accounts": [_to_backup(item) for item in merged.values()]}
    os.makedirs(config_dir, exist_ok=True)
    temporary = path + ".tmp"
    with open(temporary, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    os.replace(temporary, path)
    return path


def describe_accounts(config_dir: str, data_root: str) -> Tuple[List[Dict], str]:
    """列出所有密钥条目及其**实测**结果，供 `/api/keys/status` 与导入回执使用。

    返回 `(条目列表, 整体错误)`；一条都没有时错误为非空文案。
    """
    try:
        accounts = load_accounts(config_dir, data_root)
    except KeyError_ as exc:
        return [], str(exc)

    described = []
    for entry in accounts:
        ok, message = verify_entry(entry)
        described.append({
            "account": entry.get("account") or "",
            "source": entry.get("source") or "",          # local-backup / upstream / import
            "dbStoragePath": entry.get("db_storage_path") or "",
            "keyHexLength": len(str(entry.get("db_key") or "")),
            "verified": ok,
            "message": message,
        })
    return described, ""
