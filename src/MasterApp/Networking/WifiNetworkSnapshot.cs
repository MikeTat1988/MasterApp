namespace MasterApp.Networking;

public sealed class WifiNetworkSnapshot
{
    public bool IsAvailable { get; init; }
    public string Status { get; init; } = "unavailable";
    public string? InterfaceName { get; init; }
    public string? HostAddress { get; init; }
    public string? SubnetMask { get; init; }
    public string? NetworkPrefix { get; init; }
    public string? Message { get; init; }
}
