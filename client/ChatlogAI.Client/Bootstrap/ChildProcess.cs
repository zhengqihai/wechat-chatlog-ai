using System.Diagnostics;
using System.IO;
using System.Text;

namespace ChatlogAI.Client.Bootstrap;

/// <summary>
/// 子进程封装，等价 launcher.py 的 start_process() / stop_process_tree()（L206-L239）。
///
/// 需要复刻的三个关键行为：
///   1. CreateNoWindow —— 这是"没有控制台黑窗"的根本保证。子进程继承控制台是 Windows 默认行为，
///      必须显式关闭，否则用户会看到一个黑窗常驻。
///   2. stdout/stderr 合并写入日志文件 —— 客户端是 GUI 进程，没有控制台可看，
///      所有诊断信息只能落盘，等价 Python 的 stderr=STDOUT + stdout=log_file。
///   3. stdin 立刻关闭（等价 DEVNULL）—— 避免子进程的 input() 卡在等待上。
/// </summary>
public sealed class ChildProcess : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly object _logLock = new();
    private bool _disposed;

    private ChildProcess(Process process, StreamWriter log)
    {
        _process = process;
        _log = log;
    }

    public int Id => _process.Id;

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 启动子进程并将其纳入 <paramref name="job"/>。
    /// </summary>
    /// <param name="command">命令与参数，第 0 项为可执行文件路径。</param>
    /// <param name="workingDirectory">工作目录（上游后端对 cwd 敏感，必须显式指定）。</param>
    /// <param name="environment">追加到当前环境变量之上的键值对。</param>
    /// <param name="logPath">日志文件路径，以追加方式写入。</param>
    /// <param name="job">负责在客户端退出时连带终止该进程。</param>
    public static ChildProcess Start(
        IReadOnlyList<string> command,
        string workingDirectory,
        IDictionary<string, string> environment,
        string logPath,
        JobObjectHost job)
    {
        if (command.Count == 0)
        {
            throw new ArgumentException("启动命令不能为空", nameof(command));
        }

        var directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var log = new StreamWriter(
            new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        // 与 Python 版一致：每次启动都留一条带时间戳的分隔记录，便于对照时间线排查。
        log.WriteLine();
        log.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 启动：{string.Join(' ', command)}");

        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        for (var i = 1; i < command.Count; i++)
        {
            // ArgumentList 会按 Windows 规则转义，避免路径含空格时被拆错。
            startInfo.ArgumentList.Add(command[i]);
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var child = new ChildProcess(process, log);
        // 回调里要读 _disposed 状态，所以必须先有实例再挂事件。
        process.OutputDataReceived += (_, e) => child.WriteLog(e.Data);
        process.ErrorDataReceived += (_, e) => child.WriteLog(e.Data);

        process.Start();
        // 必须先挂读写回调再 BeginXxxReadLine，否则管道缓冲区写满后子进程会阻塞。
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // 等价 stdin=subprocess.DEVNULL：立刻给 EOF，让子进程的 input() 不会挂住。
        process.StandardInput.Close();

        job.Assign(process);
        return child;
    }

    /// <summary>
    /// 等价 stop_process_tree()：先 taskkill /T /F 整棵树，再兜底 Kill。
    /// 之所以要 /T，是因为这些后端自身也会派生子进程（如 broker），只杀根进程会留孤儿。
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                await RunTaskKillAsync(_process.Id).ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出且句柄已释放，无需处理。
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _process.Dispose();
        }
        catch (InvalidOperationException)
        {
        }

        lock (_logLock)
        {
            _log.Dispose();
        }
    }

    private static async Task RunTaskKillAsync(int processId)
    {
        try
        {
            var startInfo = new ProcessStartInfo("taskkill")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("/PID");
            startInfo.ArgumentList.Add(processId.ToString());
            startInfo.ArgumentList.Add("/T");
            startInfo.ArgumentList.Add("/F");

            using var killer = Process.Start(startInfo);
            if (killer is not null)
            {
                await killer.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // taskkill 不可用（被裁剪的系统/权限异常）时退回 Kill，不让清理流程抛异常影响退出。
        }
    }

    private void WriteLog(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_logLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _log.WriteLine(line);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
