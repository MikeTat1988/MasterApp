namespace MasterApp.Models;

public sealed class AppSecrets
{
    public string CloudflareTunnelToken { get; set; } = "PASTE_TOKEN_HERE";
    public string PublicHostname { get; set; } = "masterapp.masterapplocal.com";
    public int LocalPort { get; set; } = 19057;

    public static AppSecrets CreateDefault() => new();
}
