using System.Collections.ObjectModel;
using System.Globalization;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 聊天模块第四列「会话洞察」：群成员 / 入退群事件 / 今日发言排行；好友会话只显示今日消息数。
///
/// 状态所有权（很重要）：
///   - 服务端负责**统计**与**增量游标**，客户端不自己数消息；
///   - 客户端只在"切换会话"时 POST /api/stats/watch 建立观看会话，之后每 60 秒 GET 一次；
///   - 成员与事件是快照差分的产物，只在切换会话、手动刷新、手动扫描时拉取。
///
/// 并发保护：切换会话时 `_generation` 自增，任何晚到的响应都不再写界面，
/// 否则快速点几个会话会出现"标题是 B、列表是 A"的错位。
/// </summary>
public sealed class SessionInsightViewModel : ObservableObject
{
    private readonly ChatlogApiClient _api;
    private readonly List<GroupMemberItemViewModel> _allMembers = [];
    private int _generation;
    private string _targetId = "";
    private bool _isGroup;

    public SessionInsightViewModel(ChatlogApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(manual: true));
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => HasChat && IsGroup);

        // 用 Visibility 表达空态的占位文案；集合本身清空时显示。
        UpdatedText = "";
        StatusText = "选择会话后显示群成员与今日发言统计";
    }

    // ==================== 命令 ====================

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand ScanCommand { get; }

    // ==================== 会话上下文 ====================

    private bool _hasChat;
    public bool HasChat
    {
        get => _hasChat;
        private set
        {
            if (SetProperty(ref _hasChat, value))
            {
                OnPropertyChanged(nameof(IsFriend));
                OnPropertyChanged(nameof(HasNoChat));
                ScanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasNoChat => !_hasChat;

    public bool IsGroup
    {
        get => _isGroup;
        private set
        {
            if (SetProperty(ref _isGroup, value))
            {
                OnPropertyChanged(nameof(IsFriend));
                ScanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsFriend => _hasChat && !_isGroup;

    // ==================== 群成员 ====================

    public ObservableCollection<GroupMemberItemViewModel> Members { get; } = [];

    public ObservableCollection<GroupChangeItemViewModel> Changes { get; } = [];

    public ObservableCollection<StatsRankItemViewModel> Ranking { get; } = [];

    private string _memberSearch = "";
    public string MemberSearch
    {
        get => _memberSearch;
        set
        {
            if (SetProperty(ref _memberSearch, value))
            {
                ApplyMemberFilter();
            }
        }
    }

    private string _membersHeader = "群成员";
    public string MembersHeader
    {
        get => _membersHeader;
        private set => SetProperty(ref _membersHeader, value);
    }

    private string _changesHeader = "最近变动";
    public string ChangesHeader
    {
        get => _changesHeader;
        private set => SetProperty(ref _changesHeader, value);
    }

    private bool _hasChanges;
    public bool HasChanges
    {
        get => _hasChanges;
        private set => SetProperty(ref _hasChanges, value);
    }

    // ==================== 发言统计 ====================

    private string _statsHeader = "今日发言";
    public string StatsHeader
    {
        get => _statsHeader;
        private set => SetProperty(ref _statsHeader, value);
    }

    private string _friendSummary = "";
    public string FriendSummary
    {
        get => _friendSummary;
        private set => SetProperty(ref _friendSummary, value);
    }

    private string _totalText = "";
    public string TotalText
    {
        get => _totalText;
        private set => SetProperty(ref _totalText, value);
    }

    private string _updatedText = "";
    public string UpdatedText
    {
        get => _updatedText;
        private set => SetProperty(ref _updatedText, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private bool _statsBusy;
    public bool StatsBusy
    {
        get => _statsBusy;
        private set => SetProperty(ref _statsBusy, value);
    }

    // ==================== 对外入口 ====================

    /// <summary>切换会话。previous 的观看会话会被清掉，避免服务端保留无用的增量缓存。</summary>
    public async Task SwitchToAsync(ChatItemViewModel? chat)
    {
        var token = ++_generation;
        if (chat is null)
        {
            Reset();
            return;
        }

        var previous = _targetId;
        _targetId = chat.Id;
        HasChat = true;
        IsGroup = chat.IsGroup;
        ResetData();
        StatusText = IsGroup ? "正在读取群成员与今日发言…" : "正在统计今日消息…";

        if (!string.IsNullOrEmpty(previous) && previous != _targetId)
        {
            _ = StopWatchSilentlyAsync(previous);
        }

        if (IsGroup)
        {
            await Task.WhenAll(
                LoadMembersAsync(token),
                LoadChangesAsync(token),
                LoadStatsAsync(token, initial: true));
        }
        else
        {
            await LoadStatsAsync(token, initial: true);
        }
    }

    /// <summary>每分钟轮询（也用于手动刷新按钮）。只补游标之后的新消息。</summary>
    public async Task RefreshAsync(bool manual = false)
    {
        if (!HasChat || string.IsNullOrEmpty(_targetId))
        {
            return;
        }

        var token = _generation;
        if (manual && IsGroup)
        {
            await Task.WhenAll(LoadMembersAsync(token), LoadChangesAsync(token));
        }

        await LoadStatsAsync(token, initial: false);
    }

    /// <summary>手动触发全量成员扫描，然后刷新当前群名单与事件。</summary>
    public async Task ScanAsync()
    {
        if (!HasChat || !IsGroup)
        {
            return;
        }

        var token = _generation;
        StatusText = "正在扫描全部群成员…";
        try
        {
            var result = await _api.ScanGroupsAsync();
            if (token != _generation)
            {
                return;
            }

            StatusText = result.Groups == 0
                ? "扫描完成，但没有读到群（请确认微信本地数据可读）"
                : result.Baselines == result.Groups
                    ? $"已建立基线：{result.Groups} 个群、当前成员已记录。之后的扫描才能识别入群/退群"
                    : $"扫描完成：{result.Groups} 个群，新增 {result.Joined}，退群 {result.Left}";
            await Task.WhenAll(LoadMembersAsync(token), LoadChangesAsync(token));
        }
        catch (ChatlogApiException ex)
        {
            if (token == _generation)
            {
                StatusText = ex.Message;
            }
        }
        catch (Exception ex)
        {
            if (token == _generation)
            {
                StatusText = "扫描失败：" + ex.Message;
            }
        }
    }

    // ==================== 内部加载 ====================

    private async Task LoadMembersAsync(int token)
    {
        try
        {
            var data = await _api.GetGroupMembersAsync(_targetId);
            if (token != _generation)
            {
                return;
            }

            _allMembers.Clear();
            foreach (var member in data.Members)
            {
                _allMembers.Add(new GroupMemberItemViewModel(member.DisplayName, member.FirstSeen));
            }

            MembersHeader = $"群成员 · {data.Count}";
            ApplyMemberFilter();
        }
        catch (ChatlogApiException ex)
        {
            if (token == _generation)
            {
                _allMembers.Clear();
                ApplyMemberFilter();
                StatusText = ex.Message;
            }
        }
        catch (Exception ex)
        {
            if (token == _generation)
            {
                StatusText = "读取群成员失败：" + ex.Message;
            }
        }
    }

    private async Task LoadChangesAsync(int token)
    {
        try
        {
            var data = await _api.GetGroupChangesAsync(_targetId);
            if (token != _generation)
            {
                return;
            }

            Changes.Clear();
            foreach (var change in data.Changes)
            {
                Changes.Add(new GroupChangeItemViewModel(change.Name, change.IsJoin, change.DetectedAt));
            }

            ChangesHeader = $"最近变动 · {Changes.Count}";
            HasChanges = Changes.Count > 0;
        }
        catch (ChatlogApiException ex)
        {
            if (token == _generation)
            {
                Changes.Clear();
                HasChanges = false;
                StatusText = ex.Message;
            }
        }
        catch (Exception ex)
        {
            if (token == _generation)
            {
                StatusText = "读取变动失败：" + ex.Message;
            }
        }
    }

    private async Task LoadStatsAsync(int token, bool initial)
    {
        StatsBusy = true;
        var target = _targetId;
        try
        {
            var data = initial
                ? await _api.StartStatsWatchAsync(target)
                : await _api.GetStatsWatchAsync(target);
            if (token != _generation || target != _targetId)
            {
                return;
            }

            ApplyStats(data);
        }
        catch (ChatlogApiException ex)
        {
            if (token == _generation)
            {
                StatusText = ex.Message;
            }
        }
        catch (Exception ex)
        {
            if (token == _generation)
            {
                StatusText = "统计失败：" + ex.Message;
            }
        }
        finally
        {
            StatsBusy = false;
        }
    }

    private void ApplyStats(StatsWatchResponse data)
    {
        Ranking.Clear();
        if (string.Equals(data.Type, "group", StringComparison.OrdinalIgnoreCase))
        {
            var rank = 0;
            foreach (var member in data.Members)
            {
                Ranking.Add(new StatsRankItemViewModel(++rank, member.DisplayName, member.Count, member.Share));
            }

            StatsHeader = $"今日发言 · 共 {data.Total} 条";
            TotalText = $"{data.MemberCount} 人发言";
            FriendSummary = "";
        }
        else
        {
            StatsHeader = "今日消息";
            TotalText = $"合计 {data.Total} 条";
            FriendSummary = $"我 {data.MeCount} · 对方 {data.OtherCount}";
        }

        UpdatedText = data.UpdatedAt > 0
            ? "更新于 " + DateTimeOffset.FromUnixTimeSeconds((long)data.UpdatedAt).LocalDateTime
                .ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "";
        StatusText = "";
    }

    private void ApplyMemberFilter()
    {
        var query = MemberSearch.Trim();
        Members.Clear();
        foreach (var item in _allMembers)
        {
            if (query.Length == 0 || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                Members.Add(item);
            }
        }
    }

    private void Reset()
    {
        _targetId = "";
        HasChat = false;
        IsGroup = false;
        ResetData();
        StatusText = "选择会话后显示群成员与今日发言统计";
    }

    private void ResetData()
    {
        _allMembers.Clear();
        Members.Clear();
        Changes.Clear();
        Ranking.Clear();
        MemberSearch = "";
        MembersHeader = "群成员";
        ChangesHeader = "最近变动";
        HasChanges = false;
        StatsHeader = "今日发言";
        FriendSummary = "";
        TotalText = "";
        UpdatedText = "";
    }

    private async Task StopWatchSilentlyAsync(string targetId)
    {
        try
        {
            await _api.StopStatsWatchAsync(targetId);
        }
        catch
        {
            // 清理失败不影响界面：最坏情况下服务端多留一份内存缓存。
        }
    }
}
