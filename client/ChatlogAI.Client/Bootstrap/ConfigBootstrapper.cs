using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ChatlogAI.Client.Bootstrap;

/// <summary>
/// 配置与目录的首次运行补齐逻辑，等价 launcher.py 的
/// ensure_config_files() / ensure_directories() / require_paths() 三个函数。
///
/// 唯一的硬性约束：**已存在的文件一律不覆盖**。用户改过的 config.json 与提示词
/// 是他们的资产，补齐逻辑只负责"从无到有"。
/// 默认值必须与 app/config_defaults.py 保持一致，否则便携版与安装版行为会出现分叉。
/// </summary>
public sealed class ConfigBootstrapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Python 侧用 ensure_ascii=False；这里同样不转义中文，否则 config.json 会变成 \uXXXX 乱码串。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>config/config.json 的默认内容（对应 DEFAULT_CONFIG）。</summary>
    private static readonly Dictionary<string, object?> DefaultConfig = new()
    {
        ["port"] = AppPaths.ChatlogPort,
        ["wechat_api"] = $"http://127.0.0.1:{AppPaths.WechatPort}",
        ["wechat_account"] = "",
        ["active_provider"] = "",
    };

    /// <summary>config/api-keys.json 的默认内容（对应 DEFAULT_API_KEYS）。</summary>
    private static readonly Dictionary<string, object?> DefaultApiKeys = new()
    {
        ["providers"] = Array.Empty<object>(),
    };

    /// <summary>config/prompts/&lt;name&gt;.txt 的默认文案（对应 DEFAULT_PROMPTS）。</summary>
    private static readonly Dictionary<string, string> DefaultPrompts = new()
    {
        ["summary-meeting"] = "你是一名高效的高级会议秘书。请对以下聊天记录整理为标准的【会议纪要与决策】，包含：1. 讨论背景 2. 关键决策 3. 责任人及后续动作。",
        ["summary-todo"] = "你是一个严谨的项目经理。请从以下聊天记录中精确提取【待办事项与任务列表 (Action Items)】，包含：任务描述、责任人、预期交付时间。",
        ["summary-topics"] = "你是一个信息分析专家。请提炼以下聊天记录中的【核心讨论热点与话题】，按关注度从高到低排列，并给出简要总结。",
        ["summary-general"] = "你是一个智能聊天助手。请对以下聊天记录提供简洁清晰的 Markdown 格式结构化总结，包含核心议题、关键观点及待办项。",
        ["auto-reply-outer"] = "你是聊天记录中的“我”本人。",
        ["auto-reply-inner"] = "请根据最近的对话上下文，生成一条自然、简洁、符合“我”语气和风格的下一条回复。只输出回复正文本身，不要任何解释、引号、“回复：”等前缀或额外说明。",
    };

    private readonly AppPaths _paths;

    public ConfigBootstrapper(AppPaths paths) => _paths = paths;

    /// <summary>等价 ensure_directories()：只建可写目录，程序目录不做任何写入。</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(_paths.LogDir);
        Directory.CreateDirectory(_paths.WechatDataDir);
        Directory.CreateDirectory(_paths.WechatOutputDir);
    }

    /// <summary>
    /// 等价 ensure_config_files()。必须在 <see cref="RequirePaths"/> 之前调用：
    /// config.json 不入库，新环境首次启动时若不存在，会被 RequirePaths 判为"缺少运行文件"。
    /// </summary>
    public void EnsureConfigFiles()
    {
        Directory.CreateDirectory(_paths.ConfigDir);
        Directory.CreateDirectory(_paths.PromptsDir);
        Directory.CreateDirectory(_paths.AutoReplyDir);

        if (!File.Exists(_paths.ConfigFile))
        {
            SaveJson(_paths.ConfigFile, DefaultConfig);
        }

        if (!File.Exists(_paths.ApiKeysFile))
        {
            SaveJson(_paths.ApiKeysFile, DefaultApiKeys);
        }

        foreach (var (name, text) in DefaultPrompts)
        {
            var path = Path.Combine(_paths.PromptsDir, name + ".txt");
            if (File.Exists(path))
            {
                continue;
            }

            // Python 侧是 open(..., newline="\n") + text + "\n"，这里保持一致（含结尾空行）。
            File.WriteAllText(path, text + "\n", new UTF8Encoding(false));
        }
    }

    /// <summary>等价 require_paths()：缺少运行文件时抛出可读异常，不做静默降级。</summary>
    public void RequirePaths()
    {
        var required = new[]
        {
            _paths.WechatBackendExe,
            _paths.WechatUiDir,
            _paths.PythonExe,
            _paths.ServerScript,
            _paths.ConfigFile,
        };

        var missing = required.Where(path => !File.Exists(path) && !Directory.Exists(path)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("缺少运行文件：\n" + string.Join("\n", missing));
        }
    }

    /// <summary>
    /// 等价 load_json()：以默认值打底再叠加文件内容。
    /// 文件不存在或内容损坏时返回默认值，不抛异常——启动阶段不应因配置损坏而中断。
    /// </summary>
    public Dictionary<string, object?> LoadConfig()
    {
        return LoadJson(_paths.ConfigFile, DefaultConfig);
    }

    public static Dictionary<string, object?> LoadJson(string path, Dictionary<string, object?> defaults)
    {
        var result = new Dictionary<string, object?>(defaults);
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 与 Python 的 except (OSError, ValueError) 对齐：损坏的配置不阻断启动。
        }

        return result;
    }

    /// <summary>
    /// 等价 save_json()：先写 .tmp 再原子替换，避免断电/崩溃留下半截 JSON。
    /// </summary>
    public static void SaveJson(string path, Dictionary<string, object?> data)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        var json = JsonSerializer.Serialize(data, JsonOptions) + "\n";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
