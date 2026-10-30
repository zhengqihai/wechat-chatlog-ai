# -*- coding: utf-8 -*-
"""SQLite WAL（Write-Ahead Log）解析 —— 自研链路的「新鲜度」来源。

为什么需要它：微信是 WAL 模式，**新消息先写进 `-wal`，满了才 checkpoint 回主库**。
只读主库会滞后（这正是上游"全量解密"方案的硬伤）；解析 WAL 就能拿到秒级、甚至亚秒级的最新页。

帧格式（SQLite 官方定义，本机实测吻合）：

    WAL 头 32 字节
      0..3   magic（0x377F0682 = 校验和按小端解释，0x377F0683 = 大端）
      4..7   file format version（3007000）
      8..11  page size
      12..15 checkpoint sequence number
      16..23 salt-1 / salt-2（每次 WAL 重置都会变，是判断"帧属于本代"的钥匙）
      24..31 头部校验和

    帧 = 24 字节帧头 + page_size 字节页镜像（**加密页，与主库同一套方案**）
      0..3   页号（**大端**）
      4..7   commit 记录：提交后数据库的页数；非 commit 记录为 0
      8..15  salt-1 / salt-2（必须与 WAL 头一致，否则是上一代残留）
      16..23 校验和（跨帧滚动累积）

两道闸门，各有各的职责，都不能省：
  1. **SQLite 滚动校验和**：判断这一帧是否"写完整"（撕裂写、上一代残留）。
  2. **SQLCipher 页 HMAC**：判断页镜像的"内容 + 页号 + 密钥"是否自洽 —— 比校验和更强，
     因为 HMAC 的输入里绑定了页号。

提交语义：**只有到最后一条 commit 帧（db_size != 0）为止的内容才算已提交**，
之后的帧一律丢弃 —— 这正是 SQLite 崩溃恢复的规则，不能自作主张。
"""

from __future__ import annotations

import os
import struct
from dataclasses import dataclass, field
from typing import Dict, List, Optional

from . import cipher

WAL_HEADER_SIZE = 32
FRAME_HEADER_SIZE = 24
WAL_MAGIC_LE = 0x377F0682
WAL_MAGIC_BE = 0x377F0683
WAL_VERSION = 3007000


class WalError(RuntimeError):
    """WAL 不可解析：头部损坏、格式不符或正在被重置。"""


@dataclass
class WalFrame:
    """一条已通过校验的帧。`page` 是**加密页镜像**，交给 cipher.decrypt_page 解。"""

    index: int          # 帧序号（从 1 开始）
    page_no: int        # 页号（从 1 开始）
    db_size: int        # commit 记录里"提交后数据库页数"；非 commit 帧为 0
    page: bytes         # 4096 字节加密页镜像


@dataclass
class WalSnapshot:
    """一次 WAL 读取的完整结果（全部字段都是**已提交**的）。"""

    page_size: int = cipher.PAGE_SIZE
    salt: bytes = b""
    checkpoint_seq: int = 0
    frames: List[WalFrame] = field(default_factory=list)
    committed_db_size: int = 0      # 最后一个 commit 记录声明的页数；0 表示没有提交
    total_frames_seen: int = 0      # 校验通过并纳入统计的帧数（含未提交）
    stopped_reason: str = ""        # 提前结束的原因，便于排障

    @property
    def has_commit(self) -> bool:
        return self.committed_db_size > 0

    def page_overrides(self) -> Dict[int, bytes]:
        """页号 → 加密页镜像。**后出现的帧覆盖先出现的**（WAL 的语义）。"""
        overrides: Dict[int, bytes] = {}
        for frame in self.frames:
            overrides[frame.page_no] = frame.page
        return overrides


def wal_checksum(data: bytes, s0: int, s1: int) -> tuple:
    """SQLite WAL 滚动校验和。

    按 8 字节分块、以**本机字节序**（x86 是小端）取值累加。
    已在本机 WAL 上验证：头部与逐帧校验和都与存储值完全吻合。
    """
    for offset in range(0, len(data) - (len(data) % 8), 8):
        word0, word1 = struct.unpack("<II", data[offset:offset + 8])
        s0 = (s0 + word0 + s1) & 0xFFFFFFFF
        s1 = (s1 + word1 + s0) & 0xFFFFFFFF
    return s0, s1


def read_wal(path: str, mac_key: Optional[bytes] = None) -> WalSnapshot:
    """读取并校验 WAL，只返回**已提交**的帧。

    `mac_key` 传入时会额外做页 HMAC 校验（推荐）；为 None 时只依赖滚动校验和。
    文件不存在返回空快照（没有 WAL 是正常状态，不是错误）。
    """
    if not os.path.exists(path):
        return WalSnapshot(stopped_reason="WAL 不存在")

    with open(path, "rb") as fp:
        blob = fp.read()

    if len(blob) < WAL_HEADER_SIZE:
        raise WalError("WAL 头部不足 32 字节")

    magic, version, page_size, checkpoint_seq = struct.unpack(">IIII", blob[0:16])
    salt = blob[16:24]
    head_cksum = struct.unpack(">II", blob[24:32])

    if magic not in (WAL_MAGIC_LE, WAL_MAGIC_BE):
        raise WalError("WAL magic 非法：0x%08X" % magic)
    if version != WAL_VERSION:
        raise WalError("WAL 版本不符：%d" % version)
    if page_size != cipher.PAGE_SIZE:
        raise WalError("WAL 页大小 %d 与本项目假定值 %d 不符" % (page_size, cipher.PAGE_SIZE))

    # 头部自身也要过校验：失败说明正读到"文件正在被重建"的瞬间，本次放弃。
    if wal_checksum(blob[0:24], 0, 0) != head_cksum:
        raise WalError("WAL 头部校验和不匹配（可能正在被重置）")

    frame_size = FRAME_HEADER_SIZE + page_size
    snapshot = WalSnapshot(page_size=page_size, salt=salt, checkpoint_seq=checkpoint_seq)
    running = head_cksum            # 校验和是跨帧累积的，起点是头部校验和
    raw_frames: List[WalFrame] = []
    last_commit_at = -1

    index = 1
    while True:
        offset = WAL_HEADER_SIZE + (index - 1) * frame_size
        if offset + frame_size > len(blob):
            snapshot.stopped_reason = "帧区已到文件尾"
            break
        header = blob[offset:offset + FRAME_HEADER_SIZE]
        page = blob[offset + FRAME_HEADER_SIZE:offset + frame_size]

        page_no, db_size, frame_salt = struct.unpack(">III", header[0:12])
        frame_cksum = struct.unpack(">II", header[16:24])

        # ① salt 不符 = 已进入上一代残留（WAL 被重置过），到此为止。
        if header[8:16] != salt:
            snapshot.stopped_reason = "salt 不符（WAL 已重置，第 %d 帧起为上一代残留）" % index
            break

        # ② 滚动校验和：判断这一帧是否写完整。
        s0, s1 = wal_checksum(header[0:8], running[0], running[1])
        s0, s1 = wal_checksum(page, s0, s1)
        if (s0, s1) != frame_cksum:
            snapshot.stopped_reason = "第 %d 帧校验和不匹配（疑似撕裂写）" % index
            break

        # ③ 页 HMAC：内容 / 页号 / 密钥三重自洽。比校验和更强。
        if mac_key is not None and not cipher.verify_page(page, page_no, mac_key):
            snapshot.stopped_reason = "第 %d 帧页 HMAC 校验失败" % index
            break

        raw_frames.append(WalFrame(index=index, page_no=page_no, db_size=db_size, page=page))
        if db_size != 0:
            last_commit_at = index
        running = (s0, s1)
        index += 1

    snapshot.total_frames_seen = len(raw_frames)
    if last_commit_at > 0:
        # 只认到最后一条 commit 帧；之后的帧属于"事务未提交"，SQLite 恢复时同样丢弃。
        snapshot.frames = [f for f in raw_frames if f.index <= last_commit_at]
        snapshot.committed_db_size = next(
            f.db_size for f in reversed(snapshot.frames) if f.db_size != 0
        )
    return snapshot
