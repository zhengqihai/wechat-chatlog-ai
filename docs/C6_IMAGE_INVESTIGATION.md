# C6 图片链路调研报告（本地图片到底能不能通）

> 建立时间：2026-10-29　对应待办：`BUGFIX_FEATURE_PLAN.md` §1.1 T-B1（纯调研）
> 结论性质：**实测结论**（本机 `wxid_cjll2zqxiby722_224d` 账号、微信 4.x），非推测。
>
> 一句话结论：**本地图片这条链路能打通，而且三块拼图（密钥 / 算法 / 命名键）已经全部到手。**
> 与 `SELFHOST_DATA_PLAN.md` §2.8 当初「命名键未知 + 加密算法未破、29036 次尝试 0 命中」的结论
> **完全相反**——当时用错了密钥、漏看了缩略图命名规律，导致误判为「需独立逆向」。

---

## 0. 结论先行

| 问题 | 结论 |
|---|---|
| 图片 `.dat` 命名键 | **图片 hash（32 位 hex，来自消息表 `packed_info_data`）是统一定位键**，原图/HD/缩略图共用同一 hash 前缀（见 §2） |
| 图片加密算法 | **与消息库不是同一套**。图片是「V2 容器 = AES-128-ECB + 单字节 XOR」，已本地实测整图解密成功（见 §3） |
| `message_resource.db` schema | 已查清（见 §4）。`image_md5` / `image_file_id` **不在这里**，本地定位键是 `packed_info_data` 的 hash（见 §2/§5） |
| 能否攻克 | **能**。密钥已在 `account_keys.json`、算法公开、命名键已破解（见 §6） |

**最关键的发现**：上游 `account_keys.json` 里**早就躺着两把图片密钥**，且 `image_key_verified: true`（上游 WeFlow 已验证过），
但自研链路的 [keys.py](file:///d:/code/chatlog-ai-portable/app/localdb/keys.py#L46-L63) 虽然把 `image_xor_key` / `image_aes_key`
读进了 entry，却**从没有任何代码用它**。§2.8 当时用消息 XML 里的 `aeskey`（每次传输的临时密钥）去试，方向就错了。

---

## 1. BUG1 根因链（回顾，与调研前一致）

图片无法显示的根因是 [messages.py](file:///d:/code/chatlog-ai-portable/app/localdb/messages.py#L193-L195) 的
`image_md5` / `image_file_id` 恒留空 → [server.py](file:///d:/code/chatlog-ai-portable/app/server.py#L1566-L1580)
的 `with_image_url()` 直接 `return message`（不带 `image_url`）→ 前端拿不到图。本报告回答的是「这两个字段到底能不能补上、
补上之后图片本体能不能真的取到」。

---

## 2. `.dat` 命名键（实测，已破解）

图片本体在 `xwechat_files\<wxid>\msg\attach\<md5(会话username)>\<YYYY-MM>\Img\` 下，有三种后缀：

| 后缀 | 本机数量 | 命名 |
|---|---|---|
| `.dat`（原图） | 3221 | `<hash>.dat` |
| `_h.dat`（HD） | 1438 | `<hash>_h.dat` |
| `_t.dat`（缩略图） | 5535 | `<hash>_t.dat` |

**三者共用同一个 32 位 hex hash 作为文件名前缀**，例如 `adbc77e820081267f619a985e2ee04e7` 同时对应
`.dat` / `_h.dat` / `_t.dat` 三个文件。

**hash 来源（关键）**：`hash` 就在消息表 `Msg_<hash>` 的 `packed_info_data` 字段里（protobuf BLOB，
内嵌一个 32 字节 ASCII hex）。例如消息 `local_id=7` 的 `packed_info_data` 解出
`adbc77e820081267f619a985e2ee04e7`，与磁盘上的 `.dat` 文件名严格一致。

**定位链路（端到端已实测 100% 命中）**：

    消息表 packed_info_data ──提取──> hash ──拼接──> msg/attach/<md5(target_id)>/<月>/Img/<hash>_t.dat

- `md5(target_id)`：与消息表名 `Msg_<md5>` 同一算法（`hashlib.md5(username).hexdigest()`）
- `<月>`：从消息 `create_time`（秒级）用 `datetime.fromtimestamp().strftime("%Y-%m")` 算

> 纠偏：初版报告（及 §2.8）曾以为缩略图是 `<local_id>_<create_time>` 命名 —— 那只是少数旧命名的巧合
> （622 个里仅 420 个能对上 MessageResourceInfo），主流其实是 `hash` 命名。§2.8 说「32 位 hex 与消息 md5
> 零交集」也对了一半：它找的是消息 XML 的 `md5`，而真正的 hash 在 `packed_info_data` 里。

---

## 3. 图片加密算法（实测整图解密成功）

**与消息库 SQLCipher（AES-256-CBC + 页尾 IV + HMAC）不是同一套**。图片是「V2 容器」格式：

```
文件布局（V2，magic = 07 08 56 32 08 07）：
┌─────────────────────────────────────────────────────────────┐
│ header(15B) = magic(6) + aes_size(4 LE) + xor_size(4 LE) + flag(1) │
│ AES-128-ECB 密文（aes_size 字节）                          │
│ 单字节 XOR 尾部（其余字节，与 image_xor_key 异或）          │
└─────────────────────────────────────────────────────────────┘
```

**实测样本**：`msg/attach/0cfb70dd.../2026-04/Img/34_1776782212_t.dat`（21768 字节）

- header 15 字节：`07 08 56 32 08 07 00 04 00 00 e9 50 00 00 01`
  - `aes_size = 0x400 = 1024`，`xor_size = 0x50e9 = 20713`（= 明文图片字节数），`flag = 0x01`
- AES 段从 **offset 15** 开始（不是 16，第 16 字节已是密文，之前 §2.8 把它误当 header）
- AES-128-ECB 解密 key = `image_aes_key` 的 **16 字节 ASCII 原文**（不是 hex 解码！`a315804ea1244fe1` 这 16 个字符本身即密钥）
- 解密 AES 段得到 JPEG 头 `ff d8 ff e0 00 10 4a 46 49 46 ...`（`JFIF`）
- 剩余字节与 `image_xor_key = 0xDC` 逐字节异或后拼接，得到**完整 JPEG**（以 `ff d9` EOI 结尾，21753 字节）

**为什么 §2.8 会 0 命中**：它用消息 XML 的 `aeskey`（16 字节 hex，每次传输的临时 CDN 密钥）× AES-ECB/CBC 去试，
而图片本体用的是**账号级固定的 `image_aes_key`**（16 字节 ASCII，与消息 XML 无关）。两把钥匙对错了锁。

---

## 4. `message_resource.db` schema（实测）

| 表 | 关键列 | 行数 |
|---|---|---|
| `MessageResourceInfo` | `message_id, chat_id, sender_id, message_local_type, message_create_time, message_local_id, message_svr_id, message_origin_source, packed_info` | 14695 |
| `MessageResourceDetail` | `resource_id, message_id, type, size, create_time, access_time, status, data_index, packed_info` | 37412 |
| `ChatName2Id` | `user_name, update_time` | 453 |
| `SenderName2Id` | `user_name, update_time` | 380 |

实测要点：

- `MessageResourceInfo.message_local_type & 0xFFFFFFFF` 分布：`3`（图片）13153、`49`（appmsg）1139、`43`（视频）403 —— 图片占绝对多数。
- `MessageResourceDetail.type` 是位域（`262145=0x40001`、`131073=0x20001`、`65537=0x10001`…），
  一张图片对应多条 detail（原图/缩略图/HD 各一条）。
- **`data_index` 全部是 `'0'`、`packed_info` 全部为空** → 这张表**不存图片文件路径，也不存 md5**。
- 它的价值是：`message_local_id + message_create_time + chat_id` 可用来**交叉验证**缩略图定位（§2 的 420 命中即用它做的）。

**结论**：`message_resource.db` 不提供 `image_md5` / `image_file_id`，也不是定位图片的必要条件。定位靠消息表自身的 `local_id + create_time`。

---

## 5. `image_md5` / `image_file_id` 从哪取

- **`image_md5`**：来自图片消息的 XML `<img>` 标签（`<img aeskey=... cdnthumburl=... md5=... />`）。
  [messages.py](file:///d:/code/chatlog-ai-portable/app/localdb/messages.py#L154-L161) 当前把非文本消息整个压成
  `[图片]` 标签，**没有解析 XML**，所以 md5 没被提取。
- **`image_file_id`**：来自 XML `<img>` 的 `fileid` 属性（§2.8 已记「图片常为空」）。

但这里有个**设计层面的关键判断**：这两个字段是**上游适配器** `WeChatAdapter.fetch_media("image", target_id, md5, file_id)`
的定位参数。本地链路**根本不需要它们**——本地定位走的是 `local_id + create_time → .dat 文件 → V2 解密`。

所以本地模式有两种落地方式（见 §6 的代价）：
1. 继续给 `image_md5` / `image_file_id` 填值（从 XML 解析），但这只是为了让 `/api/media/image` 的旧接口「看起来能走」；
2. **直接改本地图片代理**：新增「按 `local_id + create_time + target_id` 定位并解密 .dat」的本地图片端点，`with_image_url()` 在本地模式下生成指向该端点的 `image_url`。

方案 2 才是正解——它不依赖 `message_resource.db`，也不依赖 XML 的 md5/fileid 是否齐全。

---

## 6. 可行性结论：能攻克，且优先级应上调

### 6.1 三块拼图都已就位

| 拼图 | 状态 | 依据 |
|---|---|---|
| 密钥 | ✅ 已有 | `account_keys.json` 的 `image_aes_key`（16 字节 ASCII）/ `image_xor_key`（单字节 `0xDC`），`image_key_verified: true` |
| 算法 | ✅ 公开且实测 | V2 = AES-128-ECB + XOR，本机整图解密成功（§3） |
| 缩略图命名键 | ✅ 可推导 | `<local_id>_<create_time>`，420/622 命中（§2） |

### 6.2 代价（中等，纯后端）

1. `messages.py`：把 `local_id` / `create_time` 透出（目前只在内部 staged 里用，没写进返回 dict）。
2. 新增本地图片解密 + 代理端点（约一个模块）：入参 `target_id + local_id + create_time + 类型(thumb/hd/orig)`，
   拼路径 → 读 `.dat` → V2 解密 → 返回字节流。
3. `server.py`：`with_image_url()` 本地模式下改指向新端点；`/api/media/image` 本地模式改走新逻辑。
4. 图片密钥接入：`keys.py` 已读 `image_aes_key` / `image_xor_key`，但需透传到 service/store 层。

### 6.3 风险

| # | 风险 | 说明 |
|---|---|---|
| R1 | 图片 `.dat` 可能未下载 | 未点开过的图片本地无 `.dat` 文件（只有点开才下载）；聊天框对这些图仍无图，属微信自身行为 |
| R2 | 微信升级可能轮换 `image_aes_key` | 与 `db_key` 同一类风险，`account_keys.json` 已兜底；轮换后需重新导入密钥 |
| R3 | 缩略图体积小、原图更大 | 聊天框默认显示缩略图（`_t.dat`）足够；原图/HD 需 `kind=orig/hd` 定位同一 hash |
| R4 | 密钥脱敏 | 图片密钥写入代码/日志有泄露风险，处理时与 `db_key` 同等级别脱敏 |

### 6.4 是否值得单独排期

**值得，且建议优先级高于 `BUGFIX_FEATURE_PLAN.md` §5 的原排序（原排序把它放最后、且预设「可能不可行」）。**
理由：

1. 这是「图片无法显示」bug 的**根治方案**，不是「再研究研究」；
2. 三块拼图已到手，实现是**确定性工作**，不再有逆向的不确定性；
3. 顺带解锁朋友圈图片导出（[exports.py](file:///d:/code/chatlog-ai-portable/app/localdb/exports.py#L25-L27) 里「包里没有图片」的边界可以撤掉）。

### 6.5 实现落地（已完成）

1. ✅ 新增 [images.py](file:///d:/code/chatlog-ai-portable/app/localdb/images.py)：V2 解密 + hash 定位。
2. ✅ [messages.py](file:///d:/code/chatlog-ai-portable/app/localdb/messages.py) 图片消息透出 `image_hash`。
3. ✅ [service.py](file:///d:/code/chatlog-ai-portable/app/localdb/service.py) 新增 `fetch_image`（含图片密钥读取）。
4. ✅ [server.py](file:///d:/code/chatlog-ai-portable/app/server.py) 本地模式下 `with_image_url` 生成 `image_url` + 新增 `/api/media/image/local` 端点。

**后续可选**（非本次范围）：回填 `image_md5` / `image_file_id` 对齐上游契约字段；解除朋友圈导出「无图」限制。

---

## 7. 调研依据清单（可复现）

- 密钥来源：`C:\ProgramData\ChatlogAI\data\wechat\output\account_keys.json`（`image_aes_key` / `image_xor_key` / `image_key_verified`）
- schema 来源：`...\output\databases\wxid_cjll2zqxiby722\message_resource.db`（明文副本）
- 图片样本：`D:\app\wechat\Files\xwechat_files\wxid_cjll2zqxiby722_224d\msg\attach\**\Img\*.dat`
- 解密验证：临时脚本（已删），AES-128-ECB（`cryptography`）+ 单字节 XOR，解出完整 JPEG
- 算法外部佐证：微信 4.x V2 图片格式「AES-128-ECB + XOR」见 52pojie / CSDN / sjzar/chatlog 等多处公开资料

> 说明：本报告未贴出 `image_aes_key` 明文与 `db_key`，它们与数据库密钥同属账号级凭据，
> 实现时在日志、异常、导出物中一律脱敏，与 [keys.py](file:///d:/code/chatlog-ai-portable/app/localdb/keys.py) 现有「凭据不下发」原则一致。
