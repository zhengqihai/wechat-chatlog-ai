using System.Windows.Documents;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// AI 对话线程的一条（对应 app.js 的 <c>renderAiThread()</c>）。
///
/// 值得注意的是 <see cref="Content"/> 是 <see cref="FlowDocument"/> 而不是字符串：
/// Markdown 解析发生在"构造这一条"的时刻，而不是绑定/渲染时刻。
/// 这样列表滚动时不会反复解析同一段 Markdown（原前端每次重渲染都会重跑一遍正则）。
/// </summary>
public sealed class AiThreadItemViewModel
{
    private AiThreadItemViewModel(string role, string content, string assistantName, bool isError)
    {
        IsUser = role == "user";
        RawContent = content;
        RoleName = IsUser ? "你" : assistantName;
        IsError = isError;

        // 用户输入按纯文本渲染（与原前端一致，不解析 Markdown），AI 回答才走 Markdown。
        Content = IsUser ? null : MarkdownRenderer.Render(content);
    }

    /// <summary>提问方（你）。</summary>
    public bool IsUser { get; }

    /// <summary>展示名：你 / 当前生效模型的名字。</summary>
    public string RoleName { get; }

    /// <summary>原始 Markdown 文本，用于"复制"。</summary>
    public string RawContent { get; }

    /// <summary>AI 回答的富文本；用户提问为 null，XAML 侧改用纯文本显示。</summary>
    public FlowDocument? Content { get; }

    /// <summary>请求失败的那一条，用警示色显示（原前端的 <c>turn.error</c>）。</summary>
    public bool IsError { get; }

    /// <summary>只有 AI 回答提供"复制"。</summary>
    public bool CanCopy => !IsUser;

    public static AiThreadItemViewModel FromHistory(HistoryItem item, string assistantName)
        => new(item.Role, item.Content, assistantName, isError: false);

    public static AiThreadItemViewModel UserMessage(string content, string assistantName)
        => new("user", content, assistantName, isError: false);

    public static AiThreadItemViewModel AssistantMessage(string content, string assistantName, bool isError = false)
        => new("assistant", content, assistantName, isError);

    /// <summary>转换成回写 <c>/api/history</c> 的形态。</summary>
    public HistoryItem ToHistoryItem() => new()
    {
        Role = IsUser ? "user" : "assistant",
        Content = RawContent,
    };
}
