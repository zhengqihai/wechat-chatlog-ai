using System.IO;
using System.Text;
using System.Text.Json;

namespace ChatlogAI.Client.Bootstrap;

/// <summary>
/// 微信数据源授权状态探测，等价 launcher.py 的 has_wechat_key() / wait_wechat_authorization()。
///
/// 为什么用"文件轮询"而不是别的方式：
///   密钥文件 account_keys.json 由上游 wechat-backend.exe 生成，客户端无法从中途拿到事件回调，
///   而授权页（http://127.0.0.1:10392）是上游的网页，我们既不能改也不能原生化。
///   因此"文件里出现了非空 db_key"就是唯一可靠的完成信号——这也正是 Python 版的做法。
/// </summary>
public sealed class WechatAuthWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);

    private readonly string _keyFile;

    public WechatAuthWatcher(string keyFilePath) => _keyFile = keyFilePath;

    /// <summary>
    /// 等价 has_wechat_key()：文件中任意一个账号的 db_key 非空即视为已授权。
    /// 解析失败一律按"未授权"处理，等待路径上不应因为一个损坏的 JSON 而崩溃。
    /// </summary>
    public bool HasWechatKey()
    {
        if (!File.Exists(_keyFile))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_keyFile, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var account in document.RootElement.EnumerateObject())
            {
                if (account.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (account.Value.TryGetProperty("db_key", out var key)
                    && key.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(key.GetString()))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// 原地等待授权完成；返回 false 表示被取消（用户要求退出）。
    /// 等价 wait_wechat_authorization()，只是把 Python 的 stop_flag 换成 CancellationToken。
    /// </summary>
    public async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (HasWechatKey())
            {
                return true;
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }
}
