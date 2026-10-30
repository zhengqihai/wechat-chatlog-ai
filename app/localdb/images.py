# -*- coding: utf-8 -*-
"""C6：本地图片（V2 `.dat`）的定位与解密。

图片本体与消息库（SQLCipher 4 / AES-256-CBC + 页尾 IV）**不是同一套加密**。
图片是「V2 容器」：`header(15B) + AES-128-ECB 密文 + 单字节 XOR 尾部`。

调研结论与实测证据见 `docs/C6_IMAGE_INVESTIGATION.md` §3：

    header(15B) = magic(6B `07 08 56 32 08 07`) + aes_size(4B LE) + xor_size(4B LE) + flag(1B)
    AES-128-ECB 密文：从 offset 15 起，共 aes_size 字节，key = image_aes_key 的 16 字节 ASCII 原文
    单字节 XOR 尾部：剩余全部字节，与 image_xor_key 逐字节异或

已在本机真实 `.dat` 上整图解密成功（解出完整 JPEG，见报告 §3）。

**定位键**（报告 §2）：图片 hash（32 位 hex）来自消息表 `packed_info_data`（protobuf 内嵌的
长度分隔字段），它是原图 `.dat`、缩略图 `_t.dat`、HD `_h.dat` 三者的**共同文件名前缀**：

    msg/attach/<md5(target_id)>/<YYYY-MM>/Img/<hash>.dat        （原图）
    msg/attach/<md5(target_id)>/<YYYY-MM>/Img/<hash>_t.dat      （缩略图）
    msg/attach/<md5(target_id)>/<YYYY-MM>/Img/<hash>_h.dat      （HD）
"""

from __future__ import annotations

import hashlib
import os
import re
from datetime import datetime

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

# V2 容器 magic。V1 是 `07 08 56 31 08 07`（固定密钥），V2 是 `07 08 56 32 08 07`（账号级密钥）。
V2_MAGIC = b"\x07\x08\x56\x32\x08\x07"
_HEADER_SIZE = 15

# 图片 hash = 32 位 hex，是 packed_info_data 里唯一可识别的长 hex 串。
_HASH_RE = re.compile(rb"[0-9a-f]{32}")

# 图片类型 → 文件名后缀。kind 由接口层指定，默认缩略图（聊天框列表显示的就是缩略图）。
_KIND_SUFFIX = {
    "thumb": "_t",
    "orig": "",
    "hd": "_h",
}


class ImageError(RuntimeError):
    """图片定位或解密失败。"""


# 常见图片魔数 → Content-Type。只认足够长的魔数，避免 2 字节的 `BM` 在误判时满天飞。
_MIME_BY_MAGIC = (
    (b"\xff\xd8\xff", "image/jpeg"),
    (b"\x89PNG", "image/png"),
    (b"GIF8", "image/gif"),
    (b"RIFF", "image/webp"),
)


def extract_image_hash(packed_info_data) -> str:
    """从 `packed_info_data`（protobuf BLOB）提取图片 hash（32 位 hex），取不到返回空串。"""
    if not packed_info_data:
        return ""
    match = _HASH_RE.search(bytes(packed_info_data))
    return match.group(0).decode("ascii") if match else ""


def decrypt_v2(data: bytes, aes_key: bytes, xor_key: int) -> bytes:
    """解密 V2 格式的图片 `.dat`，返回完整图片字节。

    `aes_key` 必须是 16 字节（AES-128）。`xor_key` 是 0~255 的单字节异或密钥。
    """
    if len(data) < _HEADER_SIZE or data[:6] != V2_MAGIC:
        raise ImageError("不是 V2 图片格式：magic=%s" % data[:6].hex(" "))
    aes_size = int.from_bytes(data[6:10], "little")
    aes_ct = data[_HEADER_SIZE:_HEADER_SIZE + aes_size]
    if len(aes_ct) != aes_size:
        raise ImageError("AES 段长度不足：期望 %d 实际 %d" % (aes_size, len(aes_ct)))

    decryptor = Cipher(algorithms.AES(aes_key), modes.ECB()).decryptor()
    aes_pt = decryptor.update(aes_ct) + decryptor.finalize()

    rest = data[_HEADER_SIZE + aes_size:]
    xor_pt = bytes(byte ^ xor_key for byte in rest)
    return aes_pt + xor_pt


def content_type(plain: bytes) -> str:
    """按图片魔数判 Content-Type，判不出退回通用二进制。"""
    for magic, mime in _MIME_BY_MAGIC:
        if plain.startswith(magic):
            return mime
    return "application/octet-stream"


def locate_dat(wxid_dir: str, target_id: str, image_hash: str,
               create_time: int, kind: str = "thumb") -> str:
    """由会话、图片 hash 与消息时间定位 `.dat` 文件的绝对路径。

    `image_hash` 是 32 位 hex（来自 packed_info_data）；`create_time` 用于算归档月份目录。
    """
    suffix = _KIND_SUFFIX.get(kind)
    if suffix is None:
        raise ImageError("未知图片类型：%s" % kind)

    month = datetime.fromtimestamp(int(create_time)).strftime("%Y-%m")
    session_md5 = hashlib.md5(str(target_id).encode("utf-8")).hexdigest()
    filename = "%s%s.dat" % (image_hash, suffix)
    return os.path.join(wxid_dir, "msg", "attach", session_md5, month, "Img", filename)
