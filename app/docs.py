# -*- coding: utf-8 -*-
"""知识文档存储（T-F4）：`DataDir/docs/` 下存文件，`index.json` 存元数据。

与 `config/`、`localdb/` 同级，是「本服务自己产生、可写」的数据目录。

设计约束：
  - 文件命名 `<id>.<原始扩展名>`，`id = doc-<10 位 hex>`（与 provider / scheduled 同构）；
  - 单文件 ≤ 4MB，上传时校验，超限直接拒绝（不落半成品）；
  - 允许的格式：md / txt / html / json（其余一律 400）。
"""

from __future__ import annotations

import json
import os
import re
import threading
import time
from typing import Dict, List, Tuple

# 单文件上限（字节）。
MAX_FILE_BYTES = 4 * 1024 * 1024
# 允许的扩展名（不含点，小写）。
ALLOWED_EXTENSIONS = ("md", "txt", "html", "json")

_ID_RE = re.compile(r"^doc-[0-9a-f]{10}$")


class DocsError(RuntimeError):
    """文档操作失败（参数非法 / 文件不存在 / 磁盘问题）。"""


class DocsStore:
    """文档目录的登记、读写与清理。"""

    def __init__(self, docs_dir: str):
        self.docs_dir = docs_dir
        self.index_path = os.path.join(docs_dir, "index.json")
        self._lock = threading.Lock()
        os.makedirs(docs_dir, exist_ok=True)

    # ---------- 元数据 ----------

    def _load_index(self) -> List[Dict]:
        try:
            with open(self.index_path, "r", encoding="utf-8") as handle:
                data = json.load(handle)
            if isinstance(data, list):
                return [item for item in data if isinstance(item, dict)]
        except (OSError, ValueError):
            pass
        return []

    def _save_index(self, items: List[Dict]) -> None:
        tmp = self.index_path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as handle:
            json.dump(items, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
        os.replace(tmp, self.index_path)

    # ---------- 对外 ----------

    def list(self) -> List[Dict]:
        """文档列表（id / filename / size / uploaded_at），按上传时间正序。"""
        with self._lock:
            items = self._load_index()
        # 元数据与磁盘对齐：文件被外部删掉的条目不再列出（不静默保留幽灵条目）。
        return [item for item in items if os.path.exists(self._path(item["id"], item.get("filename") or ""))]

    def create(self, filename: str, content: str, overwrite: bool = False) -> Dict:
        """新增或覆盖文档，返回元数据。超限 / 非法格式抛 DocsError。

        `overwrite=True` 时按文件名（忽略大小写）查找已有文档：存在则原地覆盖并沿用原 id，
        保证已经关联的 doc_ids 不失效；不存在则当作新增。
        """
        name = (filename or "").strip()
        ext = os.path.splitext(name)[1].lstrip(".").lower() if name else ""
        if ext not in ALLOWED_EXTENSIONS:
            raise DocsError("仅支持 md / txt / html / json 格式")
        if not name or name != os.path.basename(name):
            raise DocsError("文件名不合法")

        raw = content.encode("utf-8")
        if len(raw) > MAX_FILE_BYTES:
            raise DocsError("文档超过 4MB 上限")

        with self._lock:
            items = self._load_index()
            existing = next(
                (it for it in items if (it.get("filename") or "").lower() == name.lower()),
                None,
            )
            if overwrite and existing is not None:
                doc_id = existing["id"]
                path = self._path(doc_id, name)
                with open(path, "w", encoding="utf-8") as handle:
                    handle.write(content)
                existing["filename"] = name
                existing["size"] = len(raw)
                existing["uploaded_at"] = int(time.time())
                self._save_index(items)
                return dict(existing)

            doc_id = "doc-" + os.urandom(5).hex()
            path = self._path(doc_id, name)
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(content)
            item = {
                "id": doc_id,
                "filename": name,
                "size": len(raw),
                "uploaded_at": int(time.time()),
            }
            items.append(item)
            self._save_index(items)
        return item

    def read(self, doc_id: str) -> Tuple[str, str]:
        """读单个文档内容，返回 (content, filename)。"""
        if not _ID_RE.match(doc_id or ""):
            raise DocsError("文档 id 不合法")
        with self._lock:
            items = self._load_index()
        item = next((it for it in items if it.get("id") == doc_id), None)
        if item is None:
            raise DocsError("文档不存在：%s" % doc_id)
        path = self._path(doc_id, item.get("filename") or "")
        if not os.path.exists(path):
            raise DocsError("文档文件已丢失：%s" % doc_id)
        with open(path, "r", encoding="utf-8") as handle:
            return handle.read(), item.get("filename") or ""

    def delete(self, doc_id: str) -> None:
        """删除文档（文件 + 元数据）。不存在视为成功（幂等）。"""
        if not _ID_RE.match(doc_id or ""):
            raise DocsError("文档 id 不合法")
        with self._lock:
            items = self._load_index()
            item = next((it for it in items if it.get("id") == doc_id), None)
            if item is None:
                return
            path = self._path(doc_id, item.get("filename") or "")
            try:
                if os.path.exists(path):
                    os.remove(path)
            except OSError as exc:
                raise DocsError("删除文档文件失败：%s" % exc)
            items = [it for it in items if it.get("id") != doc_id]
            self._save_index(items)

    def load_docs_content(self, doc_ids: List[str], max_bytes: int = MAX_FILE_BYTES) -> Tuple[str, bool]:
        """读取若干文档内容并拼接，超 max_bytes 截断。返回 (文本, 是否被截断)。

        缺失的 doc_id 静默跳过（文档可能被删）；累计超过 max_bytes 时截断到上限并标记 truncated。
        """
        chunks: List[str] = []
        used = 0
        truncated = False
        for doc_id in (doc_ids or []):
            try:
                content, filename = self.read(doc_id)
            except DocsError:
                continue
            block = "【%s】\n%s" % (filename, content)
            size = len(block.encode("utf-8"))
            if used + size > max_bytes:
                remaining = max_bytes - used
                if remaining <= 0:
                    truncated = True
                    break
                block_bytes = block.encode("utf-8")[:remaining]
                chunks.append(block_bytes.decode("utf-8", "ignore"))
                used = max_bytes
                truncated = True
                break
            chunks.append(block)
            used += size
        return "\n\n".join(chunks), truncated

    # ---------- 内部 ----------

    def _path(self, doc_id: str, filename: str) -> str:
        """文件存储名固定为 `<id>.<扩展名>`，避免原始文件名里的特殊字符污染目录。"""
        ext = os.path.splitext(filename or "")[1].lstrip(".").lower()
        if not ext:
            ext = "txt"
        return os.path.join(self.docs_dir, "%s.%s" % (doc_id, ext))
