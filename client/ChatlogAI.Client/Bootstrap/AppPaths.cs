using System.IO;

namespace ChatlogAI.Client.Bootstrap;

/// <summary>
/// 路径解析的单一事实源，对应 app/launcher.py 顶部 L24-L47 的路径常量区。
///
/// 与便携版最大的区别在于「程序根目录」与「数据根目录」被彻底拆开（见方案 §5）：
///   - RootDir：只读。安装后位于 %ProgramFiles%\ChatlogAI，程序文件、runtime、app/static 都在这里。
///   - DataDir：可写。安装后位于 %ProgramData%\ChatlogAI，配置、日志、微信数据、摘要都在这里。
/// 之所以不用 %LOCALAPPDATA%：程序以管理员运行，若标准账户提权时输入的是另一个管理员账号，
/// %LOCALAPPDATA% 会解析到那个账号的家目录，导致用户"配置丢失"。
///
/// 两个根目录都允许用环境变量覆盖，便于开发期直接指向项目仓库：
///   CHATLOG_ROOT_DIR / CHATLOG_DATA_DIR
/// </summary>
public sealed class AppPaths
{
    /// <summary>本项目 FastAPI 后端端口（launcher.py 的 CHATLOG_PORT）。</summary>
    public const int ChatlogPort = 18765;

    /// <summary>微信数据源后端端口，同时也是其授权页端口（launcher.py 的 WECHAT_PORT）。</summary>
    public const int WechatPort = 10392;

    private static readonly Lazy<AppPaths> LazyInstance = new(() => new AppPaths());

    /// <summary>进程级单例。路径在进程生命周期内不会变化，因此只解析一次。</summary>
    public static AppPaths Instance => LazyInstance.Value;

    private AppPaths()
    {
        RootDir = ResolveRootDir();
        DataDir = ResolveDataDir();
    }

    // ---------- 根目录 ----------

    /// <summary>只读的程序根目录。等价 launcher.py 的 ROOT_DIR。</summary>
    public string RootDir { get; }

    /// <summary>可写的用户数据根目录。等价 launcher.py 的 ROOT_DIR 中"可写"的那一半。</summary>
    public string DataDir { get; }

    // ---------- 程序文件（只读） ----------

    /// <summary>Python 后端源码目录。等价 launcher.py 的 APP_DIR。</summary>
    public string AppDir => Path.Combine(RootDir, "app");

    /// <summary>后端入口脚本，等价 launcher.py 的 server_command() 里的 APP_DIR/server.py。</summary>
    public string ServerScript => Path.Combine(AppDir, "server.py");

    /// <summary>便携 Python 解释器。</summary>
    public string PythonExe => Path.Combine(RootDir, "runtime", "python", "python.exe");

    /// <summary>便携依赖包目录；必须通过 PYTHONPATH 注入，否则 import fastapi 失败。</summary>
    public string PythonPackagesDir => Path.Combine(RootDir, "runtime", "python-packages");

    // ---------- 可写数据 ----------

    /// <summary>配置目录（config.json / api-keys.json / prompts / auto-reply）。</summary>
    public string ConfigDir => Path.Combine(DataDir, "config");

    public string ConfigFile => Path.Combine(ConfigDir, "config.json");

    public string ApiKeysFile => Path.Combine(ConfigDir, "api-keys.json");

    public string PromptsDir => Path.Combine(ConfigDir, "prompts");

    public string AutoReplyDir => Path.Combine(ConfigDir, "auto-reply");

    /// <summary>等价 launcher.py 的 DATA_DIR（注意它是 DataDir 下的 data 子目录，不是同一个概念）。</summary>
    public string DataFilesDir => Path.Combine(DataDir, "data");

    /// <summary>等价 launcher.py 的 LOG_DIR。</summary>
    public string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>后端约定的退出信号文件；出现即表示请求整体退出。</summary>
    public string ShutdownFile => Path.Combine(DataFilesDir, ".shutdown-requested");

    public string WechatDataDir => Path.Combine(DataFilesDir, "wechat", "data");

    public string WechatOutputDir => Path.Combine(DataFilesDir, "wechat", "output");

    /// <summary>上游产出的账号密钥文件；db_key 非空即视为已完成授权。</summary>
    public string WechatAccountKeysFile => Path.Combine(WechatOutputDir, "account_keys.json");

    /// <summary>
    /// 本服务自己的密钥备份（= 后端 <c>localdb/keys.py</c> 的首选来源）。
    ///
    /// 与上游那份的**结构不同**（这份是 `{accounts: [{account, db_key, …}]}`），
    /// 且它是上游被移除后**唯一**的密钥来源 —— 也是「微信账号」窗口里手动导入密钥的落点。
    /// </summary>
    public string LocalKeysFile => Path.Combine(ConfigDir, "wechat-keys.json");

    // ---------- 微信上游运行时（只读） ----------

    /// <summary>等价 launcher.py 的 WECHAT_RESOURCES。</summary>
    public string WechatResourcesDir => Path.Combine(RootDir, "runtime", "wechat", "resources");

    public string WechatBackendExe => Path.Combine(WechatResourcesDir, "backend", "wechat-backend.exe");

    public string WechatNativeDir => Path.Combine(WechatResourcesDir, "backend", "native");

    public string WechatBrokerExe => Path.Combine(WechatNativeDir, "wechatdb_broker.exe");

    public string WechatNativeLibrary => Path.Combine(WechatNativeDir, "wechatdb_client.dll");

    public string WechatIntegrityPyd => Path.Combine(WechatNativeDir, "wce_integrity.pyd");

    public string WechatUiDir => Path.Combine(WechatResourcesDir, "ui");

    public string WechatFfmpegExe => Path.Combine(WechatResourcesDir, "ffmpeg", "ffmpeg.exe");

    // ---------- 便捷方法 ----------

    public string LogFile(string name) => Path.Combine(LogDir, name);

    public string ChatlogUrl => $"http://127.0.0.1:{ChatlogPort}";

    public string WechatUrl => $"http://127.0.0.1:{WechatPort}";

    private static string ResolveRootDir()
    {
        var overridden = Environment.GetEnvironmentVariable("CHATLOG_ROOT_DIR");
        var root = string.IsNullOrWhiteSpace(overridden) ? AppContext.BaseDirectory : overridden;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static string ResolveDataDir()
    {
        var overridden = Environment.GetEnvironmentVariable("CHATLOG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(overridden));
        }

        // CommonApplicationData == C:\ProgramData（机器级、提权后稳定可写）。
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "ChatlogAI");
    }
}
