/* Chatlog AI 注入脚本（微信端）
 * 在微信聊天界面增加统一管理栏与现代化高质感 Slate 深色 AI 总结面板：
 *  - 顶部常驻切换栏：[微信] / [QQ] 全自动无感免密直通
 *  - 自动识别当前激活会话
 *  - 双列对称微质感卡片筛选（时间/条数），彻底告别输入框挤压
 *  - 极光渐变一键智能总结，精简分类，聚焦事实脉络与核心结论
 *  - 沉浸式追问 Dock、舒适大输入框与快捷药丸
 */
(function () {
  'use strict';
  if (window.__chatlogAiLoaded) return;
  window.__chatlogAiLoaded = true;

  var PROXY = 'http://127.0.0.1:18765';
  var QQ_UI = 'http://127.0.0.1:7690';
  var STORE_PREFIX = 'chatlog_ai:v2:';
  var DEFAULT_SUMMARY_PROMPT = '请一针见血总结本次筛选范围内的全部核心话题与讨论脉络；按重要程度归类，准确归属发言，合并无意义附和；突出核心争议、关键方案与最终结论（关键参数与结论请加粗）；保留人物角色简表与待跟进事项，严守事实，不主观臆测，不遗漏关键限定条件。';

  var state = {
    current: { username: '', name: '', isRoom: false },
    history: [],
    range: { timeMode: 'recent', timeValue: 24, timeUnit: 'hours', countMode: 'recent', countValue: 500 },
    thinking: false,
    panelOpen: false,
    panelWidth: 480,
    loading: false,
    requestSeq: 0,
    requestController: null,
    qqActive: false,
    weqToken: '',
  };

  /* ================= 现代高质感样式系统 ================= */
  var style = document.createElement('style');
  style.textContent = [
    /* 顶部统一切换栏（绝对物理居中 + 对称三栏） */
    '#cl-top-bar{position:fixed;top:0;left:0;right:0;height:42px;z-index:2147482000;background:rgba(11,15,25,0.92);backdrop-filter:blur(20px);border-bottom:1px solid rgba(255,255,255,0.08);display:flex;align-items:center;justify-content:space-between;padding:0 20px;box-sizing:border-box;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif;color:#f1f5f9;}',
    '#cl-top-bar .cl-left-side{display:flex;align-items:center;gap:8px;}',
    '#cl-top-bar .cl-brand{font-size:13.5px;font-weight:700;letter-spacing:0.4px;color:#f8fafc;display:flex;align-items:center;gap:8px;}',
    '#cl-top-bar .cl-brand::before{content:"";width:8px;height:8px;border-radius:50%;background:#10b981;box-shadow:0 0 10px #10b981;}',
    '#cl-top-bar .cl-tabs{position:absolute;left:50%;transform:translateX(-50%);display:inline-flex;background:rgba(30,41,59,0.7);padding:3px;border-radius:20px;border:1px solid rgba(255,255,255,0.1);}',
    '#cl-top-bar .cl-tab{appearance:none;border:0;background:transparent;color:#94a3b8;font-size:12px;font-weight:600;padding:4px 18px;border-radius:16px;cursor:pointer;transition:all 0.2s ease;display:flex;align-items:center;gap:6px;}',
    '#cl-top-bar .cl-tab:hover{color:#f8fafc;}',
    '#cl-top-bar .cl-tab.active{background:linear-gradient(135deg,#059669,#10b981);color:#fff;box-shadow:0 2px 8px rgba(16,185,129,0.35);}',
    '#cl-top-bar .cl-tab.active.qq-active{background:linear-gradient(135deg,#0284c7,#0ea5e9);box-shadow:0 2px 8px rgba(14,165,233,0.35);}',
    '#cl-top-bar .cl-right-side{display:flex;align-items:center;gap:10px;}',
    '#cl-top-bar .cl-privacy-btn{appearance:none;border:1px solid rgba(255,255,255,0.12);background:rgba(255,255,255,0.06);color:#cbd5e1;border-radius:6px;padding:3px 10px;font-size:12px;font-weight:500;cursor:pointer;display:inline-flex;align-items:center;gap:6px;transition:all 0.18s ease;user-select:none;}',
    '#cl-top-bar .cl-privacy-btn:hover{color:#fff;background:rgba(255,255,255,0.12);border-color:rgba(255,255,255,0.25);}',
    '#cl-top-bar .cl-privacy-btn.is-active{background:rgba(16,185,129,0.18);border-color:#10b981;color:#34d399;box-shadow:0 0 8px rgba(16,185,129,0.25);}',
    '#cl-top-bar .cl-wechat-status{font-size:11.5px;color:#34d399;display:flex;align-items:center;gap:5px;background:rgba(16,185,129,0.1);border:1px solid rgba(16,185,129,0.25);padding:2px 8px;border-radius:6px;}',
    '#cl-top-bar .cl-meta{font-size:11.5px;color:#94a3b8;display:flex;align-items:center;gap:8px;}',
    '#cl-top-bar .cl-meta code{background:rgba(255,255,255,0.08);border:1px solid rgba(255,255,255,0.1);padding:2px 8px;border-radius:6px;font-family:ui-monospace,SFMono-Regular,Menlo,monospace;color:#38bdf8;font-size:11px;}',
    '#cl-top-bar .cl-copy-btn{background:rgba(255,255,255,0.08);border:1px solid rgba(255,255,255,0.12);color:#cbd5e1;border-radius:6px;padding:2px 8px;font-size:11px;cursor:pointer;transition:all 0.15s;}',
    '#cl-top-bar .cl-copy-btn:hover{color:#fff;background:rgba(255,255,255,0.15);}',

    /* 全局隐私脱敏雾化（与 QQ 效果完全一致：默认 blur(9px)，悬停临时看清） */
    'html[data-privacy] .session-list-avatar,' +
    'html[data-privacy] .session-list-item-name,' +
    'html[data-privacy] .session-list-item-preview,' +
    'html[data-privacy] .contact-search-input,' +
    'html[data-privacy] .chat-header-title,' +
    'html[data-privacy] .message-avatar,' +
    'html[data-privacy] .message-sender-name,' +
    'html[data-privacy] .message-content,' +
    'html[data-privacy] .message-content-text,' +
    'html[data-privacy] .message-bubble,' +
    'html[data-privacy] .message-image,' +
    'html[data-privacy] .message-video,' +
    'html[data-privacy] .message-voice,' +
    'html[data-privacy] .message-file,' +
    'html[data-privacy] .message-card,' +
    'html[data-privacy] .message-redp,' +
    'html[data-privacy] .message-transfer,' +
    'html[data-privacy] .message-refer,' +
    'html[data-privacy] .resource-sidebar-muted,' +
    'html[data-privacy] .resource-image,' +
    'html[data-privacy] .contact-item-name,' +
    'html[data-privacy] .sns-user-name,' +
    'html[data-privacy] .sns-content,' +
    'html[data-privacy] .privacy-blur{filter:blur(9px) !important;transition:filter 0.18s ease !important;}',
    'html[data-privacy] .session-list-avatar:hover,' +
    'html[data-privacy] .session-list-item-name:hover,' +
    'html[data-privacy] .session-list-item-preview:hover,' +
    'html[data-privacy] .contact-search-input:hover,' +
    'html[data-privacy] .chat-header-title:hover,' +
    'html[data-privacy] .message-avatar:hover,' +
    'html[data-privacy] .message-sender-name:hover,' +
    'html[data-privacy] .message-content:hover,' +
    'html[data-privacy] .message-content-text:hover,' +
    'html[data-privacy] .message-bubble:hover,' +
    'html[data-privacy] .message-image:hover,' +
    'html[data-privacy] .message-video:hover,' +
    'html[data-privacy] .message-voice:hover,' +
    'html[data-privacy] .message-file:hover,' +
    'html[data-privacy] .message-card:hover,' +
    'html[data-privacy] .message-redp:hover,' +
    'html[data-privacy] .message-transfer:hover,' +
    'html[data-privacy] .message-refer:hover,' +
    'html[data-privacy] .resource-sidebar-muted:hover,' +
    'html[data-privacy] .resource-image:hover,' +
    'html[data-privacy] .contact-item-name:hover,' +
    'html[data-privacy] .sns-user-name:hover,' +
    'html[data-privacy] .sns-content:hover,' +
    'html[data-privacy] .privacy-blur:hover{filter:none !important;}',

    /* 微信页面顶部留白 */
    'body{padding-top:42px !important;box-sizing:border-box !important;}',

    /* 隐藏数据源回退 403 警告条（已由底层 WAL 同步接管） */
    '.data-source-fallback-banner{display:none !important;}',

    /* QQ 嵌入容器 */
    '#cl-qq-frame{position:fixed;top:42px;left:0;right:0;bottom:0;width:100%;height:calc(100% - 42px);border:0;z-index:2147481900;display:none;background:#0b0f19;}',

    /* 浮动唤起按钮 */
    '#cl-fab{position:fixed;right:24px;bottom:28px;z-index:2147482900;display:none;align-items:center;gap:9px;background:linear-gradient(135deg,#059669 0%,#10b981 60%,#34d399 100%);color:#fff;border:1px solid rgba(255,255,255,0.2);border-radius:26px;padding:12px 24px;font-size:14px;font-weight:600;cursor:pointer;box-shadow:0 8px 28px -4px rgba(16,185,129,0.5),0 2px 6px -1px rgba(0,0,0,0.15);letter-spacing:0.3px;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif;transition:all 0.25s cubic-bezier(0.4,0,0.2,1);backdrop-filter:blur(8px);}',
    '#cl-fab:hover{transform:translateY(-2px) scale(1.02);box-shadow:0 12px 32px -4px rgba(16,185,129,0.65);filter:brightness(1.06);}',
    '#cl-fab:active{transform:translateY(0) scale(0.98);}',

    /* 侧边总结面板 */
    '#cl-panel{position:fixed;top:42px;right:0;bottom:0;z-index:2147483000;width:480px;background:radial-gradient(at 100% 0%,rgba(16,185,129,0.12) 0px,transparent 50%),radial-gradient(at 0% 100%,rgba(6,182,212,0.08) 0px,transparent 50%),#0b0f19;color:#f1f5f9;display:none;flex-direction:column;box-shadow:-20px 0 60px rgba(0,0,0,0.65);font-family:-apple-system,BlinkMacSystemFont,"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif;border-left:1px solid rgba(255,255,255,0.08);}',
    '#cl-panel .drag-handle{position:absolute;left:-6px;top:0;bottom:0;width:12px;cursor:ew-resize;z-index:10;}',

    /* 面板头部 */
    '#cl-panel .p-head{display:flex;align-items:center;justify-content:space-between;padding:14px 20px;background:rgba(15,23,42,0.85);backdrop-filter:blur(20px);border-bottom:1px solid rgba(255,255,255,0.08);}',
    '#cl-panel .p-title-wrap{display:flex;align-items:center;gap:12px;min-width:0;}',
    '#cl-panel .p-sparkle-icon{width:32px;height:32px;border-radius:10px;background:linear-gradient(135deg,rgba(16,185,129,0.25),rgba(5,150,105,0.4));border:1px solid rgba(16,185,129,0.4);color:#34d399;display:grid;place-items:center;font-size:16px;flex-shrink:0;box-shadow:0 0 12px rgba(16,185,129,0.25);}',
    '#cl-panel .p-titles{display:flex;flex-direction:column;min-width:0;}',
    '#cl-panel .p-name{font-size:15px;font-weight:600;color:#f8fafc;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:240px;letter-spacing:0.2px;}',
    '#cl-panel .p-sub{font-size:11.5px;color:#94a3b8;margin-top:1px;}',
    '#cl-panel .p-actions{display:flex;gap:8px;align-items:center;}',
    '#cl-panel .icon-action-btn{appearance:none;border:1px solid rgba(255,255,255,0.1);background:rgba(255,255,255,0.04);color:#cbd5e1;border-radius:8px;padding:6px 12px;cursor:pointer;font-size:12px;font-weight:500;transition:all 0.18s ease;display:inline-flex;align-items:center;gap:4px;}',
    '#cl-panel .icon-action-btn:hover{color:#f8fafc;background:rgba(255,255,255,0.1);border-color:rgba(255,255,255,0.25);transform:translateY(-1px);}',
    '#cl-panel .icon-action-btn.danger:hover{color:#f87171;border-color:rgba(248,113,113,0.4);background:rgba(239,68,68,0.15);}',

    /* 筛选与执行控制区 */
    '#cl-panel .p-control-card{padding:16px 20px;background:rgba(18,24,38,0.7);backdrop-filter:blur(16px);border-bottom:1px solid rgba(255,255,255,0.08);display:flex;flex-direction:column;gap:12px;}',
    
    /* 双列对称筛选网格 */
    '#cl-panel .filter-grid{display:grid;grid-template-columns:1fr 1fr;gap:10px;}',
    '#cl-panel .filter-box{background:rgba(15,23,42,0.85);border:1px solid rgba(255,255,255,0.08);border-radius:10px;padding:8px 10px;display:flex;flex-direction:column;gap:6px;}',
    '#cl-panel .filter-box-label{font-size:11px;font-weight:600;color:#94a3b8;display:flex;align-items:center;gap:4px;}',
    '#cl-panel .filter-box-inputs{display:flex;align-items:center;gap:6px;}',
    '#cl-panel .glass-select,#cl-panel .glass-input{background:rgba(30,41,59,0.8);color:#f1f5f9;border:1px solid rgba(255,255,255,0.12);border-radius:6px;padding:5px 8px;font-size:12px;outline:none;transition:border-color 0.15s;}',
    '#cl-panel .glass-select:focus,#cl-panel .glass-input:focus{border-color:#34d399;}',
    '#cl-panel .glass-input{width:100%;flex:1;min-width:0;text-align:center;font-variant-numeric:tabular-nums;}',
    '#cl-panel .unit-text{font-size:12px;color:#94a3b8;flex-shrink:0;}',

    /* 开始总结主按钮 */
    '#cl-panel .main-run-btn{appearance:none;border:1px solid rgba(255,255,255,0.15);background:linear-gradient(135deg,#059669 0%,#10b981 50%,#34d399 100%);color:#fff;border-radius:12px;padding:12px 20px;font-size:14.5px;font-weight:600;cursor:pointer;display:flex;align-items:center;justify-content:center;gap:8px;box-shadow:0 4px 18px rgba(16,185,129,0.4);letter-spacing:0.3px;transition:all 0.22s cubic-bezier(0.4,0,0.2,1);margin-top:2px;}',
    '#cl-panel .main-run-btn:hover{transform:translateY(-1px);box-shadow:0 8px 24px rgba(16,185,129,0.55);filter:brightness(1.06);}',
    '#cl-panel .main-run-btn:active{transform:translateY(1px);filter:brightness(0.96);}',
    '#cl-panel .main-run-btn:disabled{opacity:0.6;cursor:not-allowed;transform:none;box-shadow:none;}',

    /* 消息展现流 */
    '#cl-panel .p-thread{flex:1;overflow-y:auto;padding:18px 20px;display:flex;flex-direction:column;gap:16px;min-height:0;}',
    '#cl-panel .msg-card{padding:16px 18px;border-radius:14px;font-size:13.5px;line-height:1.75;word-break:break-word;position:relative;}',
    '#cl-panel .msg-card.user{align-self:flex-end;max-width:85%;background:linear-gradient(135deg,#047857,#059669);color:#fff;border-bottom-right-radius:4px;box-shadow:0 4px 14px rgba(16,185,129,0.3);}',
    '#cl-panel .msg-card.action{align-self:center;padding:5px 12px;border-radius:12px;background:rgba(255,255,255,0.06);color:#94a3b8;border:1px solid rgba(255,255,255,0.08);font-size:12px;line-height:1.4;}',
    '#cl-panel .msg-card.ai{align-self:stretch;background:rgba(22,30,46,0.75);backdrop-filter:blur(12px);color:#e2e8f0;border:1px solid rgba(255,255,255,0.08);border-bottom-left-radius:4px;box-shadow:0 6px 20px rgba(0,0,0,0.3);}',
    '#cl-panel .msg-card.ai h3,#cl-panel .msg-card.ai h4{color:#34d399;margin:14px 0 8px;font-size:14px;font-weight:600;border-bottom:1px solid rgba(255,255,255,0.06);padding-bottom:5px;display:flex;align-items:center;gap:6px;}',
    '#cl-panel .msg-card.ai h3:first-child,#cl-panel .msg-card.ai h4:first-child{margin-top:0;}',
    '#cl-panel .msg-card.ai p{margin:6px 0;}',
    '#cl-panel .msg-card.ai ul{margin:6px 0;padding-left:18px;}',
    '#cl-panel .msg-card.ai li{margin:4px 0;}',
    '#cl-panel .msg-card.ai strong{color:#fbbf24;font-weight:600;}',
    '#cl-panel .msg-card.ai .time-range-badge{display:inline-block;padding:4px 10px;border-radius:8px;background:rgba(16,185,129,0.15);color:#34d399;font-size:12px;font-weight:600;margin-bottom:10px;border:1px solid rgba(16,185,129,0.3);}',
    '#cl-panel .msg-card.ai .table-wrap{width:100%;overflow-x:auto;margin:12px 0;}',
    '#cl-panel .msg-card.ai table{width:100%;border-collapse:collapse;font-size:12px;background:rgba(15,23,42,0.6);border-radius:8px;overflow:hidden;border:1px solid rgba(255,255,255,0.1);}',
    '#cl-panel .msg-card.ai th{background:rgba(30,41,59,0.85);color:#34d399;text-align:left;padding:8px 10px;font-weight:600;border-bottom:1px solid rgba(255,255,255,0.12);white-space:nowrap;}',
    '#cl-panel .msg-card.ai td{padding:7px 10px;border-bottom:1px solid rgba(255,255,255,0.06);color:#cbd5e1;line-height:1.5;}',
    '#cl-panel .msg-card.ai tr:last-child td{border-bottom:none;}',
    '#cl-panel .msg-card.ai tr:hover td{background:rgba(255,255,255,0.02);}',
    '#cl-panel .msg-card.ai hr{border:none;border-top:1px solid rgba(255,255,255,0.1);margin:14px 0;}',
    '#cl-panel .msg-card.ai .msg-card-footer{display:flex;gap:8px;justify-content:flex-end;margin-top:14px;padding-top:10px;border-top:1px dashed rgba(255,255,255,0.08);}',
    '#cl-panel .copy-action-btn{appearance:none;border:1px solid rgba(255,255,255,0.12);background:rgba(255,255,255,0.05);color:#cbd5e1;border-radius:6px;padding:4px 10px;font-size:11.5px;cursor:pointer;transition:all 0.18s ease;display:inline-flex;align-items:center;gap:4px;}',
    '#cl-panel .copy-action-btn:hover{color:#fff;background:rgba(255,255,255,0.12);border-color:rgba(255,255,255,0.25);transform:translateY(-1px);}',
    '#cl-panel .copy-action-btn.primary{background:linear-gradient(135deg,rgba(5,150,105,0.4),rgba(16,185,129,0.5));border-color:rgba(16,185,129,0.5);color:#34d399;font-weight:600;}',
    '#cl-panel .copy-action-btn.primary:hover{background:linear-gradient(135deg,#059669,#10b981);color:#fff;}',
    '#cl-panel .msg-card.err{background:rgba(239,68,68,0.12);color:#fca5a5;border:1px solid rgba(239,68,68,0.35);}',

    /* 现代极简空状态引导 */
    '#cl-panel .empty-guide-card{background:rgba(18,24,38,0.6);border:1px dashed rgba(255,255,255,0.1);border-radius:14px;padding:32px 20px;text-align:center;display:flex;flex-direction:column;align-items:center;gap:12px;margin:auto 0;}',
    '#cl-panel .empty-guide-icon{font-size:36px;background:linear-gradient(135deg,rgba(16,185,129,0.2),rgba(52,211,153,0.4));width:60px;height:60px;border-radius:18px;display:grid;place-items:center;border:1px solid rgba(16,185,129,0.3);box-shadow:0 0 20px rgba(16,185,129,0.25);}',
    '#cl-panel .empty-guide-title{font-size:15px;font-weight:600;color:#f8fafc;}',
    '#cl-panel .empty-guide-text{font-size:12.5px;color:#94a3b8;line-height:1.6;max-width:300px;}',

    /* 动态呼吸加载卡片 */
    '#cl-panel .loading-card{padding:16px 18px;border-radius:14px;background:rgba(18,24,38,0.7);border:1px solid rgba(16,185,129,0.35);color:#34d399;font-size:13px;display:flex;align-items:center;gap:12px;animation:cl-pulse 1.8s infinite;box-shadow:0 4px 16px rgba(16,185,129,0.15);}',
    '@keyframes cl-pulse{0%,100%{opacity:0.85;transform:scale(1);}50%{opacity:1;transform:scale(1.01);box-shadow:0 6px 22px rgba(16,185,129,0.3);}}',

    /* 底部沉浸式追问 Dock */
    '#cl-panel .p-dock{padding:14px 20px 18px;background:rgba(15,23,42,0.9);backdrop-filter:blur(20px);border-top:1px solid rgba(255,255,255,0.08);display:flex;flex-direction:column;gap:10px;}',
    '#cl-panel .p-quick-tags{display:flex;gap:8px;overflow-x:auto;padding-bottom:2px;}',
    '#cl-panel .quick-pill{appearance:none;border:1px solid rgba(255,255,255,0.1);background:rgba(255,255,255,0.05);color:#94a3b8;border-radius:14px;padding:5px 12px;font-size:12px;cursor:pointer;white-space:nowrap;transition:all 0.18s ease;}',
    '#cl-panel .quick-pill:hover{color:#f8fafc;background:rgba(255,255,255,0.12);border-color:rgba(16,185,129,0.4);transform:translateY(-1px);}',

    /* 舒适大输入框容器 */
    '#cl-panel .p-input-box{display:flex;align-items:center;background:rgba(30,41,59,0.7);border:1px solid rgba(255,255,255,0.12);border-radius:12px;padding:4px 6px 4px 14px;transition:all 0.2s ease;}',
    '#cl-panel .p-input-box:focus-within{border-color:#34d399;box-shadow:0 0 0 2px rgba(16,185,129,0.25);background:rgba(30,41,59,0.9);}',
    '#cl-panel .p-input-box input{flex:1;background:transparent;color:#f1f5f9;border:0;outline:none;font-size:13.5px;padding:6px 0;}',
    '#cl-panel .p-send-btn{appearance:none;border:0;background:linear-gradient(135deg,#059669,#10b981);color:#fff;border-radius:8px;padding:8px 16px;font-size:13px;font-weight:600;cursor:pointer;transition:all 0.18s ease;display:flex;align-items:center;gap:4px;}',
    '#cl-panel .p-send-btn:hover{filter:brightness(1.1);transform:scale(1.02);}',
    '#cl-panel .p-send-btn:disabled{opacity:0.5;cursor:not-allowed;transform:none;}',
  ].join('\n');
  document.head.appendChild(style);

  /* ================= DOM 构建 ================= */
  var topBar = document.createElement('div');
  topBar.id = 'cl-top-bar';
  topBar.innerHTML =
    '<div class="cl-left-side">' +
    '<div class="cl-brand">Chatlog AI</div>' +
    '</div>' +
    '<div class="cl-tabs">' +
    '<button id="cl-tab-wechat" class="cl-tab active" type="button">💬 微信</button>' +
    '<button id="cl-tab-qq" class="cl-tab" type="button">🐧 QQ</button>' +
    '</div>' +
    '<div class="cl-right-side">' +
    '<button class="cl-privacy-btn" id="cl-privacy-btn" type="button" title="开启隐私模式 (模糊聊天记录 Alt+P)">' +
    '<span class="cl-privacy-icon" id="cl-privacy-icon"></span>' +
    '<span id="cl-privacy-label">隐私模式</span>' +
    '</button>' +
    '<div class="cl-wechat-status" id="cl-wechat-status">🟢 微信已连接</div>' +
    '<div class="cl-meta" id="cl-token-wrap" style="display:none;">' +
    '<span>QQ 令牌:</span>' +
    '<code id="cl-token-display">加载中…</code>' +
    '<button class="cl-copy-btn" id="cl-copy" type="button">复制</button>' +
    '</div>' +
    '</div>';
  document.body.appendChild(topBar);

  var qqFrame = document.createElement('iframe');
  qqFrame.id = 'cl-qq-frame';
  qqFrame.src = 'about:blank';
  document.body.appendChild(qqFrame);

  var fab = document.createElement('button');
  fab.id = 'cl-fab';
  fab.type = 'button';
  fab.innerHTML = '✨ AI 智能总结';
  document.body.appendChild(fab);

  var panel = document.createElement('div');
  panel.id = 'cl-panel';
  panel.innerHTML =
    '<div class="drag-handle" id="cl-drag"></div>' +
    '<div class="p-head">' +
    '<div class="p-title-wrap">' +
    '<div class="p-sparkle-icon">💬</div>' +
    '<div class="p-titles">' +
    '<div class="p-name" id="cl-panel-name">当前聊天</div>' +
    '<div class="p-sub" id="cl-panel-sub">微信实时会话 · DeepSeek 驱动</div>' +
    '</div>' +
    '</div>' +
    '<div class="p-actions">' +
    '<button class="icon-action-btn" id="cl-clear" type="button" title="清空对话记录">清空</button>' +
    '<button class="icon-action-btn" id="cl-close" type="button" title="关闭面板">✕</button>' +
    '</div>' +
    '</div>' +
    '<div class="p-control-card">' +
    '<div class="filter-grid">' +
    '<div class="filter-box">' +
    '<div class="filter-box-label">⏱️ 时间跨度</div>' +
    '<div class="filter-box-inputs">' +
    '<select id="cl-time-mode" class="glass-select">' +
    '<option value="none">不限</option>' +
    '<option value="recent" selected>最近</option>' +
    '</select>' +
    '<input id="cl-time-value" class="glass-input" type="number" min="1" step="1" value="24">' +
    '<select id="cl-time-unit" class="glass-select">' +
    '<option value="minutes">分</option>' +
    '<option value="hours" selected>小时</option>' +
    '<option value="days">天</option>' +
    '</select>' +
    '</div>' +
    '</div>' +
    '<div class="filter-box">' +
    '<div class="filter-box-label">📊 消息上限</div>' +
    '<div class="filter-box-inputs">' +
    '<select id="cl-count-mode" class="glass-select">' +
    '<option value="unlimited">不限</option>' +
    '<option value="recent" selected>最近</option>' +
    '</select>' +
    '<input id="cl-count-value" class="glass-input" type="number" min="1" max="2000" step="1" value="500">' +
    '<span class="unit-text">条</span>' +
    '</div>' +
    '</div>' +
    '</div>' +
    '<div style="display:flex;align-items:center;justify-content:space-between;padding:2px 4px;margin-top:-2px;">' +
    '<span style="font-size:11.5px;color:#94a3b8;">分析策略</span>' +
    '<label style="display:inline-flex;align-items:center;gap:5px;font-size:12px;color:#cbd5e1;cursor:pointer;user-select:none;" title="开启后走思考模式，深度推演分析，但响应耗时稍长">' +
    '<input type="checkbox" id="cl-thinking" style="cursor:pointer;accent-color:#10b981;">' +
    '<span>🧠 深度思考</span>' +
    '</label>' +
    '</div>' +
    '<button id="cl-run-btn" class="main-run-btn" type="button">✨ 开始智能总结</button>' +
    '</div>' +
    '<div class="p-thread" id="cl-thread"></div>' +
    '<div class="p-dock">' +
    '<div class="p-quick-tags">' +
    '<button class="quick-pill" type="button" data-prompt="请提取聊天中的关键决策与明确结论">📌 关键决策</button>' +
    '<button class="quick-pill" type="button" data-prompt="请提取所有待办事项、负责人与时间节点">📋 提取待办</button>' +
    '<button class="quick-pill" type="button" data-prompt="请梳理大家提到的核心争议与不同观点">💡 观点分歧</button>' +
    '</div>' +
    '<div class="p-input-box">' +
    '<input id="cl-prompt" type="text" placeholder="向 DeepSeek 追问或补充指令...">' +
    '<button id="cl-send" class="p-send-btn" type="button">发送</button>' +
    '</div>' +
    '</div>';
  document.body.appendChild(panel);

  function $(id) { return document.getElementById(id); }

  function escapeHtml(str) {
    return String(str || '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function formatMarkdown(content) {
    if (!content) return '';
    var lines = content.split('\n');
    var htmlParts = [];
    var inList = false;
    var inTable = false;
    var tableRows = [];

    function flushList() {
      if (inList) { htmlParts.push('</ul>'); inList = false; }
    }

    function flushTable() {
      if (!inTable) return;
      if (tableRows.length > 0) {
        var tableHtml = '<div class="table-wrap"><table>';
        var startIndex = 0;
        if (tableRows.length >= 2 && /^\|?(\s*:?-+:?\s*\|?)+$/.test(tableRows[1].raw)) {
          tableHtml += '<thead><tr>';
          tableRows[0].cols.forEach(function (col) {
            tableHtml += '<th>' + renderInline(col) + '</th>';
          });
          tableHtml += '</tr></thead><tbody>';
          startIndex = 2;
        } else {
          tableHtml += '<tbody>';
        }
        for (var i = startIndex; i < tableRows.length; i++) {
          if (/^\|?(\s*:?-+:?\s*\|?)+$/.test(tableRows[i].raw)) continue;
          tableHtml += '<tr>';
          tableRows[i].cols.forEach(function (col) {
            tableHtml += '<td>' + renderInline(col) + '</td>';
          });
          tableHtml += '</tr>';
        }
        tableHtml += '</tbody></table></div>';
        htmlParts.push(tableHtml);
      }
      inTable = false;
      tableRows = [];
    }

    lines.forEach(function (line) {
      var trimmed = line.trim();
      if (!trimmed) {
        flushList();
        flushTable();
        return;
      }

      // 分割线
      if (/^---+$/.test(trimmed) || /^\*\*\*+$/.test(trimmed)) {
        flushList();
        flushTable();
        htmlParts.push('<hr>');
        return;
      }

      // 时间范围高亮块
      if (trimmed.indexOf('消息时间范围：') === 0) {
        flushList();
        flushTable();
        htmlParts.push('<div class="time-range-badge">📅 ' + escapeHtml(trimmed) + '</div>');
        return;
      }

      // 表格行识别
      if (trimmed.indexOf('|') >= 0 && /^\|?.+\|.+/.test(trimmed)) {
        flushList();
        inTable = true;
        var cols = trimmed.replace(/^\|/, '').replace(/\|$/, '').split('|').map(function (c) { return c.trim(); });
        tableRows.push({ raw: trimmed, cols: cols });
        return;
      }
      flushTable();

      // 标题处理
      if (trimmed.indexOf('###') === 0 || trimmed.indexOf('##') === 0 || trimmed.indexOf('#') === 0) {
        flushList();
        var titleText = trimmed.replace(/^#+\s*/, '');
        htmlParts.push('<h4>' + renderInline(titleText) + '</h4>');
        return;
      }

      // 列表处理
      if (/^[-*•]\s+/.test(trimmed) || /^\d+\.\s+/.test(trimmed)) {
        if (!inList) { htmlParts.push('<ul>'); inList = true; }
        var itemText = trimmed.replace(/^([-*•]|\d+\.)\s+/, '');
        htmlParts.push('<li>' + renderInline(itemText) + '</li>');
        return;
      }

      flushList();
      htmlParts.push('<p>' + renderInline(trimmed) + '</p>');
    });

    flushList();
    flushTable();
    return htmlParts.join('');
  }

  function renderInline(text) {
    var safe = escapeHtml(text);
    safe = safe.replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>');
    safe = safe.replace(/`([^`]+)`/g, '<code style="background:rgba(255,255,255,0.1);padding:1px 5px;border-radius:4px;font-size:12px;">$1</code>');
    return safe;
  }

  function fmtTime(epochSeconds) {
    var n = Number(epochSeconds || 0);
    if (!n) return '';
    var d = new Date(n * 1000);
    var p = function (x) { return (x < 10 ? '0' : '') + x; };
    return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + ' ' + p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
  }

  function storageKey() { return STORE_PREFIX + (state.current.username || 'global'); }

  function loadStored() {
    try {
      var raw = localStorage.getItem(storageKey());
      if (!raw) { state.history = []; return; }
      var data = JSON.parse(raw);
      state.history = Array.isArray(data.history) ? data.history : [];
      if (data.range) state.range = data.range;
      if (typeof data.thinking === 'boolean') state.thinking = data.thinking;
    } catch (e) { state.history = []; }
  }

  function saveStored() {
    try {
      localStorage.setItem(storageKey(), JSON.stringify({ history: state.history, range: state.range, thinking: state.thinking }));
    } catch (e) {}
  }

  function loadRemoteHistory(username) {
    if (!username) return;
    fetch(PROXY + '/api/ai/history?platform=wechat&target_id=' + encodeURIComponent(username))
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (!d || d.status !== 'success' || !Array.isArray(d.history)) return;
        if (state.current.username !== username) return;
        state.history = d.history;
        saveStored();
        renderThread(false);
      })
      .catch(function () {});
  }

  function cancelPendingRequest() {
    state.requestSeq += 1;
    if (state.requestController) {
      try { state.requestController.abort(); } catch (e) {}
    }
    state.requestController = null;
    state.loading = false;
    if ($('cl-run-btn')) $('cl-run-btn').disabled = false;
    if ($('cl-send')) $('cl-send').disabled = false;
  }

  function syncRangeControls() {
    if ($('cl-time-mode')) $('cl-time-mode').value = state.range.timeMode || 'recent';
    if ($('cl-time-value')) $('cl-time-value').value = state.range.timeValue || 24;
    if ($('cl-time-unit')) $('cl-time-unit').value = state.range.timeUnit || 'hours';
    if ($('cl-count-mode')) $('cl-count-mode').value = state.range.countMode || 'recent';
    if ($('cl-count-value')) $('cl-count-value').value = state.range.countValue || 500;
    if ($('cl-thinking')) $('cl-thinking').checked = Boolean(state.thinking);
  }

  function readRangeControls() {
    state.range.timeMode = $('cl-time-mode').value;
    state.range.timeValue = Math.max(1, parseInt($('cl-time-value').value, 10) || 24);
    state.range.timeUnit = $('cl-time-unit').value;
    state.range.countMode = $('cl-count-mode').value;
    state.range.countValue = Math.max(1, Math.min(2000, parseInt($('cl-count-value').value, 10) || 500));
    if ($('cl-thinking')) state.thinking = Boolean($('cl-thinking').checked);
    saveStored();
  }

  function computeRangeParams() {
    var hours = null;
    if (state.range.timeMode === 'recent') {
      var val = state.range.timeValue;
      if (state.range.timeUnit === 'minutes') hours = Math.max(0.05, Math.round((val / 60) * 100) / 100);
      else if (state.range.timeUnit === 'hours') hours = val;
      else if (state.range.timeUnit === 'days') hours = val * 24;
    }
    var count = state.range.countMode === 'recent' ? state.range.countValue : 2000;
    return { recent_hours: hours, recent_count: count };
  }

  function renderThread(waiting) {
    var box = $('cl-thread');
    if (!state.history.length && !waiting) {
      box.innerHTML =
        '<div class="empty-guide-card">' +
        '<div class="empty-guide-icon">✨</div>' +
        '<div class="empty-guide-title">已准备就绪</div>' +
        '<div class="empty-guide-text">可直接对话；涉及聊天内容时，AI 会按上方范围自动读取当前微信会话。</div>' +
        '</div>';
      return;
    }
    box.innerHTML = '';
    state.history.forEach(function (item) {
      var div = document.createElement('div');
      var isSummaryAction = item.role === 'user' && item.content === DEFAULT_SUMMARY_PROMPT;
      div.className = 'msg-card ' + (isSummaryAction ? 'action' : (item.role === 'user' ? 'user' : 'ai')) + (item.error ? ' err' : '');
      if (isSummaryAction) {
        div.textContent = '已请求总结当前筛选范围';
      } else if (item.role === 'user') {
        div.textContent = item.content || '';
      } else {
        var reasoningHtml = '';
        if (item.reasoning) {
          reasoningHtml = '<details style="margin-bottom:10px;padding:8px 12px;background:rgba(255,255,255,0.04);border:1px solid rgba(255,255,255,0.08);border-radius:8px;font-size:12px;color:#94a3b8;"><summary style="cursor:pointer;font-weight:600;color:#cbd5e1;user-select:none;">💭 深度思考过程（点击展开/收起）</summary><div style="margin-top:8px;white-space:pre-wrap;line-height:1.6;font-family:inherit;">' + escapeHtml(item.reasoning) + '</div></details>';
        }
        div.innerHTML = reasoningHtml + formatMarkdown(item.content || '');
        if (item.content && !item.error) {
          var footer = document.createElement('div');
          footer.className = 'msg-card-footer';

          var copyBtn = document.createElement('button');
          copyBtn.className = 'copy-action-btn primary';
          copyBtn.type = 'button';
          copyBtn.innerHTML = '📋 复制群聊格式';
          (function (raw) {
            copyBtn.onclick = function () {
              var text = toHumanFriendlyChatText(raw);
              copyToClipboard(text, copyBtn, '📋 复制群聊格式');
            };
          })(item.content);

          var copyMdBtn = document.createElement('button');
          copyMdBtn.className = 'copy-action-btn';
          copyMdBtn.type = 'button';
          copyMdBtn.innerHTML = '📝 复制 Markdown';
          (function (raw) {
            copyMdBtn.onclick = function () {
              copyToClipboard(raw, copyMdBtn, '📝 复制 Markdown');
            };
          })(item.content);

          footer.appendChild(copyBtn);
          footer.appendChild(copyMdBtn);
          div.appendChild(footer);
        }
      }
      box.appendChild(div);
    });
    if (waiting) {
      var w = document.createElement('div');
      w.className = 'loading-card';
      w.innerHTML = state.thinking
        ? '<span>⚡</span> 正在深度思考并分析当前微信会话（思考推演中，请稍候）…'
        : '<span>⚡</span> 正在识别意图并准备回答，需要时会自动读取当前微信会话…';
      box.appendChild(w);
    }
    box.scrollTop = box.scrollHeight;
  }

  function toHumanFriendlyChatText(md) {
    if (!md) return '';
    var lines = md.split('\n');
    var result = [];
    var inTable = false;

    for (var i = 0; i < lines.length; i++) {
      var line = lines[i].trim();
      if (!line) {
        if (!inTable) result.push('');
        inTable = false;
        continue;
      }

      // 分割线
      if (/^---+$/.test(line) || /^\*\*\*+$/.test(line)) {
        result.push('────────────────────────');
        inTable = false;
        continue;
      }

      // 表格转换
      if (line.indexOf('|') >= 0 && /^\|?.+\|.+/.test(line)) {
        var cols = line.replace(/^\|/, '').replace(/\|$/, '').split('|').map(function (c) { return c.trim(); });
        if (/^:?-+:?$/.test(cols[0] || '')) continue;
        if (!inTable) {
          inTable = true;
          continue;
        } else {
          if (cols.length >= 3) {
            result.push('• ' + cols[0] + '（' + cols[1] + '）：' + cols[2]);
          } else if (cols.length === 2) {
            result.push('• ' + cols[0] + '：' + cols[1]);
          } else {
            result.push('• ' + cols.join(' ｜ '));
          }
          continue;
        }
      }
      inTable = false;

      // 标题处理
      if (/^###\s+/.test(line)) {
        var t = line.replace(/^###\s+/, '').replace(/\*\*/g, '');
        result.push('\n【' + t + '】');
        continue;
      }
      if (/^##\s+/.test(line)) {
        var t = line.replace(/^##\s+/, '').replace(/\*\*/g, '');
        result.push('\n📌 ' + t);
        continue;
      }
      if (/^#\s+/.test(line)) {
        var t = line.replace(/^#\s+/, '').replace(/\*\*/g, '');
        result.push('🎯 ' + t + '\n');
        continue;
      }

      // 列表和粗体清理
      var cleanLine = line.replace(/\*\*(.+?)\*\*/g, '$1');
      if (/^[-*•]\s+/.test(cleanLine)) {
        cleanLine = cleanLine.replace(/^[-*•]\s+/, '🔹 ');
      }
      result.push(cleanLine);
    }

    return result.join('\n').replace(/\n{3,}/g, '\n\n').trim();
  }

  function copyToClipboard(text, btn, defaultHtml) {
    function onSuccess() {
      btn.innerHTML = '✓ 已复制到剪贴板';
      btn.style.filter = 'brightness(1.2)';
      setTimeout(function () {
        btn.innerHTML = defaultHtml;
        btn.style.filter = '';
      }, 1500);
    }

    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(onSuccess).catch(function () {
        fallbackCopy(text);
      });
    } else {
      fallbackCopy(text);
    }

    function fallbackCopy(str) {
      try {
        var ta = document.createElement('textarea');
        ta.value = str;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        document.execCommand('copy');
        document.body.removeChild(ta);
        onSuccess();
      } catch (e) {
        btn.innerHTML = '❌ 复制失败';
        setTimeout(function () { btn.innerHTML = defaultHtml; }, 1500);
      }
    }
  }

  /* ================= 状态轮询与会话感知 ================= */
  var sessionMap = {};

  function refreshSessions() {
    fetch('/api/chat/accounts')
      .then(function (r) { return r.json(); })
      .then(function (accData) {
        var account = accData.default_account || (Array.isArray(accData.accounts) && accData.accounts[0]) || '';
        if (!account) return;
        fetch('/api/chat/sessions?account=' + encodeURIComponent(account) + '&limit=500&source=realtime')
          .then(function (r) { return r.json(); })
          .then(function (res) {
            var list = (res && res.sessions) || [];
            list.forEach(function (item) {
              var u = String(item.username || item.id || '').trim();
              if (u && item.name) {
                sessionMap[u] = String(item.name).trim();
              }
            });
            if (state.current.username && sessionMap[state.current.username]) {
              var newName = sessionMap[state.current.username];
              if (state.current.name !== newName) {
                state.current.name = newName;
                $('cl-panel-name').textContent = newName;
              }
            }
          }).catch(function () {});
      }).catch(function () {});
  }

  function refreshStatus() {
    fetch(PROXY + '/api/status').then(function (r) { return r.json(); }).then(function (d) {
      if (!d) return;
      if (d.weq_token && d.weq_token !== state.weqToken) {
        state.weqToken = d.weqToken = d.weq_token;
        var tokenDisp = $('cl-token-display');
        if (tokenDisp) tokenDisp.textContent = d.weq_token.slice(0, 8) + '…' + d.weq_token.slice(-6);
      }
    }).catch(function () {});
  }

  function detectWechatChat() {
    var path = location.pathname || '';
    var match = path.match(/\/chat\/([^/?#]+)/);
    var username = match ? decodeURIComponent(match[1]) : '';
    if (!username) return;

    var titleEl = document.querySelector('.chat-page-header .name, .chat-title, .title-name, .contact-name, [data-testid="chat-header-title"]');
    var domName = (titleEl && titleEl.textContent && titleEl.textContent.trim()) || '';
    var mappedName = sessionMap[username] || domName || '';
    var isRoom = username.indexOf('@chatroom') >= 0;
    var name = mappedName || (isRoom ? '当前微信群' : '当前聊天');

    if (username !== state.current.username || (mappedName && state.current.name !== mappedName)) {
      if (username !== state.current.username) cancelPendingRequest();
      state.current = { username: username, name: name, isRoom: isRoom };
      $('cl-panel-name').textContent = name;
      $('cl-panel-sub').textContent = (isRoom ? '微信群聊' : '微信私聊') + ' · DeepSeek 驱动';
      loadStored();
      syncRangeControls();
      renderThread(false);
      loadRemoteHistory(username);
      checkAndAttachActiveTask(username);
      if (!state.qqActive) fab.style.display = 'flex';
    }
  }

  function checkAndAttachActiveTask(username) {
    if (!username) return;
    fetch(PROXY + '/api/ai/active_task?platform=wechat&target_id=' + encodeURIComponent(username))
      .then(function (r) { return r.json(); })
      .then(function (res) {
        if (state.current.username !== username) return;
        if (res && res.has_active_task && res.task) {
          if (res.task.status === 'running') {
            attachTaskStream(res.task);
          } else if (res.task.status === 'finished') {
            loadRemoteHistory(username);
          }
        }
      })
      .catch(function () {});
  }

  function attachTaskStream(task) {
    if (!task || !task.task_id) return;
    if (state.loading && state.activeTaskId === task.task_id) return;

    state.loading = true;
    state.activeTaskId = task.task_id;
    var requestUsername = state.current.username;
    var requestSeq = ++state.requestSeq;
    var controller = new AbortController();
    state.requestController = controller;

    var userPromptText = task.prompt || DEFAULT_SUMMARY_PROMPT;
    var lastItem = state.history.length ? state.history[state.history.length - 1] : null;
    if (!lastItem || lastItem.role !== 'user') {
      state.history.push({ role: 'user', content: userPromptText });
      saveStored();
    }
    renderThread(true);

    $('cl-run-btn').disabled = true;
    $('cl-send').disabled = true;

    var threadBox = $('cl-thread');
    var liveCard = document.createElement('div');
    liveCard.className = 'msg-card ai';

    var thinkingDetails = document.createElement('details');
    thinkingDetails.open = true;
    thinkingDetails.style.cssText = 'display:none;margin-bottom:10px;padding:8px 12px;background:rgba(255,255,255,0.04);border:1px solid rgba(255,255,255,0.08);border-radius:8px;font-size:12px;color:#94a3b8;';
    var thinkingSummary = document.createElement('summary');
    thinkingSummary.style.cssText = 'cursor:pointer;font-weight:600;color:#34d399;user-select:none;display:flex;align-items:center;gap:6px;';
    thinkingSummary.innerHTML = '<span>⚡ 正在深度思考中…</span>';
    var thinkingBody = document.createElement('div');
    thinkingBody.style.cssText = 'margin-top:8px;white-space:pre-wrap;line-height:1.6;font-family:inherit;max-height:260px;overflow-y:auto;';
    thinkingDetails.appendChild(thinkingSummary);
    thinkingDetails.appendChild(thinkingBody);
    liveCard.appendChild(thinkingDetails);

    var contentDiv = document.createElement('div');
    contentDiv.innerHTML = '<span style="color:#94a3b8;font-size:12.5px;">正在深度分析…</span>';
    liveCard.appendChild(contentDiv);

    threadBox.appendChild(liveCard);
    threadBox.scrollTop = threadBox.scrollHeight;

    var fullReasoning = '';
    var fullContent = '';
    var hasStartedContent = false;

    fetch(PROXY + '/api/ai/stream?task_id=' + encodeURIComponent(task.task_id) + '&last_seq=0', {
      signal: controller.signal
    })
      .then(function (r) {
        if (!r.ok) throw new Error('连接后台任务流失败');
        var reader = r.body.getReader();
        var decoder = new TextDecoder('utf-8');
        var buffer = '';

        function pump() {
          return reader.read().then(function (result) {
            if (result.done) return;
            buffer += decoder.decode(result.value, { stream: true });
            var lines = buffer.split('\n');
            buffer = lines.pop();

            for (var i = 0; i < lines.length; i++) {
              var line = lines[i].trim();
              if (!line || line.indexOf('data:') !== 0) continue;
              var jsonStr = line.slice(5).trim();
              if (jsonStr === '[DONE]') continue;
              try {
                var chunk = JSON.parse(jsonStr);
                if (chunk.type === 'reasoning') {
                  fullReasoning += chunk.delta || '';
                  thinkingDetails.style.display = 'block';
                  thinkingBody.textContent = fullReasoning;
                  thinkingBody.scrollTop = thinkingBody.scrollHeight;
                  threadBox.scrollTop = threadBox.scrollHeight;
                } else if (chunk.type === 'content') {
                  if (!hasStartedContent) {
                    hasStartedContent = true;
                    if (fullReasoning) {
                      thinkingSummary.innerHTML = '<span>💭 深度思考过程（已完成，点击展开/收起）</span>';
                      thinkingSummary.style.color = '#cbd5e1';
                      thinkingDetails.open = false;
                    }
                  }
                  fullContent += chunk.delta || '';
                  contentDiv.innerHTML = formatMarkdown(fullContent);
                  threadBox.scrollTop = threadBox.scrollHeight;
                } else if (chunk.type === 'finish') {
                  if (chunk.reply) fullContent = chunk.reply;
                  if (chunk.reasoning) fullReasoning = chunk.reasoning;
                } else if (chunk.type === 'error') {
                  throw new Error(chunk.detail || '流式生成中断');
                }
              } catch (e) {
                if (e.message && e.message.indexOf('流式生成中断') >= 0) throw e;
              }
            }
            return pump();
          });
        }
        return pump();
      })
      .then(function () {
        if (requestSeq !== state.requestSeq || state.current.username !== requestUsername) return;
        state.history.push({
          role: 'assistant',
          content: fullContent,
          reasoning: fullReasoning
        });
        saveStored();
      })
      .catch(function (err) {
        if (err && err.name === 'AbortError') return;
        if (requestSeq !== state.requestSeq || state.current.username !== requestUsername) return;
        var msg = err && err.message ? err.message : err;
        state.history.push({ role: 'assistant', content: '分析请求失败：' + msg, error: true });
        saveStored();
      })
      .finally(function () {
        if (requestSeq !== state.requestSeq) return;
        state.loading = false;
        state.activeTaskId = null;
        state.requestController = null;
        $('cl-run-btn').disabled = false;
        $('cl-send').disabled = false;
        renderThread(false);
      });
  }

  /* ================= 总结与分析 ================= */
  function runSummary(customPrompt) {
    if (!state.current.username) return;
    if (state.loading) return;
    state.loading = true;
    readRangeControls();

    var requestUsername = state.current.username;
    var requestName = state.current.name;
    var requestIsRoom = state.current.isRoom;
    var requestSeq = ++state.requestSeq;
    var controller = new AbortController();
    state.requestController = controller;
    var userPromptText = customPrompt ? customPrompt : DEFAULT_SUMMARY_PROMPT;
    state.history.push({ role: 'user', content: userPromptText });
    saveStored();
    renderThread(true);

    $('cl-run-btn').disabled = true;
    $('cl-send').disabled = true;

    var range = computeRangeParams();
    var payload = {
      platform: 'wechat',
      target_id: requestUsername,
      target_name: requestName || requestUsername,
      is_group: requestIsRoom,
      prompt: userPromptText,
      request_type: customPrompt ? 'chat' : 'summary',
      thinking: Boolean(state.thinking),
      stream: true,
      recent_hours: range.recent_hours,
      recent_count: range.recent_count
    };

    var threadBox = $('cl-thread');
    var liveCard = document.createElement('div');
    liveCard.className = 'msg-card ai';

    var thinkingDetails = document.createElement('details');
    thinkingDetails.open = true;
    thinkingDetails.style.cssText = 'display:none;margin-bottom:10px;padding:8px 12px;background:rgba(255,255,255,0.04);border:1px solid rgba(255,255,255,0.08);border-radius:8px;font-size:12px;color:#94a3b8;';
    var thinkingSummary = document.createElement('summary');
    thinkingSummary.style.cssText = 'cursor:pointer;font-weight:600;color:#34d399;user-select:none;display:flex;align-items:center;gap:6px;';
    thinkingSummary.innerHTML = '<span>⚡ 正在深度思考中…</span>';
    var thinkingBody = document.createElement('div');
    thinkingBody.style.cssText = 'margin-top:8px;white-space:pre-wrap;line-height:1.6;font-family:inherit;max-height:260px;overflow-y:auto;';
    thinkingDetails.appendChild(thinkingSummary);
    thinkingDetails.appendChild(thinkingBody);
    liveCard.appendChild(thinkingDetails);

    var contentDiv = document.createElement('div');
    contentDiv.innerHTML = '<span style="color:#94a3b8;font-size:12.5px;">正在准备总结…</span>';
    liveCard.appendChild(contentDiv);

    threadBox.appendChild(liveCard);
    threadBox.scrollTop = threadBox.scrollHeight;

    var fullReasoning = '';
    var fullContent = '';
    var hasStartedContent = false;

    fetch(PROXY + '/api/ai/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
      signal: controller.signal
    })
      .then(function (r) {
        if (!r.ok) {
          return r.text().then(function (t) {
            var errObj;
            try { errObj = JSON.parse(t); } catch (e) {}
            throw new Error((errObj && (errObj.detail || errObj.message)) || ('服务异常（HTTP ' + r.status + '）'));
          });
        }
        var tid = r.headers.get('X-Task-Id');
        if (tid) state.activeTaskId = tid;
        var reader = r.body.getReader();
        var decoder = new TextDecoder('utf-8');
        var buffer = '';

        function pump() {
          return reader.read().then(function (result) {
            if (result.done) return;
            buffer += decoder.decode(result.value, { stream: true });
            var lines = buffer.split('\n');
            buffer = lines.pop();

            for (var i = 0; i < lines.length; i++) {
              var line = lines[i].trim();
              if (!line || line.indexOf('data:') !== 0) continue;
              var jsonStr = line.slice(5).trim();
              if (jsonStr === '[DONE]') continue;
              try {
                var chunk = JSON.parse(jsonStr);
                if (chunk.type === 'reasoning') {
                  fullReasoning += chunk.delta || '';
                  thinkingDetails.style.display = 'block';
                  thinkingBody.textContent = fullReasoning;
                  thinkingBody.scrollTop = thinkingBody.scrollHeight;
                  threadBox.scrollTop = threadBox.scrollHeight;
                } else if (chunk.type === 'content') {
                  if (!hasStartedContent) {
                    hasStartedContent = true;
                    if (fullReasoning) {
                      thinkingSummary.innerHTML = '<span>💭 深度思考过程（已完成，点击展开/收起）</span>';
                      thinkingSummary.style.color = '#cbd5e1';
                      thinkingDetails.open = false;
                    }
                  }
                  fullContent += chunk.delta || '';
                  contentDiv.innerHTML = formatMarkdown(fullContent);
                  threadBox.scrollTop = threadBox.scrollHeight;
                } else if (chunk.type === 'finish') {
                  if (chunk.reply) fullContent = chunk.reply;
                  if (chunk.reasoning) fullReasoning = chunk.reasoning;
                } else if (chunk.type === 'error') {
                  throw new Error(chunk.detail || '流式生成中断');
                }
              } catch (e) {
                if (e.message && e.message.indexOf('流式生成中断') >= 0) throw e;
              }
            }
            return pump();
          });
        }
        return pump();
      })
      .then(function () {
        if (requestSeq !== state.requestSeq || state.current.username !== requestUsername) return;
        state.history.push({
          role: 'assistant',
          content: fullContent,
          reasoning: fullReasoning
        });
        saveStored();
      })
      .catch(function (err) {
        if (err && err.name === 'AbortError') return;
        if (requestSeq !== state.requestSeq || state.current.username !== requestUsername) return;
        var msg = err && err.message ? err.message : err;
        state.history.push({ role: 'assistant', content: '总结分析请求失败：' + msg, error: true });
        saveStored();
      })
      .finally(function () {
        if (requestSeq !== state.requestSeq) return;
        state.loading = false;
        state.activeTaskId = null;
        state.requestController = null;
        $('cl-run-btn').disabled = false;
        $('cl-send').disabled = false;
        renderThread(false);
      });
  }

  /* ================= 面板开闭与交互 ================= */
  function openPanel() {
    state.panelOpen = true;
    panel.style.display = 'flex';
    fab.style.display = 'none';
  }

  function closePanel() {
    state.panelOpen = false;
    panel.style.display = 'none';
    if (state.current.username && !state.qqActive) fab.style.display = 'flex';
  }

  fab.addEventListener('click', openPanel);
  $('cl-close').addEventListener('click', closePanel);

  $('cl-clear').addEventListener('click', function () {
    if (confirm('确定清空“' + (state.current.name || '当前会话') + '”的全部 AI 对话和上下文吗？')) {
      cancelPendingRequest();
      state.history = [];
      saveStored();
      renderThread(false);
      if (state.current.username) {
        fetch(PROXY + '/api/ai/history?platform=wechat&target_id=' + encodeURIComponent(state.current.username), {
          method: 'DELETE'
        }).catch(function () {});
      }
    }
  });

  $('cl-run-btn').addEventListener('click', function () {
    runSummary();
  });

  $('cl-send').addEventListener('click', function () {
    var text = $('cl-prompt').value.trim();
    if (!text) return;
    $('cl-prompt').value = '';
    runSummary(text);
  });

  $('cl-prompt').addEventListener('keydown', function (e) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      $('cl-send').click();
    }
  });

  document.querySelectorAll('.quick-pill').forEach(function (pill) {
    pill.addEventListener('click', function () {
      var promptText = this.getAttribute('data-prompt');
      if (promptText) runSummary(promptText);
    });
  });

  ['cl-time-mode', 'cl-time-value', 'cl-time-unit', 'cl-count-mode', 'cl-count-value', 'cl-thinking'].forEach(function (id) {
    if ($(id)) $(id).addEventListener('change', function () { readRangeControls(); });
  });

  (function () {
    var dragging = false;
    $('cl-drag').addEventListener('pointerdown', function (e) {
      dragging = true;
      e.preventDefault();
      document.body.style.cursor = 'ew-resize';
      document.body.style.userSelect = 'none';
    });
    window.addEventListener('pointermove', function (e) {
      if (!dragging) return;
      var w = window.innerWidth - e.clientX;
      w = Math.max(380, Math.min(880, w));
      state.panelWidth = w;
      panel.style.width = w + 'px';
    });
    window.addEventListener('pointerup', function () {
      dragging = false;
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    });
  })();

  /* ================= 双平台切换 ================= */
  function setTab(qq) {
    state.qqActive = qq;
    $('cl-tab-wechat').classList.toggle('active', !qq);
    $('cl-tab-qq').classList.toggle('active', qq);
    $('cl-tab-qq').classList.toggle('qq-active', qq);
    qqFrame.style.display = qq ? 'block' : 'none';
    $('cl-wechat-status').style.display = qq ? 'none' : 'flex';
    $('cl-token-wrap').style.display = qq ? 'flex' : 'none';
    if (qq) {
      var targetUrl = QQ_UI + '/';
      if (state.weqToken) {
        targetUrl = QQ_UI + '/?token=' + encodeURIComponent(state.weqToken);
      }
      if (qqFrame.src.indexOf(QQ_UI) !== 0) {
        qqFrame.src = targetUrl;
      }
      if (state.panelOpen) closePanel();
      fab.style.display = 'none';
    } else {
      if (state.current.username) fab.style.display = 'flex';
    }
  }

  $('cl-tab-wechat').addEventListener('click', function () { setTab(false); });
  $('cl-tab-qq').addEventListener('click', function () { setTab(true); });
  $('cl-copy').addEventListener('click', function () {
    try {
      navigator.clipboard.writeText(state.weqToken);
      this.textContent = '已复制';
      var self = this;
      setTimeout(function () { self.textContent = '复制'; }, 1500);
    } catch (e) {}
  });

  /* ================= 隐私脱敏雾化控制 ================= */
  var PRIVACY_KEY = 'chatlog_privacy_mode';
  var SVG_EYE = '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"></path><circle cx="12" cy="12" r="3"></circle></svg>';
  var SVG_EYE_OFF = '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24"></path><line x1="1" y1="1" x2="23" y2="23"></line></svg>';

  var privacyEnabled = false;
  try {
    var storedPrivacy = localStorage.getItem(PRIVACY_KEY) || localStorage.getItem('ui.privacy_mode');
    privacyEnabled = storedPrivacy === '1' || storedPrivacy === 'true';
  } catch (e) {}

  function setPrivacyMode(on) {
    privacyEnabled = !!on;
    if (privacyEnabled) {
      document.documentElement.setAttribute('data-privacy', '');
    } else {
      document.documentElement.removeAttribute('data-privacy');
    }
    try {
      localStorage.setItem(PRIVACY_KEY, privacyEnabled ? '1' : '0');
      localStorage.setItem('ui.privacy_mode', privacyEnabled ? '1' : '0');
    } catch (e) {}
    updatePrivacyUi();
  }

  function togglePrivacyMode() {
    setPrivacyMode(!privacyEnabled);
  }

  function syncNativeSidebarPrivacy() {
    var actions = document.querySelectorAll('.sidebar-rail-action');
    actions.forEach(function (act) {
      var title = (act.getAttribute('title') || '').trim();
      if (title.indexOf('隐私模式') >= 0) {
        if (!act.__privacyBound) {
          act.__privacyBound = true;
          act.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            togglePrivacyMode();
          }, true);
        }
        act.setAttribute('title', privacyEnabled ? '关闭隐私模式' : '开启隐私模式');
        var iconWrap = act.querySelector('.sidebar-rail-icon');
        if (iconWrap) {
          iconWrap.classList.toggle('sidebar-rail-icon-active', privacyEnabled);
          iconWrap.innerHTML = privacyEnabled ? SVG_EYE_OFF : SVG_EYE;
        }
      }
    });
  }

  function updatePrivacyUi() {
    var btn = $('cl-privacy-btn');
    var icon = $('cl-privacy-icon');
    var label = $('cl-privacy-label');
    if (btn) {
      btn.classList.toggle('is-active', privacyEnabled);
      btn.title = privacyEnabled ? '关闭隐私模式 (Alt+P)' : '开启隐私模式 (模糊聊天记录 Alt+P)';
    }
    if (icon) icon.innerHTML = privacyEnabled ? SVG_EYE_OFF : SVG_EYE;
    if (label) label.textContent = privacyEnabled ? '已雾化' : '雾化';
    syncNativeSidebarPrivacy();
  }

  /* ================= 初始化 ================= */
  setPrivacyMode(privacyEnabled);
  if ($('cl-privacy-btn')) {
    $('cl-privacy-btn').addEventListener('click', togglePrivacyMode);
  }
  window.addEventListener('keydown', function (e) {
    if (e.altKey && (e.key === 'p' || e.key === 'P')) {
      e.preventDefault();
      togglePrivacyMode();
    }
  });
  syncNativeSidebarPrivacy();
  setInterval(syncNativeSidebarPrivacy, 1200);

  refreshSessions();
  setInterval(refreshSessions, 10000);
  refreshStatus();
  setInterval(refreshStatus, 5000);
  detectWechatChat();
  setInterval(detectWechatChat, 1000);

  /* ================= 实时增量消息静默守护 ================= */
  var lastLiveMsgId = '';
  var lastLiveMsgTime = 0;
  function guardLiveChatMessages() {
    if (state.qqActive) return;
    var path = location.pathname || '';
    var match = path.match(/\/chat\/([^/?#]+)/);
    var username = match ? decodeURIComponent(match[1]) : '';
    if (!username) return;

    fetch('/api/chat/messages?username=' + encodeURIComponent(username) + '&limit=1&order=desc')
      .then(function (r) { return r.json(); })
      .then(function (data) {
        var msgs = (data && data.messages) || [];
        if (!msgs.length) return;
        var top = msgs[0];
        var id = String(top.id || top.localId || '');
        var time = Number(top.createTime || 0);
        if (!lastLiveMsgId) {
          lastLiveMsgId = id;
          lastLiveMsgTime = time;
          return;
        }
        if (id !== lastLiveMsgId || time > lastLiveMsgTime) {
          lastLiveMsgId = id;
          lastLiveMsgTime = time;
          if (typeof window.__chatRealtimeTrigger === 'function') {
            window.__chatRealtimeTrigger();
          }
        }
      })
      .catch(function () {});
  }
  setInterval(guardLiveChatMessages, 2000);
})();
