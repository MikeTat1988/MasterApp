using System.Net;
using System.Net.Sockets;

namespace MasterApp.Hosting;

public static class LocalPortSelector
{
    public static int SelectAvailablePort(int preferredPort, int fallbackCount)
    {
        var start = Math.Clamp(preferredPort, 1, 65535);
        var attempts = Math.Clamp(fallbackCount, 0, 100);
        for (var port = start; port <= Math.Min(65535, start + attempts); port++)
        {
            if (CanBind(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException($"No available local port found from {start} to {Math.Min(65535, start + attempts)}.");
    }

    private static bool CanBind(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }
}
