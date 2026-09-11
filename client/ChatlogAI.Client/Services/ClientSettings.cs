using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatlogAI.Client.Bootstrap;

namespace ChatlogAI.Client.Services;

/// <summary>
/// 客户端本地偏好（T18 新建）：主题、免责声明的"不再提示"。
///
/// 为什么不写进后端的 config.json：那是**后端**配置（端口 / 账号 / 供应商密钥），
/// 由 Python 侧读写；主题和"不再提示"是纯客户端界面偏好，塞进去等于让两套代码
/// 共同维护一个文件，任何一边的写入都可能覆盖另一边的字段。
///
/// 为什么放 AppPaths.DataDir：它是"可写入数据"的单一事实源，安装后在
/// %ProgramData%\ChatlogAI（开发期由环境变量 CHATLOG_DATA_DIR 指向仓库目录）。
/// 刻意不用 %LOCALAPPDATA%：程序以管理员运行，提权时若输入了另一个管理员账号，
/// 偏好会落到那个账号的家目录，用户下次启动就"配置丢了"（AppPaths 的注释里有完整说明）。
///
/// 读写策略：**全部失败静默**。这个文件丢了最多是"回到浅色主题、免责声明再弹一次"，
/// 而抛异常会打断启动流程——界面偏好没有资格让程序起不来。
/// </summary>
public sealed class ClientSettings
{
    /// <summary>浅色主题标识。与 Themes/Theme.Light.xaml 对应。</summary>
    public const string LightTheme = "light";

    /// <summary>深色主题标识。与 Themes/Theme.Dark.xaml 对应。</summary>
    public const string DarkTheme = "dark";

    private const string FileName = "client-settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>主题标识，只认 <see cref="LightTheme" /> / <see cref="DarkTheme" />。</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = LightTheme;

    /// <summary>勾了"不再提示"后为 true，下次启动直接跳过免责声明。</summary>
    [JsonPropertyName("suppressDisclaimer")]
    public bool SuppressDisclaimer { get; set; }

    /// <summary>
    /// 主题的布尔投影：界面只关心"是不是深色"，把字符串解析收在这里，
    /// 避免每个调用点各写一遍字符串比较（写错一处就会得到两套判据）。
    /// 标 <see cref="JsonIgnoreAttribute" />：它不落盘，落盘的是 <see cref="Theme" />。
    /// </summary>
    [JsonIgnore]
    public bool IsDarkTheme
    {
        get => string.Equals(Theme, DarkTheme, StringComparison.OrdinalIgnoreCase);
        set => Theme = value ? DarkTheme : LightTheme;
    }

    /// <summary>设置文件路径。每次求值，便于开发期改 CHATLOG_DATA_DIR 后立即生效。</summary>
    public static string FilePath => Path.Combine(AppPaths.Instance.DataDir, FileName);

    /// <summary>读取偏好。文件不存在、内容损坏、无权限读取时一律返回默认值（浅色 + 每次弹免责声明）。</summary>
    public static ClientSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new ClientSettings();
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<ClientSettings>(json, SerializerOptions);

            return settings is null ? new ClientSettings() : Normalize(settings);
        }
        catch (Exception)
        {
            // 含 JsonException / IOException / UnauthorizedAccessException：
            // 偏好读不出来不该影响启动，回默认值即可。
            return new ClientSettings();
        }
    }

    /// <summary>写回偏好。写失败时静默——用户这次的选择会在下次启动时表现为"没有生效"，但程序照常可用。</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Instance.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception)
        {
            // 见上：界面偏好的持久化失败不值得打断用户操作。
        }
    }

    /// <summary>
    /// 把外部内容归一化成合法状态。必须做这一步：JSON 里 theme 可能是 null、
    /// 空串或将来版本才认识的值，直接拿去比字符串会静默退化成"每次都当浅色"，
    /// 而 <see cref="Theme" /> 又是会被原样写回文件的，脏值会一直留着。
    /// </summary>
    private static ClientSettings Normalize(ClientSettings settings)
    {
        settings.Theme = settings.IsDarkTheme ? DarkTheme : LightTheme;
        return settings;
    }
}
