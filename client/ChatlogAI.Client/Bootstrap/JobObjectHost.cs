using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChatlogAI.Client.Bootstrap;

/// <summary>
/// Windows Job Object 封装，等价 launcher.py 的 ProcessJob 类（L86-L123）。
///
/// 为什么必须要有它：
///   客户端会拉起两个后台进程（微信数据源、Python 后端），它们各自还会派生 subprocess。
///   如果只靠"关闭时逐个 Kill"，一旦客户端被强杀（任务管理器结束进程、崩溃），
///   Kill 代码根本不会执行，端口就被残留进程长期占用，下次启动直接报"端口被占用"。
///   Job Object 把这个责任交给内核：句柄一关（进程退出时句柄必然关闭），
///   组内所有进程由内核无条件终止——这是"父死子必死"最可靠的做法。
///
/// 关键语义 KILL_ON_JOB_CLOSE(0x2000)：关闭 job 句柄 = 杀光组内进程。
/// 因此本对象必须在整个程序生命周期内保持存活，只在退出时才 Dispose。
/// </summary>
public sealed class JobObjectHost : IDisposable
{
    private const uint KillOnJobClose = 0x00002000;
    private const int ExtendedLimitInformationClass = 9;
    private const int ErrorAccessDenied = 5;

    private IntPtr _handle;
    private bool _disposed;

    /// <summary>创建 job 失败时为 false；调用方应据此决定是否退化到"逐个 Kill"。</summary>
    public bool IsValid => _handle != IntPtr.Zero;

    public JobObjectHost()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = KillOnJobClose,
            },
        };

        if (!TrySetExtendedLimitInformation(_handle, ref info))
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 把进程纳入本 job。等价 ProcessJob.assign()。
    ///
    /// 注意：这与 Python 版一样存在一个极小的竞态——进程从 Start 返回到被执行 Assign
    /// 之间若已派生孙进程，孙进程不会被纳入。上游 broker 另有 --parent-pid 兜底，
    /// 彻底消除需要 CREATE_SUSPENDED，属后续独立议题（方案 R2）。
    /// </summary>
    public void Assign(Process process)
    {
        if (_disposed || _handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // Process.Handle 拿到的是原生进程句柄，等价 Python 的 process._handle。
            if (!AssignProcessToJobObject(_handle, process.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorAccessDenied)
                {
                    throw new Win32Exception(error, "把子进程加入 Job Object 失败");
                }

                // ERROR_ACCESS_DENIED 常见于进程已退出，或本进程自身已在某个不允许嵌套的 job 中。
                // 这属于可降级情形：子进程仍会被后续的 StopAsync 收掉，不应让启动流程中断。
            }
        }
        catch (InvalidOperationException)
        {
            // 进程还没真正启动或已经退出，此时 Process.Handle 会抛异常，忽略即可。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            // 关闭句柄 = 立即终止组内全部进程（KILL_ON_JOB_CLOSE）。
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static bool TrySetExtendedLimitInformation(IntPtr handle, ref JobObjectExtendedLimitInformation info)
    {
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            return SetInformationJobObject(handle, ExtendedLimitInformationClass, buffer, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------- P/Invoke ----------
    // 结构体字段顺序/大小必须与 Windows 头文件严格一致，错一个字段就会 SetInformationJobObject 失败。

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int jobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
