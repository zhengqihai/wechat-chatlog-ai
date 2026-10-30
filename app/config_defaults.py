# -*- coding: utf-8 -*-
"""配置默认值的单一事实源。

本模块只存放纯数据、不导入任何业务模块，因此可被任意入口安全引用：
- server：文件读取失败时用它作为兜底。

（原 launcher.py 也用它自动补齐缺失的 config/ 文件；launcher 已退役，见
docs/DOTNET_CLIENT_PLAN.md §11.2 / T14，该职责现由 .NET 客户端的 Bootstrap 承担。）

config/ 下的密钥与提示词文件不入库，新环境首次启动时按此处的默认值生成。
"""

# config/config.json 的默认内容。
DEFAULT_CONFIG = {
    "port": 18765,
    "wechat_api": "http://127.0.0.1:10392",
    "wechat_account": "",
    "active_provider": "",
    # 数据来源：local（自研本地解密链路）/ upstream（上游 HTTP）。
    # **默认 local**（阶段 D2）：C1–C5 已覆盖会话/消息、通讯录+头像、朋友圈时间线+用户、朋友圈导出；
    # 仍走上游的只剩「媒体本体（图片/语音/视频）」与账号枚举（阻塞于 C6，见 SELFHOST_DATA_PLAN.md）。
    # 本地链路未就绪时 `get_source()` 会自动回退上游，原因经 /api/data-source/status 暴露。
    "data_source": "local",
    # 防撤回（T-F5）：开启后，打开会话时把好友/群成员撤回的消息标出来并尽量显示原文。
    "anti_recall_enabled": False,
}

# config/api-keys.json 的默认内容。
DEFAULT_API_KEYS = {"providers": []}

# config/prompts/<name>.txt 的默认文案；键名即 load_prompt(name) 的 name。
DEFAULT_PROMPTS = {
    "summary-meeting": "你是一名高效的高级会议秘书。请对以下聊天记录整理为标准的【会议纪要与决策】，包含：1. 讨论背景 2. 关键决策 3. 责任人及后续动作。",
    "summary-todo": "你是一个严谨的项目经理。请从以下聊天记录中精确提取【待办事项与任务列表 (Action Items)】，包含：任务描述、责任人、预期交付时间。",
    "summary-topics": "你是一个信息分析专家。请提炼以下聊天记录中的【核心讨论热点与话题】，按关注度从高到低排列，并给出简要总结。",
    "summary-general": "你是一个智能聊天助手。请对以下聊天记录提供简洁清晰的 Markdown 格式结构化总结，包含核心议题、关键观点及待办项。",
    "auto-reply-outer": "你是聊天记录中的“我”本人。",
    "auto-reply-inner": "请根据最近的对话上下文，生成一条自然、简洁、符合“我”语气和风格的下一条回复。只输出回复正文本身，不要任何解释、引号、“回复：”等前缀或额外说明。",
    "scheduled-greeting": "你是聊天记录中的“我”本人。请先回顾最近的对话上下文（上次聊到哪、是否还有没接上的话题），再据此写一条自然、不突兀的主动打招呼消息，像真人临时想起对方一样。只输出要发送的消息正文，不要任何解释、引号、“消息：”等前缀或额外说明。",
}
