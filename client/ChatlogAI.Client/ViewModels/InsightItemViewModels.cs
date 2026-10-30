using System.Globalization;

namespace ChatlogAI.Client.ViewModels;

/// <summary>群成员列表的一行。</summary>
public sealed class GroupMemberItemViewModel
{
    public GroupMemberItemViewModel(string displayName, double? firstSeen)
    {
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "未知成员" : displayName;
        AvatarText = DisplayName[..1];
        FirstSeenText = firstSeen.HasValue && firstSeen.Value > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)firstSeen.Value).LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "尚未记录";
    }

    public string DisplayName { get; }

    public string AvatarText { get; }

    /// <summary>首次观测到的时间。首次扫描只建基线，因此这里显示的是"开始观察的时间"。</summary>
    public string FirstSeenText { get; }
}

/// <summary>入群 / 退群事件的一行。</summary>
public sealed class GroupChangeItemViewModel
{
    public GroupChangeItemViewModel(string name, bool isJoin, double detectedAt)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "未知成员" : name;
        IsJoin = isJoin;
        TypeText = isJoin ? "入群" : "退群";
        TimeText = DateTimeOffset.FromUnixTimeSeconds((long)detectedAt).LocalDateTime
            .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public string Name { get; }

    public bool IsJoin { get; }

    public string TypeText { get; }

    public string TimeText { get; }
}

/// <summary>发言排行的一行。</summary>
public sealed class StatsRankItemViewModel
{
    public StatsRankItemViewModel(int rank, string displayName, int count, double share)
    {
        Rank = rank.ToString(CultureInfo.InvariantCulture);
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "未知成员" : displayName;
        CountText = count.ToString(CultureInfo.InvariantCulture);
        Share = share;
        ShareText = share.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    public string Rank { get; }

    public string DisplayName { get; }

    public string CountText { get; }

    public double Share { get; }

    public string ShareText { get; }
}
