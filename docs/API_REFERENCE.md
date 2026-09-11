# Chatlog AI 接口与参数参考

> 用途：完整记录本项目的**全部对外 HTTP 接口**、**入参 / 出参字段**、**错误码**、**调用时序**，以及**下游数据源接口**。
> 目标是让后续用其它语言（非 HTML/JS）重写前端时，**无需再读 Python 源码即可对接**。
> 事实来源：`app/server.py`、`app/adapters.py`、`app/static/app.js`。生成时间：2026-09-10。
>
> **⚠️ 2026-09-11 更新**：网页版前端（`app/static/`，含 `index.html` / `style.css` / `app.js`）与
> Python 启动器（`app/launcher.py`）**已随 T14 退役并删除**（见 `DOTNET_CLIENT_PLAN.md` §7.18）。
> 本文中凡引用 `app/static/*` 或 `launcher.py` 的位置，**仅作历史对照**，不再是现行事实来源；
> 唯一的前端是 .NET 客户端。`app.js` 与 `style.css` 曾未被 git 跟踪，删除前已专门提交入库
> （commit `03cf2f5`），需要对照时可用 `git show 03cf2f5:app/static/app.js` 取回。

---

## 0. 基础约定

| 项 | 值 |
|---|---|
| Base URL | `http://127.0.0.1:18765`（端口取自 `config/config.json` 的 `port`） |
| 协议 | HTTP/1.1，JSON（`Content-Type: application/json`） |
| 字符集 | UTF-8 |
| CORS | 全放开（`allow_origins=["*"]`、`allow_credentials=True`、所有方法与头） |
| 成功标识 | 多数接口返回 `"status": "success"`（`/api/health` 返回 `"ok"`） |
| 鉴权 | **无鉴权**。服务仅监听 `127.0.0.1`，靠本地绑定保证安全 |
| 前端调用风格 | 全部用 `fetch`，读接口带 `{ cache: 'no-store' }` |

### 0.1 统一错误格式

FastAPI 默认错误体：

```json
{ "detail": "错误描述文本" }
```

前端 `apiError()` 的解析优先级（[app.js L593](file:///d:/code/chatlog-ai-portable/app/static/app.js#L593)）：
`data.detail` → `data.message` → `data.status_info.msg` → 兜底文案。

### 0.2 状态码汇总

| 码 | 含义 | 触发场景 |
|---|---|---|
| 200 | 成功 | — |
| 400 | 参数错误 / 前置条件不满足 | 缺字段、未配置模型、非 Windows 调自动化 |
| 404 | 资源不存在 | 供应商 id 不存在 |
| 422 | FastAPI 参数校验失败 | 路径/查询参数类型不符（如 `limit` 超范围） |
| 500 | 服务内部错误 | 自动化发送失败 |
| 502 | 上游错误 | 模型 API 或余额接口返回错误 / 不可达 |
| 503 | 数据源不可用 | 微信数据源请求失败（`/api/chats`、`/api/messages`） |

---

## 1. 接口总览

| # | 方法 | 路径 | 说明 |
|---|---|---|---|
| 1 | GET | `/` | 返回前端页面 `index.html` |
| 2 | GET | `/static/*` | 静态资源（`style.css`、`app.js`） |
| 3 | GET | `/api/health` | 健康检查 |
| 4 | GET | `/api/config` | 当前生效模型的简要信息 |
| 5 | GET | `/api/providers` | 供应商列表 + 预设 + 激活项 |
| 6 | POST | `/api/providers/validate` | 真实调用一次模型验证 Key |
| 7 | POST | `/api/providers` | 新增 / 更新供应商 |
| 8 | POST | `/api/providers/activate` | 切换生效供应商 |
| 9 | DELETE | `/api/providers/{provider_id}` | 删除供应商 |
| 10 | GET | `/api/providers/balance` | 查询当前供应商余额 |
| 11 | GET | `/api/chats` | 会话列表 |
| 12 | GET | `/api/messages` | 会话消息 |
| 13 | POST | `/api/summarize` | AI 分析 / 总结 / 多轮问答 |
| 14 | GET | `/api/history` | 读取某会话的 AI 历史 |
| 15 | POST | `/api/history` | 保存某会话的 AI 历史 |
| 16 | POST | `/api/reply/generate` | 生成一条回复（不发送） |
| 17 | POST | `/api/reply/send` | 键盘模拟发送（仅 Windows） |
| 18 | GET | `/api/auto-reply/config` | 读取自动回复配置 |
| 19 | POST | `/api/auto-reply/config` | 保存自动回复配置 |
| 20 | GET | `/api/auto-reply/status` | 自动回复引擎状态与日志 |
| 21 | POST | `/api/auto-reply/test` | 测试快捷键 / 剪贴板 / 完整流程 |
| 22 | GET | `/api/scheduled-send/config` | 读取定时发送配置 |
| 23 | POST | `/api/scheduled-send/config` | 保存定时发送配置（`tasks` 为**整份替换**） |
| 24 | GET | `/api/scheduled-send/status` | 定时发送引擎状态与每个任务的下次触发时间 |
| 25 | POST | `/api/scheduled-send/test` | 预览 / 试发一条打招呼消息（支持未保存草稿） |
| 26 | POST | `/api/shutdown` | 退出全部本地服务 |

---

## 2. 静态与页面

### 2.1 `GET /`

返回 `{"message": "Chatlog AI 后端服务正常运行。"}`。（原实现会返回 `app/static/index.html` 文件；网页版退役后只保留这句 JSON 横幅，端点本身保留作为"服务在跑吗"的探针。）

### 2.2 `GET /static/{path}`

**已随 T14 退役。** 原由 `StaticFiles` 挂载 `app/static/`；该目录与挂载代码已于 2026-09-11 一并删除（见 `DOTNET_CLIENT_PLAN.md` §7.18）。现在访问 `/static/*` 会得到 404，这是预期行为——前端只有 .NET 客户端，不再有静态资源需要经后端分发。

### 2.3 `GET /api/health`

**响应**

```json
{ "status": "ok", "service": "chatlog" }
```

---

## 3. 模型供应商

### 3.1 `GET /api/config`

**响应**

```json
{
  "has_key": true,
  "model": "deepseek-chat",
  "provider_name": "DeepSeek"
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `has_key` | bool | 当前生效供应商是否配置了 API Key |
| `model` | string | 当前模型名 |
| `provider_name` | string | 当前供应显示名 |

无供应商时返回 `{"has_key": false, "model": "", "provider_name": ""}`。

---

### 3.2 `GET /api/providers`

**响应**

```json
{
  "status": "success",
  "providers": [
    {
      "id": "prov-88xxxxx857",
      "name": "DeepSeek",
      "provider_type": "deepseek",
      "base_url": "https://api.deepseek.com",
      "model": "deepseek-chat",
      "api_key_masked": "sk-5****xxxxf",
      "balance_supported": true,
      "active": true
    }
  ],
  "active_id": "prov-88xxxxx857",
  "presets": { "...见 3.2.1..." }
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `providers[].id` | string | 供应商标识，形如 `prov-<10位hex>` |
| `providers[].name` | string | 显示名 |
| `providers[].provider_type` | string | 预设类型：`deepseek`/`kimi`/`zhipu`/`qwen`/`custom` |
| `providers[].base_url` | string | OpenAI 兼容基址（**不含** `/chat/completions`） |
| `providers[].model` | string | 模型名 |
| `providers[].api_key_masked` | string | 脱敏 Key（前 4 + `****` + 后 4；短 Key 前 2 + `***`） |
| `providers[].balance_supported` | bool | 是否支持余额查询（等价于 `balance_path` 非空） |
| `providers[].active` | bool | 是否当前生效 |
| `active_id` | string | 当前生效供应商 id（active 失效时回落第一个） |
| `presets` | object | 供应商预设表（见下） |

#### 3.2.1 `presets` 结构（`PROVIDER_PRESETS`）

```json
{
  "deepseek": { "name": "DeepSeek",        "base_url": "https://api.deepseek.com",                                "model": "deepseek-chat",   "balance_path": "/user/balance",       "balance_parser": "deepseek" },
  "kimi":     { "name": "Kimi（Moonshot）", "base_url": "https://api.moonshot.cn/v1",                              "model": "moonshot-v1-8k",  "balance_path": "/users/me/balance",   "balance_parser": "moonshot" },
  "zhipu":    { "name": "智谱 GLM",         "base_url": "https://open.bigmodel.cn/api/paas/v4",                    "model": "glm-4-flash",     "balance_path": null,                  "balance_parser": null },
  "qwen":     { "name": "通义千问（DashScope）", "base_url": "https://dashscope.aliyuncs.com/compatible-mode/v1",   "model": "qwen-plus",       "balance_path": null,                  "balance_parser": null },
  "custom":   { "name": "自定义（OpenAI 兼容）", "base_url": "",                                                     "model": "",                "balance_path": null,                  "balance_parser": null }
}
```

> 重写前端时可直接复用该表：选择 `provider_type` 后自动填充 `base_url` / `model`。

---

### 3.3 `POST /api/providers/validate`

真实调用一次模型（`temperature=0`、`timeout=60`），验证 Key 可用。

**请求体**

```json
{ "base_url": "https://api.deepseek.com", "model": "deepseek-chat", "api_key": "sk-..." }
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `base_url` | 是 | 会做 `rstrip("/")` |
| `model` | 是 | — |
| `api_key` | 是 | — |

**响应**

```json
{ "status": "success", "message": "API Key 验证通过" }
```

**错误**：400（三者任一为空）；502（模型 API 报错/不可达）。

---

### 3.4 `POST /api/providers`

新增或更新供应商（`id` 存在则更新）。

**请求体**

```json
{
  "id": "prov-88xxxxx857",
  "provider_type": "deepseek",
  "name": "DeepSeek-账号2",
  "base_url": "https://api.deepseek.com",
  "model": "deepseek-chat",
  "api_key": "sk-...",
  "balance_path": "/user/balance",
  "balance_parser": "deepseek"
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `id` | 否 | 传入=更新；不传=新增 |
| `provider_type` | 否 | 默认 `custom`；用于取预设的 `balance_path` / `balance_parser` / `name` |
| `name` | 否 | 为空时取预设 `name`，再退回 `provider_type` |
| `base_url` | 是 | 会 `rstrip("/")` |
| `model` | 是 | — |
| `api_key` | 更新时否 / 新增时是 | **更新时留空 = 沿用旧 Key**（见下） |
| `balance_path` | 否 | 显式传入则覆盖预设 |
| `balance_parser` | 否 | 显式传入则覆盖预设 |

**更新语义（重要）**：
- 更新且 `api_key` 为空 → 沿用已有 Key；若已有 Key 也为空 → 400。
- 新增且 `api_key` 为空 → 400。
- 新增时 `id = "prov-" + secrets.token_hex(5)`。
- 保存后：若 `active_provider` 为空或已失效，则自动指向本次保存的 id。

**响应**

```json
{ "status": "success", "id": "prov-88xxxxx857" }
```

**错误**：400（base_url/model 为空；api_key 缺失）；404（更新时 id 不存在）。

---

### 3.5 `POST /api/providers/activate`

**请求体**

```json
{ "id": "prov-88xxxxx857" }
```

**响应**

```json
{ "status": "success" }
```

**错误**：400（缺 id）；404（供应商不存在）。

---

### 3.6 `DELETE /api/providers/{provider_id}`

**路径参数**：`provider_id`。

**响应**

```json
{ "status": "success" }
```

删除后若删的是当前生效项，`active_provider` 自动切到剩余第一个（无剩余则置空）。
**错误**：404（不存在）。

---

### 3.7 `GET /api/providers/balance`

**响应（支持余额时）**

```json
{
  "status": "success",
  "supported": true,
  "balance": {
    "available": true,
    "text": "可用余额 ¥123.45",
    "available_balance": "123.45",
    "voucher_balance": "0.00",
    "cash_balance": "123.45"
  }
}
```

**响应（不支持余额时）**

```json
{ "status": "success", "supported": false, "message": "该供应商暂不支持余额查询" }
```

`balance` 对象字段随 parser 不同：

| parser | 字段 |
|---|---|
| `deepseek` | `available`, `text`（形如 `CNY 12.34；USD 0.00`）；不可用时为 `{available:false, message}` |
| `moonshot` | `available`, `available_balance`, `voucher_balance`, `cash_balance`, `text` |
| 其他 | `{available: true, text: "<原始响应>"}` |

**错误**：400（尚未配置任何供应商）；502（余额接口报错/不可达）。

---

## 4. 聊天数据

### 4.1 `GET /api/chats`

**查询参数**：无。固定读取当前生效微信账号的会话列表（账号取自 `config/config.json` 的 `wechat_account`）。

**响应**

```json
{
  "status": "success",
  "status_info": {
    "ready": true,
    "msg": "微信实时数据库已连接，共 42 个会话",
    "source": "realtime",
    "account": "wxid_xxx"
  },
  "chats": [
    {
      "id": "wxid_abc123",
      "name": "文件传输助手",
      "type": "friend",
      "source": "wechat-realtime",
      "last_message": "好的",
      "last_time": "2026-09-10 14:03:21",
      "unread": 0
    }
  ]
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `status_info.ready` | bool | 数据源是否就绪（成功响应时恒为 `true`，失败走 503） |
| `status_info.msg` | string | 面向用户的提示文案（前端直接展示在状态栏） |
| `status_info.source` | string | 上游返回的 `source`，缺省 `realtime` |
| `status_info.account` | string | 当前微信账号标识 |
| `chats[].id` | string | 会话 id（微信 `username`） |
| `chats[].name` | string | 会话显示名 |
| `chats[].type` | string | `"group"` 或 `"friend"` |
| `chats[].source` | string | 固定 `wechat-realtime` |
| `chats[].last_message` | string | 最新消息预览 |
| `chats[].last_time` | string | 最新消息时间（上游原样字符串） |
| `chats[].unread` | int | 未读数 |

微信最多取 400 个会话（`WeChatAdapter.list_chats(limit=400)`，见 [adapters.py L78-L115](file:///d:/code/chatlog-ai-portable/app/adapters.py#L78-L115)）。
**错误**：503（数据源请求失败，`detail` 为异常信息）。

---

### 4.2 `GET /api/messages`

**查询参数**

| 参数 | 类型 | 默认 | 约束 | 说明 |
|---|---|---|---|---|
| `target_id` | string | 必填 | — | 会话 id（来自 `/api/chats`） |
| `limit` | int | `500` | 1–2000 | 读取条数上限 |
| `recent_hours` | float | 无 | 可选 | 只保留最近 N 小时（服务端过滤） |
| `recent_count` | int | 无 | 可选 | 只保留最近 N 条（在 `recent_hours` 之后应用） |

**响应**

```json
{
  "status": "success",
  "messages": [
    {
      "id": "1234567890",
      "sender": "我",
      "time": "2026-09-10 14:03:21",
      "timestamp": 1757484201,
      "content": "好的",
      "message_type": "message",
      "source": "wechat-realtime"
    }
  ],
  "source_valid": true,
  "message": ""
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `messages[].id` | string | 消息 id（用于去重） |
| `messages[].sender` | string | 发送者：自己恒为 `"我"`；他人为昵称 / uin / uid；未知为 `"未知成员"` |
| `messages[].time` | string | 格式化后的本地时间 `YYYY-MM-DD HH:MM:SS` |
| `messages[].timestamp` | number | 原始时间戳（**可能秒或毫秒**） |
| `messages[].content` | string | 正文；非文本消息为类型标签（`[图片]`/`[语音]`/`[文件]`…） |
| `messages[].message_type` | string | 微信 `renderType`（如 `text`/`image`/`voice`） |
| `messages[].source` | string | 固定 `wechat-realtime` |
| `source_valid` | bool | 恒为 `true`（有消息时） |
| `message` | string | 为空表示正常；无消息时为提示文案 |

**排序**：始终按时间**正序**（旧 → 新）。上游是倒序返回，适配器内部已 `reversed`。
**分页**：微信每页（上游 `/api/chat/messages`）最多 500 条，适配器内部按 `offset` 循环拉取直到达到 `limit`（见 [adapters.py L117-L164](file:///d:/code/chatlog-ai-portable/app/adapters.py#L117-L164)）。
**错误**：503（数据源失败）。

#### 4.2.1 非文本消息标签映射

| 微信 `renderType` | 标签 |
|---|---|
| `image` | `[图片]` |
| `emoji` | `[动画表情]` |
| `video` | `[视频]` |
| `voice` | `[语音]` |
| `file` | `[文件]` |
| `link` | `[链接]` |
| `quote` | `[引用消息]` |
| `chatHistory` | `[聊天记录]` |
| `transfer` | `[转账]` |
| `redPacket` | `[红包]` |
| `location` | `[位置]` |
| `voip` | `[通话]` |
| `system` | `[系统消息]` |
| 其他 | `[消息]` |

映射表见 [adapters.py L31-L46](file:///d:/code/chatlog-ai-portable/app/adapters.py#L31-L46)：仅当 `content` 为空时才用标签兜底，否则直接使用正文。

---

## 5. AI 分析与历史

### 5.1 `POST /api/summarize`

**请求体**

```json
{
  "target_name": "项目群",
  "messages": [ { "sender": "张三", "time": "2026-09-10 10:00:00", "content": "..." } ],
  "type": "general",
  "prompt": "请综合总结当前筛选范围内的聊天记录",
  "history": [ { "role": "user", "content": "..." }, { "role": "assistant", "content": "..." } ],
  "recent_hours": 24,
  "recent_count": 500
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `target_name` | 否 | 默认 `"未知对话"`，仅用于提示词 |
| `messages[]` | 是 | 消息数组，只用到 `sender` / `time` / `content` 三个字段 |
| `type` | 否 | `general`(默认) / `meeting` / `todo` / `topics` |
| `prompt` | 否 | 用户自定义提问；为空时用「请按指定类型分析这段聊天记录。」 |
| `history[]` | 否 | 多轮上下文，**只取最后 10 条**，每条 `content` 截断 **8000 字符**，`role` 仅接受 `user`/`assistant` |
| `recent_hours` | 否 | 服务端再过滤一次（与前端筛选可叠加） |
| `recent_count` | 否 | 同上 |

**服务端拼装的提示词**：

```
system:  <按 type 选择的内置 system prompt>
        + history（最多 10 条）
user:   "对话名称：{target_name}\n本次提供 {N} 条真实聊天消息。\n\n聊天记录：\n<[时间] 发送者: 正文 逐行>\n\n本次要求：{prompt}"
temperature = 0.3
```

**type → system prompt**

| type | system prompt（原文） |
|---|---|
| `meeting` | 你是一名高效的高级会议秘书。请对以下聊天记录整理为标准的【会议纪要与决策】，包含：1. 讨论背景 2. 关键决策 3. 责任人及后续动作。 |
| `todo` | 你是一个严谨的项目经理。请从以下聊天记录中精确提取【待办事项与任务列表 (Action Items)】，包含：任务描述、责任人、预期交付时间。 |
| `topics` | 你是一个信息分析专家。请提炼以下聊天记录中的【核心讨论热点与话题】，按关注度从高到低排列，并给出简要总结。 |
| `general` / 其他 | 你是一个智能聊天助手。请对以下聊天记录提供简洁清晰的 Markdown 格式结构化总结，包含核心议题、关键观点及待办项。 |

**响应**

```json
{ "status": "success", "summary": "## 核心议题\n...", "analyzed_count": 128 }
```

| 字段 | 说明 |
|---|---|
| `summary` | 模型返回的 Markdown 正文（前端用 `renderMarkdown` 渲染） |
| `analyzed_count` | 实际参与分析的消息条数（过滤后） |

**错误**：400（未配置 Key / 缺 base_url 或 model / 无有效消息）；502（模型 API 错误）。

---

### 5.2 `GET /api/history`

**查询参数**：`target_id`（必填）。

**响应**

```json
{ "status": "success", "history": [ { "role": "user", "content": "..." }, { "role": "assistant", "content": "..." } ] }
```

数据来自 `data/summaries.json` 的 `summaries[target_id]`；不存在时返回空数组。

---

### 5.3 `POST /api/history`

**请求体**

```json
{ "target_id": "wxid_abc123", "history": [ { "role": "user", "content": "..." } ] }
```

整体**覆盖**写入 `data/summaries.json` 的 `summaries[target_id]`。
**响应**：`{ "status": "success" }`；**错误**：400（缺 `target_id` 或 `history`）。

---

## 6. 自动回复

### 6.1 `GET /api/auto-reply/config`

**响应**

```json
{
  "status": "success",
  "config": {
    "enabled": false,
    "guard_targets": [
      { "id": "wxid_abc", "name": "小王", "type": "friend", "prompt": "用轻松的口吻回复" }
    ],
    "context_count": 80,
    "reply_prompt": "",
    "hotkeys": { "open_wechat": "ctrl+alt+w", "search": "ctrl+f", "paste": "ctrl+v", "send": "enter" }
  },
  "available": true
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `config.enabled` | bool | 是否启用守护 |
| `config.guard_targets[]` | array | 守护会话，元素见下 |
| `config.guard_targets[].id` | string | 会话 id |
| `config.guard_targets[].name` | string | 会话名（发送时用它搜索） |
| `config.guard_targets[].type` | string | `friend` 或 `group` |
| `config.guard_targets[].prompt` | string | 该会话的**内层**提示词（单独口吻） |
| `config.context_count` | int | 生成回复时携带的上下文条数，1–2000，默认 80 |
| `config.reply_prompt` | string | **外层**提示词（全局身份），空则用默认身份 |
| `config.hotkeys` | object | 微信快捷键映射 |
| `available` | bool | 自动化功能是否可用（`automation` 可导入 且 `os.name == "nt"`） |

**默认值**（`AUTO_REPLY_DEFAULTS`）：`enabled=false`、`guard_targets=[]`、`context_count=80`、`reply_prompt=""`、`hotkeys` 如上表。

**存储位置**：`config/auto-reply/{account}.json`，`account` 来自 `config/config.json` 的 `wechat_account`（非法字符剔除，空则 `default`）。与 `config/config.json` 分开存放，避免并发写冲突。

---

### 6.2 `POST /api/auto-reply/config`

**请求体**（部分更新，只处理出现的字段）

```json
{
  "enabled": true,
  "guard_targets": [ { "id": "wxid_abc", "name": "小王", "type": "friend", "prompt": "..." } ],
  "context_count": 120,
  "reply_prompt": "你是一个热心但简洁的助手",
  "hotkeys": { "open_wechat": "ctrl+alt+w", "search": "ctrl+f", "paste": "ctrl+v", "send": "enter" }
}
```

**清洗规则**（服务端）：

| 字段 | 规则 |
|---|---|
| 白名单 | 仅 `enabled` / `guard_targets` / `context_count` / `reply_prompt` / `hotkeys` 生效 |
| `context_count` | `int` 后夹到 `[1, 2000]`；类型错误则忽略该字段 |
| `enabled` | `bool()` 转换 |
| `guard_targets` | 必须是数组；逐项校验（`id`/`name` 非空、`type ∈ {friend, group}`）、按 `id` 去重；**最多 8 个 friend + 3 个 group**，超限报 400 |
| `hotkeys` | 必须是对象；只保留已知键且值非空 |
| `reply_prompt` | `strip()` |
| `None` 值 | 跳过（不覆盖） |

**响应**：`{ "status": "success", "config": <合并后的完整配置> }`
**错误**：400（`guard_targets` 非数组 / 超限）。

---

### 6.3 `GET /api/auto-reply/status`

**响应**

```json
{
  "status": "success",
  "enabled": true,
  "running": true,
  "active_chat_name": "小王",
  "log": [
    { "time": "14:03:21", "level": "info", "message": "已回复「小王」" }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `enabled` | 配置里是否启用 |
| `running` | 后台线程是否存活 |
| `active_chat_name` | 微信当前激活的会话名（用于跳过重复搜索） |
| `log` | 最近日志，`deque(maxlen=200)`，`level ∈ {info, error}` |

**引擎行为**：线程 `POLL_INTERVAL=5s`；`DETECT_WINDOW=30` 条消息；首轮仅建基线不回复；用 `last_message|last_time|unread` 指纹 + 最后一条非「我」消息的签名双重判断是否触发。

---

### 6.4 `POST /api/auto-reply/test`

**请求体**

```json
{ "action": "clipboard", "target_name": "小王", "text": "测试文本" }
```

| `action` | 需要字段 | 行为 |
|---|---|---|
| `open_wechat` / `search` / `paste` / `send`（即 `hotkeys` 的键名） | — | 触发对应快捷键：`automation.press_hotkey(hotkeys[action])` |
| `clipboard` | `text?`（默认「测试剪贴板」） | 写入剪贴板 |
| `full` | `target_name` + `text` | 完整执行一次发送流程 |

**响应**：`{ "status": "success", "message": "..." }`
**错误**：400（非 Windows / `full` 缺字段 / 未知 action）；500（执行失败）。

---

## 6.5 定时发送（2026-09-11 新增）

> 与自动回复的区别：自动回复是「被动响应别人发来的消息」，定时发送是「到点主动开口」。
> 到点后由 Python 侧 `ScheduledSendEngine` 取上下文 → 让模型写一条打招呼消息 → 用与自动回复**同一套键盘模拟**发出。
> 两者共用同一把互斥锁，不会互相抢剪贴板 / 前台窗口。
>
> **触发模型**：`基础到期时刻` + `随机抖动（jitter_minutes）` = 真正的发出时刻；
> 另有一道 `冷却（cooldown_minutes）` 闸门，距上次发送未满该时长一律不发（对所有模式生效）。
> 已摇定的待触发时刻会落盘（`data/scheduled-send-state.json`），进程重启不会重摇。

### 6.5.1 `GET /api/scheduled-send/config`

**响应**

```json
{
  "status": "success",
  "config": {
    "enabled": false,
    "tasks": [
      {
        "id": "sched-3f9a1c2b04",
        "target_id": "wxid_abc123",
        "target_name": "小王",
        "target_type": "friend",
        "enabled": true,
        "mode": "daily",
        "time_of_day": "09:00",
        "weekdays": [],
        "interval_minutes": 120,
        "run_at": "",
        "cooldown_minutes": 60,
        "jitter_minutes": 15,
        "context_count": 80,
        "prompt": ""
      }
    ]
  },
  "available": true
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `config.enabled` | bool | 定时发送总开关；关掉时引擎不做任何判定 |
| `config.tasks[]` | array | 任务数组，最多 **50** 条 |
| `tasks[].id` | string | 任务 id，形如 `sched-<10位hex>`；为空 = 服务端新建 |
| `tasks[].target_id` | string | 微信会话 id（取上下文用） |
| `tasks[].target_name` | string | 会话名（发送时用它搜索会话，必须与微信显示名一致） |
| `tasks[].target_type` | string | `friend` 或 `group`（仅展示用） |
| `tasks[].enabled` | bool | 单任务开关 |
| `tasks[].mode` | string | `daily` / `weekly` / `interval` / `once`，非法值回退 `daily` |
| `tasks[].time_of_day` | string | `HH:MM`，`daily` / `weekly` 生效；非法回退 `09:00` |
| `tasks[].weekdays` | int[] | `1`=周一 … `7`=周日，`weekly` 生效；为空时补 `[1]` |
| `tasks[].interval_minutes` | int | `interval` 生效，夹到 `[1, 10080]`，默认 120 |
| `tasks[].run_at` | string | `YYYY-MM-DDTHH:MM`，`once` 生效；**解析失败的任务会被直接丢弃** |
| `tasks[].cooldown_minutes` | int | 冷却分钟，夹到 `[0, 10080]`，默认 60；`0` = 不启用 |
| `tasks[].jitter_minutes` | int | 随机推迟分钟，夹到 `[0, 1440]`，默认 15；`0` = 不抖动 |
| `tasks[].context_count` | int | 生成时携带的上下文条数，夹到 `[1, 2000]`，默认 80 |
| `tasks[].prompt` | string | 该任务专属提示词，为空则只用内置 `scheduled-greeting` |
| `available` | bool | 自动化是否可用（`automation` 可导入 且 `os.name == "nt"`） |

**默认值**：`enabled=false`、`tasks=[]`。

**存储位置**：`config/scheduled-send/{account}.json`；`account` 规则与自动回复完全一致（取自 `config.json` 的 `wechat_account`，非法字符剔除，空则 `default`）。

**运行时状态**：`data/scheduled-send-state.json`，形如
`{"sched-3f9a1c2b04": {"last_sent_at": 1789092000.0, "pending_fire_at": 1789092900.0}}`。
**刻意与配置分开放**：配置文件会被界面随时整份覆盖，若把状态写回配置必然丢更新。

---

### 6.5.2 `POST /api/scheduled-send/config`

**请求体**（部分更新，只处理出现的字段）

```json
{ "enabled": true, "tasks": [ { "target_id": "wxid_abc123", "target_name": "小王", "mode": "daily", "time_of_day": "09:00" } ] }
```

| 字段 | 规则 |
|---|---|
| 白名单 | 仅 `enabled` / `tasks` 生效 |
| `enabled` | `bool()` 转换 |
| `tasks` | 必须是数组；逐项清洗（见下表），**整份替换**而不是增量合并 |
| `None` 值 | 跳过（不覆盖） |

**逐项清洗规则**：

| 情况 | 处理 |
|---|---|
| `target_id` 或 `target_name` 为空 | 丢弃该项 |
| `id` 为空 | 生成 `"sched-" + secrets.token_hex(5)` |
| `id` 重复 | 保留第一条 |
| `mode` 非法 | 回退 `daily` |
| `mode == "weekly"` 且 `weekdays` 为空 | 补 `[1]`（周一） |
| `mode == "once"` 且 `run_at` 解析失败 | **丢弃该项**（否则是一条永远不会触发的死任务） |
| 各项数值 | 按 §6.5.1 的范围夹紧；类型错误回退默认值 |
| 任务数 > 50 | 400 |

**响应**：`{ "status": "success", "config": <合并后的完整配置> }`
**错误**：400（`tasks` 非数组 / 任务数超限）。

---

### 6.5.3 `GET /api/scheduled-send/status`

**响应**

```json
{
  "status": "success",
  "enabled": true,
  "running": true,
  "tasks": [
    { "id": "sched-3f9a1c2b04", "enabled": true, "next_fire_at": 1789092900.0, "last_sent_at": 1789092000.0 }
  ],
  "log": [ { "time": "14:03:21", "level": "info", "message": "已定时向「小王」发送消息" } ]
}
```

| 字段 | 说明 |
|---|---|
| `enabled` | 总开关（配置值） |
| `running` | 后台线程是否存活 |
| `tasks[].next_fire_at` | 下次触发的 Unix 秒（**不含**随机抖动）；无下次为 `null` |
| `tasks[].last_sent_at` | 上次发送的 Unix 秒；从未发送为 `null` |
| `log` | 最近日志，`deque(maxlen=200)`，`level ∈ {info, error}`（结构与自动回复一致） |

**引擎行为**：`POLL_INTERVAL=30s`；`interval` 模式**首次观测只写基线、不补发**；
每轮只对发生变化的任务回写状态文件。

---

### 6.5.4 `POST /api/scheduled-send/test`

**请求体**（提交**完整任务**而不是 task_id，因此未保存的草稿也能先试一条）

```json
{ "action": "preview", "task": { "target_id": "wxid_abc123", "target_name": "小王", "mode": "daily", "time_of_day": "09:00" } }
```

| `action` | 行为 |
|---|---|
| `preview` | 只取上下文 + 调模型生成，**不发送** |
| `send` | 生成后**真实发送**；若该任务已保存，同时回写 `last_sent_at`（避免紧接着又被计划重复发一条） |

`task` 会走与 §6.5.2 相同的清洗；清洗后为空则 400。

**响应**：`{ "status": "success", "text": "<生成的正文>", "message": "已发送" }`（`preview` 不含 `message`）
**错误**：400（非 Windows / 缺 `task` / 任务参数不完整 / 未知 action）；500（生成或发送失败）。

---

## 7. 系统控制

### 7.1 `POST /api/shutdown`

无请求体。行为：若设置了环境变量 `CHATLOG_SHUTDOWN_FILE`，则 `touch` 该文件；随后 0.5 秒调用 `os._exit(0)` 退出进程。由于两个服务在同一个 Windows Job Object 中，**整个进程组会一起结束**。

**响应**

```json
{ "status": "success", "message": "统一服务正在退出。" }
```

> 前端 `shutdownAll()` 调用后不求返回值，直接静默处理失败。

---

## 8. 数据模型速查

### 8.1 Chat（会话）

```
{ id, name, type: "group"|"friend", source, last_message, last_time, unread }
```
- `source`：固定 `wechat-realtime`

### 8.2 Message（消息）

```
{ id, sender, time, timestamp, content, message_type, source }
```
- `sender`：自己 = `"我"`；他人 = 昵称 / username；未知 = `"未知成员"`
- `time`：`YYYY-MM-DD HH:MM:SS`
- `timestamp`：数字，**可能为秒或毫秒**（`> 100000000000` 视为毫秒）
- `message_type`：微信 `renderType`
- `source`：固定 `wechat-realtime`

### 8.3 Provider（供应商）

```
{ id, provider_type, name, base_url, model, api_key, balance_path, balance_parser }
```
对外读取时 `api_key` 以 `api_key_masked` 出现，另附 `balance_supported`、`active`。
**持久化位置**：`config/api-keys.json`（结构恒为 `{"providers": [...]}`）；当前生效供应商 id 记在 `config/config.json` 的 `active_provider`。

### 8.4 GuardTarget（守护会话）

```
{ id, name, type: "friend"|"group", prompt }
```

### 8.5 AI History Item（对话历史）

```
{ role: "user"|"assistant", content: string }
```
前端额外使用 `error: true` 标记失败项（仅前端本地状态，不参与提交校验）。

---

### 8.6 ScheduledSendTask（定时发送任务）

```
{ id, target_id, target_name, target_type, enabled, mode, time_of_day,
  weekdays[], interval_minutes, run_at, cooldown_minutes, jitter_minutes,
  context_count, prompt }
```

- `mode`：`daily` / `weekly` / `interval` / `once`（各模式生效的字段见 §6.5.1）
- 持久化位置：`config/scheduled-send/{account}.json`
- 运行状态（`last_sent_at` / `next_fire_at`）**不在**任务对象里，只在 `data/scheduled-send-state.json` 与 `/status` 响应中

---

## 9. 前端调用时序

### 9.1 页面加载（`window.onload`）

```
setupPaneSplitter()
toggleRangeControls()
GET  /api/providers            → 填充供应商列表 / 预设
GET  /api/auto-reply/config    → 填充自动回复表单
GET  /api/chats
GET  /api/providers/balance    → 刷新余额（异步，不 await）
若无 activeProviderId → 打开模型弹窗
```

### 9.2 定时轮询

| 周期 | 调用 |
|---|---|
| 10s | `GET /api/chats` |
| 5s | `GET /api/messages?...`（页面隐藏且未开自动回复时跳过） |
| 3s | `GET /api/auto-reply/status` |
| 60s | `GET /api/providers/balance` |

### 9.3 选择会话

```
点击会话 → selectChat(id)
  → GET /api/chats（隐含，用已有 chats 数据定位）
  → GET /api/history?target_id={id}
  → GET /api/messages?target_id={id}&limit={n}
  → applyFilters() → renderMessages()
```

### 9.4 生成分析

```
runAnalysis(prompt, type)
  → AI 线程追加 {role:'user', content:prompt}
  → POST /api/summarize {target_name, messages:visibleMessages, type, prompt, history: 前10条}
  → AI 线程追加 {role:'assistant', content:summary}
  → POST /api/history {target_id, history:aiHistory}
```

### 9.5 模型设置

```
打开弹窗 → GET /api/providers
验证 Key   → POST /api/providers/validate
保存       → POST /api/providers  → 重新 GET /api/providers
使用       → POST /api/providers/activate → 重新 GET /api/providers
删除       → DELETE /api/providers/{id}  → 重新 GET /api/providers
```

### 9.6 自动回复设置

```
打开弹窗   → GET /api/auto-reply/config
加载会话   → GET /api/chats（供选择守护对象）
保存       → POST /api/auto-reply/config → 回填返回的 config
状态轮询   → GET /api/auto-reply/status
测试快捷键 → POST /api/auto-reply/test {action:'open_wechat'|'search'|'paste'|'send'}
测试剪贴板 → POST /api/auto-reply/test {action:'clipboard', text}
完整流程   → POST /api/auto-reply/test {action:'full', target_name, text}
```

---

### 9.7 定时发送设置（.NET 客户端）

```
左栏初始化   → GET /api/scheduled-send/config → GET /api/scheduled-send/status
15s 轮询     → GET /api/scheduled-send/status（刷"下次触发 / 上次发送"与状态文案）
总开关/勾选  → POST /api/scheduled-send/config { enabled, tasks: <整份> }
弹窗打开     → GET /api/chats（供选择发送对象）
弹窗预览     → POST /api/scheduled-send/test { action: 'preview', task }
弹窗试发     → POST /api/scheduled-send/test { action: 'send', task }
弹窗保存     → 只把编好的任务交回左栏面板，由面板整份提交（不走接口）
删除任务     → POST /api/scheduled-send/config { enabled, tasks: <删完后的整份> }
```

> **整份替换的后果**：弹窗不能自己去写 `tasks`，否则与面板的那次提交互相覆盖。
> 因此 `POST /api/scheduled-send/config` 的调用点只有面板 VM 一处。

---

## 10. 下游数据源接口（本服务的上游依赖）

> 重写前端**不需要**直连这些接口；此处记录是为了在排查数据问题时理解字段来源。

### 10.1 微信数据源 WeChatDataAnalysis（默认 `http://127.0.0.1:10392`）

| 方法 | 路径 | 参数 | 返回 |
|---|---|---|---|
| GET | `/api/chat/accounts` | — | `{ default_account }` |
| GET | `/api/chat/sessions` | `account, limit=400, include_hidden=false, include_official=false, preview=latest, source=realtime` | `{ sessions: [{ id?, username, name, isGroup, lastMessage, lastMessageTime, unreadCount }], source }` |
| GET | `/api/chat/messages` | `username, account, limit(≤500/页), offset, order=desc, source=realtime` | `{ messages: [{ id, createTime, isSent, senderDisplayName, senderUsername, content, renderType }] }` |

成功判定：`status ∈ {success, completed, 0, null}`，否则抛错。

---

## 11. 重写前端时的对接检查清单

1. 所有请求指向 `http://127.0.0.1:<port>`，`port` 读自 `config/config.json`。
2. 读接口加 `Cache-Control: no-store`（或等价语义）。
3. 判断成功：`HTTP 200` **且** `body.status === "success"`；`/api/chats`、`/api/messages` 还要额外看 `status_info` / `source_valid`。
4. 错误文案优先级：`detail` → `message` → `status_info.msg`。
5. 编辑供应商时 **api_key 留空表示沿用旧值**，不要误传空串导致 400。
6. 消息时间戳可能是秒或毫秒，展示层需自行兜底。
7. 自动回复相关功能在非 Windows 上不可用（`available=false`），需隐藏或禁用入口。
8. 守护对象上限：8 个好友 + 3 个群聊。
9. `/api/shutdown` 会整体退出，界面需给出确认。
10. 四个轮询周期（10s / 5s / 3s / 60s）与页面隐藏时的行为要一并迁移。
