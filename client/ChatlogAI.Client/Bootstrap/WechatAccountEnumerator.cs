using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ChatlogAI.Client.Bootstrap;

/// <summary>
/// 微信账号枚举，等价 launcher.py 的 list_wechat_accounts()（L327-L370）。
///
/// 为什么要有"接口优先、扫盘兜底"两段逻辑：
///   正常路径是问微信后端要账号（它有登录态，知道真实昵称）；
///   但后端刚起来时可能还没把人脸数据准备好，这时候如果直接放弃，用户就会看到
///   "只有一个账号"甚至"没有账号"，从而白跑一次授权。所以退一步扫描本地已落盘的
///   数据库目录——那是上一次成功授权的产物，至少能保证"账号标识"是对的。
///
/// 去重规则为什么长这样：同一台机器上重装/换设备后，导出目录会同时存在
/// wxid_x 与 wxid_x_e42e 两个目录（后者带后缀）。它们其实是一个人，
/// 让用户在两个几乎一样的选项里选是没有意义的，所以"是别人前缀 + 下划线"的项被丢弃。
/// </summary>
public sealed class WechatAccountEnumerator
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly AppPaths _paths;
    private readonly HttpClient _http;

    public WechatAccountEnumerator(AppPaths paths, HttpClient http)
    {
        _paths = paths;
        _http = http;
    }

    /// <summary>
    /// 返回去重后的账号列表。任何异常都降级为"空列表"，绝不中断启动流程
    /// （等价 Python 的 except Exception: accounts = []）。
    /// </summary>
    public async Task<IReadOnlyList<WechatAccount>> ListAsync(CancellationToken cancellationToken)
    {
        var accounts = await FetchFromBackendAsync(cancellationToken).ConfigureAwait(false);

        if (accounts.Count == 0)
        {
            accounts = ScanLocalDatabases();
        }

        return Deduplicate(accounts);
    }

    private async Task<List<WechatAccount>> FetchFromBackendAsync(CancellationToken cancellationToken)
    {
        var result = new List<WechatAccount>();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_paths.WechatUrl}/api/chat/accounts");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("accounts", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = ReadString(item, "name");
                var account = ReadString(item, "account");
                if (string.IsNullOrWhiteSpace(account))
                {
                    account = name;
                }

                account = account.Trim();
                if (account.Length == 0)
                {
                    continue;
                }

                result.Add(new WechatAccount(account, name.Length == 0 ? account : name));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException)
        {
            // 与 Python 的 except Exception 对齐：后端不可用不是致命错误，交给扫盘兜底。
            return new List<WechatAccount>();
        }

        return result;
    }

    private List<WechatAccount> ScanLocalDatabases()
    {
        var result = new List<WechatAccount>();
        var databases = Path.Combine(_paths.WechatOutputDir, "databases");
        if (!Directory.Exists(databases))
        {
            return result;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(databases))
            {
                var account = Path.GetFileName(directory);
                if (account.Length == 0)
                {
                    continue;
                }

                result.Add(new WechatAccount(account, account));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 权限异常同样按"拿不到账号"处理。
        }

        return result;
    }

    private static IReadOnlyList<WechatAccount> Deduplicate(List<WechatAccount> accounts)
    {
        var unique = new Dictionary<string, WechatAccount>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            // 等价 dict.setdefault：先到的那个（接口返回值）优先于扫盘值。
            unique.TryAdd(account.Account, account);
        }

        var result = new List<WechatAccount>();
        foreach (var (account, item) in unique)
        {
            var duplicated = unique.Keys.Any(other =>
                !string.Equals(other, account, StringComparison.Ordinal)
                && account.StartsWith(other + "_", StringComparison.Ordinal));

            if (!duplicated)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static string ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }
}
