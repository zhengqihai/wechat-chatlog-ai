# -*- coding: utf-8 -*-
"""增量解密镜像：把「加密主库 + 它的 WAL」维护成一份**始终较新**的明文 SQLite 副本。

为什么要镜像而不是每次现解：主库 157MB，全量解密 0.9s、还要重写 157MB；
而实测主库每 50–60 秒才 checkpoint 一次、WAL 每 5 秒只多出 6–30 帧 ——
真正变化的只有几个页。镜像把「每轮成本」从「整库」降到「几个页」。

两条互补路径（设计权衡见 docs/SELFHOST_DATA_PLAN.md §4 阶段 B）：

    正常路径  每轮只读 WAL 的**新帧**（index > 上轮已处理），解密后覆盖镜像 → 成本≈0
    恢复路径  索引为空（首次/重启）或 WAL 被重置时，顺扫主库密文算**页指纹**，
              只把真正变化的页解出来覆盖 → 只读不写，不产生"每分钟重写 200MB"

顺序上有讲究：**先补主库、后写 WAL**。checkpoint 会把 WAL 内容写进主库，
所以同一页两边都有时，WAL 的版本更新（或相同），最后写 WAL 才不会被旧数据盖回去。
"""

from __future__ import annotations

import os
import threading
import zlib
from array import array
from typing import Dict, Optional

from . import cipher, wal


class MirrorError(RuntimeError):
    """镜像无法同步：主库损坏、密钥不匹配或磁盘写入失败。"""


class DbMirror:
    """单个数据库的增量解密镜像。

    `lock` 供读取层（阶段 C）与写入互斥：镜像会被原地改写，
    读方必须持锁查询，否则可能读到「写了一半」的页。
    """

    def __init__(self, db_path: str, mirror_path: str, passphrase: bytes):
        self.db_path = db_path
        self.mirror_path = mirror_path
        self.wal_path = db_path + "-wal"
        self._passphrase = passphrase
        self.lock = threading.RLock()

        self._salt: Optional[bytes] = None
        self._enc_key: Optional[bytes] = None
        self._mac_key: Optional[bytes] = None
        self._fps = array("I")          # 主库密文页指纹，下标 = 页号（0 位不用）
        self._wal_salt: Optional[bytes] = None
        self._wal_last_index = 0

    # ---------- 对外 ----------

    @property
    def exists(self) -> bool:
        return os.path.exists(self.mirror_path)

    def sync(self) -> Dict:
        """同步一次，返回本轮统计（便于日志与排障）。失败抛 MirrorError / cipher.CipherError。"""
        stats = {
            "scan": False,          # 本轮是否扫了主库
            "scan_reason": "",
            "db_pages": 0,
            "changed_pages": 0,
            "wal_frames": 0,
            "wal_applied": 0,
            "wal_reset": False,
        }
        with self.lock:
            self._load_keys()
            mirror = self._open_mirror()
            try:
                snap = wal.read_wal(self.wal_path, mac_key=self._mac_key)

                # WAL 的 salt 变了 = 期间发生过 checkpoint + 重置，可能漏帧 → 必须回主库对账。
                wal_reset = self._wal_salt is not None and snap.salt != self._wal_salt
                stats["wal_reset"] = wal_reset

                if not self._fps:
                    stats["scan_reason"] = "首次同步（建立页指纹）"
                elif wal_reset:
                    stats["scan_reason"] = "WAL 已重置，回主库对账"
                if stats["scan_reason"]:
                    stats["scan"] = True
                    self._scan_main(mirror, stats)
                # else 分支**刻意什么都不做**：主库页数可能比镜像小（镜像里有 WAL 撑出来的页），
                # 按主库页数去 truncate 会把刚拿到的 WAL 扩展页砍掉，产出"页头与文件长度不符"的坏库。

                # 先补完主库，再落 WAL：WAL 的版本更新，必须最后写。
                if wal_reset:
                    self._wal_last_index = 0
                stats["wal_frames"] = len(snap.frames)
                for frame in snap.frames:
                    if frame.index <= self._wal_last_index:
                        continue
                    plain = cipher.decrypt_page(frame.page, frame.page_no, self._enc_key)
                    self._write_page(mirror, frame.page_no, plain)
                    stats["wal_applied"] += 1
                if snap.frames:
                    self._wal_last_index = max(self._wal_last_index, snap.frames[-1].index)
                if snap.salt:
                    self._wal_salt = snap.salt
                # commit 记录里的 db_size 是"提交后数据库应有几页"，**是权威值**：
                # 它既可能比主库大（WAL 撑出来的新页），也可能比主库小（截断类事务）。只在有提交时才对齐。
                if snap.has_commit:
                    self._resize(mirror, snap.committed_db_size)
            finally:
                mirror.flush()
                mirror.close()
        return stats

    # ---------- 内部 ----------

    def _load_keys(self) -> None:
        """读主库前 16 字节取 salt，派生 enc/mac 两个密钥。salt 变化说明库被换过，索引随之作废。"""
        with open(self.db_path, "rb") as fp:
            salt = fp.read(16)
        if len(salt) != 16:
            raise MirrorError("%s 不足 16 字节" % self.db_path)
        if salt == self._salt:
            return
        self._salt = salt
        self._enc_key = cipher.derive_enc_key(self._passphrase, salt)
        self._mac_key = cipher.derive_mac_key(self._enc_key, salt)
        self._fps = array("I")          # 换库 → 旧指纹全部失效

    def _open_mirror(self):
        parent = os.path.dirname(self.mirror_path)
        if parent:
            os.makedirs(parent, exist_ok=True)
        mode = "r+b" if os.path.exists(self.mirror_path) else "w+b"
        return open(self.mirror_path, mode)

    def _resize(self, mirror, pages: int) -> None:
        """把镜像对齐到指定页数。只在**权威信息**给出时才调用（主库扫描结果 / WAL commit 记录）。"""
        want = pages * cipher.PAGE_SIZE
        if want > 0 and os.path.getsize(self.mirror_path) != want:
            mirror.truncate(want)

    def _write_page(self, mirror, page_no: int, plain: bytes) -> None:
        mirror.seek((page_no - 1) * cipher.PAGE_SIZE)
        mirror.write(plain)

    def _scan_main(self, mirror, stats: Dict) -> None:
        """顺扫主库密文，按页指纹找出真正变化的页并解密覆盖。

        指纹索引**先在本轮局部构建、扫完才提交**：如果中途出错（磁盘满、进程被杀），
        索引不会领先于镜像，下一次同步仍能发现这些页 —— 宁可多做一次，不可漏一次。
        """
        total = os.path.getsize(self.db_path) // cipher.PAGE_SIZE
        if total == 0:
            raise MirrorError("%s 不足一页" % self.db_path)
        stats["db_pages"] = total
        new_fps = array("I", [0]) * (total + 1)
        changed = 0
        old = self._fps
        with open(self.db_path, "rb") as fp:
            for page_no in range(1, total + 1):
                page = fp.read(cipher.PAGE_SIZE)
                if len(page) < cipher.PAGE_SIZE:
                    break
                fingerprint = zlib.crc32(page) & 0xFFFFFFFF
                new_fps[page_no] = fingerprint
                if page_no < len(old) and old[page_no] == fingerprint:
                    continue
                plain = cipher.decrypt_page(page, page_no, self._enc_key)
                if page_no == 1 and not cipher.looks_like_sqlite_header(plain):
                    raise cipher.CipherError("第 1 页页头校验失败：密钥不匹配或参数已变化")
                self._write_page(mirror, page_no, plain)
                changed += 1
        if total * cipher.PAGE_SIZE != os.path.getsize(self.mirror_path):
            self._resize(mirror, total)
        self._fps = new_fps
        stats["changed_pages"] = changed
