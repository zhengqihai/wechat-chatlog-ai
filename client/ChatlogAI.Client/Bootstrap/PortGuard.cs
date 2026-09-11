using System.Net;
using System.Net.Sockets;

namespace ChatlogAI.Client.Bootstrap;

/// <summary>
/// 端口占用探测，等价 launcher.py 的 ensure_port_free()。
///
/// 探测方式与 Python 完全一致：用 connect_ex 主动连一次，连得上就说明有人监听。
/// 注意这里刻意不用"尝试绑定端口"来判断——绑定会带上 TIME_WAIT 等副作用，
/// 而"能连上"才是我们真正关心的事实：已经有旧实例在跑。
///
/// 这条检查存在的意义：上一次异常退出若留下残留进程，端口仍被占用，
/// 后续启动会表现为"服务起不来"却没有任何提示，所以必须在启动前显式失败。
/// </summary>
public static class PortGuard
{
    public static void EnsurePortFree(int port)
    {
        using var client = new TcpClient();
        if (IsPortOpen(client, port))
        {
            throw new InvalidOperationException(
                $"端口 {port} 已被占用，请先关闭旧的 Chatlog AI 相关服务");
        }
    }

    private static bool IsPortOpen(TcpClient client, int port)
    {
        try
        {
            // 等价 Python 的 settimeout(0.3)：本机端口探测要么立刻成功，要么立刻被拒。
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            return connect.Wait(TimeSpan.FromMilliseconds(300)) && client.Connected;
        }
        catch (Exception ex) when (ex is AggregateException or SocketException or ObjectDisposedException)
        {
            // 连接被拒绝（RST）会以 SocketException 的形式从 Task 里抛出，这正是"端口空闲"的信号。
            return false;
        }
    }
}
