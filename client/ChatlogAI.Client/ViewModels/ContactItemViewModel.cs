using System.Globalization;
using System.Windows.Media;
using ChatlogAI.Client.Models;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 通讯录列表的一行（T22）。
///
/// 为什么这一行同时带"勾选状态"和"头像图"两样看起来不相干的东西：
/// 1. 勾选是**导出选集**的唯一载体，而选集必须跨筛选保留，所以勾选状态挂在行上、
///    由 <see cref="ContactsModuleViewModel"/> 按 username 统一记账 —— 行被列表刷新丢弃时，
///    选集里的那条记录仍然活着（这是"选一批好友 → 切到群聊再选几个 → 一起导出"能成立的前提）。
/// 2. 头像只给"当前详情的那一个联系人"取（见 §14.7.3：495 条逐条请求头像是无谓压力），
///    取到的图缓存在 VM 的字典里、再回填到行上。列表里的其他行不绑定 <see cref="AvatarImage"/>，
///    因此不绑定就不会触发任何请求。
///
/// 本类的所有展示字段都在构造时算好、之后不再变（DTO 是不可变 record，刷新列表时整行重建），
/// 所以只有 <see cref="IsSelected"/> 与 <see cref="AvatarImage"/> 两个可变属性需要通知。
/// </summary>
public sealed class ContactItemViewModel : ObservableObject
{
    private bool _isSelected;
    private ImageSource? _avatarImage;

    public ContactItemViewModel(ContactSummary contact, bool isSelected)
    {
        Contact = contact;
        _isSelected = isSelected;

        DisplayName = DisplayNameOf(contact);
        AvatarText = BuildAvatarText(DisplayName);
        SecondaryText = BuildSecondaryText(contact);
        TypeBadge = BuildTypeBadge(contact);
    }

    /// <summary>原始 DTO。导出与详情都从这里取字段，行 VM 不复制数据。</summary>
    public ContactSummary Contact { get; }

    public string Username => Contact.Username;

    /// <summary>备注 → 昵称 → 微信号，逐级兜底（与微信客户端"备注优先"的显示口径一致）。</summary>
    public string DisplayName { get; }

    /// <summary>列表里的第二行：微信号优先，否则昵称；两者都缺或与显示名重复时留空。</summary>
    public string SecondaryText { get; }

    /// <summary>圆形头像里的字（有头像图时被盖住）。</summary>
    public string AvatarText { get; }

    /// <summary>
    /// 类型徽标。**只按 <c>type</c> 显示 friend / group**：DTO 里没有 isBlocked / isFormerFriend
    /// 这类判别字段（§14.7.1 已逐字核对），"已删除好友 / 黑名单"只能靠用户当前勾选的分类去理解。
    /// 于是"同时勾了好友与已删除好友"时，哪些是已删除的**无从判别** —— 这种情况刻意不猜、不编造徽标。
    /// </summary>
    public string TypeBadge { get; }

    public bool HasTypeBadge => TypeBadge.Length > 0;

    public string RemarkText => OrDash(Contact.Remark);

    public string NicknameText => OrDash(Contact.Nickname);

    public string AliasText => OrDash(Contact.Alias);

    /// <summary>地区：上游偶尔只给 country / province / city 三段，拼起来兜底。</summary>
    public string RegionText => OrDash(RegionOf(Contact));

    public string SourceText => OrDash(Contact.Source);

    public string AddTimeText => OrDash(Contact.AddTimeText);

    /// <summary>勾选 = 进入导出选集。真正的记账在 <see cref="ContactsModuleViewModel"/> 里。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>仅详情区使用；取到时才赋值，未取到时留空即显示首字母。</summary>
    public ImageSource? AvatarImage
    {
        get => _avatarImage;
        set => SetProperty(ref _avatarImage, value);
    }

    private static string DisplayNameOf(ContactSummary contact)
        => FirstNonEmpty(contact.Remark, contact.Nickname, contact.Username, "未知联系人");

    private static string BuildSecondaryText(ContactSummary contact)
    {
        if (!string.IsNullOrWhiteSpace(contact.Alias))
        {
            return "微信号：" + contact.Alias.Trim();
        }

        // 显示名已经是昵称时不再重复一遍，否则两行字一模一样。
        var nickname = contact.Nickname.Trim();
        return nickname.Length > 0 && !string.Equals(nickname, DisplayNameOf(contact), StringComparison.Ordinal)
            ? "昵称：" + nickname
            : string.Empty;
    }

    private static string BuildTypeBadge(ContactSummary contact)
    {
        if (contact.IsGroup)
        {
            return "群聊";
        }

        return string.Equals(contact.Type, "friend", StringComparison.OrdinalIgnoreCase) ? "好友" : string.Empty;
    }

    private static string BuildAvatarText(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "?";
        }

        // 用 StringInfo 取"第一个文本元素"而不是 Substring(0,1)：昵称里出现 emoji 时，
        // 按 UTF-16 切半会切出一个孤立代理项，界面上就是一个方框。
        var first = StringInfo.GetNextTextElement(displayName.Trim());
        return first.Length == 0 ? "?" : first.ToUpperInvariant();
    }

    private static string RegionOf(ContactSummary contact)
    {
        if (!string.IsNullOrWhiteSpace(contact.Region))
        {
            return contact.Region.Trim();
        }

        return string.Join(
            " ",
            new[] { contact.Country, contact.Province, contact.City }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part.Trim()));
    }

    private static string FirstNonEmpty(params string[] candidates)
        => candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string OrDash(string value)
        => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
}
