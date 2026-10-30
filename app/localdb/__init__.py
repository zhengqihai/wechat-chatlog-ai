# -*- coding: utf-8 -*-
"""自研本地数据链路（替代上游 runtime/wechat）。

模块划分见 docs/SELFHOST_DATA_PLAN.md §3：
    cipher.py   页解密与 HMAC 校验（参数已在本机 19 个库上逐页验证）
    wal.py      WAL 帧解析与提交语义
    mirror.py   增量镜像（首轮全量 + 之后按新增页/帧）
    chats.py / messages.py / contacts.py / sns.py / media.py / exports.py
    keys.py     密钥来源（备份文件优先，可替换）
"""
