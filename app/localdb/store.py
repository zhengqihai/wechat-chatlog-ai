# -*- coding: utf-8 -*-
"""本机微信数据的只读入口：镜像同步 + 只读连接。

职责边界（不要把它做成业务层）：
  - 把 `db_storage` 下的加密库解析成**镜像路径**并同步（委托给 mirror.DbMirror）
  - 给出「库名 → 只读 sqlite3 连接」，并在查询期间持镜像锁
  - 解决「会话的消息表在哪一个 message_*.db 里」

**读方必须走 `with store.read(name) as con:`**：镜像会被原地改写，
读方若不在锁内查询，可能读到"写了一半"的页。这是本模块唯一的硬约束。
"""

from __future__ import annotations

import os
import re
import sqlite3
from contextlib import contextmanager
from typing import Dict, Iterator, List, Optional

from . import mirror as mirror_mod

# 账号目录形如 `wxid_cjll2zqxiby722_224d`，尾部那截是设备/登录相关的短哈希。
_ACCOUNT_SUFFIX_RE = re.compile(r"_[0-9a-f]{3,8}$")

# 每轮固定同步的库。**刻意不含 message 分片**：分片数量与名字随机器不同，必须动态发现
# （见 message_shards）。head_image.db 放进来是因为 C3 的头像从这里取（`image_buffer`
# 是本机缓存字节）；sns.db 是 C4 的朋友圈时间线（单表 410 行，2.67MB，同步成本可忽略）。
BASE_DBS = ("session.db", "contact.db", "head_image.db", "sns.db")

# message 分片名的形态：`message_<数字>.db`。
# 刻意排除 `message_fts.db` / `message_resource.db`：它们不存 `Msg_<hash>` 会话消息表，
# 同步它们纯属白花成本（fts 单库就有几十 MB）。
_MESSAGE_SHARD_RE = re.compile(r"^message_(\d+)\.db$")

# 分片发现失败时的兜底：退化成"只同步 message_0.db"（即修复前的行为），
# 而不是彻底不同步消息 —— 宁可少读，不可全无。
FALLBACK_MESSAGE_DBS = ("message_0.db",)

# db_storage 的已知分层：库名 → 子目录名。命中不了再退回递归查找。
_KNOWN_SUBDIRS = {
    "session.db": "session",
    "contact.db": "contact",
    "message_0.db": "message",
    "message_fts.db": "message",
    "message_resource.db": "message",
    "sns.db": "sns",
    "favorite.db": "favorite",
    "general.db": "general",
    "emoticon.db": "emoticon",
    "head_image.db": "head_image",
    "hardlink.db": "hardlink",
    "solitaire.db": "solitaire",
}


class StoreError(RuntimeError):
    """存储层不可用：库文件缺失、密钥不对或镜像同步失败。"""


class LocalStore:
    def __init__(self, storage_path: str, mirror_root: str, passphrase: bytes):
        if not storage_path or not os.path.isdir(storage_path):
            raise StoreError("db_storage 路径不存在：%s" % storage_path)
        self.storage_path = storage_path
        self.mirror_root = mirror_root
        self.passphrase = passphrase
        self._db_files: Dict[str, str] = {}
        self._mirrors: Dict[str, mirror_mod.DbMirror] = {}
        # 表名 → 含该表的分片列表（可能不止一个，见 shards_for_table）。
        self._table_owners: Dict[str, List[str]] = {}
        self._message_shards: Optional[List[str]] = None
        self._self_wxid: Optional[str] = None

    # ---------- 自身账号 ----------

    @property
    def self_wxid(self) -> str:
        """本机登录账号的 wxid，用于把「自己发的消息」判成 `我`。

        来源是 `db_storage` 的父目录名（`wxid_cjll2zqxiby722_224d` → 去掉尾部短哈希）。
        这是唯一**无需先解密就能拿到**的账号标识；调用方可显式覆盖。
        """
        if self._self_wxid is None:
            folder = os.path.basename(os.path.dirname(self.storage_path.rstrip("\\/")))
            self._self_wxid = _ACCOUNT_SUFFIX_RE.sub("", folder) or folder
        return self._self_wxid

    @self_wxid.setter
    def self_wxid(self, value: str) -> None:
        self._self_wxid = str(value or "")

    # ---------- 路径解析 ----------

    def db_file(self, name: str) -> str:
        """库名 → db_storage 下的真实加密文件路径（结果缓存）。"""
        cached = self._db_files.get(name)
        if cached:
            return cached
        sub = _KNOWN_SUBDIRS.get(name)
        candidates = []
        if sub:
            candidates.append(os.path.join(self.storage_path, sub, name))
        candidates.append(os.path.join(self.storage_path, name))
        for path in candidates:
            if os.path.exists(path):
                self._db_files[name] = path
                return path
        for dirpath, _dirnames, filenames in os.walk(self.storage_path):
            if name in filenames:
                path = os.path.join(dirpath, name)
                self._db_files[name] = path
                return path
        raise StoreError("在 %s 下找不到数据库 %s" % (self.storage_path, name))

    def message_shards(self) -> List[str]:
        """可能承载 `Msg_<hash>` 会话消息表的分片（`message_<数字>.db`），按分片号升序。

        **必须动态发现而不是写死 `message_0.db`**：实测存在多分片的机器 ——
        `message_0.db` 在某天之后冻结、新消息全部写入 `message_1.db`。
        写死会让新分片永远没有镜像，表现为"会话列表是新的、消息却停在旧日期"，
        且全程不报错（本模块最难查的一类故障）。
        """
        if self._message_shards is not None:
            return self._message_shards
        # 用"分片号 → 文件名"的字典去重，最后按分片号排序；
        # 直接 sorted(名字) 会把 message_10 排到 message_2 前面，排序键必须是数字。
        found: Dict[int, str] = {}
        for _dirpath, _dirnames, filenames in os.walk(self.storage_path):
            for filename in filenames:
                match = _MESSAGE_SHARD_RE.match(filename)
                if match:
                    found[int(match.group(1))] = filename
        self._message_shards = [found[index] for index in sorted(found)]
        return self._message_shards

    def sync_targets(self) -> List[str]:
        """每轮要同步的库 = 固定库 + **全部** message 分片。"""
        return list(BASE_DBS) + (self.message_shards() or list(FALLBACK_MESSAGE_DBS))

    def shards_for_table(self, table_name: str) -> List[str]:
        """会话的消息表落在**哪些** message 分片里（首次查找后缓存；找不到返回空列表）。

        表名规则：`Msg_ + md5(username)`（已实测：本机 77 个群全部对得上）。

        返回**全部**命中的分片，而不是"命中即返回第一个"：分片会随微信写入轮转，
        同一张表可能在旧分片（冻结）与新分片（续写）里各有一部分，
        只返回第一个会稳定地只读到旧的那一半，且不报错。
        调用方（messages.list_messages）负责跨分片合并。
        """
        cached = self._table_owners.get(table_name)
        if cached is not None:
            return list(cached)
        owners: List[str] = []
        for name in self.message_shards():
            # 镜像可能还没建好（首次同步尚未覆盖到该分片）：按需同步一次再查，
            # 否则会把"还没轮到"误判成"这张表不在任何分片里"。
            if not self._ensure_mirror(name):
                continue
            try:
                with self.read(name) as con:
                    row = con.execute(
                        "select 1 from sqlite_master where type='table' and name=?", (table_name,)
                    ).fetchone()
            except Exception:
                continue
            if row:
                owners.append(name)
        if owners:
            self._table_owners[table_name] = list(owners)
        return owners

    def _ensure_mirror(self, name: str) -> bool:
        """确保某库已有镜像（没有就先同步一次）。任何失败返回 False，不抛。"""
        try:
            mirror = self.mirror(name)
        except Exception:
            return False
        if mirror.exists:
            return True
        try:
            mirror.sync()
        except Exception:
            return False
        return mirror.exists

    # ---------- 镜像与同步 ----------

    def mirror(self, name: str) -> mirror_mod.DbMirror:
        cached = self._mirrors.get(name)
        if cached is None:
            cached = mirror_mod.DbMirror(
                self.db_file(name), os.path.join(self.mirror_root, name), self.passphrase
            )
            self._mirrors[name] = cached
        return cached

    def sync(self, names: Optional[List[str]] = None) -> Dict[str, dict]:
        """同步指定库（默认"固定库 + 全部分片"，见 sync_targets）。单库失败不阻断其它库。"""
        result: Dict[str, dict] = {}
        for name in (names if names is not None else self.sync_targets()):
            try:
                result[name] = self.mirror(name).sync()
            except Exception as exc:                       # 密钥错 / 文件缺失 / 磁盘问题
                result[name] = {"error": "%s: %s" % (type(exc).__name__, exc)}
        return result

    # ---------- 只读连接 ----------

    @contextmanager
    def read(self, name: str) -> Iterator[sqlite3.Connection]:
        """持镜像锁打开只读连接。**读方必须用 with 包住整个查询。**"""
        m = self.mirror(name)
        if not m.exists:
            raise StoreError("%s 尚未同步，先调用 sync()" % name)
        with m.lock:
            con = sqlite3.connect("file:%s?mode=ro" % m.mirror_path.replace("\\", "/"), uri=True)
            try:
                con.text_factory = lambda b: b.decode("utf-8", "replace")
                yield con
            finally:
                con.close()
