# 自研本地数据链路方案（SELFHOST DATA PLAN）

> 用途：把「微信数据」这条链路从上游 `runtime/wechat`（WeChatDataAnalysis / WeFlow）**完整收回自研**，
> 彻底摆脱上游 native core 的 license/lease 依赖。
>
> **本文件是这一改造的唯一进度事实源**：每完成一个任务，必须回来更新 §7 进度表与相应条目的状态。
> 新开对话窗口时，先读本文件即可恢复上下文（配合 `docs/PROJECT_SCAN.md`、`docs/API_REFERENCE.md`）。
>
> 建档时间：2026-09-30　当前状态：**设计完成，未写代码**

---

## 0. 背景与问题定位（为什么必须自研）

上游 `runtime/wechat` 的 native core 需要一张在线签发的 lease（`wechatdb_native_build.json` 中 `codeSignatureEnforced: true`，
`windowsSignerTrustMode: private-pki`）。当前机器上它拿不到：

```
Native core open account failed: Native core license service is unavailable.
```

后果（已在上游日志中逐条证实）：

1. `source=realtime` 全部失败，上游**静默降级**成 `fallbackSource: "decrypted"`，返回 HTTP 200；
2. 它读的那份「解密库」只在授权时（2026-09-30 14:53:58–14:54:10）生成过一次，此后 mtime 再未变化；
3. 会话指纹永远不变 → **自动回复永不触发**；
4. 上游自己的增量同步 `/api/chat/realtime/sync` 同样走 native core，一并不能用。

结论：只要还依赖上游的实时通道，就永远受制于它的授权。**自研是唯一根治路径。**

---

## 1. 目标与非目标

### 目标

| # | 目标 | 验收口径 |
|---|---|---|
| G1 | 数据新鲜度 **WAL 级（< 1s）** | 好友发消息后 1s 内能在本服务查到该条 |
| G2 | 采集成本与聊天库体积**解耦** | 库涨到 10GB，单轮刷新耗时不随之线性增长 |
| G3 | **全部功能**不依赖上游 | 会话 / 消息 / 联系人 / 朋友圈 / 导出 / 媒体 全部自研 |
| G4 | 对外契约不变 | `docs/API_REFERENCE.md` 的接口与字段**零改动** |
| G5 | 密钥自主 | 不再依赖上游产出的 `account_keys.json` |

### 非目标

- 不改上游 exe、不碰它的 license/lease 校验、不做任何二进制 patch（明确放弃）。
- 不重写 .NET 客户端 UI、不动 `automation.py`、不动自动回复/定时发送引擎的业务逻辑。

---

## 2. 已确认的技术事实（★ 这些是实测结论，不要再重新猜）

> 验证对象：本机 `D:\app\wechat\Files\xwechat_files\wxid_cjll2zqxiby722_224d\db_storage\message\message_0.db`
> 验证时间：2026-09-30　验证方式：用便携 Python + `cryptography` 直接解密第 1/2 页，检查 SQLite 页头自洽性。

### 2.1 加密参数

| 项 | 值 |
|---|---|
| 加密算法 | AES-256-CBC |
| 页大小 | **4096** 字节 |
| reserve | **80 字节 = IV(16) + HMAC(64)** |
| 页布局（第 1 页） | `salt(16)` + `密文(4000)` + `IV(16)` + `HMAC(64)` |
| 页布局（第 ≥2 页） | `密文(4016)` + `IV(16)` + `HMAC(64)` |
| **IV 来源** | **直接存在页尾，不是按页号派生**（微信对标准 SQLCipher 的改造点） |
| 密钥派生 | `enc_key = PBKDF2-HMAC-SHA512(password = db_key 的 32 字节原始值, salt = 文件前 16 字节, iterations = 256000, dklen = 32)` |
| 明文页头（第 1 页解出） | `10 00 | 02 02 | 50 | 40 20 20` → 页大小 4096 / WAL 模式 / reserved=80 / payload 分数 64,32,32 —— 全部自洽 |
| 页尾 80 字节 | 原文件中存放 `IV(16) + HMAC(64)`；**解密后必须清零填充**，明文页长度仍是 4096（少这 80 字节产出文件就与上游不一致） |
| HMAC 计算范围 | `HMAC-SHA512(mac_key, 密文 ‖ IV ‖ 页号(小端 4 字节))`；`mac_key = PBKDF2-HMAC-SHA512(enc_key, salt XOR 0x3A, 2, 32)` |

**逐字节金标准比对结果（2026-09-30）**：19 个库全量解密 **5.29s**（上游 7.1s）；
其中 11 个「自上游那次解密后源文件未再变化」的库，与上游 `output/databases/` 产物
**sha256 完全一致 11/11**。（另外 5 个源文件已变化、3 个上游无产物，无法比对。）

**HMAC 公式的确定方式**：对 9 种候选组合（`ct` / `ct+iv` / `iv+ct` / ±页号大小端 等）
在 `weclaw.db` 第 1 页、`message_0.db` 第 2 页、`contact.db` 第 5 页上做唯一性测定，
只有 `ct+iv+pgnoLE` 命中 —— 不是猜的。篡改一字节后校验立即失败，已验证。

**关键推论：因为 IV 存在页内、每页独立 AES-CBC，所以「按页独立解密」成立 → 真正的增量刷新可行，
且可以直接处理 WAL 帧（WAL 帧里就是最新页数据）→ 新鲜度可达 WAL 级。**

### 2.2 最小可复现验证片段（保留以防参数漂移时重新校准）

```python
# 参数：slt = 文件前 16 字节；enc_key 由 PBKDF2 派生；页内最后 80 字节 = IV(16) + HMAC(64)
salt = blob[:16]
enc_key = hashlib.pbkdf2_hmac("sha512", bytes.fromhex(db_key_hex), salt, 256000, 32)
iv = page[4016:4032]                      # 第 1 页；第 ≥2 页为 page[4016:4032]（相对页首）
ciphertext = blob[16:4016]                # 第 1 页；第 ≥2 页为 page[0:4016]
plain = AES(enc_key).decrypt_cbc(ciphertext, iv)
# 第 1 页明文需补回前 16 字节魔数 b"SQLite format 3\x00"，再检查 offset 16 起为 10 00 02 02 50 ...
```

### 2.3 数据库结构（读上游已解密副本得出，可直接作为自研读取层的依据）

- `session/session.db`
  - `SessionTable(username, type, unread_count, is_hidden, summary, draft, status, last_timestamp,
    sort_timestamp, last_msg_locald_id, last_msg_type, last_msg_sender, last_sender_display_name, ...)`
    → `108` 行，**足以重建 `adapters.list_chats()` 的 chats[]**
  - `session_last_message(username, sort_seq, local_id, create_time, local_type, sender_username,
    preview, db_stem, table_name, built_at)` → **直接给出「会话 → 消息表名」映射，不用猜 md5**
- `message/message_0.db`
  - 每个会话一张 `Msg_<hash>` 表（当前 78 张），列为 `local_id, server_id, local_type, sort_seq,
    real_sender_id, create_time, status, ..., message_content, compress_content, packed_info_data,
    WCDB_CT_message_content, WCDB_CT_source`
  - `Name2Id(user_name, is_session)`：`real_sender_id` → 发送者
- `contact/contact.db`
  - `contact(3974)`：`username, remark, nick_name, alias, head_img_md5, local_type, ...`
  - `chat_room(52)` / `chatroom_member(2577)`：群与群成员
- 加密源库另有 `-wal` 文件（`message_0.db-wal` 常驻 4MB），**新消息先落 WAL**。

### 2.4 运行时能力（现状）

| 项 | 状态 |
|---|---|
| `cryptography 46.0.7` | ✅ 已在 `runtime/python-packages` |
| `_sqlite3` / `_hashlib` / `_lzma` / `_bz2` | ✅ 已在 `runtime/python/DLLs` |
| `zstandard` | ✅ **已补**：`0.25.0`（cp311 win_amd64 wheel，0.48MB，已装入 `runtime/python-packages`） |

> **为什么必须补 zstandard（实测证据）**：非文本消息的 `message_content` 列**本身就是 zstd 压缩的**，
> 字节开头就是 zstd 魔数 `28 B5 2F FD`；而 `compress_content` 列反而**全是空的**（近 19 万条里 0 条非空）。
> 压缩标志是 `WCDB_CT_message_content`：**CT=0 的 150769 条零压缩、CT=4 的 44050 条全部 zstd，零例外**。

### 2.5 WAL（实测结论，2026-09-30）

微信是 SQLite WAL 模式：**新消息先写 `-wal`，主库要等 checkpoint 才更新**。实测数据（75 秒观测窗口）：

| 时刻 | WAL 已提交帧 | 与主库内容不同的页 | 主库页数 |
|---|---|---|---|
| 16:08:10 | 28 | **20** | 38455 |
| 16:08:15 | 0 | 0 | **38456（+1，checkpoint 发生，WAL 重置）** |
| 16:08:55 | 17 | **13** | 38456 |
| 16:09:05 | 0 | 0 | **38457（+2，又一次 checkpoint）** |
| 16:09:15 | 14 | **10** | 38457 |

**结论：主库大约每 50–60 秒才更新一次，而 WAL 每 5 秒就积累 6–30 帧主库还没有的数据。**
端到端验证（把 WAL 已提交页覆盖进镜像后）：

```
仅主库      最新消息 = 16:09:15     主库 + WAL  最新消息 = 16:09:29     →  WAL 领先 14 秒
覆盖后镜像 pragma quick_check = ok
```

**WAL 帧格式**（已实测吻合，校验算法与 SQLite 官方一致）：

| 项 | 值 |
|---|---|
| WAL 头 | 32 字节：magic(`0x377F0682`) / version(`3007000`) / page_size(4096) / checkpoint_seq / salt-1,salt-2 / 头部校验和 |
| 帧 | 24 字节帧头 + 4096 字节页镜像（**加密页，与主库同一套方案 → 可直接 `verify_page`**） |
| 帧头 | 页号（**大端**） / commit 记录里的提交后页数（非 commit 为 0） / salt-1,salt-2 / 滚动校验和 |
| 提交语义 | **只认到最后一条 commit 帧**，之后的帧一律丢弃（与 SQLite 恢复规则一致） |
| WAL 重置 | salt 变化即代表进入上一代残留 → 立即停止（实测：重置时帧数归零，header 已换 salt） |

**两道闸门都必须有**：① SQLite 滚动校验和 → 判断帧是否写完整（撕裂写/上一代残留）；
② SQLCipher 页 HMAC → 判断内容+页号+密钥是否自洽（HMAC 输入里绑定了页号，比校验和更强）。

### 2.6 表结构与字段语义（实测，阶段 C 的地基）

> 这些是**逆向出来的口径**，不是文档抄来的。阶段 C 的实现直接依赖它们，改代码前先看这里。

| 项 | 结论 |
|---|---|
| 会话 → 消息表 | 表名 = `Msg_` + `md5(username)`（本机 20 个活跃会话命中 14 个，其余本机无消息记录；77 个群 == 77 张 Msg_ 表） |
| ⚠️ `session_last_message` | **它不在真实库里**，是上游 `/api/chat/session-last-message/build` 自建的缓存表 → 映射必须自己算 |
| 群判定 | **只能用 `username` 结尾 `@chatroom`**。`contact.local_type` 不能用来判群：实测 `local_type=1` 里群 3 个/非群 147 个，`local_type=2` 里群 74 个/非群 22 个 |
| 显示名 | `contact.remark`（备注）优先 → `nick_name` → 退回 username。实测群「44536055599@chatroom」remark=摸鱼群 / nick=摸鱼吹牛技术群 |
| `local_type` 位域 | 低位 32 位是**基础类型**，高位是**子类型**：`244813135921 = (0x39 << 32) \| 49`。基础类型实测分布：1=文本 / 3=图片 / 34=语音 / 42=名片 / 43=视频 / 47=表情 / 48=位置 / 49=appmsg / 10000=系统 |
| 群消息发送者 | 正文自带前缀：`wxid_xxx:\n内容`；单聊无前缀。也可以走 `real_sender_id` → `Name2Id.rowid` → `user_name`。我方 wxid 在 Name2Id 里的 rowid 用来判「我」 |
| 非文本正文 | 解压后是 XML：图片 `<img aeskey cdnthumburl …>`、语音 `<voicemsg voiceurl …>`、表情 `<emoji md5="…">`、位置 `<location …>`、appmsg `<appmsg><title>…<type>N</type>`、系统 `<sysmsg type="…">` |
| 图片定位信息 | 图片消息的 `message_content` 里**没有**完整 md5；`packed_info_data` 里是短值（如 `77ced89f`）。上游 API 的 `imageMd5`/`imageFileId` 应来自 `message_resource.db` → **归入 C6（媒体）再解决**，C2 阶段先留空 |
| ⚠️ 排序键 | **用 `create_time` 排序，不要用 `sort_seq`**。`sort_seq` 是**本机接收时间(ms)**、`create_time` 是**服务端时间(s)**，两者会毫秒级错位：实测群聊 500 条里有 2 处逆序（`sort_seq` 差 1ms、`create_time` 差 1s 且方向相反）。契约要求 `messages[].time` 单调递增，故以 `create_time` 为准、同秒内用 `sort_seq` 兜底。按此排序后 **500/2000 条全部单调** |
| `status` | 群聊分布 3:186626 / 2:4644 / 4:3602 / 0:1，且各状态**都有正文**（不是墓碑）→ **C2 不做过滤**，待 D1 接入后用上游真值差分定标（宁可多不可漏） |

**会话可见性规则**（C1 实测校准，两条独立证据吻合）：

```
保留 = is_hidden == 0
     且 username 在通讯录中，且 contact.local_type ∈ {1,2,3}     # 排除服务号占位(0)、openim(4/5/6)
     且 不以 gh_ 开头，且不是 weixin（微信团队）                  # include_official=false
```

- **锚点一**：按此过滤本机 108 条会话 → **92 条**，与上游日志同一时刻的 `rawCount: 108 → filteredCount: 92` 精确吻合（排除 16 条 = 不在通讯录 4 + local_type 0/5/6 共 6 + 公众号 5 + 微信团队 1）。
- **锚点二**：`filehelper`（文件传输助手）**必须保留** —— `docs/API_REFERENCE.md` §4.1 的示例数据里就带着它。

### 2.7 通讯录（C3 实测口径）

| 项 | 结论 |
|---|---|
| 分类依据 | **只能按 username 形态**：`@chatroom` = 群、`gh_` = 公众号、`@openim` = 企业微信、其余 = 好友。**`contact.local_type` 不能用作分类器**：实测 lt=0 里有 224 个真实好友（ohh / Nu / 恭喜發財…），lt=3 里也有 473 个自定义账号好友，lt 与"好友/群/公众号"没有对应关系 |
| 显示名 | `remark` → `nick_name` → `username` |
| 拼音有两套 | `quan_pin`/`pin_yin_initial` 是**昵称**的拼音（chunjidejingse / CJDJS）；`remark_quan_pin`/`remark_pin_yin_initial` 是**备注**的拼音（biaoyi / BY）。踩过的坑：一开始只读了前一套，导致按备注拼音 `biaoyi` 搜"表姨"命中 0。现在 `pinyinKey/pinyinInitial` 跟着**显示名**走，搜索同时覆盖两套 |
| 头像来源 | `head_image.db` 的 `image_buffer`（本机缓存字节，实测 JPEG）。**不要用 `contact.small_head_url`** —— 那是 `wx.qlogo.cn` 外网地址，离线取不到、也不该由本机代取。覆盖 **1516/3976 ≈ 38%**，缺图时客户端用文字头像兜底 |
| 本地**没有**的字段 | `gender / signature / country / province / city / region / sourceScene / source / addTime / addTimeText` —— `contact` 表里**根本没这些列**，一律留空，不编造。<br>⚠️ 踩坑记录：`select "gender" from contact` **不报错**，因为 SQLite 把未知的双引号标识符当字符串字面量，返回 `'gender'` 这个假值，看上去像"有数据" |
| 「已删除好友 / 黑名单」 | **本地无依据**：`delete_flag` 3974/3976 全为 0、`stranger` 表仅 7 行、无相关表。故 `formerFriends`/`blocked` 恒 0，这两个开关在本地模式下**不产生过滤效果** |
| 公众号细分 | **无法区分**：公众号的 `verify_flag` 全是 24。故全部计入 `officialUnknown`，`@openim` 计入 `officialEnterprises` |
| ⚠️ 与上游基准不可比 | 上游文档记的基准是「本机 495 条 = 好友 132 + 群 74 + 公众号 24 + 已删除 261 + 黑名单 4」。本地同一口径下是 **3976**（好友 3832 + 群 77 + 公众号 67）。群数 74 vs 77 很接近，但好友差 29 倍 —— 上游那 261+4 的"已删除/黑名单"在本机数据库里**找不到任何对应信号**，因此**不排除该基准是在另一个账号上测的**。此处不做猜测，等有真值再校准 |
| 性能 | `list_contacts` 服务端 **67ms**（构建 35.6 + 序列化 29.5），响应 **2.11MB**。头像是懒加载的，不影响此项。若将来觉得大，**去掉空字段可省 46%**（但那会改变"键是否出现"，属契约变更，暂不做） |

### 2.8 媒体（C6 实测：图片本体**未**攻克）

| 项 | 结论 |
|---|---|
| 图片本体位置 | `msg/attach/<md5(会话)>/<月>/Img/<hash>.dat`（原图）、`_h.dat`（HD）、`_t.dat`（缩略）；缓存副本另有 `cache/<月>/Message/<md5(会话)>/Bubble/<hash>_b.dat` |
| ⚠️ **命名键未知** | 文件名里的 32 位 hex **与消息 XML 的 `md5` 零交集**（615 个 `.dat` vs 3999 条图片消息，实测 0 命中）→ 无法由消息定位到文件 |
| `.dat` 头部 | 16 字节：`07 08 56 32 08 07 00 04 00 00 <4B LE 长度 @偏移 10..13> 01 xx`；**文件大小 = 该长度 + 1055**（7 个样本恒等）。⚠️ 踩坑：长度字段在 **10..13** 不是 8..12，读错会得到天文数字 |
| 消息 XML 能给什么 | `aeskey`(16 字节)、`md5`(＝契约的 `image_md5`)、`fileid`(图片常为空)、`cdnbigimgurl`(内含 UUID)；视频另有 `newmd5`/`cdnvideourl`；语音有 `voiceurl`/`voiceformat=4`(SILK) |
| **已排除**的方案 | ① 用消息 XML 的 aeskey 做 AES-ECB / CBC（`[16:]`、`[0:]`、`CBC0`、按 decl 截断共 4 种）→ **29036 次尝试 0 命中**（1414 个 `.dat` × 时间窗内至多 40 个候选密钥）。<br>② 尺寸配对 + AES-ECB 逐块判定 → **43602 次尝试 0 命中**（7267 个候选密钥 × 6 对）。<br>⚠️ **但②的前提不成立**：那 6 对是"同会话内 `decl` 尺寸相等"配出来的，实测把 **2026-03 的 `.dat`** 配到了 **2026-09 的缩略图** —— 属尺寸巧合。**没有可信的「密文↔明文」配对，就不能把 AES-ECB 一票否决**，只能说"简单方案没命中"。 |
| 另外两把密钥 | `image_xor_key` = `0xDC`（1 字节）、`image_aes_key` = `a315804ea1244fe1`（8 字节）—— **都不是标准 AES 密钥长度**，用途未知。已连带试过 `ljust(16/24/32, 0)`、`repeat` 等 7 种构造，均未命中 |
| 尺寸线索 | `decl`（偏移 10..13 的 4 字节 LE）与文件大小恒差 **1055**；且它**恰好等于某些明文缩略图的字节数**（如 decl=3788 ↔ 一个 3788 字节的 Thumb）→ 疑似"解密后大小"，但**因配对不可信，此语义尚未证实** |
| 明文可用物 | `msg/file/<月>/<真实文件名>`（PDF/XLSX 明文，文件名与 appmsg 的 `<title>` 同源）、`msg/video/<月>/<hash>_thumb.jpg`（明文 JPEG，但 hash 同样未知）、`cache/<月>/Sns/Video/*.mp4`（明文）、`ImageTemp/<local_id>_<时间>_*`（明文图片，**但覆盖仅 70/7282 ≈ 1%**）、`Thumb/<local_id>_<时间>_thumb.jpg`（明文，对应的却是 base=49 卡片而非 base=3 图片） |
| 朋友圈图片 | **同样加密**（`cache/<月>/Sns/Img/*` 196/196 密文）→ **C6 是 C4 图片部分的前置** | 
| 结论 | **图片本体需要独立逆向研究**（两个未知：文件命名键 + 加密算法）。当前信息下无法诚实交付 —— 硬凑只会得到"看着有图其实解错"的结果。<br>**重要背景**：上游本就坏了，图片功能**现在本来就是不可用的**，所以不做 C6 不构成功能回归，只是"没改善"。 |

**下一步可行路径（任选，尚未执行）**：
1. **做出「可信配对」再攻击**（最推荐）：目前唯一缺的就是一对可信的「密文 ↔ 明文」。可用**两个独立约束交叉定位**：`Thumb/<local_id>_<ctime>_thumb.jpg` 的 `ctime` 能定位到消息，而消息下载该图时会把 `.dat` 的 **mtime** 落在同一时刻附近；再用 `decl == 明文字节数` 做第二重约束。两个约束同时命中才算一对，之后：
   - 若 `E_K(明文首块) == 密文首块` 成立 → AES-ECB + 该密钥，**一步破**；
   - 若不成立，用 `明文 XOR 密文` 看是否出现规律（非零且无周期 → 排除简单 XOR/流密码）。
2. **样本扩容**：换图片更多、更可能被查看过的会话，明文副本（`ImageTemp`）会更多，配对概率上升。
3. **接受现状**：图片继续不显示（客户端已有 `[图片]` 标签 + 文字头像兜底，不会崩）。
   **注意**：上游本就坏了 → 图片功能**现在本来就是不可用的**，所以不攻 C6 不构成功能回归，只是"没改善"。

### 2.9 朋友圈（C4 侦察，2026-09-30）

> 数据源：`db_storage/sns/sns.db`（本机 2.67MB，653 页）。**上游从未解密过 sns.db**
> （`output/databases/<账号>/` 里没有它），因此**没有上游产物可比对**，
> 以下全部是直接读原始表 + 逐字节解析 XML 得到的结论。

**表清单（13 张，只有 4 张有数据）**

| 表 | 行数 | 结论 |
|---|---|---|
| `SnsTimeLine` | **410** | **主时间线**。列只有 4 个：`tid, user_name, content, pack_info_buf` |
| `SnsMessage_tmp3` | 30 | 与我相关的互动通知（点赞 / 评论）。全部 `to_username = 自己` |
| `SnsTopItem_1` | 1387 | **不用**。是"谁发过什么"的已读状态缓存（`tid+username+is_read`，同一个 tid 重复多行） |
| `SnsMainTimeLineBreakFlag` / `SnsUserTimeLineBreakFlagV2` | 151 / 210 | **不用**。分页断点缓存（`tid_heigh_bit / tid_low_bit`），本地全量读取不需要 |
| `SnsAdTimeLine` / `SnsDraft` / `SnsNoteVoice` / `SnsIgnoreDataItem` / `SnsErrorMessage` / `SnsPublishTask` / `SnsPendingDraftDeletionTable` | 0 | 空表，不处理 |

**★ `SnsTimeLine.content` 是纯 XML，不压缩**

- 列类型实测是 **TEXT**（`sqlite3` 直接返回 `str`），**没有 WCDB 压缩标志**，也不是 zstd
  （`convert.decode_content` 的 CT=4 逻辑在这里用不上，但读方仍应容忍 `bytes`）。
- `pack_info_buf` 实测只有 2 字节（`0a 00`），**无信息量，忽略**。

**★ `SnsDataItem` 的完整元素树（实测，`<like_user_list>`/`<comment_user_list>` 是 `LocalExtraInfo` 的子节点）**

```
<SnsDataItem>
  <TimelineObject>
    <id>15021192649805607579</id>          ← 无符号 64 位，**与 SnsTimeLine.tid 是同一个 64 位值**（见下）
    <username>wxid_xxx</username>          ← 发布者
    <createTime>1790665704</createTime>     ← 秒
    <contentDesc>正文</contentDesc>
    <publicUserName>gh_xxx</publicUserName> <publicBrandContactType>1</publicBrandContactType>
    <sourceNickName>科博士 Canrd</sourceNickName>    ← 只在 type=3（文章）出现
    <location latitude=".." longitude=".." poiName="深圳市 · 楼村湿地公园" poiAddress=".." city=".."/>
    <ContentObject>
      <title>…</title> <description>…</description> <contentUrl>…</contentUrl>
      <type>1|3|15|28|…</type>              ← 契约的 post.type
      <mediaList>
        <media>
          <id>…</id> <type>2</type> <sub_type>0</sub_type>
          <thumb type="1" key="…" enc_idx="1" token="…">http://…/150</thumb>
          <url   type="1" md5="…" key="…" enc_idx="1" token="…">http://…/0</url>
          <size width="1280" height="1707" totalSize="126019"/>
        </media>
      </mediaList>
      [<finderFeed>视频号的 nickname/desc/avatar/objectId/mediaList(url/thumbUrl/coverUrl/width/height/videoPlayDuration)</finderFeed>]
      [<mmreadershare>…</mmreadershare>]
    </ContentObject>
    <appInfo/> <actionInfo/> <weappInfo/> <streamvideo/>
  </TimelineObject>
  <LocalExtraInfo>
    <tid>14879205261013758585</tid>        ← **无符号**写法，与 <id> 同值
    <nickname>210901020625小顺子</nickname> ← ★ **displayName 的唯一来源**（410/410 全部非空，**无需查 contact 表**）
    [<like_user_list>   <user_comment type="1" …> … </user_comment> </like_user_list>]
    [<comment_user_list><user_comment type="2" …> … </user_comment> </comment_user_list>]
    <is_bidirectional_fan/><is_rich_text/><like_flag/><no_change/><ext_flag/>
  </LocalExtraInfo>
</SnsDataItem>
```

**★ `tid` 与 `<id>` 是同一个 64 位数（实测 8/8 逐字节相等）**

- `tid`（列）是**有符号** int64，`<id>` 是同一值的**无符号**写法：
  `tid = (uint64)id - 2^64`（如 `15021192649805607579 → -3427622702053772589`）。
- 推论：`SnsMessage_tmp3.feed_id`（有符号）与 `tid` **同一域，可直接相等比较**，
  不需要任何转换；`comment.client_id` 的第一段就是该值的**无符号**写法。
- **排序：按 `tid desc` 与按 `createTime desc` 的结果完全一致（410/410 位置全同）**
  → 分页直接 `order by tid desc`，既是最新优先，又与微信本机顺序一致。

**★ 点赞 / 评论就在 XML 里（此前以为要另找表，实际 `SnsMessage_tmp3` 只覆盖"与我相关"的 30 条）**

| 项 | 结论 |
|---|---|
| 位置 | `LocalExtraInfo/like_user_list/user_comment`（`type=1`）与 `LocalExtraInfo/comment_user_list/user_comment`（`type=2`） |
| 覆盖 | **410 条里 84 条有点赞、16 条有评论**；`user_comment` 共 268 个（like 内 152 / comment 内 116 / 顶层 0） |
| 「我」发起的 | 该 post 的 `user_comment.username == 自己` 就是"我赞过 / 我评论过"；`like_flag` 恒 0，**不可用** |
| `user_comment` 字段 | `comment_id, comment_64id, username, nickname, [content], source, type(1赞/2评论), create_time, is_local_added, ref_comment_id, ref_comment_64id, [ref_username], is_rich_text, b_deleted, comment_flag, comment_content_type(0纯文本/1带表情/2带图片), comment_imageinfo_count, comment_emojiinfo_count, imagelist, emojilist` |
| 图片评论 | `imagelist/imageinfo{url, token, key, enc_idx, thumb_url, thumb_url_token, thumb_key, thumb_enc_idx, width, height, file_size, media_id, md5}` —— **同样需要 C6 才能取图** |
| 表情评论 | `emojilist/emojiinfo{md5, width, height, desc, sns_emoji_data{url, encrypt_url, aes_key, extern_url, extern_md5}}` |
| `SnsMessage_tmp3` 的正确定位 | 只有 30 行、**全部 `to_username = 自己`** → 它是"朋友圈消息（别人对我的互动）"，**不能**当时间线的点赞/评论源，只能做补充 |

**`ContentObject/type` 实测分布（410 条）**：`1`:173（图文）、`7`:65、`54`:53、`28`:39（视频号）、
`3`:31（公众号文章）、`15`:27（小视频）、`2`:20、`30`:1、`5`:1。

**★ 上游 UI 的 `sourceName` 兜底逻辑（从上游前端 bundle 反推，供 C4 对齐参考）**

上游 `resources/ui/_nuxt/*.js` 里对 `sourceName` 有一段明确逻辑：

```js
source = post.sourceName?.trim() || hostname(post.contentUrl || post.media[0].url)
         || (post.type === 42 ? "音乐" : "外部分享")
```

即：**后端给 `sourceName` 就用；没给则由前端按 contentUrl 的主机名兜底，再兜底成"音乐 / 外部分享"**。
XML 侧唯一同义的字段是 `<sourceNickName>`（实测只在 type=3 文章出现，值是公众号名，如「科博士 Canrd」）。
**⚠️ 注意**：`_nuxt/*.js` 是**上游自己的前端**，我们的新客户端（`ChatlogAI.Client`）**没有**这段兜底，
只显示 `location · sourceName`。故"后端给什么"直接决定用户看到什么 —— 属**必须拍板**的口径（见待定项）。

**待定项（已拍板，2026-09-30）**

1. `sourceName` = **只用 `<sourceNickName>`，为空即空**。不复刻上游前端的兜底链
   （contentUrl 主机名 → 音乐 → 外部分享）：那是展示层逻辑，后端不编造。
2. **不做** `official` / `finderFeed` 的结构化透传（键保留、值为 `null`）。
   依据：我方客户端（`ChatlogAI.Client`）目前**完全没有读这两个字段**，只用 `title` + `contentUrl` 兜底展示。
3. `cover` / `covers` 本地无数据源 → 恒 `null`。
4. `/api/sns/media`、`/api/sns/video` **继续走上游**（图片本体阻塞于 C6）→ 本地模式下这两个端点
   在能力表中**不注册**，因此不会被误切到本地。
5. `sns_sync_latest` = **真同步一次镜像 + 回报真实数字**（`scanned`/`upserted`/`maxId`/`lastMaxId`/`elapsedMs`），
   不是空操作。`upserted` 的基准游标只活在内存里（重启后首次为 0），`force` 本地无意义、忽略。

### 2.10 导出（C5 侦察，2026-09-30）

> **结论先行：整个"导出"只有朋友圈一处需要自研。** `server.py` 里**根本没有**聊天 / 通讯录导出端点 ——
> 那两块是客户端拿已本地化的数据自己排版的，所以它们**早在 C2 / C3 完成时就已经不依赖上游了**。

| 导出项 | 谁在做 | 依赖上游？ |
|---|---|---|
| 聊天导出 | 客户端 `ExportViewModel` → `GET /api/messages`（C2）+ `ChatExporter` 排版 + 另存为 | **否** |
| 通讯录导出 | 客户端 `ContactsModuleViewModel` → `GET /api/contacts`（C3）+ `ContactExporter` | **否**（§14.7.4 明确"自己格式化"） |
| **朋友圈导出** | **后端**：`POST /api/sns/export` → 轮询 `GET /api/sns/export/{id}` → `/download` | **是**（唯一一处） |

**★ 客户端读的字段（逐行读 `MomentsExportViewModel.DescribeProgress` 得到，不是猜的）**

```
任务对象 = { exportId, status, zipReady, error,
             progress: { postsTotal, postsExported, mediaPrepareTotal, mediaPrepared } }
```

三条必须遵守的判据，否则界面会卡在"导出中…"或提前去下载空包：

1. `FindTaskObject` 要求对象里有 **`zipReady`**，或同时有 `exportId` + `status`。
2. **`zipReady` 优先于 `status`**：`ResolveState` 先看 `zipReady`，为 `false` 一律算"还在跑"。
   这是客户端刻意绕开上游"status=done 但 ZIP 还在打包 → 下载撞 409"的设计，我们必须两个字段都给。
3. 完成态字符串白名单是 `succeeded / done / finished / complete / completed`；
   失败态是 `failed / fail / error / canceled / cancelled`；失败原因读 `error`。
   ⚠️ `success` / `ok` **不在完成态白名单里** —— 那是**信封**的取值，收进去会误判成完成。

**已拍板的边界（三条都要遵守，不要"顺手改回去"）**

| # | 决定 | 理由 |
|---|---|---|
| 1 | **包里没有图片**，正文用 `[图片 ×N]` 占位，并在 ZIP 里附 `README.txt` 说明原因 | C6 未攻克：图片在本地只有密文（`cache/<月>/Sns/Img/*` 实测 196/196）。导出包是外发物，不能让接收方以为"导出漏了图" |
| 2 | **excel = UTF-8 带 BOM 的 CSV**（`moments.csv`） | 本地没有 xlsx 库；客户端把产物整体当 ZIP 下载、不解析内部文件名，与上游真 `.xlsx` 兼容性一致。BOM 是 Excel 中文乱码的解药 |
| 3 | **导出内容里不含 `token` / `key` / `enc_idx`** | `media[].urlAttrs` 那两串是**解密凭据**（§14.8.2 的后端代理设计就是刻意不下发）。导出文件会被转发、归档、外发，写进凭据等于泄露。故 `media` 在导出里只剩 `{type, width, height}` |

> 踩坑记录：给导出做"凭据零泄漏"断言时，先写成了 `assert "token" not in document` → **当场误报**。
> 原因是本机时间线里真有人聊 "Sa-Token" 框架、还有网易云分享链接带 `shareToken=`。
> 正确的判据是"把 `urlAttrs` 里的**值**全部收集起来，断言一个都不出现"（实测 1818 个值）。

**实现选择（含被否掉的方案）**

| 选择 | 说明 |
|---|---|
| **同步生成**，任务一出生就是 `zipReady=true` | 410 条且无图片，实测渲染 + 打包 74~126ms。上游是"其实也很轻却做成异步任务"，我们不必跟着做；真起后台线程只会多出"任务跑到一半进程退出"这类额外状态 |
| 任务表**只在内存** | 上游流程是"创建 → 每秒轮询 → 下载"，全程几十秒。为这几十秒做持久化是过度设计；重启后旧任务不可查（`503 导出任务不存在或已过期`） |
| 产物落 `data/exports/<id>.zip` | 与镜像同级；只通过 `/download` 回传，**客户端与用户都看不到这个目录**（与"上游输出目录不下发"同一原则） |
| 清理：**TTL 24h + 只留最近 50 个**，在 `__init__` 与每次 `create` 后各跑一次 | 导出是低频动作，不值得常驻清理线程；两个维度同时挡，避免磁盘无限增长 |
| ZIP 内文件名与包名**全用 ASCII** | 中文名在部分解压工具上会乱码；而 `server._export_filename` 会把非 ASCII 打成 `_`，给中文名只会得到一串下划线 |
| ⚠️ 已知小瑕疵 | `scope=selected` 但没给 `usernames` 时返回的是 **503** 而不是 400（验证在 `exports.SnsExportManager` 里、异常被端点统一转成 503）。客户端本身已在前端拦住这种情况，不影响使用，故不改 |

---

## 3. 架构总览
```
┌──────────────────────────────────────────────────────────────┐
│  app/server.py（业务层：AI 分析 / 自动回复 / 定时发送 / REST） │
└───────────────┬──────────────────────────────────────────────┘
                │ 契约不变（docs/API_REFERENCE.md）
┌───────────────▼──────────────────────────────────────────────┐
│  app/adapters.py  ← 实现替换点（HTTP 适配器 → 本地库适配器）   │
└───────────────┬──────────────────────────────────────────────┘
                │
┌───────────────▼──────────────────────────────────────────────┐
│  自研数据层（新增，建议 app/localdb/）                        │
│  ├ keys.py     密钥提取（db_key / image keys）                │
│  ├ cipher.py   页解密 + HMAC 校验（参数见 §2.1）              │
│  ├ wal.py      WAL 帧解析与提交语义                            │
│  ├ mirror.py   增量镜像（首轮全量 + 之后按页/按帧）            │
│  ├ chats.py    会话列表                                        │
│  ├ messages.py 消息（含 compress_content 解压、local_type 语义）│
│  ├ contacts.py 联系人与群成员                                  │
│  ├ sns.py      朋友圈（实际文件名是 moments.py）              │
│  ├ exports.py  朋友圈导出（聊天/通讯录导出在客户端，见 §2.10）    │
│  └ media.py    图片解密代理 / 语音 / 视频缩略图                 │
└───────────────┬──────────────────────────────────────────────┘
                │
       微信本地数据（D:\app\wechat\Files\xwechat_files\<wxid>_<hash>\db_storage）
```

**替换原则**：`server.py` 及其以上**一行不改**；只把 `adapters.py` 内部的数据获取方式换掉，
并新增一层配置开关（`config.json` 中 `data_source: "local" | "upstream"`）以便随时回退、便于差分验证。

---

## 4. 阶段划分（按技术依赖顺序，非功能裁剪）

> 最终范围 = 全部模块自研（§1 G3）。阶段划分只决定**开工顺序**。

### 阶段 A｜密钥自主（★ 已完成 spike，结论见 A2）

| 任务 | 说明 | 状态 |
|---|---|---|
| A1 | 数据目录 / 账号发现（替代上游 `/api/chat/accounts` 的作用） | 待开始 |
| A2 | `db_key` 提取 | **spike 完成，路线 1/2 实测排除，仅剩 Hook** |
| A2b | 密钥来源抽象成可替换接口 + 冗余备份 + 失效自检 | 待开始（建议提升为优先项） |
| A3 | 图片密钥提取（`image_xor_key` / `image_aes_key`） | 待开始 |

#### A2 spike 结论（2026-09-30 实测，本机 Weixin 4.1.12.26）

用**已知的 `db_key` 当针**去反查它的存放位置，三条候选路线的实测结果：

| 路线 | 做法 | 结果 | 证据 |
|---|---|---|---|
| ① 磁盘明文 | 扫账号目录 `config\*`、`resource\*`、`xwechat_files` 顶层与 `logs`（28 个文件 / 5.4MB），8 种编码 | ❌ 零命中 | 无任何文件含明文 key |
| ①' DPAPI 兜底 | 同批文件搜 DPAPI 块 magic（`01000000D08C9DDF…` / `D08C9DDF`） | ❌ 零命中 | 配置里没有 DPAPI 保护块 |
| ② 进程内存 | 5 个 `Weixin.exe`，`PRIVATE 0.28GB + MAPPED 0.36GB` **全扫**，8 种编码（明文/大写/UTF-16/带 BOM/32 字节/反转/Base64×2） | ❌ 零命中 | 内存里**只有 salt**，没有任何形式的 key |
| ③ Hook `Weixin.dll` | 未实施 | ⏸ 唯一剩下的路线 | 社区结论：4.1+ 明文 key 不常驻内存，须 DLL 注入 + Hook |

**内存里唯一能稳定命中的是 `PRAGMA cipher_salt = 'x''<salt_hex>'''` 这类 SQLCipher 语句**（8 处），
说明 WCDB 是**显式把 salt 传进去**的（而非从文件头读），也印证了「key 与 salt 是成对提供」的设计。

**两条重要的旁证事实：**

1. **非管理员也能打开 `Weixin.exe` 的 `PROCESS_VM_READ`**（实测 5 个进程全部成功）——
   即内存扫描这条路**本来不需要提权**，门槛比预期低；但**扫不到就是扫不到**。
2. `db_key` 是**账号级、全局**的（已验证：同一把 key + 各库自己的 salt 可分别解密 `message_0.db` /
   `session.db` / `contact.db`）。它**不随每次启动变化**，因此「拿到一次即可长期复用」。

#### A2 的结论与建议顺序调整

- 自研密钥提取**不是"做不出来"，而是成本与收益严重不成比例**：只剩 DLL 注入 + Hook 一条路，
  需要 native 代码、注入器、微信版本偏移维护、并应对其安全机制，且微信一升级就要重做；
  而它换来的只是**一把我们已经拥有、且不会频繁变化的 key**。
- 因此建议把 A2 的定位从"必须先做"改为：**密钥来源做成可替换接口（A2b），先用现有 key；
  Hook 方案列为独立 spike，不阻塞 B/C**。
- 无论如何，**A2b 的三个动作应当立刻做**（它是整条链路的根）：
  1. key 取出后**冗余备份**到多处并校验；
  2. 启动时**自检**：解第 1 页校验页头（§2.1），失败即明确报「密钥失效，需重新提取」；
  3. 记录 key 的**失效条件**（换账号 / 改密码 / 重新登录设备 / 重装微信）。

> **A2 的金标准**：自研提取结果必须与 `C:\ProgramData\ChatlogAI\data\wechat\output\account_keys.json`
> 中现有的 `db_key` **完全一致**（同机同账号可比对）。本机该值为 64 位十六进制，已验证可用于解密。

#### A2b 密钥备份（✅ 已完成 2026-09-30）

**决策（用户拍板）**：密钥**允许先用上游产出的那一把**——程序首次使用时就引导用户完成授权并**复制密钥到配置文件**；
若用户当时没复制，界面提供一个「重新走一遍上游授权」的按钮即可重新生成。自研 Hook 不再作为前置条件。

**备份动作与校验结果**（三份副本，内容一致，均为 `version:1` + 1 个账号 + 逐库校验明细）：

| 位置 | 用途 |
|---|---|
| `C:\ProgramData\ChatlogAI\config\wechat-keys.json` | **正规副本**（未来 `keys.py` 的首选读取源） |
| `C:\ProgramData\ChatlogAI\data\wechat-keys.backup-20260930.json` | 带日期的快照，防误覆盖 |
| `C:\ProgramData\ChatlogAI\logs\.keys-backup-20260930.json` | 第三份冗余 |

**校验方式**：对 `db_storage` 下全部 `.db` 逐个「取各自 salt → `PBKDF2(db_key, salt, 256000)` → 解第 1 页 →
校验页头 `10 00 | 02 02 | 50 | 40 20 20`」。**结果 19/19 全部通过**，
`verified: true` 已写入备份文件。（这次校验同时把 §2.1 的参数从"1 个库验证"升级为"19 个库验证"，
并再次确认 `db_key` 是**账号级 passphrase**：19 个库共用同一把 key、各自独立 salt。）

**失效条件**（任一条发生即需重新取密钥）：更换微信账号 / 修改微信密码 / 重新登录设备 / 重装微信 / 微信大版本升级。

**未来读取优先级**（A2b 接口的设计约定，待实现）：
`config/wechat-keys.json`（本备份） → 上游 `output/account_keys.json` → 引导用户在界面粘贴 / 重跑上游授权。

> ⚠️ 三份副本均含**明文密钥**。它们位于 `C:\ProgramData\ChatlogAI`（**不在 git 仓库内**，无提交泄漏风险），
> 但仍属禁止外发内容。建议用户额外手工复制一份到离线介质，以防本机损坏。

### 阶段 B｜解密引擎

| 任务 | 说明 | 状态 |
|---|---|---|
| B1 | 页解密（§2.1 参数） | ✅ **完成**：`app/localdb/cipher.py`，与上游产物逐字节一致 11/11 |
| B2 | HMAC 校验（范围已实测确定） | ✅ **完成**：`page_hmac` / `verify_page`，篡改一字节即失败 |
| B3 | WAL 解析（帧头 / 提交标记 / 半写帧靠 HMAC 丢弃 / checkpoint 后重置） | ✅ **完成**：`app/localdb/wal.py`，实测 WAL 比主库新 14 秒 |
| B4 | 增量镜像：首轮全量 → 之后只处理「主库变化页 + WAL 新增帧」 | ✅ **完成**：`app/localdb/mirror.py`，与全量重解**逐字节一致** |

**已落地的代码**：`app/localdb/__init__.py`、`cipher.py`、`wal.py`、`mirror.py`。
`decrypt_first_page` 就是 A2b 承诺的**启动自检**：密钥错 / 参数漂移 / 文件损坏一律显式抛
`CipherError`，不会像上游那样静默降级。

#### B4 实测结果（2026-09-30，`message_0.db`，70 轮 × 1s）

| 场景 | 成本 | 说明 |
|---|---|---|
| 首轮建立镜像 | 1.11s | 全量 38468 页；此后进程重启只需再做一次 |
| 幂等（无变化） | **0.005s** | 无扫描、无写入 |
| 正常路径（只读 WAL 新帧） | **0.008–0.03s** | 每轮 6–20 帧 |
| WAL 重置回主库对账 | **0.16s，只改 6–20 页** | 全程跨过 4 次真实 checkpoint，**没有一次全量重写** |

**正确性对账**：与「独立全量解密（主库 + WAL 覆盖）」比对 ——
**38468 页逐页比对，不同 0 页（0.000%）**；`quick_check = ok`；最新消息时间一致（镜像未落后）。
70 轮一致性检查**异常轮数 0**。

> 过程中发现并修复一个真实缺陷：WAL 的 commit 记录会把库**撑大**（`db_size` 可能大于主库页数），
> 而初版在"不扫主库"的分支里按主库页数 `truncate` 镜像，把刚拿到的扩展页砍掉 →
> 产出"页头与文件长度不符"的坏库（`database disk image is malformed`）。
> 修法：**只在有权威信息时才对齐尺寸**（主库扫描结果 / WAL commit 记录），其余情况一律不动文件长度。
> 这个 bug 只有靠"1 秒轮询 + 每轮一致性检查"的 torture 测试才暴露得出来。

### 阶段 C｜读取层（替代 `adapters.py` 的全部能力）

| 任务 | 对应现有契约 | 状态 |
|---|---|---|
| C0 | 补 `zstandard` 依赖（实测必需） | ✅ **完成**：0.25.0 已装入 `runtime/python-packages` |
| C1 | 会话列表 | ✅ **完成**：`app/localdb/chats.py`，会话数 **92 == 上游基准 92** |
| C2 | 消息（含压缩正文解压、`local_type` → 渲染语义标签） | ✅ **完成**：`app/localdb/messages.py`，4 个会话全部单调正序、零 XML 泄漏 |
| C3 | 联系人 + 群成员 + 头像 | ✅ **完成**：`app/localdb/contacts.py`（群成员接口上游本就没有，见下） |
| C4 | 朋友圈时间线 / 用户 / 媒体 | ✅ **完成（时间线+用户；媒体仍走上游，见 C6）**：`app/localdb/moments.py` |
| C5 | 导出（聊天 / 通讯录 / 朋友圈） | ✅ **完成（只需做朋友圈导出，见 §2.10）**：`app/localdb/exports.py` |
| C6 | 媒体（图片解密代理 / 语音 / 视频），含图片 md5/file_id 来源 | ⏸ **阻塞于 `.dat` 逆向**：命名键未知 + 加密算法未破，已排除 29036 次尝试，见 §2.8 |

**C1 已落地的代码**：`app/localdb/keys.py`（密钥来源，备份优先）、`store.py`（镜像同步 +
只读连接 + 消息表归属解析）、`convert.py`（时间/群判定/`local_type` 位域/压缩解压 等公共转换）、
`chats.py`（会话列表）。实测耗时 **0.018s**，`last_time` 全部符合客户端正则。

**C1 的三条验收线**：① 会话数 92 对齐上游；② 字段与 §4.1 完全一致；③ `filehelper` 在列表内、
`weixin` 不在列表内。

**C4 已落地的代码**：`app/localdb/moments.py`（`list_timeline` / `list_users` / `sync_latest`）+
`service.py` 三个同名方法 + `server.py` 三个端点切到 `get_source("sns")`。

**C4 的验收证据（隔离数据目录 + 独立端口 18766，`wechat_api` 指向死端口以证明不回退）**

| 项 | 结果 |
|---|---|
| `GET /api/sns/timeline?limit=3` | **200**，`source=local`、`sourceFallback=false`；单次 ≈87ms |
| 时间线总数 | **410**（`SnsTimeLine` 全量），`hasMore` 与 `offset` 边界均正确 |
| 字段对齐 | timeline 项的**键集合与 `adapters.normalize_sns_post` 完全相同**（双向差集为空） |
| `media[]` | `urlAttrs` 5 键 / `thumbAttrs` 4 键（缩略图**没有 md5**），与 §14.8.1.1 实测逐字一致 |
| 点赞 / 评论 | 84 条有点赞、16 条有评论；实测样本 `真美` / `嘻嘻嘻`（带 `refUsername`）正常渲染 |
| `keyword` / `usernames` 过滤 | `电池` → 命中科路得系列；按 username 过滤全部命中 |
| `GET /api/sns/users` | **102 人 == 时间线 distinct user_name**，`postCount` 求和 **= 410**；≈74ms |
| `POST /api/sns/sync-latest` | 两次调用 `lastMaxId` 正确承接、无新帖时 `upserted=0` |
| 热切换 `local → upstream → local` | `capabilities.sns` 三态随之变化，timeline 200 → 503 → 200 |
| `/api/sns/media` | **503**（仍走上游）→ 证明路由是**能力级**的，媒体没被误切 |
| 回归 | `/api/chats` 92、`/api/contacts` 3976、`/api/messages` 正常 |

> **C 阶段金标准**：同一时刻「上游 HTTP 输出」与「自研输出」做差分比对；
> `docs/API_REFERENCE.md` 的字段名、类型、排序、边界值必须逐项对齐。
> ⚠️ **C4 是本阶段第一个"无上游产物可比对"的模块**（上游从未解密 `sns.db`，
> `output/databases/` 里没有它），因此验收只能靠"契约键集合对齐 + 内部一致性
> （`postCount` 求和 == 时间线条数、distinct user == users 条数）"，**不能声称与上游数值差分通过**。

**C5 已落地的代码**：`app/localdb/exports.py`（`SnsExportManager` + 4 种渲染器）+
`service.py` 三个同名方法 + `server.py` 三个端点切到 `get_source("export")`。

**C5 的验收证据（同一隔离环境）**

| 项 | 结果 |
|---|---|
| 4 种格式 `create → 轮询 → download` 全链路 | **全部 200**，ZIP 内恒为 `moments.<ext>` + `README.txt` 两项 |
| `export` 任务对象 | `{exportId, status:"done", zipReady:true, error:"", progress:{postsTotal:410, postsExported:410, …}}` —— 与客户端探针逐字对齐 |
| 单次 create 耗时 | **74~126ms**（含解析 410 条 XML + 渲染 + 打包），产物 31~62KB |
| `Content-Disposition` | `filename="sns-export-…-html.zip"; filename*=UTF-8''sns-export-…-html.zip`（ASCII 双写，无目录穿越） |
| `scope=selected` | 2 人 → **26 条**；1 人 → **24 条**，实测集合精确命中 |
| **凭据零泄漏** | 用时间线里 **1818 个** `token`/`key` 值逐一比对 4 种产物的正文与 README → **0 命中** |
| CSV | 首字节是 `\ufeff`（BOM），表头 13 列，Excel 可直接打开 |
| 错误路径 | 无 `usernames` 的 selected → 503；非法 format → 400；未知 id（查/下载）→ 503 |
| 产物落盘与清理 | 落在 `<DATA_ROOT>/data/exports/`；重启后任务表清空（旧 id 不可查）而文件按 TTL 仍在 |
| 热切换 `local → upstream → local` | `capabilities.export` 三态随之变化，create 200 → 503 → 200 |
| 回归 | `chats=92`、`contacts=3976`、`sns timeline`、`users=102` 全部正常 |

### 阶段 D｜接入与下线

| 任务 | 说明 | 状态 |
|---|---|---|
| D1 | `config.json` 增加 `data_source` 开关，可一键切 `local` | ✅ **完成**（**能力级路由**：chat / contacts / sns / export 四类） |
| D2 | **默认切到 `local`** | ✅ **完成**：`config_defaults.py` 与 `ConfigBootstrapper.cs` 的默认值都改成 `local`；现有 `config.json` 也补了字段 |
| D3 | **移除 `runtime/wechat`** | ✅ **完成**：299.6MB 已删（仓库 + 打包脚本 + 客户端启动链三处同步改动），安装包 **87.3MB** |
| D4 | **密钥手动导入**（原计划外，但它是 D3 的前置缺口） | ✅ **完成**：`/api/keys/status` + `/api/keys/import` + 「微信账号」窗口的密钥区 |

**D2 的改动（3 处，缺一不可）**

1. `app/config_defaults.py` 的 `DEFAULT_CONFIG["data_source"]`：`upstream` → `local`；
2. `client/.../Bootstrap/ConfigBootstrapper.cs` 的 `DefaultConfig["data_source"]`：同上（两处必须同步，注释里互相点名）；
3. `%ProgramData%\ChatlogAI\config\config.json` 补上 `"data_source": "local"`。

> 为什么第 3 步其实可有可无：Python 侧 `load_config()` 是"**默认值打底 + 文件叠加**"
> （`default_config = dict(DEFAULT_CONFIG)` 再 `update(文件内容)`），所以改了默认值后，
> 即使文件里没有这个键也会取到 `local`。仍然补上是为了让"当前生效值"在配置文件里可见 ——
> 免得以后有人对着一个没有该键的文件猜它到底是什么模式。

**D3 的改动（4 处，任何一处漏掉都会"删了文件程序起不来"）**

| # | 位置 | 改动 |
|---|---|---|
| 1 | `ConfigBootstrapper.RequirePaths()` | 必需项去掉 `wechat-backend.exe` 与 `resources\ui` |
| 2 | `build-installer.ps1` 的 `$requiredPaths` | 同步去掉那两项（换成 `app\localdb\service.py` 与 `runtime\python-packages\zstandard`）；脚本注释原本就写着"与 RequirePaths() 对齐"，两边是**必须同步**的 |
| 3 | `StartupOrchestrator.StartWechatServiceAsync()` | 改为**返回 `bool`**：文件不存在→直接跳过（不再白等 180 秒健康检查）；健康检查失败→Report 一句后返回 false，**不再让整个启动失败** |
| 4 | `StartupOrchestrator.RunAsync()` 的授权判定 | 只有 `upstreamReady && needsAuthorizeFlow` 才进授权页 |

> ⚠️ 第 4 条修的是一个**会静默卡死**的缺陷：授权页本身由上游提供（`http://127.0.0.1:10392`），
> 上游没了之后它打不开（只 Report 一句），而紧接着的 `WechatAuthWatcher.WaitAsync()` 会
> **永久等待一个永远不会出现的密钥文件** —— 界面一直停在"正在等待微信授权完成"，
> 不报错、不超时。上游一旦被移除，这条路径必然被走到，所以它必须在删除**之前**修掉。
>
> 另外**没密钥也不能拦住启动**：手动导入密钥的入口在「微信账号」窗口里，
> 拦住启动等于把唯一的补救入口一起关掉 —— 改为带提示继续启动，由界面引导用户导入。

**D4 的改动（为什么它必须先做）**

上游的授权页是**唯一**能"解析出 db_key"的东西。删上游之前，密钥只剩两个来源：
`config/wechat-keys.json`（A2b 时人工从上游产物复制过去的）与上游的 `account_keys.json`。
实测发现前者**曾经丢失**（一次"卸载时勾了删除数据"就没了），也就是说密钥当时是**单点**。
所以 D4 提供的不是"锦上添花"，而是"删上游之后还能换账号/换机器"的唯一通道：

| 位置 | 改动 |
|---|---|
| `app/localdb/keys.py` | 新增 `parse_import`（宽松解析两种形态）/ `verify_entry`（**实测解密**）/ `infer_account` / `save_accounts`（合并 + 原子替换）/ `describe_accounts` / `backup_path` |
| `app/server.py` | 新增 `GET /api/keys/status`、`POST /api/keys/import`；导入成功后调 `_reset_wechat_adapters()` 让新密钥**立刻生效**（本地服务按账号缓存，否则要等重启） |
| 客户端 | `AccountModels.cs` 新增 5 个 DTO；`ChatlogApiClient` 新增 2 个调用；`AccountWindowViewModel` + `AccountWindow.xaml` 新增「微信数据库密钥」区（来源/可用性/粘贴框/导入按钮） |
| `WechatAuthWatcher` | 改为同时认**两个结构不同**的密钥文件（上游 `{账号:{db_key}}` 与本地 `{accounts:[{db_key}]}`） |

**D4 的两条硬约束**

1. **实测才算通过**：导入时必须真的解开本机 `session.db` 第 1 页（`cipher.decrypt_first_page`）。
   只校验"64 位 hex"会让界面显示"已就绪"而链路全 503 —— 那正是上游坏掉时最难查的形态。
   未通过的条目**不写盘**；整批都没通过时返回 400 且不写盘。
2. **入口不依赖上游**：`/api/keys/*` 打的是本地后端，所以在"账号列表整片 503"时它照样能用。

实测验收：

| 项 | 结果 |
|---|---|
| `GET /api/keys/status`（真实密钥） | **200**，`verified=true`，`message=密钥可用（session.db 第 1 页解密通过）`，耗时 **259ms**（PBKDF2 256000 次，属预期） |
| `POST /api/keys/import`（密钥文件全文） | **200**，`imported` 1 条，`savedTo` 指向 `config/wechat-keys.json` |
| `POST /api/keys/import`（**错误的** 64 位 hex） | **400**「第 1 页页头校验失败：密钥不匹配或加密参数已变化」，且**逐字节确认密钥文件未被改动** |
| 其它非法输入 | hex 长度不对 / 乱文本 / 裸 key 却缺目录 / 空内容 → 全部 **400**，文案说明期望格式 |
| 导入后 `status` | 来源由 `upstream` 变成 `local-backup`，仍 `verified=true` |

> 真实用例：本次把现有的密钥（当时只躺在上游产物里）用这条通道导入了一次，
> 于是 `config/wechat-keys.json` 这次**真的**有内容了，来源标记也变成 `local-backup`。

**D1 的实现与验收**（`app/localdb/service.py` + `server.py` 12 处调用点）

- **只切 12 个调用点**：`get_real_wechat_chats` / `get_real_wechat_messages` / `AutoReplyEngine` /
  `ScheduledSendEngine`（C1–C2，共 4）+ `/api/contacts` / `/api/contacts/avatar`（C3，共 2）
  + `/api/sns/timeline` / `/api/sns/users` / `/api/sns/sync-latest`（C4，共 3）
  + `/api/sns/export` / `/api/sns/export/{id}` / `/api/sns/export/{id}/download`（C5，共 3）。
  其余（**媒体** / 账号枚举）**继续走上游**，因为 C6 还没做。
- `get_source(capability)` 是唯一的抉择点：`WeChatAdapter` 与 `LocalDataService` **同名同形**，可直接互换。
  `local` 模式下若本地未就绪则**回退上游**，原因通过 `/api/data-source/status` 暴露（不静默降级）。
- `LocalDataService` 带**后台同步线程**（1s 一轮，失败退避到最多 30s），
  `/api/data-source/status` 报 `state / ready / syncCount / lastSyncAgoSeconds / initError / syncError`。

实测验收（隔离数据目录 + 独立端口）：

| 项 | 结果 |
|---|---|
| 冷启动首个请求 `/api/chats` | **200，92 个会话，55ms**（预热放在"开始监听"之前） |
| `/api/messages` 实测新鲜度 | **距当前时刻 3 秒**（活数据） |
| `/api/data-source/status` | `state=ready active=local syncCount=10 lastSyncAgo=0.86s syncError=""` |
| 热切换 `local → upstream → local`（不重启进程） | 切换生效、可来回切；切回后恢复 92 会话 |
| 上游独有能力（如 `/api/contacts`） | 仍走上游（上游未启动时 503），证明路由是能力级的 |

> **顺带修掉一个真实隐患（不在原计划内，但它直接削弱这个开关）**：
> `load_config()` 原先用 `encoding="utf-8"` 且 `except Exception: pass` ——
> `config.json` 只要带 BOM（**记事本保存的默认行为**）就会解析失败，
> 于是**整份配置**（端口 / 上游地址 / 账号 / `data_source`）被静默换成默认值。
> 实测对照：带 BOM 时 `utf-8` 抛 `Unexpected UTF-8 BOM`。已改为 `utf-8-sig` 并让失败在控制台可见。

---

## 5. 风险与对策

| # | 风险 | 影响 | 对策 |
|---|---|---|---|
| R1 | **密钥提取成本极高**（实测：磁盘与内存均拿不到明文 key，只剩 Hook 一条路） | 若把它当"必须先做"，会拖死整条链路 | 已实测排除路线 ①②（见 §4 A2）；改走 **A2b：来源可替换 + 冗余备份 + 失效自检**，Hook 列为独立 spike 不阻塞 B/C |
| R2 | 微信版本升级导致参数/表结构变化 | 读取层失效 | 参数集中一处（§2.1）+ 启动自检（解第 1 页校验页头），失败即告警并回退 `upstream` |
| R3 | WAL 并发读：半写帧、checkpoint 竞争 | 读到脏页/崩 | 半写帧一律靠 HMAC 校验丢弃；镜像写入用「临时文件 + 原子替换」 |
| R4 | 镜像一致性维护复杂 | 难查的偶发错误 | 与上游产物做差分测试作为回归门禁 |
| R5 | 合规边界 | — | 仅处理本机本人数据；密钥与聊天缓存**不得外发**（沿用现有 `.gitignore` 规则） |

---

## 6. `runtime/wechat` 移除清单（**已于 2026-09-30 执行**）

实测整个 `runtime/wechat` = **299.6 MB / 313 个文件**。下表是逐项的最终结论：

| 组件 | 体积 | 结论 | 依据 |
|---|---|---|---|
| `resources/ffmpeg/ffmpeg.exe` | 79.0 MB | **已移除** | 上游转码用 |
| `resources/app.asar.unpacked/node_modules/ffmpeg-static/ffmpeg.exe` | 79.1 MB | **已移除** | ⚠️ **同一份 ffmpeg 的第二份拷贝**（原文档漏了这一项） |
| `resources/backend/wechat-backend.exe` | 62.6 MB | **已移除** | 上游解析服务（10392），被 §3 的自研读取层替代 |
| `resources/ui/`（Nuxt 站点） | 41.9 MB | **已移除** | 授权页 / 解密页，自研后不再需要 |
| `resources/backend/native/VoipEngine.dll` | 17.7 MB | **已移除** | 上游语音引擎 |
| `resources/backend/native/wce_integrity.pyd` | 6.7 MB | **已移除** | 上游完整性校验模块 |
| `resources/app.asar` + `app-update.yml` + `elevate.exe` | 3.8 MB | **已移除** | 上游 Electron 壳 / 升级 / 提权 |
| `resources/backend/native/weflow_wasm/wasm_video_decode.wasm` | 3.6 MB | **已移除** | 上游视频解码 |
| `resources/backend/native/` 其余（broker / SDL2 / client.dll / msvcp…） | ≈5 MB | **已移除** | native core 通道，授权门槛所在 |
| `resources/signing/` | 0.03 MB | **已移除** | 上游签名根证书 |
| `resources/backend/native/macos/` | 0.5 MB | **已移除** | macOS 产物，Windows 用不到 |
| `runtime/python/`、`runtime/python-packages/` | 172.7 MB | **必须保留** | 我们自己的 `server.py` 跑在里面（含 C0 必需的 `zstandard`） |
| 微信客户端本身（`Weixin.exe`） | — | **必须保留** | 数据来源 |

**执行结果**：`runtime` 从 **472.3 MB → 172.7 MB**；staging **310.2 MB**；安装包
`client\build\dist\ChatlogAI-Setup-1.0.0.exe` = **87.3 MB**
（SHA256 `197E00EC84A3353A600A4D425229E147198B0D49AC24A568695C2B56040D2675`）。

**代价（必须说清，不能含糊）**：媒体本体（图片 / 语音 / 视频）**永久不可用**，
因为 C6 未攻克而提供它们的上游又没了。不过**这不是功能回归**：上游当前因为拿不到 lease
本来就取不到任何媒体（§2.8 已实测），客户端本来就只有 `[图片]` 标签 + 文字头像兜底。
受影响的具体端点是 `/api/media/image`、`/api/sns/media`、`/api/sns/video`（删上游后一律 503）。

**怎么回滚**：
1. 仓库里 `runtime/wechat` 是**被 git 跟踪的**（304 个文件），`git checkout` 即可恢复；
2. 用户另有一份完整备份在桌面（`Desktop\runtime`，472 MB / 4719 文件 + `runtime.zip`）；
3. 客户端代码是"**上游可选**"而不是"上游已删"（见 §4 D3 的 4 处改动），
   把目录放回安装位置就能恢复上游 —— 这正是选"最小改动 + 可回退"的原因。

> **移除前要求的两件事，都已执行**：
> 1. `docs/PROJECT_SCAN.md` 里关于 10392 / 上游启动编排的描述已标注作废（见该文件顶部横幅）；
> 2. `client/` 的启动编排（`StartupOrchestrator` / `AppPaths` / `ConfigBootstrapper` / 打包脚本）
>    已去掉对 `wechat-backend.exe`、broker、授权页的**硬依赖** —— 逐项见 §4 的 D3 表格。
>
> 移除前还发现并修掉一个**会静默卡死**的缺陷（授权等待永不返回），详见 §4 D3 的说明。

---

## 7. 进度表（每完成一步必须更新）

| 日期 | 阶段 | 完成内容 | 验收证据 |
|---|---|---|---|
| 2026-09-30 | — | 问题定位：上游 native core license 不可用导致静默降级为静态快照 | 上游日志 `30_wechat_tool.log` 连续 464 处 license 失败 |
| 2026-09-30 | — | 确认加密参数并在真机库上验证通过（§2.1） | 解密页头 `1000020250402020` 自洽 |
| 2026-09-30 | — | 摸清 `session/message/contact` 关键表结构（§2.3） | sqlite3 读取上游解密副本 |
| 2026-09-30 | 设计 | 本方案建档 | 本文件 |
| 2026-09-30 | A2 spike | 侦察账号目录：`config\*` 均为 4096 整数倍加密页文件；未提权也可以 `PROCESS_VM_READ` 打开 5 个 `Weixin.exe` | OpenProcess 全部成功 |
| 2026-09-30 | A2 spike | 排除路线①：磁盘小文件（28 个 / 5.4MB）× 8 种编码扫描 + DPAPI magic 扫描，全部零命中 | 无任何文件含明文 key |
| 2026-09-30 | A2 spike | 排除路线②：5 进程内存 `PRIVATE 0.28GB + MAPPED 0.36GB` 全扫 × 8 编码零命中；仅命中 `PRAGMA cipher_salt = 'x''<salt_hex>'''` 共 8 处 | 内存中只有 salt，没有 key |
| 2026-09-30 | A2 spike | 结论：自研提取只剩 Hook 一条路；建议 A2 降级为 A2b（来源可替换 + 备份 + 自检），Hook 独立推进 | 见 §4 A2 |
| 2026-09-30 | A2b | 用户拍板：密钥先用上游产出（引导用户复制 / 提供按钮重跑上游授权），不再以自研 Hook 为前置 | 见 §4 A2b |
| 2026-09-30 | A2b | 密钥备份完成：三份副本（config / data / logs），并做逐库真实校验 | **19/19 库全部解出合法页头，verified=true** |
| 2026-09-30 | B1 | 产出 `app/localdb/cipher.py`：页解密 + 密钥派生 + 页头自检 | 与上游产物 **sha256 逐字节一致 11/11**；19 库全量 5.29s（上游 7.1s） |
| 2026-09-30 | B1 | 修正两处自研实现缺陷：① 页头校验偏移写错（自检当场抓到）② 漏了页尾 80 字节零填充 | 用 `weclaw.db`（单页）逐字节定位到差异 |
| 2026-09-30 | B2 | HMAC 计算范围实测确定为 `ct ‖ IV ‖ 页号小端` | 3 库 3 页号唯一命中；篡改一字节 → 校验失败 |
| 2026-09-30 | B3 | 产出 `app/localdb/wal.py`：WAL 头/帧解析、滚动校验和、提交语义、重置检测 | 校验和与存储值吻合；HMAC 在真实帧上全通过 |
| 2026-09-30 | B3 | 75 秒观测：主库约 50–60s 才 checkpoint 一次，WAL 每 5s 累积 6–30 帧 | 见 §2.5 表格 |
| 2026-09-30 | B3 | 端到端：WAL 覆盖后镜像 `quick_check=ok`，最新消息比只读主库新 **14 秒** | 见 §2.5 结论 |
| 2026-09-30 | B4 | 产出 `app/localdb/mirror.py`：WAL 新帧为主 + 页指纹作恢复路径 | 正常路径 0.008–0.03s；重置只改 6–20 页 |
| 2026-09-30 | B4 | torture 测试（70 轮 × 1s，跨 4 次真实 checkpoint）：异常轮数 0 | 逐页比对与全量重解 **0 页不同** |
| 2026-09-30 | B4 | 修复缺陷：WAL commit 会把库撑大，初版按主库页数 truncate 掉扩展页 → 镜像损坏 | 改为只在权威信息下对齐尺寸 |
| 2026-09-30 | C0 | 实测证实必需：`message_content` 本身是 zstd（魔数 `28B52FFD`），`compress_content` 全空 | `WCDB_CT_message_content`：CT=0 零压缩 / CT=4 全 zstd，零例外 |
| 2026-09-30 | C0 | 装入 `zstandard 0.25.0`（cp311 win_amd64，0.48MB） | `import zstandard` 与 round-trip 均通过 |
| 2026-09-30 | C1 | 逆向出表结构与字段语义（`Msg_=md5(username)`、群判定、显示名、位域、压缩标志） | 汇总进 §2.6 |
| 2026-09-30 | C1 | 产出 `keys.py` / `store.py` / `convert.py` / `chats.py` | 会话数 **92 == 上游基准 92**，耗时 0.018s |
| 2026-09-30 | C2 | 产出 `app/localdb/messages.py`：位域拆解 / 压缩解压 / 群前缀剥离 / `Name2Id` 解析 / 标签化 | 4 个会话（2 群 + 单聊 + 文件传输助手）全部时间单调、零 XML 泄漏、零空内容 |
| 2026-09-30 | C2 | 修复：初版忘了把倒序结果翻转成正序（契约要求时间正序） | 修正后 500/2000 条全部单调 |
| 2026-09-30 | C2 | 实测确定排序键：`create_time`(服务端 s) 而非 `sort_seq`(本机接收 ms) | `sort_seq` 会带来 2/500 处逆序 |
| 2026-09-30 | D1 | 完成**能力级**数据源路由：只切会话/消息 4 个调用点，其余仍走上游 | 冷启动首请求 200/92 会话/55ms；热切换可来回切 |
| 2026-09-30 | D1 | 产出 `app/localdb/service.py`（后台同步线程 + 状态透出）+ `/api/data-source/status` | 实测新鲜度距当前 3 秒 |
| 2026-09-30 | D1 | 修冷启动缺陷：镜像未建好时首批请求会撞上"回退上游→上游也不在"的 503 | 改为开始监听前预热（`wait_until_ready`） |
| 2026-09-30 | D1 | 修 `load_config` 的 BOM 静默失败：带 BOM 的 config.json 会让**整份配置**回退默认值 | 改 `utf-8-sig` + 失败可见；已实测对照 |
| 2026-09-30 | D1 | 路由改为**按能力**（`get_source(capability)` + `LOCAL_CAPABILITIES` 表） | 未实现的能力不可能被误切到本地；status 逐能力列出来源 |
| 2026-09-30 | C3 | 产出 `app/localdb/contacts.py` + 头像走 `head_image.db` | HTTP 端到端通过：3976 条、服务端 67ms、头像返回真实 JPEG |
| 2026-09-30 | C3 | 修 bug①：拼音列用错（读了昵称拼音），按备注拼音搜"表姨"命中 0 | 改为跟显示名走 + 搜索覆盖两套拼音 |
| 2026-09-30 | C3 | 修 bug②：`include_officials=false` 被"未传参默认 True"救回，关掉后总数不变 | 改为只看显式传入的开关 |
| 2026-09-30 | C3 | 查清本地**无**"已删除/黑名单"信号（`delete_flag` 近乎全 0） | 与上游 495 基准不可比，如实记录不猜测 |
| 2026-09-30 | C6 | 摸清媒体全貌：图片在 `msg/attach/<会话>/<月>/Img/<hash>.dat`、`.dat` 头 16 字节 + 长度字段在偏移 10..13 | 汇总进 §2.8 |
| 2026-09-30 | C6 | **29036 次解密尝试 0 命中**（aeskey × AES-ECB/CBC 各方案），且 `.dat` 命名键与消息 `md5` 零交集 | C6 图片本体判定为需独立逆向，**不再猜测** |
| 2026-09-30 | C6 | 纠正一处误判：朋友圈**视频**是明文、但朋友圈**图片同样加密**（196/196 密文） | C6 是 C4 图片部分的前置 |
| 2026-09-30 | C4 | 摸清 `sns.db` 全貌并写进 §2.9：时间线只有 `SnsTimeLine`（410 条）、`content` 是**纯 XML 不压缩** | 13 张表逐张核对，只有 4 张有数据 |
| 2026-09-30 | C4 | 证明 `tid` 与 XML `<id>` 是**同一个 64 位数**，且 `tid desc` == `createTime desc` | 8/8 逐字节相等；410/410 排序位置全同 |
| 2026-09-30 | C4 | 找到**点赞 / 评论的真实位置**：`LocalExtraInfo/like_user_list` 与 `comment_user_list`（此前以为在 `SnsMessage_tmp3`，实际它只有 30 行"别人对我的互动"） | 84 条有点赞 / 16 条有评论；`user_comment` 268 个（152+116+顶层 0） |
| 2026-09-30 | C4 | 从上游前端 bundle 反推 `sourceName` 的兜底链，作为口径拍板依据 | 结论：只用 `sourceNickName`，不复刻前端兜底 |
| 2026-09-30 | C4 | 产出 `app/localdb/moments.py` + `service.py` 三方法 + 3 个端点切 `get_source("sns")` | 键集合与 `normalize_sns_post` 双向零差集；410 条；102 人且 `postCount` 求和 = 410 |
| 2026-09-30 | C4 | 端到端验收（隔离数据目录 + 端口 18766 + 上游指向死端口） | timeline/users/sync-latest 全部 200；`/api/sns/media` 仍 503（能力级路由成立）；热切换三态通过；chats=92 / contacts=3976 无回归 |
| 2026-09-30 | C5 | 查清范围：**聊天 / 通讯录导出在客户端自研**（走已本地化的 `/api/messages`、`/api/contacts`），后端**只有朋友圈导出** | `server.py` 里没有任何聊天/通讯录导出端点；`§14.7.4` 早已拍板"自己格式化" |
| 2026-09-30 | C5 | 逐行读出客户端导出任务的字段契约（`zipReady` 优先于 `status`、`success` 不算完成态） | 汇总进 §2.10，避免"卡在导出中"或"提前下载撞 409" |
| 2026-09-30 | C5 | 产出 `app/localdb/exports.py` + `service.py` 三方法 + 3 个端点切 `get_source("export")` | 4 种格式全链路 200；74~126ms；产物 31~62KB |
| 2026-09-30 | C5 | **凭据零泄漏断言**：用时间线 1818 个 `token`/`key` 值比对 4 种产物 → 0 命中 | 先写成 `"token" not in doc` 当场误报（帖子在聊 Sa-Token），已改为按**值**比对 |
| 2026-09-30 | C5 | 端到端验收（同一隔离环境）：selected 26/24 条精确命中、错误路径、重启后任务表清空、热切换三态 | 回归 chats=92 / contacts=3976 / users=102 全部正常 |
| 2026-09-30 | D2/D3 | 重新评估阻塞面：**只剩 C6（媒体）**，导出与朋友圈已自持 | D2/D3 的阻塞原因从"C3–C6"缩小为"C6" |
| 2026-09-30 | — | 定位"运行了但和以前一样"的根因：**装在 `D:\Chatlog AI\` 的是 9/11 打包的旧包**（`app\` 里没有 `localdb`、`server.py` 里没有 `LOCAL_CAPABILITIES`、`python-packages` 里没有 `zstandard`）；另外 `config.json` 里也没有 `data_source` | 逐项比对安装版与仓库版的文件/行数/哈希 |
| 2026-09-30 | D2 | 默认切 `local`：`config_defaults.py` + `ConfigBootstrapper.cs` 默认值，并给现有 `config.json` 补字段 | 重装后 `data/localdb` 自动开始同步（17:40:13） |
| 2026-09-30 | D4 | 发现密钥是**单点**：一次"卸载时勾了删除数据"就把 `config/wechat-keys.json` 与 `logs/.keys-backup-*.json` 全清了，当时只剩上游产物一个来源 | 逐目录核对 17:38:34 的重建时间戳 |
| 2026-09-30 | D4 | 产出 `keys.py` 的导入能力 + `GET /api/keys/status` + `POST /api/keys/import`（**实测解密校验**）+ 客户端「微信账号」窗口的密钥区 | 真实密钥 200/259ms；**错误密钥 400 且逐字节确认文件未被改动**；4 类非法输入全部 400 |
| 2026-09-30 | D4 | 用该通道把存活的那把密钥导入 `config/wechat-keys.json`（此前该文件并不存在），来源标记变 `local-backup` | `describe_accounts` 输出 `verified=true` |
| 2026-09-30 | D3 | 客户端启动链改成"**上游可选**"（4 处）+ 同步打包脚本必需项 + 修掉会被走到的**静默卡死**授权等待 | 见 §4 D3 表格；打包闸门第一次拦下了"必需项仍含上游"的不一致 |
| 2026-09-30 | D3 | 删除 `runtime/wechat`（299.6MB / 313 文件）并重新打包 | `runtime` 472.3→**172.7MB**；staging 310.2MB；安装包 **87.3MB**（SHA256 `197E00EC…D2675`） |
| 2026-09-30 | D3 | 踩坑：编辑 `build-installer.ps1` 后**丢了 UTF-8 BOM** → PowerShell 5.1 按 GBK 解析中文注释，报一片 "Unexpected token" | 已修回 BOM 并在文件头写明该要求；`PSParser` 复检 0 错误 |

---

## 8. 已拍板的决策（避免重复讨论）

| # | 决策 | 结论 |
|---|---|---|
| D-1 | 架构 | **C-2 自持**：自建解密镜像 + 自研读取层，不写回上游目录 |
| D-2 | 最终范围 | **全部模块自研**（会话/消息/联系人/朋友圈/导出/媒体），彻底不依赖上游 |
| D-3 | 新鲜度目标 | **WAL 级（< 1s）** |
| D-4 | 密钥 | **自研提取**，不长期依赖上游产出的 key ⚠️ **2026-09-30 实测后建议调整**：磁盘/内存两条路均已排除，自研只剩 Hook；改为 **A2b（来源可替换 + 备份 + 自检）先行，Hook 独立推进**（待确认） |
| D-5 | 明确放弃 | 不对上游 exe 做任何破解 / patch / 重签 |
