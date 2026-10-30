# -*- coding: utf-8 -*-
"""C4：朋友圈时间线 / 用户 / 增量同步（对应 `WeChatAdapter.sns_timeline` / `sns_users` / `sns_sync_latest`）。

实测口径见 `docs/SELFHOST_DATA_PLAN.md` §2.9（全部是直接读原始表 + 逐字节解析 XML 得到的，
上游从未解密过 `sns.db`，所以**没有上游产物可比对**）。这里只写"为什么这么写"：

  - 时间线只有 `SnsTimeLine` 一张表（本机 410 条），`content` 是**纯 XML 明文**、不压缩。
    结构是 `<SnsDataItem><TimelineObject>…</TimelineObject><LocalExtraInfo>…</LocalExtraInfo></SnsDataItem>`。
  - **`tid`（列，有符号）与 XML 的 `<id>`（无符号）是同一个 64 位数**，且"按 tid 倒序"与
    "按 createTime 倒序"实测 **410/410 完全一致** → 分页直接用 `tid desc`。
  - **点赞 / 评论就在 XML 里**：`LocalExtraInfo/like_user_list`（`type=1`）与
    `comment_user_list`（`type=2`）。**不要去读 `SnsMessage_tmp3`** —— 它只有 30 行、
    全部 `to_username = 自己`，是"别人对我的互动"通知，不是时间线的互动源。
  - `displayName` 直接取 `LocalExtraInfo/nickname`（实测 410/410 非空），**不查 contact 表**。
  - **图片本体没有自研实现（C6 未攻克）**：这里只出 `urlAttrs` / `thumbAttrs` 元数据；
    `/api/sns/media`、`/api/sns/video` 仍走上游（见 `server.py` 的能力路由）。
  - `official` / `finderFeed` **本地不做**（已拍板）：键保留、值为 `null`，与
    `adapters.normalize_sns_post` 的 None 透传同形，客户端 DTO 是 `JsonElement?` 不受影响。
"""

from __future__ import annotations

import time
import xml.etree.ElementTree as ET
from typing import Dict, List, Optional, Set, Tuple

from .convert import format_time
from .store import LocalStore, StoreError

# 与 `adapters.SNS_ATTR_KEYS` 逐字一致：客户端会把这一整套原样回传给 /api/sns/media，
# 键名一旦不同，取图就会失败（且只在"点开大图"时才暴露）。
SNS_ATTR_KEYS = ("type", "md5", "key", "enc_idx", "token")

LIKE_LIST_TAG = "like_user_list"
COMMENT_LIST_TAG = "comment_user_list"

# 时间线本机只有 410 条：全量解析（ElementTree）实测 ≈48ms、只取标量的纯正则 ≈2ms。
# 选择**全量解析一次**而不是"先正则粗筛、再细读命中项"：只有一条代码路径，
# 不存在两个解析器口径不一致的风险；48ms 相对上游的 1~3s 仍快两个数量级。
_TIMELINE_SQL = "select tid, content from SnsTimeLine order by tid desc"
_NEWEST_SQL = "select tid from SnsTimeLine order by tid desc limit ?"


# ---------- 标量工具 ----------

def _text(node: Optional[ET.Element], name: str) -> str:
    """取直接子节点的文本并去首尾空白（`findtext` 只找直接子节点，不会误取嵌套的同名节点）。"""
    if node is None:
        return ""
    return (node.findtext(name) or "").strip()


def _to_int(value) -> int:
    """小整数（尺寸 / createTime / 类型码）。**不要拿它转 tid** —— float 中转会丢 64 位精度。"""
    try:
        return int(float(value))
    except (TypeError, ValueError):
        return 0


def _to_int64(value) -> Optional[int]:
    """64 位整数（tid / feed_id）。走 `int()` 而不是 `int(float())`，否则会丢精度。"""
    text = str("" if value is None else value).strip()
    if not text:
        return None
    try:
        return int(text)
    except ValueError:
        return None


def _attrs(node: Optional[ET.Element], keys: Tuple[str, ...]) -> Optional[Dict]:
    """取属性白名单（与 `adapters.normalize_sns_attrs` 同形：空值丢弃、全空则 None）。"""
    if node is None:
        return None
    picked = {}
    for key in keys:
        value = (node.get(key) or "").strip()
        if value:
            picked[key] = value
    return picked or None


def _element_text(node: Optional[ET.Element]) -> str:
    return (node.text or "").strip() if node is not None else ""


# ---------- XML → 契约字段 ----------

def _media_list(content_object: Optional[ET.Element]) -> List[Dict]:
    """`ContentObject/mediaList/media` → 契约的 `media[]`（字段与 §14.8.1.1 实测清单逐字一致）。

    `url` / `thumb` 各自的属性是**两套不同的 token**（§14.8.1.1 实测 1），
    必须分开放进 `urlAttrs` / `thumbAttrs`，**不要合并、也不要互相补字段**。
    """
    holder = content_object.find("mediaList") if content_object is not None else None
    if holder is None:
        return []
    result = []
    for node in holder.findall("media"):
        url_node = node.find("url")
        thumb_node = node.find("thumb")
        size_node = node.find("size")
        result.append({
            "type": _text(node, "type"),
            "id": _text(node, "id"),
            "url": _element_text(url_node),
            "thumb": _element_text(thumb_node),
            "urlAttrs": _attrs(url_node, SNS_ATTR_KEYS),
            "thumbAttrs": _attrs(thumb_node, SNS_ATTR_KEYS),
            # size 恒为三项整数（与 normalize_sns_size 同形），缺值时给 0 而不是省略键。
            "size": {
                "width": _to_int(size_node.get("width") if size_node is not None else 0),
                "height": _to_int(size_node.get("height") if size_node is not None else 0),
                "totalSize": _to_int(size_node.get("totalSize") if size_node is not None else 0),
            },
        })
    return result


def _interaction(node: ET.Element) -> Dict:
    """一条点赞 / 评论（`createTime` 用 DB 的 `create_time`，`content` 对点赞为空）。"""
    return {
        "username": _text(node, "username"),
        "nickname": _text(node, "nickname"),
        "content": (node.findtext("content") or "").strip(),
        "createTime": _text(node, "create_time"),
        "refUsername": _text(node, "ref_username"),
        "contentType": _to_int(node.findtext("comment_content_type")),
    }


def _interactions(local_extra: Optional[ET.Element]) -> Tuple[List[Dict], List[Dict]]:
    """`LocalExtraInfo` → (点赞列表, 评论列表)。

    两个列表是 `LocalExtraInfo` 的**子节点**（不是 `TimelineObject` 的），
    位置在 `<nickname>` 之后；本机 410 条里 84 条有点赞、16 条有评论。
    """
    likes: List[Dict] = []
    comments: List[Dict] = []
    if local_extra is None:
        return likes, comments
    for tag, bucket in ((LIKE_LIST_TAG, likes), (COMMENT_LIST_TAG, comments)):
        holder = local_extra.find(tag)
        if holder is None:
            continue
        for node in holder.findall("user_comment"):
            # b_deleted=1 是"已删除的评论"。实测本机 268 个全为 0，仍按语义排除掉。
            if _text(node, "b_deleted") == "1":
                continue
            bucket.append(_interaction(node))
    return likes, comments


def _to_post(tree: ET.Element, tid_text: str) -> Optional[Dict]:
    """一棵 `<SnsDataItem>` → 契约的一条 timeline 项（键集合与 normalize_sns_post 一致）。"""
    timeline = tree.find("TimelineObject")
    if timeline is None:
        return None
    content_object = timeline.find("ContentObject")
    local_extra = tree.find("LocalExtraInfo")
    location = timeline.find("location")

    username = _text(timeline, "username")
    create_time = _to_int(timeline.findtext("createTime"))
    likes, comments = _interactions(local_extra)

    return {
        "id": _text(timeline, "id"),
        "tid": tid_text,
        "username": username,
        # 朋友圈的昵称就在本地 XML 里，**不要退回去查 contact 表**（实测 410/410 非空）。
        "displayName": _text(local_extra, "nickname") or username,
        "createTime": str(create_time),
        "createTimeText": format_time(create_time),
        "contentDesc": _text(timeline, "contentDesc"),
        "location": (location.get("poiName") or "").strip() if location is not None else "",
        # sourceName 只用 <sourceNickName>（实测只在 type=3 文章出现，值是公众号名），
        # **不复刻上游前端的兜底链**（主机名 → 音乐 → 外部分享），那是展示层的事。
        "sourceName": _text(timeline, "sourceNickName"),
        "media": _media_list(content_object),
        "likes": likes,
        "comments": comments,
        "type": _text(content_object, "type"),
        "title": _text(content_object, "title"),
        "contentUrl": _text(content_object, "contentUrl"),
        "finderFeed": None,
        "official": None,
    }


def _load_posts(store: LocalStore) -> List[Dict]:
    """读全量时间线（`tid desc` == `createTime desc`，实测 410/410 位置全同）。"""
    with store.read("sns.db") as con:
        rows = list(con.execute(_TIMELINE_SQL))
    posts = []
    for tid, content in rows:
        if not content:
            continue
        try:
            tree = ET.fromstring(
                content if isinstance(content, str) else bytes(content).decode("utf-8", "replace")
            )
        except ET.ParseError:
            # 单条 XML 坏掉不该让整条时间线 503：跳过它继续（本机实测 410/410 全部可解析）。
            continue
        post = _to_post(tree, _tid_text(tid))
        if post is not None:
            posts.append(post)
    return posts


def _tid_text(value) -> str:
    number = _to_int64(value)
    return "" if number is None else str(number)


# ---------- 查询 ----------

def _wanted_usernames(value) -> Set[str]:
    """`usernames` 归一成集合：不传 / 空串 / `[""]` 一律得到空集合（= 全部）。"""
    if value is None:
        parts = []
    elif isinstance(value, (list, tuple, set)):
        parts = [str(item or "") for item in value]
    else:
        parts = [str(value)]
    return {piece.strip() for part in parts for piece in part.split(",") if piece.strip()}


def _search_text(post: Dict) -> str:
    """关键词匹配范围：正文 + 标题（小写化后比较）。"""
    return ("%s\n%s" % (post["contentDesc"], post["title"])).lower()


def all_posts(store: LocalStore, usernames=None, keyword: str = "") -> List[Dict]:
    """按发布人 / 关键词筛选后的**全量**时间线（`tid desc`）。

    导出（C5）需要全量、不分页，所以把它从 `list_timeline` 里抽出来：
    没有它就只能靠 `limit=一个很大的数` 硬凑，那是在用分页接口假装不分页。
    """
    posts = _load_posts(store)

    wanted = _wanted_usernames(usernames)
    if wanted:
        posts = [post for post in posts if post["username"] in wanted]
    needle = str(keyword or "").strip().lower()
    if needle:
        posts = [post for post in posts if needle in _search_text(post)]
    return posts


def list_timeline(store: LocalStore, limit: int = 20, offset: int = 0,
                  usernames=None, keyword: str = "") -> Dict:
    """与 `WeChatAdapter.sns_timeline` 同形的响应。

    `sourceFallback` 恒为 **False**：那是上游"实时库连不上、当前看的是解密库快照"的降级信号，
    而本地读的是 WAL 级实时镜像，比上游的快照更新鲜 —— 这里不模仿降级。
    """
    posts = all_posts(store, usernames=usernames, keyword=keyword)

    size = max(1, _to_int(limit) or 20)
    start = max(0, _to_int(offset))
    page = posts[start:start + size]
    return {
        "timeline": page,
        "hasMore": start + len(page) < len(posts),
        "limit": size,
        "offset": start,
        "source": "local",
        "cover": None,
        "covers": None,
        "sourceRequested": "local",
        "sourceFallback": False,
        "sourceFallbackReason": "",
        "sourceFallbackMessage": "",
        "sourceFallbackRetryAfterSeconds": 0,
    }


def list_users(store: LocalStore, keyword: str = "", limit: int = 5000) -> Dict:
    """发过朋友圈的人 + `postCount`（按发帖数倒序，同数按 username 定序，保证结果稳定）。"""
    counts: Dict[str, int] = {}
    names: Dict[str, str] = {}
    for post in _load_posts(store):
        who = post["username"]
        if not who:
            continue
        counts[who] = counts.get(who, 0) + 1
        names.setdefault(who, post["displayName"])

    items = [{"username": who, "displayName": names[who], "postCount": count}
             for who, count in counts.items()]
    items.sort(key=lambda item: (-item["postCount"], item["username"]))

    needle = str(keyword or "").strip().lower()
    if needle:
        items = [item for item in items
                 if needle in item["username"].lower() or needle in item["displayName"].lower()]

    size = max(1, _to_int(limit) or 5000)
    return {"items": items[:size], "count": len(items), "limit": size}


def sync_latest(store: LocalStore, last_tid=None, max_scan: int = 200,
                force: bool = False) -> Dict:
    """本地模式下的"同步最新"= **立即做一次镜像同步**（把 WAL 里的新帧拉进来）。

    与上游语义的差别（说清楚，不伪装）：
      - 上游是"把实时朋友圈增量拉进解密库"；本地镜像本来就每秒自动同步，
        这里只是让用户点一下**立刻**看到结果，并回报真实数字（不是空操作）。
      - `force` 上游用于绕缓存，本地没有缓存可言 → 忽略（保留参数以保持同形）。
      - `upserted` 的相对基准由调用方（`LocalDataService`）持有的游标给出；
        **首次调用没有基准，恒为 0**，此时 `lastMaxId` 为空串。
    """
    started = time.time()
    stats = store.sync(["sns.db"])
    error = (stats.get("sns.db") or {}).get("error")
    if error:
        raise StoreError("朋友圈镜像同步失败：%s" % error)

    scan = max(1, _to_int(max_scan) or 200)
    with store.read("sns.db") as con:
        newest = list(con.execute(_NEWEST_SQL, (scan,)))

    previous = _to_int64(last_tid)
    upserted = 0
    if previous is not None:
        upserted = sum(1 for (tid,) in newest if _to_int64(tid) > previous)
    return {
        "scanned": len(newest),
        "upserted": upserted,
        "maxId": _tid_text(newest[0][0]) if newest else "",
        "lastMaxId": "" if previous is None else str(previous),
        "elapsedMs": int((time.time() - started) * 1000),
    }
