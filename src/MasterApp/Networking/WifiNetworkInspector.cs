using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MasterApp.Networking;

public sealed class WifiNetworkInspector
{
    public WifiNetworkSnapshot GetSnapshot()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(candidate => candidate.OperationalStatus == OperationalStatus.Up &&
                                         candidate.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
        {
            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch
            {
                continue;
            }

            var hasGateway = properties.GatewayAddresses.Any(candidate => candidate.Address.AddressFamily == AddressFamily.InterNetwork);
            if (!hasGateway)
            {
                continue;
            }

            foreach (var unicastAddress in properties.UnicastAddresses.Where(candidate => candidate.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                var address = NormalizeAddress(unicastAddress.Address);
                var mask = NormalizeAddress(unicastAddress.IPv4Mask);
                if (address is null || mask is null || !IsPrivateIPv4(address))
                {
                    continue;
                }

                return new WifiNetworkSnapshot
                {
                    IsAvailable = true,
                    Status = "available",
                    InterfaceName = networkInterface.Name,
                    HostAddress = address.ToString(),
                    SubnetMask = mask.ToString(),
                    NetworkPrefix = BuildNetworkPrefix(address, mask),
                    Message = $"Wi-Fi ready on {networkInterface.Name}."
                };
            }
        }

        return new WifiNetworkSnapshot
        {
            IsAvailable = false,
            Status = "unavailable",
            Message = "No active Wi-Fi network with a private IPv4 address was found."
        };
    }

    public static bool IsPrivateIPv4(IPAddress address)
    {
        var bytes = NormalizeAddress(address)?.GetAddressBytes();
        if (bytes is not { Length: 4 })
        {
            return false;
        }

        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    public static bool IsInSameSubnet(IPAddress left, IPAddress right, IPAddress mask)
    {
        var leftBytes = NormalizeAddress(left)?.GetAddressBytes();
        var rightBytes = NormalizeAddress(right)?.GetAddressBytes();
        var maskBytes = NormalizeAddress(mask)?.GetAddressBytes();

        if (leftBytes is not { Length: 4 } || rightBytes is not { Length: 4 } || maskBytes is not { Length: 4 })
        {
            return false;
        }

        for (var index = 0; index < 4; index++)
        {
            if ((leftBytes[index] & maskBytes[index]) != (rightBytes[index] & maskBytes[index]))
            {
                return false;
            }
        }

        return true;
    }

    public static IPAddress? NormalizeAddress(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetwork ? address : null;
    }

    private static string BuildNetworkPrefix(IPAddress address, IPAddress mask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var networkBytes = new byte[addressBytes.Length];
        var cidr = 0;

        for (var index = 0; index < addressBytes.Length; index++)
        {
            networkBytes[index] = (byte)(addressBytes[index] & maskBytes[index]);
            cidr += CountBits(maskBytes[index]);
        }

        return $"{new IPAddress(networkBytes)}/{cidr}";
    }

    private static int CountBits(byte value)
    {
        var count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }

        return count;
    }
}
