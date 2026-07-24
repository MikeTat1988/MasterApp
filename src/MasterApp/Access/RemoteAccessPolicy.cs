using MasterApp.Networking;
using System.Net;

namespace MasterApp.Access;

public static class RemoteAccessPolicy
{
    public static bool IsLoopbackOrLocalMachine(IPAddress? remoteAddress, IEnumerable<string> machineAddresses)
    {
        var normalized = WifiNetworkInspector.NormalizeAddress(remoteAddress);
        if (normalized is null)
        {
            return true;
        }

        if (IPAddress.IsLoopback(normalized))
        {
            return true;
        }

        return machineAddresses.Contains(normalized.ToString(), StringComparer.Ordinal);
    }

    public static bool IsAllowedWifiClient(IPAddress? remoteAddress, string? hostAddress, string? subnetMask)
    {
        var normalizedRemote = WifiNetworkInspector.NormalizeAddress(remoteAddress);
        if (normalizedRemote is null ||
            string.IsNullOrWhiteSpace(hostAddress) ||
            string.IsNullOrWhiteSpace(subnetMask))
        {
            return false;
        }

        return WifiNetworkInspector.IsInSameSubnet(
            normalizedRemote,
            IPAddress.Parse(hostAddress),
            IPAddress.Parse(subnetMask));
    }
}
