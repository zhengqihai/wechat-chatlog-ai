using System.IO;
using System.Text;
using System.Text.Json;

namespace ChatlogAI.Client.Bootstrap;

/// <summary>
/// 微信数据源授权状态探测，等价 launcher.py 的 has_wechat_key() / wait_wechat_authorization()。
///
/// 为什么用"文件轮询"而不是别的方式：
///   密钥文件由上游 wechat-backend.exe 生成，客户端无法从中途拿到事件回调，
///   而授权页（http://127.0.0.1:10392）是上游的网页，我们既不能改也不能原生化。
///   因此"文件里出现了非空 db_key"就是唯一可靠的完成信号——这也正是 Python 版的做法。
///
/// 阶段 D3 起**同时认两个文件**（上游被移除后仍要能判定"密钥已就绪"）：
///   ① 上游产出 `data\wechat\output\account_keys.json`：`{ "wxid_xxx": { "db_key": … }, … }`
///   ② 我们自己的 `config\wechat-keys.json`：`{ "accounts": [ { "account": …, "db_key": … } ] }`
/// 两者结构不同，但这里只回答"**有没有**可用密钥"，不解析具体账号，所以统一按
/// "结构里出现任意非空 db_key" 判定，避免为两种结构各写一套账号解析（那是后端 keys.py 的职责）。
/// </summary>
public sealed class WechatAuthWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);

    private readonly string[] _keyFiles;

    public WechatAuthWatcher(params string[] keyFilePaths) => _keyFiles = keyFilePaths;

    /// <summary>
    /// 等价 has_wechat_key()：任一文件里出现非空 db_key 即视为已授权。
    /// 解析失败一律按"未授权"处理，等待路径上不应因为一个损坏的 JSON 而崩溃。
    /// </summary>
    public bool HasWechatKey() => _keyFiles.Any(HasKeyIn);

    private static bool HasKeyIn(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && ContainsDbKey(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>递归找任意非空 `db_key`（两种来源的结构不同，见类型注释）。</summary>
    private static bool ContainsDbKey(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("db_key"))
                    {
                        if (property.Value.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                        {
                            return true;
                        }

                        continue;
                    }

                    if (ContainsDbKey(property.Value))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                return element.EnumerateArray().Any(ContainsDbKey);

            default:
                return false;
        }
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
