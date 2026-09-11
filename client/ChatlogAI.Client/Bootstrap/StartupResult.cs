namespace ChatlogAI.Client.Bootstrap;

/// <summary>
/// 启动编排的终态。
///
/// 为什么把"被用户取消"与"失败"分开：两者对用户的意义完全不同——
/// 取消是用户的主动意图（不该再弹错误框），失败是需要用户知晓并排查的。
/// 用一个 bool 表达会导致"用户点了退出，结果弹一个红色错误框"。
/// </summary>
/// <param name="Success">阶段 0-4 是否全部完成，可以进入主界面。</param>
/// <param name="ErrorMessage">失败时的可读原因（含日志目录提示），成功或取消时为 null。</param>
/// <param name="IsCancelled">是否由用户主动取消（关闭启动窗口 / 退出）。</param>
public sealed record StartupResult(bool Success, string? ErrorMessage, bool IsCancelled)
{
    public static StartupResult Ok() => new(true, null, false);

    public static StartupResult Fail(string message) => new(false, message, false);

    public static StartupResult Cancelled() => new(false, null, true);
}
