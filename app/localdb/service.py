# -*- coding: utf-8 -*-
"""C 阶段的运行时外壳：把「密钥 → 镜像 → 后台同步 → 查询」串成一个可用的服务对象。

对外只暴露两件事：
  - `list_chats()` / `list_messages()`：与 `WeChatAdapter` **同名同形**，可被无差别替换
  - `status()`：把降级原因、最近一次同步统计透出去（**不允许静默降级**）

设计约束（都是踩过的坑）：
  1. **密钥缺失 / 镜像同步失败绝不能拖垮服务**：`ready` 为假时上层回退到上游，
     原因写进 `status()`，而不是抛异常让整个 FastAPI 起不来。
  2. 后台同步循环是**低频低成本**的（正常路径 0.01s/轮），失败时退避，不空转刷日志。
  3. 所有查询都走 `LocalStore.read()` 的镜像锁，与写入互斥。
"""

from __future__ import annotations

import os
import threading
import time
from typing import Dict, List, Optional, Tuple

from . import chats as chats_mod
from . import contacts as contacts_mod
from . import exports as exports_mod
from . import images as images_mod
from . import keys as keys_mod
from . import members as members_mod
from . import messages as messages_mod
from . import moments as moments_mod
from . import stats as stats_mod
from .store import LocalStore, StoreError

# 同步间隔：实测正常路径 0.008~0.03s，1 秒一轮既能压住延迟又不浪费。
SYNC_INTERVAL_SECONDS = 1.0
# 防撤回备份的捕获间隔（秒）。全库扫最近 2 分钟约 45ms，2 秒一次足够及时。
RECALL_CAPTURE_INTERVAL_SECONDS = 2.0
# 连续失败时的退避上限，避免密钥失效后每秒钟刷一条错误日志。
MAX_BACKOFF_SECONDS = 30.0


class LocalDataService:
    def __init__(self, config_dir: str, data_root: str, account: str = ""):
        self.config_dir = config_dir
        self.data_root = data_root
        self.account = str(account or "")
        self.mirror_root = os.path.join(data_root, "data", "localdb")

        self._store: Optional[LocalStore] = None
        self._exports: Optional[exports_mod.SnsExportManager] = None
        # 图片解密密钥（账号级）：来自 account_keys.json 的 image_aes_key / image_xor_key。
        # 仅在建立 store 时一并取出；空串表示本机没有图片密钥（图片功能降级，不阻断其它能力）。
        self._image_aes_key = ""
        self._image_xor_key = ""
        self._init_error = ""
        self._sync_error = ""
        self._last_stats: Dict[str, dict] = {}
        self._last_sync_at = 0.0
        self._sync_count = 0
        # 朋友圈"同步最新"用的游标（上次见到的最大 tid）。纯内存，见 sns_sync_latest。
        self._sns_cursor: Optional[str] = None
        self._recall_backup = messages_mod.RecallBackup()
        self._last_recall_capture_at = 0.0
        self._lock = threading.RLock()
        self._thread: Optional[threading.Thread] = None
        self._stop = threading.Event()

    # ---------- 生命周期 ----------

    def start(self) -> None:
        """启动后台同步线程。幂等。"""
        with self._lock:
            if self._thread and self._thread.is_alive():
                return
            self._stop.clear()
            self._thread = threading.Thread(target=self._loop, name="localdb-sync", daemon=True)
            self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    @property
    def ready(self) -> bool:
        """密钥与镜像都就绪才为真。为假时上层应回退到上游。"""
        return self._store is not None and not self._init_error

    def wait_until_ready(self, timeout: float = 10.0) -> bool:
        """阻塞等首次同步完成。用于**冷启动预热**，见 server.main()。

        判据是"镜像已建好且至少跑完一轮同步"，而不是 `ready` —— 后者在 store 建好、
        首次 sync 还没结束时就为真，这时查询仍会撞上"镜像文件尚不存在"。
        初始化失败（多为密钥缺失）立即返回 False，不无谓地耗满超时：后台线程会带退避继续重试。
        """
        deadline = time.time() + max(0.0, timeout)
        while time.time() < deadline:
            if self.ready and self._sync_count > 0:
                return True
            if self._init_error:
                return False
            time.sleep(0.1)
        return self.ready and self._sync_count > 0

    # ---------- 内部 ----------

    def _maybe_capture_recalls(self) -> None:
        """节流地给防撤回备份抄一份最近消息（每 2 秒一次，全库扫最近 2 分钟）。"""
        now = time.time()
        if now - self._last_recall_capture_at < RECALL_CAPTURE_INTERVAL_SECONDS:
            return
        self._last_recall_capture_at = now
        try:
            self._recall_backup.capture(self._store)
        except Exception:
            # 捕获失败不该拖垮同步循环；下一轮再试即可。
            pass

    def _ensure_store(self) -> bool:
        """建 store（只建一次）。任何失败都记进 `_init_error` 并返回 False，不抛。"""
        if self._store is not None:
            return True
        with self._lock:
            if self._store is not None:
                return True
            try:
                entry = keys_mod.pick_account(self.config_dir, self.data_root, self.account)
                storage = entry.get("db_storage_path") or ""
                if not storage or not os.path.isdir(storage):
                    raise StoreError("密钥记录里的 db_storage 路径不可用：%r" % storage)
                self.account = entry.get("account") or self.account
                self._store = LocalStore(storage, self.mirror_root, bytes.fromhex(entry["db_key"]))
                # 图片解密密钥：与 db_key 同账号级，但语义独立（只用于图片本体，见 C6 调研报告）。
                self._image_aes_key = str(entry.get("image_aes_key") or "")
                self._image_xor_key = str(entry.get("image_xor_key") or "")
                # 导出产物与镜像同级（`data/exports`）：都是"本服务自己产生、不外发路径"的东西。
                self._exports = exports_mod.SnsExportManager(
                    self._store, os.path.join(self.data_root, "data", "exports"))
                self._init_error = ""
            except Exception as exc:
                self._init_error = "%s: %s" % (type(exc).__name__, exc)
                return False
        return True

    def _loop(self) -> None:
        backoff = SYNC_INTERVAL_SECONDS
        while not self._stop.is_set():
            try:
                if self._ensure_store():
                    stats = self._store.sync()
                    errors = [name for name, item in stats.items() if item.get("error")]
                    self._last_stats = stats
                    self._sync_error = ("；".join("%s: %s" % (n, stats[n]["error"]) for n in errors)
                                        if errors else "")
                    self._sync_count += 1
                    self._last_sync_at = time.time()
                    self._maybe_capture_recalls()
                    backoff = SYNC_INTERVAL_SECONDS
                else:
                    backoff = min(max(backoff * 2, SYNC_INTERVAL_SECONDS), MAX_BACKOFF_SECONDS)
            except Exception as exc:
                self._sync_error = "%s: %s" % (type(exc).__name__, exc)
                backoff = min(max(backoff * 2, SYNC_INTERVAL_SECONDS), MAX_BACKOFF_SECONDS)
            self._stop.wait(backoff)

    # ---------- 查询入口（与 WeChatAdapter 同形） ----------

    def list_chats(self) -> Dict:
        self._require_ready()
        return chats_mod.list_chats(self._store, self.account)

    def list_messages(self, target_id: str, limit: int = 500,
                      annotate_recalls: bool = False) -> List[Dict]:
        self._require_ready()
        return messages_mod.list_messages(self._store, target_id, limit, annotate_recalls,
                                          self._recall_backup)

    def list_contacts(self, include=None, keyword: str = "", account: str = "") -> Dict:
        self._require_ready()
        return contacts_mod.list_contacts(self._store, include=include, keyword=keyword,
                                          account=account)

    def fetch_avatar(self, username: str):
        self._require_ready()
        return contacts_mod.avatar_bytes(self._store, username)

    def fetch_image(self, target_id: str, image_hash: str, create_time: int,
                    kind: str = "thumb") -> Tuple[bytes, str]:
        """解密并返回某条图片消息的字节。返回 (内容, Content-Type)。

        定位走 `image_hash`（32 位 hex，来自消息表 packed_info_data），不依赖
        message_resource.db 或上游。图片密钥缺失 / 文件不存在 / 解密失败都会抛
        StoreError / ImageError，由接口层转 503。
        """
        self._require_ready()
        if not self._image_aes_key or not self._image_xor_key:
            raise StoreError("缺少图片解密密钥（image_aes_key / image_xor_key）")
        try:
            aes_key = self._image_aes_key.encode("ascii")
        except UnicodeEncodeError:
            raise StoreError("image_aes_key 不是合法 ASCII")
        if len(aes_key) != 16:
            raise StoreError("image_aes_key 长度应为 16 字节，实际 %d" % len(aes_key))
        try:
            xor_key = int(self._image_xor_key, 16)
        except ValueError:
            raise StoreError("image_xor_key 不是合法 hex：%r" % self._image_xor_key)

        wxid_dir = os.path.dirname(self._store.storage_path)
        path = images_mod.locate_dat(wxid_dir, target_id, image_hash, create_time, kind)
        if not os.path.exists(path):
            raise StoreError("图片文件不存在：%s" % path)
        with open(path, "rb") as handle:
            data = handle.read()
        plain = images_mod.decrypt_v2(data, aes_key, xor_key)
        return plain, images_mod.content_type(plain)

    def sns_timeline(self, limit: int = 20, offset: int = 0,
                     usernames="", keyword: str = "") -> Dict:
        self._require_ready()
        return moments_mod.list_timeline(self._store, limit=limit, offset=offset,
                                         usernames=usernames, keyword=keyword)

    def sns_users(self, keyword: str = "", limit: int = 5000) -> Dict:
        self._require_ready()
        return moments_mod.list_users(self._store, keyword=keyword, limit=limit)

    def sns_sync_latest(self, max_scan: int = 200, force: bool = False) -> Dict:
        """"同步最新"：立即同步一次镜像，并把 `upserted` 的相对基准（上次最大 tid）记住。

        游标只活在内存里：服务重启后第一次调用没有基准，`upserted` 会是 0、
        `lastMaxId` 为空串 —— 这是诚实的"我不知道上次看到哪"，而不是编一个数出来。
        """
        self._require_ready()
        result = moments_mod.sync_latest(self._store, last_tid=self._sns_cursor,
                                        max_scan=max_scan, force=force)
        self._sns_cursor = result.get("maxId") or self._sns_cursor
        return result

    # ---------- 群成员与发言统计（C7 / C8） ----------

    def _group_snapshot_dir(self) -> str:
        """群成员快照目录：按账号隔离在 `data/group-members/{account}` 下。

        账号名可能带 `@` / 点号等字符，这里与 auto-reply 文件路径同一套清洗规则。
        """
        safe = "".join(ch for ch in str(self.account or "") if ch.isalnum() or ch in "-_")
        return os.path.join(self.data_root, "data", "group-members", safe or "default")

    def list_group_members(self, chatroom: str) -> Dict:
        self._require_ready()
        return members_mod.list_members(self._store, self._group_snapshot_dir(), chatroom)

    def group_changes(self, chatroom: str, limit: int = 50) -> List[Dict]:
        # 事件只读落盘文件，不依赖密钥/镜像是否就绪：本机首次启动就能看到历史。
        return members_mod.list_changes(self._group_snapshot_dir(), chatroom, limit)

    def scan_groups(self) -> Dict:
        self._require_ready()
        return members_mod.scan_all(self._store, self._group_snapshot_dir())

    def collect_message_stats(self, target_id: str, since_ts: float,
                              until_ts: Optional[float] = None,
                              cursor: Optional[List[int]] = None) -> Dict:
        self._require_ready()
        return stats_mod.collect(self._store, target_id, since_ts, until_ts, cursor)

    def resolve_display_names(self, keys: List[str]) -> Dict[str, str]:
        self._require_ready()
        return stats_mod.resolve_display_names(self._store, keys)

    @property
    def self_wxid(self) -> str:
        """本机登录账号的 wxid；统计"我发了多少条"时用它做键，避免靠显示名猜。"""
        return str(self._store.self_wxid) if self._store is not None else ""

    # ---------- 朋友圈导出（C5） ----------

    def sns_export_create(self, payload: Dict) -> Dict:
        self._require_ready()
        return self._exports.create(payload or {})

    def sns_export_status(self, export_id: str) -> Dict:
        self._require_ready()
        return self._exports.status(export_id)

    def sns_export_download(self, export_id: str):
        """返回 `(内容, content-type, 响应头)` —— 与 `WeChatAdapter.sns_export_download` 同形。"""
        self._require_ready()
        return self._exports.download(export_id)

    def _require_ready(self) -> None:
        if not self.ready:
            raise StoreError(self._init_error or "本地数据链路尚未就绪")
        if not self._store.mirror("session.db").exists:
            raise StoreError("镜像尚未建立，请稍后重试")

    # ---------- 状态透出 ----------

    def status(self) -> Dict:
        mirrors = {}
        if self._store is not None:
            # 动态列出全部 message 分片：写死 message_0.db 会让多分片机器上的状态看着"正常"，
            # 而新分片其实从未被同步 —— 那正是"会话列表新、消息停在旧日期"的现场。
            for name in ["session.db", "contact.db", "sns.db"] + self._store.message_shards():
                try:
                    mirrors[name] = self._store.mirror(name).mirror_path
                except Exception:
                    mirrors[name] = ""
        if self._init_error:
            state = "error"
        elif self.ready and self._sync_count > 0:
            state = "ready"
        else:
            state = "init"
        return {
            "state": state,
            "ready": self.ready,
            "account": self.account,
            "storage_path": self._store.storage_path if self._store else "",
            "mirror_root": self.mirror_root,
            "mirrors": mirrors,
            "initError": self._init_error,
            "syncError": self._sync_error,
            "syncCount": self._sync_count,
            "lastSyncAt": self._last_sync_at,
            "lastSyncAgoSeconds": round(time.time() - self._last_sync_at, 2) if self._last_sync_at else None,
            "lastStats": self._last_stats,
        }
