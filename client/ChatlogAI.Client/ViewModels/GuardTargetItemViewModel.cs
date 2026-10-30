using ChatlogAI.Client.Models;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 守护会话候选列表的一行，对照 <c>app.js renderGuardTargetList()</c> 渲染出的
/// <c>.guard-target-item</c>（checkbox + 名称 + 徽标 + 可选的"单独提示词"输入框）。
///
/// 这一层是必需的：原前端每一行都是 DOM，勾选态、禁用态、提示词框显隐全靠命令式地改
/// <c>checked</c> / <c>disabled</c> / <c>style.display</c>；WPF 里这些必须是可绑定状态，
/// 否则没有对象能承载它们，也就无法声明式表达。
///
/// 两条容易写漏的交互规则（由 <see cref="AutoReplyViewModel"/> 负责落实）：
/// 1. <c>checkbox.disabled = !isChecked &amp;&amp; currentCount &gt;= maxCount</c>：
///    超限是"提前禁用未选中项"，而不是让用户在保存时才吃一个 400；
///    已选中项恒为可点，否则用户没法取消勾选来腾出名额。
/// 2. <c>promptInput.style.display = isChecked ? 'block' : 'none'</c>：
///    单独提示词框只在勾选后才出现。
/// </summary>
public sealed class GuardTargetItemViewModel : ObservableObject
{
    private readonly Action? _selectionChanged;

    private bool _isSelected;
    private bool _isEnabled = true;
    private string _prompt = "";
    private List<string> _docIds = [];

    public GuardTargetItemViewModel(ChatSummary chat, Action? selectionChanged = null)
    {
        Id = chat.Id;
        Name = string.IsNullOrEmpty(chat.Name) ? chat.Id : chat.Name;

        // 上游 type 固定是 group / friend，这里统一归一化：
        // 分组归属与"8 好友 + 3 群聊"的上限计数都靠它，不允许出现第三种取值。
        Type = chat.IsGroup ? "group" : "friend";
        _selectionChanged = selectionChanged;
    }

    /// <summary>会话 id（微信 username）。提交时原样回传。</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>提交给后端的类型：<c>friend</c> 或 <c>group</c>。</summary>
    public string Type { get; }

    public bool IsGroup => Type == "group";

    /// <summary>徽标文案（对照 <c>badge.textContent</c>）。</summary>
    public string TypeText => IsGroup ? "群聊" : "好友";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsPromptVisible));

            // 勾选态一变，两组的"是否还能再勾"就都变了，必须整体重算而不是只改自己。
            _selectionChanged?.Invoke();
        }
    }

    /// <summary>单独提示词框是否可见（只在勾选后出现）。</summary>
    public bool IsPromptVisible => IsSelected;

    /// <summary>
    /// 该会话的单独提示词。留空不是"没有"，而是"用全局默认身份回退"
    /// （见 <c>AutoReplyModels.cs</c> 中 <c>ReplyPrompt</c> 的语义说明）。
    /// </summary>
    public string Prompt
    {
        get => _prompt;
        set => SetProperty(ref _prompt, value);
    }

    /// <summary>当前是否可勾选。由所属分组的已选数量动态重算，见 <see cref="AutoReplyViewModel"/>。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>关联文档按钮的文案：未关联时「关联文档」，已关联时带数量。</summary>
    public string DocCountText => _docIds.Count == 0 ? "关联文档" : $"关联文档（{_docIds.Count}）";

    /// <summary>当前关联的知识文档 id（只读，改动走 <see cref="SetDocIds"/>）。</summary>
    public IReadOnlyList<string> DocIds => _docIds;

    /// <summary>文档选择器返回后写入勾选的文档 id，并刷新按钮文案。</summary>
    public void SetDocIds(IReadOnlyList<string> docIds)
    {
        _docIds = docIds.ToList();
        OnPropertyChanged(nameof(DocCountText));
    }

    /// <summary>构造提交给后端的守护对象；提示词按原前端做 trim。</summary>
    public GuardTarget ToModel() => new()
    {
        Id = Id,
        Name = Name,
        Type = Type,
        Prompt = Prompt.Trim(),
        DocIds = _docIds,
    };
}
