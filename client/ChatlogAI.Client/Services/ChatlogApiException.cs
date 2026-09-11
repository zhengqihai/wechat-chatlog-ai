using System.Net;

namespace ChatlogAI.Client.Services;

/// <summary>
/// 本地后端（127.0.0.1:18765）调用失败的统一异常。
///
/// 为什么要单独定义一个异常类型：
/// 1. 后端所有错误都遵循 <c>{"detail": "..."}</c> 形态（见 docs/API_REFERENCE.md §0.1），
///    解析后已经是**面向用户的中文可读文案**，界面层可以原样弹出，不需要再加工；
/// 2. 界面层需要区分「后端返回了业务错误」与「本地服务根本连不上」（后者是进程已死），
///    前者重试无意义，后者应当走"服务已退出"的路径。
/// </summary>
public sealed class ChatlogApiException : Exception
{
    public ChatlogApiException(HttpStatusCode? statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// HTTP 状态码。为 null 表示"没有拿到响应"（连接失败 / 超时 / 响应体不可解析），
    /// 而非服务端返回了某个错误码。
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>是否属于"本地服务不可达"——通常意味着后端进程已经不在了。</summary>
    public bool IsUnreachable => StatusCode is null;
}
