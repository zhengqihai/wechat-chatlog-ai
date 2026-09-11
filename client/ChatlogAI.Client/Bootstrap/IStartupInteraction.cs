namespace ChatlogAI.Client.Bootstrap;

/// <summary>
/// 启动编排与界面之间的唯一接口。
///
/// 之所以要抽出这层：编排逻辑（阶段 0-5）是纯流程，不应该知道 WPF 的存在。
/// 有了这个接口，<see cref="StartupOrchestrator"/> 不需要引用任何 View 类型，
/// 界面只负责"把状态显示出来 / 把用户的选择问回来"。
/// </summary>
public interface IStartupInteraction
{
    /// <summary>刷新当前进度文案。</summary>
    void Report(string message);

    /// <summary>
    /// 告知界面微信授权页是否已可用（可用时界面才显示"打开授权页面"入口）。
    /// </summary>
    void SetAuthorizationPageAvailable(bool available);

    /// <summary>
    /// 需要用户手动确认时使用（等价 Python 版的 input("按回车继续")）。
    /// 返回 false 表示用户取消。
    /// </summary>
    Task<bool> ConfirmAsync(string message, string confirmButtonText, CancellationToken cancellationToken);

    /// <summary>
    /// 多账号时让用户选择。返回 null 或空串表示"保持当前，不做修改"
    /// （等价 Python 版直接回车）。
    /// </summary>
    Task<string?> ChooseAccountAsync(
        IReadOnlyList<WechatAccount> accounts,
        string currentAccount,
        CancellationToken cancellationToken);
}
