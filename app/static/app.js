let chats = [];
let currentChat = null;
let rawMessages = [];
let visibleMessages = [];
let analysisType = 'general';
let aiHistory = [];
let messageLoading = false;
let autoReplyConfig = null;
let guardChatOptions = [];
let providerPresets = {};
let providers = [];
let activeProviderId = null;

window.addEventListener('load', async function () {
    setupPaneSplitter();
    toggleRangeControls();
    await loadProviders();
    await loadAutoReplyConfig();
    await loadChats(true);
    refreshBalance();
    if (!activeProviderId) openModelModal();
    window.setInterval(function () { loadChats(false); }, 10000);
    window.setInterval(function () { refreshMessages(); }, 5000);
    window.setInterval(refreshAutoReplyStatus, 3000);
    window.setInterval(refreshBalance, 60000);
});

async function loadChats(showLoading) {
    const list = document.getElementById('chatList');
    if (showLoading) list.innerHTML = '<div class="empty loading">正在读取真实会话</div>';
    try {
        const response = await fetch('/api/chats', { cache: 'no-store' });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '会话读取失败'));
        chats = Array.isArray(data.chats) ? data.chats : [];
        setSourceStatus(data.status_info && data.status_info.msg ? data.status_info.msg : '实时数据源已连接', 'ready');
        if (currentChat) currentChat = chats.find(function (item) { return item.id === currentChat.id; }) || currentChat;
        renderChatList();
    } catch (error) {
        chats = [];
        setSourceStatus(error.message || '数据源连接失败', 'error');
        list.innerHTML = '<div class="empty">' + escapeHtml(error.message || '数据源连接失败') + '</div>';
    }
}

async function saveHistory() {
    if (!currentChat || !aiHistory.length) return;
    try {
        await fetch('/api/history', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ target_id: currentChat.id, history: aiHistory })
        });
    } catch (error) {
        console.error('Failed to save history:', error);
    }
}

function setSourceStatus(text, state) {
    const element = document.getElementById('sourceStatus');
    element.className = 'source-status ' + state;
    document.getElementById('sourceStatusText').textContent = text;
}

function renderChatList() {
    const list = document.getElementById('chatList');
    const query = document.getElementById('searchInput').value.trim().toLowerCase();
    const filtered = chats.filter(function (chat) { return String(chat.name || '').toLowerCase().includes(query); });
    list.replaceChildren();
    if (!filtered.length) {
        list.innerHTML = '<div class="empty">没有匹配的真实会话</div>';
        return;
    }
    filtered.forEach(function (chat) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'chat-item' + (currentChat && currentChat.id === chat.id ? ' active' : '');
        button.addEventListener('click', function () { selectChat(chat.id); });

        const avatar = document.createElement('span');
        avatar.className = 'avatar wechat-avatar';
        avatar.textContent = chat.type === 'group' ? '群' : '微';

        const copy = document.createElement('span');
        copy.className = 'chat-copy';
        const line = document.createElement('span');
        line.className = 'chat-line';
        const name = document.createElement('span');
        name.className = 'chat-name';
        name.textContent = chat.name || chat.id;
        const time = document.createElement('span');
        time.className = 'chat-time';
        time.textContent = shortTime(chat.last_time);
        line.append(name, time);
        const preview = document.createElement('span');
        preview.className = 'chat-preview';
        preview.textContent = chat.last_message || (chat.type === 'group' ? '群聊' : '私聊');
        copy.append(line, preview);
        button.append(avatar, copy);

        if (Number(chat.unread || 0) > 0) {
            const unread = document.createElement('span');
            unread.className = 'unread';
            unread.textContent = Number(chat.unread) > 99 ? '99+' : String(chat.unread);
            button.append(unread);
        }
        list.append(button);
    });
}

async function selectChat(chatId) {
    currentChat = chats.find(function (chat) { return chat.id === chatId; });
    if (!currentChat) return;
    renderChatList();
    document.getElementById('chatTitle').textContent = currentChat.name;
    document.getElementById('chatSubtitle').textContent = '微信 · ' + (currentChat.type === 'group' ? '群聊' : '私聊') + ' · 实时来源';
    await loadHistory();
    await reloadMessages();
}

async function loadHistory() {
    if (!currentChat) {
        aiHistory = [];
        renderAiThread();
        return;
    }
    try {
        const response = await fetch(`/api/history?target_id=${currentChat.id}`, { cache: 'no-store' });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '历史记录加载失败'));
        aiHistory = data.history || [];
    } catch (error) {
        console.error('Failed to load history:', error);
        aiHistory = [];
    }
    renderAiThread();
}

function getRangeConfig() {
    const timeMode = document.getElementById('timeMode').value;
    const countMode = document.getElementById('countMode').value;
    const timeValue = Math.max(1, Number(document.getElementById('timeValue').value || 1));
    const countValue = Math.max(1, Math.min(2000, Number(document.getElementById('countInput').value || 500)));
    let recentHours = null;
    if (timeMode === 'recent') {
        const unit = document.getElementById('timeUnit').value;
        recentHours = unit === 'minutes' ? timeValue / 60 : (unit === 'days' ? timeValue * 24 : timeValue);
    }
    return {
        recentHours: recentHours,
        count: countMode === 'recent' ? countValue : null,
        requestLimit: countMode === 'recent' ? countValue : 2000
    };
}

function requestedLimit() {
    return getRangeConfig().requestLimit;
}

function toggleRangeControls() {
    const timeRecent = document.getElementById('timeMode').value === 'recent';
    const countRecent = document.getElementById('countMode').value === 'recent';
    document.getElementById('timeValue').disabled = !timeRecent;
    document.getElementById('timeUnit').disabled = !timeRecent;
    document.getElementById('countInput').disabled = !countRecent;
    document.getElementById('timeValue').setAttribute('aria-disabled', String(!timeRecent));
    document.getElementById('countInput').setAttribute('aria-disabled', String(!countRecent));
}

async function reloadMessages() {
    if (!currentChat || messageLoading) return;
    messageLoading = true;
    document.getElementById('messageList').innerHTML = '<div class="empty loading">正在读取真实消息</div>';
    try {
        await fetchMessages();
    } catch (error) {
        document.getElementById('messageList').innerHTML = '<div class="empty">' + escapeHtml(error.message || '消息读取失败') + '</div>';
    } finally {
        messageLoading = false;
    }
}

async function refreshMessages() {
    if (!currentChat || messageLoading) return;
    const autoReplyOn = autoReplyConfig && autoReplyConfig.enabled;
    if (document.hidden && !autoReplyOn) return;
    messageLoading = true;
    try { await fetchMessages(); } catch (error) { setSourceStatus(error.message || '实时刷新失败', 'error'); }
    finally { messageLoading = false; }
}

async function fetchMessages() {
    const selectedId = currentChat.id;
    const params = new URLSearchParams({
        target_id: selectedId,
        limit: String(requestedLimit())
    });
    const response = await fetch('/api/messages?' + params.toString(), { cache: 'no-store' });
    const data = await readJson(response);
    if (!response.ok || data.status !== 'success' || data.source_valid !== true) throw new Error(apiError(data, '消息读取失败'));
    if (!currentChat || currentChat.id !== selectedId) return;
    rawMessages = Array.isArray(data.messages) ? data.messages : [];
    applyFilters();
    setSourceStatus('微信 实时数据源已连接', 'ready');
}

function applyFilters() {
    const range = getRangeConfig();
    let result = rawMessages.slice();
    if (range.recentHours !== null) {
        const cutoff = Date.now() / 1000 - range.recentHours * 3600;
        result = result.filter(function (message) {
            const timestamp = messageTimestamp(message);
            return timestamp !== null && timestamp >= cutoff;
        });
    }
    if (range.count !== null) result = result.slice(-range.count);
    visibleMessages = result;
    renderMessages();
    updateAnalysisCount();
}

function renderMessages() {
    const list = document.getElementById('messageList');
    list.replaceChildren();
    if (!visibleMessages.length) {
        list.innerHTML = '<div class="empty">当前筛选范围内没有真实消息</div>';
        return;
    }
    visibleMessages.forEach(function (message) {
        const item = document.createElement('article');
        item.className = 'message' + (message.sender === '我' ? ' mine' : '');
        const meta = document.createElement('div');
        meta.className = 'message-meta';
        const sender = document.createElement('span');
        sender.className = 'message-sender';
        sender.textContent = message.sender || '未知发送者';
        const time = document.createElement('time');
        time.textContent = message.time || '';
        meta.append(sender, time);
        const content = document.createElement('div');
        content.className = 'message-content';
        content.textContent = message.content || '[消息]';
        item.append(meta, content);
        list.append(item);
    });
    list.scrollTop = list.scrollHeight;
}

function selectAnalysisType(button) {
    analysisType = button.dataset.type || 'general';
    document.querySelectorAll('.analysis-tab').forEach(function (item) { item.classList.toggle('active', item === button); });
}

function runPreset() {
    const labels = { general: '请综合总结当前筛选范围内的聊天记录', meeting: '请整理会议纪要与关键决策', todo: '请提取待办事项、责任人和时间', topics: '请分析核心话题及关注度' };
    runAnalysis(labels[analysisType], analysisType);
}

function sendCustomPrompt() {
    const input = document.getElementById('promptInput');
    const prompt = input.value.trim();
    if (!prompt) return;
    input.value = '';
    runAnalysis(prompt, 'general');
}

function handlePromptKey(event) {
    if (event.key === 'Enter' && !event.isComposing) sendCustomPrompt();
}

async function runAnalysis(prompt, type) {
    if (!currentChat) { window.alert('请先选择会话'); return; }
    if (!visibleMessages.length) { window.alert('当前筛选范围内没有消息'); return; }
    const runButton = document.getElementById('runButton');
    const sendButton = document.getElementById('sendButton');
    runButton.disabled = true;
    sendButton.disabled = true;
    aiHistory.push({ role: 'user', content: prompt });
    renderAiThread(true);
    try {
        const priorHistory = aiHistory.slice(0, -1).slice(-10);
        const response = await fetch('/api/summarize', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                target_name: currentChat.name,
                messages: visibleMessages,
                type: type,
                prompt: prompt,
                history: priorHistory
            })
        });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '模型请求失败'));
        aiHistory.push({ role: 'assistant', content: data.summary || '' });
        await saveHistory();
    } catch (error) {
        aiHistory.push({ role: 'assistant', content: '请求失败：' + (error.message || '未知错误'), error: true });
    } finally {
        runButton.disabled = false;
        sendButton.disabled = false;
        renderAiThread();
    }
}

function clearAiThread() { aiHistory = []; renderAiThread(); }

function renderAiThread(waiting) {
    const thread = document.getElementById('aiThread');
    if (!aiHistory.length) {
        thread.innerHTML = '<div class="empty">选择会话后可生成分析或直接提问</div>';
        return;
    }
    thread.innerHTML = aiHistory.map(function (turn, index) {
        const roleName = turn.role === 'user' ? '你' : assistantName();
        const content = turn.role === 'assistant' ? renderMarkdown(turn.content) : '<p>' + escapeHtml(turn.content) + '</p>';
        const copyBtn = turn.role === 'assistant'
            ? `<button class="copy-btn" onclick="copyHistoryItem(${index})">复制</button>`
            : '';
        return '<div class="ai-turn ' + turn.role + '"><div class="ai-role">' + roleName + '</div><div class="ai-bubble"' + (turn.error ? ' style="color:var(--danger)"' : '') + '>' + content + copyBtn + '</div></div>';
    }).join('') + (waiting ? '...' : '');
    thread.scrollTop = thread.scrollHeight;
}

function updateAnalysisCount() {
    const range = getRangeConfig();
    const scope = range.count === null ? '最多 2000 条' : '最近 ' + range.count + ' 条';
    const timeScope = range.recentHours === null ? '不限制时间' : '最近 ' + formatHours(range.recentHours);
    document.getElementById('analysisCount').textContent = '将分析 ' + visibleMessages.length + ' 条真实消息（' + timeScope + '，' + scope + '）';
    document.getElementById('chatSubtitle').textContent = currentChat
        ? '微信 · ' + (currentChat.type === 'group' ? '群聊' : '私聊') + ' · 当前显示 ' + visibleMessages.length + ' 条'
        : '未选择实时会话';
}

function formatHours(hours) {
    if (hours < 1) return Math.round(hours * 60) + ' 分钟';
    if (hours % 24 === 0) return (hours / 24) + ' 天';
    return hours + ' 小时';
}

function setupPaneSplitter() {
    const splitter = document.getElementById('paneSplitter');
    const shell = document.querySelector('.app-shell');
    if (!splitter || !shell) return;
    let resizing = false;
    splitter.addEventListener('pointerdown', function (event) {
        if (window.innerWidth <= 900) return;
        resizing = true;
        splitter.setPointerCapture(event.pointerId);
        document.body.classList.add('is-resizing');
    });
    splitter.addEventListener('pointermove', function (event) {
        if (!resizing) return;
        const rect = shell.getBoundingClientRect();
        const aiWidth = Math.max(320, Math.min(620, rect.right - event.clientX));
        shell.style.gridTemplateColumns = '300px minmax(320px, 1fr) 8px ' + aiWidth + 'px';
    });
    function stopResize() {
        resizing = false;
        document.body.classList.remove('is-resizing');
    }
    splitter.addEventListener('pointerup', stopResize);
    splitter.addEventListener('pointercancel', stopResize);
    splitter.addEventListener('keydown', function (event) {
        if (window.innerWidth <= 900) return;
        const rect = shell.getBoundingClientRect();
        const current = parseFloat(getComputedStyle(shell).gridTemplateColumns.split(' ').pop()) || 440;
        if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
            event.preventDefault();
            const delta = event.key === 'ArrowLeft' ? 24 : -24;
            const aiWidth = Math.max(320, Math.min(620, current + delta));
            shell.style.gridTemplateColumns = '300px minmax(320px, 1fr) 8px ' + aiWidth + 'px';
        }
    });
    window.addEventListener('resize', function () {
        if (window.innerWidth <= 900) shell.style.gridTemplateColumns = '';
    });
}

async function loadProviders() {
    try {
        const response = await fetch('/api/providers', { cache: 'no-store' });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '读取模型配置失败'));
        providerPresets = data.presets || {};
        providers = Array.isArray(data.providers) ? data.providers : [];
        activeProviderId = data.active_id || '';
        renderProviderList();
        updateModelStatus();
        return true;
    } catch (error) {
        document.getElementById('modelStatus').textContent = '模型配置读取失败';
        return false;
    }
}

function updateModelStatus() {
    const active = providers.find(function (p) { return p.id === activeProviderId; });
    const el = document.getElementById('modelStatus');
    if (!active) {
        el.textContent = '未配置模型，点击“模型设置”添加 API Key';
        return;
    }
    el.textContent = (active.name || active.provider_type) + ' / ' + (active.model || '');
}

function assistantName() {
    const active = providers.find(function (p) { return p.id === activeProviderId; });
    return active ? (active.name || 'AI') : 'AI';
}

function renderProviderList() {
    const container = document.getElementById('providerList');
    if (!container) return;
    container.replaceChildren();
    if (!providers.length) {
        container.innerHTML = '<div class="form-hint">尚未添加模型供应商，请在下方填写并验证后保存。</div>';
        return;
    }
    providers.forEach(function (p) {
        const item = document.createElement('div');
        item.className = 'provider-item' + (p.id === activeProviderId ? ' active' : '');
        const info = document.createElement('div');
        info.className = 'provider-info';
        const title = document.createElement('div');
        title.className = 'provider-title';
        title.textContent = (p.name || p.provider_type) + (p.id === activeProviderId ? '（当前使用）' : '');
        const sub = document.createElement('div');
        sub.className = 'provider-sub';
        sub.textContent = (p.model || '') + ' · ' + (p.base_url || '') + ' · Key ' + (p.api_key_masked || '未设置');
        info.append(title, sub);
        const actions = document.createElement('div');
        actions.className = 'provider-actions';
        const useBtn = document.createElement('button');
        useBtn.type = 'button';
        useBtn.className = 'btn';
        useBtn.textContent = p.id === activeProviderId ? '使用中' : '使用';
        useBtn.disabled = p.id === activeProviderId;
        useBtn.addEventListener('click', function () { activateProvider(p.id); });
        const delBtn = document.createElement('button');
        delBtn.type = 'button';
        delBtn.className = 'btn btn-danger';
        delBtn.textContent = '删除';
        delBtn.addEventListener('click', function () { deleteProvider(p.id); });
        actions.append(useBtn, delBtn);
        item.append(info, actions);
        container.append(item);
    });
}

function populateProviderTypeOptions() {
    const select = document.getElementById('mpType');
    select.replaceChildren();
    Object.keys(providerPresets).forEach(function (key) {
        const opt = document.createElement('option');
        opt.value = key;
        opt.textContent = providerPresets[key].name || key;
        select.append(opt);
    });
    onProviderTypeChange();
}

function onProviderTypeChange() {
    const type = document.getElementById('mpType').value;
    const preset = providerPresets[type];
    if (!preset) return;
    document.getElementById('mpBaseUrl').value = preset.base_url || '';
    document.getElementById('mpModel').value = preset.model || '';
    if (!document.getElementById('mpName').value) {
        document.getElementById('mpName').value = preset.name || '';
    }
}

function collectProviderForm() {
    return {
        provider_type: document.getElementById('mpType').value,
        name: document.getElementById('mpName').value.trim(),
        base_url: document.getElementById('mpBaseUrl').value.trim(),
        model: document.getElementById('mpModel').value.trim(),
        api_key: document.getElementById('mpApiKey').value.trim()
    };
}

async function validateProvider() {
    const payload = collectProviderForm();
    if (!payload.base_url || !payload.model || !payload.api_key) {
        window.alert('请填写 Base URL、模型名和 API Key');
        return;
    }
    const btn = document.getElementById('mpValidateBtn');
    const msg = document.getElementById('mpValidateMsg');
    btn.disabled = true;
    msg.textContent = '正在验证……';
    try {
        const response = await fetch('/api/providers/validate', {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
        });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '验证失败'));
        msg.textContent = '验证通过：' + (data.message || 'Key 可用');
        document.getElementById('mpSaveBtn').disabled = false;
    } catch (error) {
        msg.textContent = '验证失败：' + (error.message || '未知错误');
        document.getElementById('mpSaveBtn').disabled = true;
    } finally {
        btn.disabled = false;
    }
}

async function saveProvider() {
    const payload = collectProviderForm();
    try {
        const response = await fetch('/api/providers', {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
        });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '保存失败'));
        document.getElementById('mpApiKey').value = '';
        document.getElementById('mpSaveBtn').disabled = true;
        document.getElementById('mpValidateMsg').textContent = '';
        await loadProviders();
        refreshBalance();
    } catch (error) {
        window.alert('保存失败：' + (error.message || '未知错误'));
    }
}

async function activateProvider(id) {
    try {
        const response = await fetch('/api/providers/activate', {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: id })
        });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '切换失败'));
        await loadProviders();
        refreshBalance();
    } catch (error) {
        window.alert('切换模型失败：' + (error.message || '未知错误'));
    }
}

async function deleteProvider(id) {
    if (!window.confirm('确定删除该模型供应商？')) return;
    try {
        const response = await fetch('/api/providers/' + encodeURIComponent(id), { method: 'DELETE' });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '删除失败'));
        await loadProviders();
        refreshBalance();
    } catch (error) {
        window.alert('删除失败：' + (error.message || '未知错误'));
    }
}

async function refreshBalance() {
    const el = document.getElementById('balanceStatus');
    if (!activeProviderId) { el.textContent = '余额：未配置模型'; return; }
    try {
        const response = await fetch('/api/providers/balance', { cache: 'no-store' });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') { el.textContent = '余额：查询失败'; return; }
        if (data.supported && data.balance && data.balance.text) {
            el.textContent = '余额：' + data.balance.text;
        } else {
            el.textContent = '余额：该供应商不支持查询';
        }
    } catch (error) {
        el.textContent = '余额：查询失败';
    }
}

function openModelModal() {
    populateProviderTypeOptions();
    renderProviderList();
    document.getElementById('modelModal').classList.add('open');
}

function closeModelModal() {
    document.getElementById('modelModal').classList.remove('open');
}

async function shutdownAll() {
    if (!window.confirm('确定退出 Chatlog AI 及其本地数据服务？')) return;
    try { await fetch('/api/shutdown', { method: 'POST' }); } catch (error) {}
    document.body.innerHTML = '<div class="empty" style="height:100vh">Chatlog AI 已退出</div>';
}

async function readJson(response) {
    try { return await response.json(); } catch (error) { return {}; }
}

function apiError(data, fallback) { return data.detail || data.message || (data.status_info && data.status_info.msg) || fallback; }

function messageTimestamp(message) {
    const value = message.timestamp !== undefined ? message.timestamp : message.time;
    const numeric = Number(value);
    if (String(value || '').trim() && !Number.isNaN(numeric)) return numeric > 100000000000 ? numeric / 1000 : numeric;
    const parsed = Date.parse(value);
    return Number.isNaN(parsed) ? null : parsed / 1000;
}

function shortTime(value) {
    if (!value) return '';
    const text = String(value);
    const match = text.match(/(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2})/);
    if (!match) return text.slice(0, 16);
    const today = new Date().toISOString().slice(0, 10);
    return match[1] === today ? match[2] : match[1].slice(5);
}

function renderMarkdown(markdown) {
    const lines = escapeHtml(markdown || '').split('\n');
    let html = '';
    let inList = false;
    lines.forEach(function (line) {
        const formatted = line.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
        if (/^###\s+/.test(formatted)) { if (inList) { html += '</ul>'; inList = false; } html += '<h3>' + formatted.replace(/^###\s+/, '') + '</h3>'; }
        else if (/^##\s+/.test(formatted)) { if (inList) { html += '</ul>'; inList = false; } html += '<h2>' + formatted.replace(/^##\s+/, '') + '</h2>'; }
        else if (/^#\s+/.test(formatted)) { if (inList) { html += '</ul>'; inList = false; } html += '<h1>' + formatted.replace(/^#\s+/, '') + '</h1>'; }
        else if (/^[-*]\s+/.test(formatted)) { if (!inList) { html += '<ul>'; inList = true; } html += '<li>' + formatted.replace(/^[-*]\s+/, '') + '</li>'; }
        else { if (inList) { html += '</ul>'; inList = false; } if (formatted.trim()) html += '<p>' + formatted + '</p>'; }
    });
    if (inList) html += '</ul>';
    return html;
}

function escapeHtml(value) {
    return String(value || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
}

function copyToClipboard(text) {
    const value = String(text == null ? '' : text);

    function legacyCopy() {
        const textarea = document.createElement('textarea');
        textarea.value = value;
        textarea.setAttribute('readonly', '');
        textarea.style.position = 'fixed';
        textarea.style.left = '-9999px';
        document.body.appendChild(textarea);
        textarea.select();
        textarea.setSelectionRange(0, textarea.value.length);
        let ok = false;
        try {
            ok = document.execCommand('copy');
        } catch (e) {
            ok = false;
        }
        document.body.removeChild(textarea);
        return ok;
    }

    function notify(ok) {
        if (ok) {
            alert('已复制到剪贴板');
        } else {
            alert('复制失败，请手动复制');
        }
    }

    if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(value).then(
            function () { notify(true); },
            function () { notify(legacyCopy()); }
        );
    } else {
        notify(legacyCopy());
    }
}

function copyHistoryItem(index) {
    const turn = aiHistory[index];
    if (!turn || !turn.content) return;
    copyToClipboard(turn.content);
}

async function loadAutoReplyConfig() {
    try {
        const response = await fetch('/api/auto-reply/config', { cache: 'no-store' });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '读取自动回复配置失败'));
        autoReplyConfig = data.config || {};
    } catch (error) {
        autoReplyConfig = { enabled: false, guard_targets: [], context_count: 80, reply_prompt: '', hotkeys: { open_wechat: 'ctrl+alt+w', search: 'ctrl+f', paste: 'ctrl+v', send: 'enter' } };
    }
    applyAutoReplyConfigToForm();
    updateAutoReplyStatus();
}

function openAutoReplyModal() {
    applyAutoReplyConfigToForm();
    loadGuardChatOptions();
    document.getElementById('autoReplyModal').classList.add('open');
}

function closeAutoReplyModal() {
    document.getElementById('autoReplyModal').classList.remove('open');
}

function applyAutoReplyConfigToForm() {
    const cfg = autoReplyConfig || {};
    document.getElementById('arEnabled').checked = !!cfg.enabled;
    document.getElementById('arContextCount').value = cfg.context_count || 80;
    document.getElementById('arReplyPrompt').value = cfg.reply_prompt || '';
    const hk = cfg.hotkeys || {};
    document.getElementById('hkOpen').value = hk.open_wechat || 'ctrl+alt+w';
    document.getElementById('hkSearch').value = hk.search || 'ctrl+f';
    document.getElementById('hkPaste').value = hk.paste || 'ctrl+v';
    document.getElementById('hkSend').value = hk.send || 'enter';
}

async function loadGuardChatOptions() {
    try {
        const response = await fetch('/api/chats', { cache: 'no-store' });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '读取微信会话失败'));
        guardChatOptions = Array.isArray(data.chats) ? data.chats : [];
    } catch (error) {
        guardChatOptions = [];
    }
    renderGuardTargetList();
}

function renderGuardTargetList() {
    const container = document.getElementById('guardTargetList');
    if (!container) return;
    const cfg = autoReplyConfig || {};
    const selected = cfg.guard_targets || [];
    const selectedIds = {};
    selected.forEach(function (t) { selectedIds[t.id] = t; });
    const friendCount = selected.filter(function (t) { return t.type === 'friend'; }).length;
    const groupCount = selected.filter(function (t) { return t.type === 'group'; }).length;

    const list = guardChatOptions || [];
    if (!list.length) {
        container.innerHTML = '<div class="form-hint">正在加载微信会话……</div>';
        return;
    }

    container.replaceChildren();

    function renderGroup(title, items, type, currentCount, maxCount) {
        const header = document.createElement('div');
        header.className = 'form-hint';
        header.textContent = title + '（已选 ' + currentCount + '/' + maxCount + '）';
        container.appendChild(header);
        items.forEach(function (chat) {
            const selected = selectedIds[chat.id];
            const isChecked = !!selected;

            const item = document.createElement('div');
            item.className = 'guard-target-item';

            const row = document.createElement('div');
            row.className = 'guard-target-row';

            const label = document.createElement('label');
            const checkbox = document.createElement('input');
            checkbox.type = 'checkbox';
            checkbox.dataset.id = chat.id;
            checkbox.dataset.name = chat.name || chat.id;
            checkbox.dataset.type = type;
            checkbox.checked = isChecked;
            checkbox.disabled = !isChecked && currentCount >= maxCount;
            label.appendChild(checkbox);
            const nameSpan = document.createElement('span');
            nameSpan.textContent = chat.name || chat.id;
            label.appendChild(nameSpan);

            const badge = document.createElement('span');
            badge.className = 'guard-badge ' + type;
            badge.textContent = type === 'friend' ? '好友' : '群聊';

            row.appendChild(label);
            row.appendChild(badge);
            item.appendChild(row);

            const promptInput = document.createElement('input');
            promptInput.type = 'text';
            promptInput.className = 'guard-target-prompt';
            promptInput.dataset.id = chat.id;
            promptInput.placeholder = '单独提示词（可选，留空用默认）';
            promptInput.value = (selected && selected.prompt) ? selected.prompt : '';
            promptInput.style.display = isChecked ? 'block' : 'none';
            item.appendChild(promptInput);

            checkbox.addEventListener('change', function () {
                promptInput.style.display = checkbox.checked ? 'block' : 'none';
            });

            container.appendChild(item);
        });
    }

    renderGroup('好友', list.filter(function (c) { return c.type !== 'group'; }), 'friend', friendCount, 8);
    renderGroup('群聊', list.filter(function (c) { return c.type === 'group'; }), 'group', groupCount, 3);
}

function collectGuardTargets() {
    const prompts = {};
    document.querySelectorAll('#guardTargetList input.guard-target-prompt').forEach(function (input) {
        prompts[input.dataset.id] = input.value.trim();
    });
    const targets = [];
    document.querySelectorAll('#guardTargetList input[type=checkbox]').forEach(function (cb) {
        if (cb.checked) {
            targets.push({
                id: cb.dataset.id,
                name: cb.dataset.name,
                type: cb.dataset.type,
                prompt: prompts[cb.dataset.id] || ''
            });
        }
    });
    return targets;
}

async function saveAutoReplyConfig() {
    const payload = {
        enabled: document.getElementById('arEnabled').checked,
        guard_targets: collectGuardTargets(),
        context_count: Number(document.getElementById('arContextCount').value || 80),
        reply_prompt: document.getElementById('arReplyPrompt').value.trim(),
        hotkeys: {
            open_wechat: document.getElementById('hkOpen').value.trim(),
            search: document.getElementById('hkSearch').value.trim(),
            paste: document.getElementById('hkPaste').value.trim(),
            send: document.getElementById('hkSend').value.trim()
        }
    };
    try {
        const response = await fetch('/api/auto-reply/config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '保存失败'));
        autoReplyConfig = data.config || payload;
        applyAutoReplyConfigToForm();
        updateAutoReplyStatus();
        closeAutoReplyModal();
    } catch (error) {
        window.alert('保存自动回复配置失败：' + (error.message || '未知错误'));
    }
}

function updateAutoReplyStatus() {
    const el = document.getElementById('autoReplyStatus');
    const cfg = autoReplyConfig || {};
    if (!cfg.enabled) { el.textContent = '自动回复：未启用'; return; }
    const count = (cfg.guard_targets || []).length;
    el.textContent = '自动回复：已启用（守护 ' + count + ' 个会话，最近 ' + (cfg.context_count || 80) + ' 条上下文）';
}

async function refreshAutoReplyStatus() {
    try {
        const response = await fetch('/api/auto-reply/status', { cache: 'no-store' });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') return;
        const el = document.getElementById('autoReplyStatus');
        if (!data.enabled) { el.textContent = '自动回复：未启用'; return; }
        const running = data.running ? '运行中' : '未运行';
        const latest = (data.log && data.log.length) ? data.log[data.log.length - 1].message : '';
        el.textContent = '自动回复：' + running + (latest ? ' · ' + latest : '');
    } catch (error) {}
}

async function testHotkey(name) {
    try {
        const response = await fetch('/api/auto-reply/test', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ action: name }) });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '测试失败'));
        window.alert(data.message || '已触发');
    } catch (error) {
        window.alert('测试失败：' + (error.message || '未知错误'));
    }
}

async function testFullFlow() {
    if (!currentChat) { window.alert('请先选择一个会话'); return; }
    if (!window.confirm('将真实发送一条测试消息到「' + currentChat.name + '」，确认继续？')) return;
    try {
        const response = await fetch('/api/auto-reply/test', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ action: 'full', target_name: currentChat.name, text: '这是一条 Chatlog AI 自动回复测试消息' })
        });
        const data = await readJson(response);
        if (!response.ok || data.status !== 'success') throw new Error(apiError(data, '测试失败'));
        window.alert(data.message || '已执行');
    } catch (error) {
        window.alert('完整流程测试失败：' + (error.message || '未知错误'));
    }
}
