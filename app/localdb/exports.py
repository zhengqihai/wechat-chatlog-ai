# -*- coding: utf-8 -*-
"""C5：朋友圈导出（对应 `WeChatAdapter.sns_export_create` / `sns_export_status` / `sns_export_download`）。

**为什么"导出"只有朋友圈这一块需要自研**（见 `docs/SELFHOST_DATA_PLAN.md` §2.10）：

| 导出项 | 谁在做 | 依赖上游？ |
|---|---|---|
| 聊天导出 | 客户端：`/api/messages`（C2 已本地化）+ `ChatExporter` 排版 + 另存为 | 否 |
| 通讯录导出 | 客户端：`/api/contacts`（C3 已本地化）+ `ContactExporter` | 否 |
| **朋友圈导出** | **后端**：`create → 轮询 → download` 的异步任务 + ZIP | 是（唯一一处） |

`server.py` 里根本没有聊天 / 通讯录导出端点，那两块的排版逻辑在客户端 `ChatExporter` /
`ContactExporter` 里，走的是已经本地化的 `/api/messages` 与 `/api/contacts`。

**契约**（逐行读 `client/ChatlogAI.Client/ViewModels/MomentsExportViewModel.cs` 得到，不是猜的）：

    任务对象 = {exportId, status, zipReady, error,
                progress{postsTotal, postsExported, mediaPrepareTotal, mediaPrepared}}

客户端把 **`zipReady` 优先于 `status`** 当完成信号（`ResolveState`）—— 那是它刻意绕开上游
"status=done 但 ZIP 还在打包、此刻下载撞 409" 的设计，所以我们必须同时给这两个字段。

**两处已拍板的边界（不要"顺手改回去"）**：

1. **包里没有图片**。C6 未攻克：朋友圈图片在本地只有密文（`cache/<月>/Sns/Img/*` 实测 196/196），
   而 `.dat` 的命名键与加密算法都未知。因此正文用 `[图片 ×N]` 占位，并在 ZIP 里附一份
   `README.txt` 说明原因 —— 导出包是外发物，不能让接收方以为导出坏了。
2. **excel = UTF-8 BOM 的 CSV**（零新依赖）。客户端把产物整体当 ZIP 下载、不解析内部文件名，
   与上游的真 `.xlsx` 在兼容性上没有差别。
3. **导出内容里不含 `token` / `key`**。`media[].urlAttrs` 里那两串是**解密凭据**
   （§14.8.2 的后端代理设计就是刻意不下发它们）。导出文件会被转发、归档、外发，
   把凭据写进去等于泄露，所以正文只保留图片的**数量与尺寸**。
"""

from __future__ import annotations

import csv
import html
import io
import json
import os
import threading
import time
import zipfile
from datetime import datetime
from typing import Dict, List, Tuple

from . import moments as moments_mod
from .store import LocalStore, StoreError

SCOPES = ("selected", "all")
FORMATS = ("html", "json", "txt", "excel")

# 产物是"导出 → 立刻下载"的短命文件，但也要能撑住"关掉弹窗再回来下载"。
# 上限两个维度同时生效：时间（超 24h 删）+ 数量（留最近 50 个），避免磁盘无限增长。
ARCHIVE_TTL_SECONDS = 24 * 3600
MAX_KEPT_ARCHIVES = 50

# 正文文件名与 README 都用 ASCII：ZIP 内的中文名在部分解压工具上会乱码，
# 而"名字不是中文"只是不好看，"名字乱码"是打不开。
_INNER_NAMES = {
    "html": "moments.html",
    "json": "moments.json",
    "txt": "moments.txt",
    "excel": "moments.csv",
}


# ---------- 渲染 ----------

def _scope_text(scope: str, usernames: List[str], posts: List[Dict]) -> str:
    if scope == "all":
        return "全部发布人（%d 人 / %d 条）" % (len({p["username"] for p in posts}), len(posts))
    return "指定发布人（%d 人 / %d 条）：%s" % (
        len(usernames), len(posts), "、".join(usernames))


def _post_summary(post: Dict) -> str:
    """点赞 / 评论压成一行，供 txt / csv / html 复用（不编造字段，只取已有的）。"""
    likes = "、".join((item.get("nickname") or item.get("username") or "")
                      for item in post.get("likes") or [])
    comments = "；".join("%s：%s" % (item.get("nickname") or item.get("username") or "",
                                    item.get("content") or "")
                         for item in post.get("comments") or [])
    return likes, comments


def _media_brief(post: Dict) -> List[Dict]:
    """导出用的媒体摘要：**只有类型与尺寸，没有 token / key / URL**（见模块 docstring 第 3 条）。"""
    brief = []
    for item in post.get("media") or []:
        size = item.get("size") or {}
        brief.append({
            "type": item.get("type") or "",
            "width": int(size.get("width") or 0),
            "height": int(size.get("height") or 0),
        })
    return brief


def _render_json(posts: List[Dict], meta: Dict) -> bytes:
    payload = {
        "exported_at": meta["exportedAt"],
        "scope": meta["scope"],
        "usernames": meta["usernames"],
        "total": len(posts),
        "posts": [
            {
                # 与 /api/sns/timeline 的字段同名同义，只把 media 换成"无凭据摘要"。
                "id": post["id"],
                "tid": post["tid"],
                "username": post["username"],
                "displayName": post["displayName"],
                "createTime": post["createTime"],
                "createTimeText": post["createTimeText"],
                "contentDesc": post["contentDesc"],
                "location": post["location"],
                "sourceName": post["sourceName"],
                "type": post["type"],
                "title": post["title"],
                "contentUrl": post["contentUrl"],
                "media": _media_brief(post),
                "likes": post["likes"],
                "comments": post["comments"],
            }
            for post in posts
        ],
    }
    return json.dumps(payload, ensure_ascii=False, indent=2).encode("utf-8")


def _render_txt(posts: List[Dict], meta: Dict) -> bytes:
    lines = [
        "朋友圈导出",
        "=" * 40,
        "导出时间：%s" % meta["exportedAt"],
        "导出范围：%s" % meta["scopeText"],
        "动态条数：%d" % len(posts),
        "说明：本文件不含图片（本机图片为加密存储，当前版本尚未实现解密）。",
        "",
    ]
    for index, post in enumerate(posts, 1):
        likes, comments = _post_summary(post)
        lines.append("[%d] %s  %s" % (index, post["displayName"], post["createTimeText"]))
        if post["contentDesc"]:
            lines.append(post["contentDesc"])
        if post["title"]:
            lines.append("标题：%s" % post["title"])
        if post["contentUrl"]:
            lines.append("链接：%s" % post["contentUrl"])
        if post["media"]:
            lines.append("[图片 ×%d]" % len(post["media"]))
        if post["location"]:
            lines.append("位置：%s" % post["location"])
        if post["sourceName"]:
            lines.append("来源：%s" % post["sourceName"])
        if likes:
            lines.append("赞：%s" % likes)
        if comments:
            lines.append("评论：%s" % comments)
        lines.append("-" * 40)
    return "\n".join(lines).encode("utf-8")


def _render_csv(posts: List[Dict], meta: Dict) -> bytes:
    """UTF-8 **带 BOM**：Excel 对不带 BOM 的 UTF-8 会按本地代码页解码，中文必然乱码。"""
    buffer = io.StringIO(newline="")
    writer = csv.writer(buffer)
    writer.writerow(["时间", "发布人", "发布人ID", "类型", "正文", "标题", "链接",
                     "图片数", "位置", "来源", "点赞", "评论", "tid"])
    for post in posts:
        likes, comments = _post_summary(post)
        # 正文里的换行会撑破单元格观感，压成空格（内容不丢，只是不换行）。
        writer.writerow([
            post["createTimeText"], post["displayName"], post["username"], post["type"],
            (post["contentDesc"] or "").replace("\r", " ").replace("\n", " "),
            post["title"], post["contentUrl"], len(post["media"] or []),
            post["location"], post["sourceName"], likes, comments, post["tid"],
        ])
    return buffer.getvalue().encode("utf-8-sig")


def _render_html(posts: List[Dict], meta: Dict) -> bytes:
    def esc(value) -> str:
        return html.escape(str(value or ""))

    blocks = []
    for post in posts:
        likes, comments = _post_summary(post)
        body = []
        if post["contentDesc"]:
            body.append('<div class="text">%s</div>' % esc(post["contentDesc"]).replace("\n", "<br>"))
        if post["title"]:
            body.append('<div class="title">%s</div>' % esc(post["title"]))
        if post["contentUrl"]:
            body.append('<div class="url">%s</div>' % esc(post["contentUrl"]))
        if post["media"]:
            # 刻意不写 <img>：图片拿不到，写外链就是死链。用与聊天列表一致的语义标签占位。
            body.append('<div class="media">[图片 ×%d]</div>' % len(post["media"]))
        meta_parts = [part for part in (post["location"], post["sourceName"]) if part]
        if meta_parts:
            body.append('<div class="meta">%s</div>' % esc(" · ".join(meta_parts)))
        if likes:
            body.append('<div class="like">赞：%s</div>' % esc(likes))
        if comments:
            body.append('<div class="comment">评论：%s</div>' % esc(comments))
        blocks.append(
            '<article><header><span class="name">%s</span>'
            '<span class="time">%s</span></header>%s</article>'
            % (esc(post["displayName"]), esc(post["createTimeText"]), "".join(body))
        )

    document = """<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>朋友圈导出</title><style>
body{font-family:"Microsoft YaHei",system-ui,sans-serif;background:#f5f6f8;color:#1f2329;
     margin:0;padding:24px;line-height:1.7}
h1{font-size:20px;margin:0 0 4px}
.summary{color:#646a73;font-size:13px;margin-bottom:8px}
.notice{background:#fff7e6;border:1px solid #ffd591;color:#874d00;padding:8px 12px;
        border-radius:6px;font-size:13px;margin-bottom:20px}
article{background:#fff;border-radius:8px;padding:14px 16px;margin-bottom:12px;
        box-shadow:0 1px 2px rgba(0,0,0,.06)}
header{display:flex;justify-content:space-between;margin-bottom:6px}
.name{font-weight:600;color:#576b95}
.time{color:#8f959e;font-size:13px}
.title{font-weight:600;margin-top:4px}
.url,.meta{color:#8f959e;font-size:13px;word-break:break-all}
.media{color:#576b95;font-size:13px;margin-top:4px}
.like,.comment{margin-top:6px;padding-top:6px;border-top:1px dashed #e5e6eb;font-size:13px}
</style></head><body>
<h1>朋友圈导出</h1>
<div class="summary">导出时间：%s　|　%s</div>
<div class="notice">本文件不含图片：本机微信图片为加密存储，当前版本尚未实现解密，
原动态有几张图已用「[图片 ×N]」标注。</div>
%s
</body></html>
""" % (esc(meta["exportedAt"]), esc(meta["scopeText"]), "".join(blocks))
    return document.encode("utf-8")


_RENDERERS = {
    "html": _render_html,
    "json": _render_json,
    "txt": _render_txt,
    "excel": _render_csv,
}


def _readme(meta: Dict, posts: List[Dict], inner_name: str) -> bytes:
    likes = sum(len(post.get("likes") or []) for post in posts)
    comments = sum(len(post.get("comments") or []) for post in posts)
    images = sum(len(post.get("media") or []) for post in posts)
    text = """朋友圈导出说明
========================================

导出时间：%s
导出范围：%s
动态条数：%d
点赞合计：%d 条
评论合计：%d 条
原图数量：%d 张（见下方说明）
正文文件：%s

⚠️ 本压缩包内没有图片
----------------------------------------
原因：本机微信的图片本体是加密存储的（文件名命名键与加密算法都未知），
当前版本尚未实现解密，因此图片无法包含在导出内容中。
原动态有几张图，已在正文里用「[图片 ×N]」标注，
以便接收方知道这里原本有图，而不是"导出时漏了"。

导出内容里也不包含图片的解密凭据（token / key）：
导出文件常被转发与归档，写入凭据等于泄露。

（本文件由 Chatlog AI 生成）
""" % (meta["exportedAt"], meta["scopeText"], len(posts), likes, comments, images, inner_name)
    return text.encode("utf-8")


def _build_zip(posts: List[Dict], export_format: str, meta: Dict) -> Tuple[bytes, str]:
    """渲染正文 + README，打成 ZIP。返回 (zip 字节, 正文文件名)。"""
    inner_name = _INNER_NAMES[export_format]
    document = _RENDERERS[export_format](posts, meta)
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(inner_name, document)
        archive.writestr("README.txt", _readme(meta, posts, inner_name))
    return buffer.getvalue(), inner_name


# ---------- 任务管理 ----------

class SnsExportManager:
    """朋友圈导出任务的登记、产物落盘与清理。

    产物落在 `data/exports/<exportId>.zip`，只通过 `/api/sns/export/{id}/download` 回传，
    客户端与用户都看不到这个目录（与"上游输出目录不下发"同一原则）。

    **任务表只在内存里**：上游的流程是"创建 → 每秒轮询 → 下载"，全程几十秒；
    服务重启后旧任务不再可查（产物文件仍在磁盘，但按 TTL 会被清理）。
    为这几十秒的流程做持久化是过度设计。
    """

    def __init__(self, store: LocalStore, export_dir: str):
        self.store = store
        self.export_dir = export_dir
        self._lock = threading.Lock()
        self._tasks: Dict[str, Dict] = {}
        os.makedirs(self.export_dir, exist_ok=True)
        self._prune()

    # ---------- 对外三步 ----------

    def create(self, options: Dict) -> Dict:
        """创建任务：**同步**渲染完并落盘，任务对象一出生就是完成态。

        为什么不做真异步：本机时间线只有 410 条、且不含图片（图片在 C6 之前根本进不了包），
        实测渲染 + 打包在百毫秒级。为它起一个后台线程，只会多出"任务跑到一半进程退出"
        这类需要额外状态机才能处理的情况，换不到任何东西。
        """
        scope = str(options.get("scope") or "selected").strip().lower()
        if scope not in SCOPES:
            raise StoreError("scope 只能是 selected 或 all")
        export_format = str(options.get("format") or "html").strip().lower()
        if export_format not in FORMATS:
            raise StoreError("format 只能是 html / json / txt / excel")

        raw_names = options.get("usernames")
        usernames = []
        if isinstance(raw_names, (list, tuple)):
            usernames = [str(item).strip() for item in raw_names if str(item).strip()]
        if scope == "selected" and not usernames:
            # 与客户端同口径：**绝不静默改成 all** —— 那会给用户一份自己没预期的完整导出。
            raise StoreError("scope=selected 但没有指定 usernames")

        posts = moments_mod.all_posts(
            self.store, usernames=usernames if scope == "selected" else None)

        meta = {
            "exportedAt": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
            "scope": scope,
            "usernames": usernames,
            "scopeText": _scope_text(scope, usernames, posts),
        }
        archive_bytes, _inner = _build_zip(posts, export_format, meta)

        export_id = self._new_id()
        with open(self._path(export_id), "wb") as handle:
            handle.write(archive_bytes)

        task = {
            # 字段名逐字对齐客户端实测的任务对象（见模块 docstring）。
            "exportId": export_id,
            "status": "done",
            "zipReady": True,
            "error": "",
            "progress": {
                "postsTotal": len(posts),
                "postsExported": len(posts),
                # 图片一张也没准备 —— 不是"还没准备"，是 C6 之前根本准备不了。
                "mediaPrepareTotal": 0,
                "mediaPrepared": 0,
            },
            "scope": scope,
            "format": export_format,
            "usernames": usernames,
            "fileName": self._archive_name(export_format),
            "createdAt": int(time.time()),
            "archiveBytes": len(archive_bytes),
            "postsTotal": len(posts),
        }
        with self._lock:
            self._tasks[export_id] = task
        self._prune()
        return dict(task)

    def status(self, export_id: str) -> Dict:
        with self._lock:
            task = self._tasks.get(export_id)
        if task is None:
            raise StoreError("导出任务不存在或已过期：%s" % export_id)
        return dict(task)

    def download(self, export_id: str) -> Tuple[bytes, str, Dict]:
        task = self.status(export_id)
        if not task.get("zipReady"):
            raise StoreError("导出任务尚未完成，暂时不能下载")
        path = self._path(export_id)
        if not os.path.exists(path):
            raise StoreError("导出产物已被清理，请重新发起导出")
        with open(path, "rb") as handle:
            data = handle.read()
        name = task.get("fileName") or ("sns-export-%s.zip" % export_id)
        # 只给文件名本体：server 侧 _export_filename 会再做一次目录穿越与响应头注入的清洗。
        return data, "application/zip", {"Content-Disposition": 'attachment; filename="%s"' % name}

    # ---------- 内部 ----------

    def _path(self, export_id: str) -> str:
        # exportId 是我们自己生成的（时间戳 + 随机 hex），不含路径分隔符；
        # 仍然 basename 一次，避免任何来路不明的 id 被拼进路径。
        safe = os.path.basename(str(export_id))
        return os.path.join(self.export_dir, "%s.zip" % safe)

    def _new_id(self) -> str:
        return "sns-%s-%s" % (time.strftime("%Y%m%d-%H%M%S"), os.urandom(3).hex())

    def _archive_name(self, export_format: str) -> str:
        """ZIP 文件名用纯 ASCII：`server._export_filename` 会把非 ASCII 打成下划线，
        给一个中文名只会让它变成一串 `_`，不如自己给一个干净的名字。

        后缀是 `.zip` 而不是 `.csv` / `.html` —— 产物永远是 ZIP 包，
        里面的 `moments.csv` / `moments.html` 才是正文（客户端另有 `UseZipExtension` 兜底）。
        """
        return "sns-export-%s-%s.zip" % (time.strftime("%Y%m%d-%H%M%S"), export_format)

    def _prune(self) -> None:
        """按时长 + 数量两个维度清理产物，并把任务表与磁盘对齐。

        在 `__init__` 与每次 `create` 之后调用 —— 导出是低频动作，
        不值得为它常驻一个清理线程。
        """
        try:
            names = [name for name in os.listdir(self.export_dir) if name.endswith(".zip")]
        except OSError:
            return
        now = time.time()
        for name in names:
            path = os.path.join(self.export_dir, name)
            try:
                if now - os.path.getmtime(path) > ARCHIVE_TTL_SECONDS:
                    os.remove(path)
            except OSError:
                continue

        with self._lock:
            # 产物没了的任务一并清掉，避免"任务说能下、文件已经不在"。
            for export_id in [key for key in self._tasks if not os.path.exists(self._path(key))]:
                self._tasks.pop(export_id, None)
            if len(self._tasks) > MAX_KEPT_ARCHIVES:
                ordered = sorted(self._tasks.values(), key=lambda item: item.get("createdAt") or 0)
                for task in ordered[:len(self._tasks) - MAX_KEPT_ARCHIVES]:
                    self._tasks.pop(task["exportId"], None)
                    try:
                        os.remove(self._path(task["exportId"]))
                    except OSError:
                        pass
