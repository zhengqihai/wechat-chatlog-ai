# -*- coding: utf-8 -*-
"""微信 4.x（Weixin 4.1.x）本地数据库的页级解密。

参数来源与验证过程见 docs/SELFHOST_DATA_PLAN.md §2.1；已在本机 19 个库上逐页校验通过。

与标准 SQLCipher 的两处差异（本模块存在的理由）：
  1. **reserve = 80**：每页尾部是 `IV(16) + HMAC(64)`，IV 直接存在页里，
     而不是按页号从 MAC key 派生 —— 这使「按页独立解密」成立，增量刷新才成为可能。
  2. **第 1 页前 16 字节是 salt**，加密只覆盖 `[16, 页尾-80)`；
     因此解密后必须把 `SQLite format 3\\0` 补回前 16 字节，否则不是合法 SQLite 文件。

密钥层级（三个不同的东西，最容易在这里出错，务必分清）：

    passphrase（账号级，64 位十六进制字符串）
      └─ PBKDF2-HMAC-SHA512(passphrase, salt, 256000, 32)      → enc_key   （AES-256-CBC 用）
           └─ PBKDF2-HMAC-SHA512(enc_key, salt ^ 0x3A, 2, 32)  → mac_key   （HMAC 校验用）

    salt 是**每个库文件各自独立**的（取文件前 16 字节）；passphrase 是账号级的，全库共用。
"""

from __future__ import annotations

import hashlib
import hmac as _hmac
import os
import struct
from typing import Tuple

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

# ---------- 加密参数（改动前必须重跑 docs/SELFHOST_DATA_PLAN.md §2.2 的校验） ----------

PAGE_SIZE = 4096
IV_SIZE = 16
HMAC_SIZE = 64
RESERVE = IV_SIZE + HMAC_SIZE          # 80
KDF_ITERATIONS = 256000
PAYLOAD_SIZE = PAGE_SIZE - RESERVE      # 4016：非首页可加密的字节数
PAGE1_PAYLOAD_SIZE = PAGE_SIZE - RESERVE - IV_SIZE   # 4000：首页被 salt 占掉 16 字节

SQLITE_MAGIC = b"SQLite format 3\x00"
MAC_SALT_XOR = 0x3A

# 明文页尾的 80 字节填充。明文头里声明了 `reserved = 80`，所以每页必须仍是 4096 字节；
# 这 80 字节在原文件里放的是 IV + HMAC，解密后**必须清零**（已与上游产物逐字节比对确认）。
RESERVED_PADDING = b"\x00" * RESERVE

# 解密成功的判据：页头自洽。这比"没抛异常"严格得多，能挡住密钥错误与参数漂移。
EXPECTED_PAGE_FLAGS = b"\x02\x02"       # 写/读版本：2 = WAL
EXPECTED_RESERVED = RESERVE


class CipherError(RuntimeError):
    """页解密失败：密钥不匹配、加密参数漂移或文件损坏。"""


# ---------- 密钥派生 ----------

def derive_enc_key(passphrase: bytes, salt: bytes) -> bytes:
    """passphrase → 该库专属的 32 字节 AES 密钥。"""
    if len(salt) != 16:
        raise CipherError("salt 必须是 16 字节，实际 %d" % len(salt))
    return hashlib.pbkdf2_hmac("sha512", passphrase, salt, KDF_ITERATIONS, 32)


def derive_mac_key(enc_key: bytes, salt: bytes) -> bytes:
    """enc_key → MAC 密钥。注意 salt 要先异或 0x3A，且只迭代 2 轮（SQLCipher 老方案）。"""
    mac_salt = bytes(b ^ MAC_SALT_XOR for b in salt)
    return hashlib.pbkdf2_hmac("sha512", enc_key, mac_salt, 2, 32)


# ---------- 页布局 ----------

def cipher_span(page_no: int) -> Tuple[int, int]:
    """返回该页「密文区间」的 (起点, 终点)。第 1 页被 salt 占掉前 16 字节。

    密文终点固定为 `PAGE_SIZE - RESERVE`；末尾 80 字节是 IV(16) + HMAC(64)，不参与加密。
    """
    start = IV_SIZE if page_no == 1 else 0
    return start, PAGE_SIZE - RESERVE


def page_iv(page: bytes) -> bytes:
    """IV 就存在页尾 80 字节的最前面 16 字节里。"""
    return page[PAGE_SIZE - RESERVE:PAGE_SIZE - RESERVE + IV_SIZE]


def page_stored_hmac(page: bytes) -> bytes:
    return page[PAGE_SIZE - HMAC_SIZE:PAGE_SIZE]


# ---------- 单页解密 ----------

def decrypt_page(page: bytes, page_no: int, enc_key: bytes) -> bytes:
    """解密一整页，返回 **4096 字节**明文页（第 1 页把 SQLite 魔数补回前 16 字节）。

    结构：`明文载荷(4000/4016) + 80 字节零填充`。零填充不是可有可无的 ——
    明文头声明了 reserved=80，页长必须是 4096；少了这 80 字节产出文件就与上游不一致
    （2026-09-30 已用 weclaw.db 逐字节定位到这一处差异）。

    只依赖本页自身，不依赖任何跨页状态 —— 这是增量刷新的前提。
    """
    if len(page) != PAGE_SIZE:
        raise CipherError("页长度必须是 %d，实际 %d" % (PAGE_SIZE, len(page)))
    start, end = cipher_span(page_no)
    decryptor = Cipher(algorithms.AES(enc_key), modes.CBC(page_iv(page))).decryptor()
    payload = decryptor.update(page[start:end]) + decryptor.finalize()
    if page_no == 1:
        # 前 16 字节在文件里是 salt，明文里应是魔数，需要补回。
        return SQLITE_MAGIC + payload + RESERVED_PADDING
    return payload + RESERVED_PADDING


def looks_like_sqlite_header(page: bytes) -> bool:
    """用页头自洽性判断解密是否正确（魔数 + 页大小 + 版本 + reserved + 载荷分数）。

    注意偏移：第 1 页前 16 字节是魔数，页大小字段从**第 16 字节**开始算起；
    而加密只覆盖 [16, 页尾-80)，所以解密出来的是「页内容」而非整页。
    """
    if len(page) < 24:
        return False
    return (
        page[0:16] == SQLITE_MAGIC
        and page[16:18] == b"\x10\x00"                 # 页大小 4096（大端）
        and page[18:20] == EXPECTED_PAGE_FLAGS         # 写/读版本 2（WAL）
        and page[20] == EXPECTED_RESERVED              # reserved = 80
        and page[21:24] == b"\x40\x20\x20"             # 载荷分数 64 / 32 / 32
    )


# ---------- 页完整性与 HMAC 校验 ----------

def page_hmac(page: bytes, page_no: int, mac_key: bytes) -> bytes:
    """计算该页应有的 HMAC-SHA512。

    计算范围 = **密文 ‖ IV ‖ 页号(小端 4 字节)**。
    这个范围是在 weclaw.db 第 1 页、message_0.db 第 2 页、contact.db 第 5 页上
    对 9 种候选组合做唯一性测定得出的（2026-09-30），不是猜的。
    """
    start, end = cipher_span(page_no)
    message = page[start:end] + page_iv(page) + struct.pack("<I", page_no)
    return _hmac.new(mac_key, message, hashlib.sha512).digest()


def verify_page(page: bytes, page_no: int, mac_key: bytes) -> bool:
    """校验一页是否完整。

    主库的整库解密不依赖它（第 1 页页头自检已经足够严格），它的真正用途是
    **WAL 帧**：微信正在写入时，WAL 末尾可能出现"写了一半"的帧，
    这种帧的 HMAC 必然对不上，必须丢弃 —— 这是 B3 的正确性基石。
    """
    if len(page) != PAGE_SIZE:
        return False
    return _hmac.compare_digest(page_hmac(page, page_no, mac_key), page_stored_hmac(page))


def decrypt_first_page(blob: bytes, passphrase: bytes) -> Tuple[bytes, bytes]:
    """从文件头部（至少 4096 字节）解出第 1 页明文，返回 (明文页, salt)。

    这是**启动自检**的实现：密钥错、参数漂移、文件损坏，都会在这里失败，
    而不是像上游那样静默降级、还返回 HTTP 200。
    """
    if len(blob) < PAGE_SIZE:
        raise CipherError("文件不足一页，无法校验")
    if blob[:16] == SQLITE_MAGIC:
        raise CipherError("该文件已是明文 SQLite，无需解密")
    salt = blob[:16]
    enc_key = derive_enc_key(passphrase, salt)
    plain = decrypt_page(blob[:PAGE_SIZE], 1, enc_key)
    if not looks_like_sqlite_header(plain):
        raise CipherError("第 1 页页头校验失败：密钥不匹配或加密参数已变化")
    return plain, salt


# ---------- 整库解密（B1 的基线实现，B4 会在此之上做增量） ----------

def is_plaintext_database(path: str) -> bool:
    """已解密过的库直接跳过，避免重复劳动。"""
    try:
        with open(path, "rb") as fp:
            return fp.read(16) == SQLITE_MAGIC
    except OSError:
        return False


def decrypt_database(src_path: str, dst_path: str, passphrase: bytes) -> dict:
    """把整个加密库解密到 `dst_path`（原子写：先写 .tmp 再替换）。

    返回统计信息；第 1 页会做页头自检，失败即抛 CipherError，不产出半成品文件。
    """
    total_pages = os.path.getsize(src_path) // PAGE_SIZE
    if total_pages == 0:
        raise CipherError("%s 不足一页" % src_path)

    tmp_path = dst_path + ".tmp"
    enc_key = None
    written = 0
    try:
        with open(src_path, "rb") as fin, open(tmp_path, "wb") as fout:
            for page_no in range(1, total_pages + 1):
                page = fin.read(PAGE_SIZE)
                if len(page) < PAGE_SIZE:
                    break
                if enc_key is None:
                    salt = page[:16]
                    enc_key = derive_enc_key(passphrase, salt)
                plain = decrypt_page(page, page_no, enc_key)
                if page_no == 1 and not looks_like_sqlite_header(plain):
                    raise CipherError("第 1 页页头校验失败：密钥不匹配或参数已变化")
                fout.write(plain)
                written += 1
        os.replace(tmp_path, dst_path)
    except BaseException:
        # 失败不留半成品：增量引擎靠"镜像是否完整"决定能否复用。
        try:
            os.remove(tmp_path)
        except OSError:
            pass
        raise
    return {"pages": written, "bytes": written * PAGE_SIZE}
