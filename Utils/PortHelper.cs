using System.Net;
using System.Net.Sockets;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 端口工具类
/// </summary>
public static class PortHelper
{
    /// <summary>
    /// 获取一个可用的端口
    /// </summary>
    /// <param name="initPort">起始端口号</param>
    /// <param name="maxAttempts">最大尝试次数</param>
    /// <returns>可用的端口号</returns>
    public static int GetAvailablePort(int initPort = 13000, int maxAttempts = 100)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            int port = initPort + i;
            if (IsPortAvailable(port))
            {
                return port;
            }
        }

        // 如果指定范围内找不到可用端口，让系统分配一个
        return GetRandomAvailablePort();
    }

    /// <summary>
    /// 检查端口是否可用
    /// </summary>
    /// <param name="port">端口号</param>
    /// <returns>端口是否可用</returns>
    public static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// 获取一个随机可用端口
    /// </summary>
    /// <returns>可用的端口号</returns>
    public static int GetRandomAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
