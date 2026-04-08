namespace MasterApp.Packages;

public sealed class AppManifest
{
    public string SchemaVersion { get; set; } = "2";
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string AppType { get; set; } = AppTypes.Static;
    public string Entry { get; set; } = "index.html";
    public string? Icon { get; set; }
    public AppLaunchManifest Launch { get; set; } = new();
    public AppBuildManifest? Build { get; set; }
    public AppPublishManifest? Publish { get; set; }
    public List<string> DataDirectories { get; set; } = new();
    public AppDisplayManifest Display { get; set; } = new();
    public AppPwaManifest? Pwa { get; set; }
}

public static class AppTypes
{
    public const string Static = "static";
    public const string Portable = "portable";
    public const string Source = "source";
}

public static class LaunchKinds
{
    public const string Static = "static";
    public const string WebApp = "webApp";
}

public sealed class AppLaunchManifest
{
    public string Kind { get; set; } = LaunchKinds.Static;
    public string? ExecutablePath { get; set; }
    public string? WorkingDirectory { get; set; }
    public List<string> Arguments { get; set; } = new();
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int? Port { get; set; }
    public string UrlTemplate { get; set; } = "http://127.0.0.1:{port}/";
    public string HealthPath { get; set; } = "/";
    public int StartupTimeoutSeconds { get; set; } = 20;
}

public sealed class AppBuildManifest
{
    public string? InstallCommand { get; set; }
    public string? WorkingDirectory { get; set; }
}

public sealed class AppPublishManifest
{
    public string? Command { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? OutputPath { get; set; }
    public bool PreferSingleFile { get; set; } = true;
    public bool CreateZip { get; set; } = true;
}

public sealed class AppDisplayManifest
{
    public string? ShortName { get; set; }
    public bool StoreVisible { get; set; } = true;
    public bool ShowInLibrary { get; set; } = true;
}

public sealed class AppPwaManifest
{
    public string? Name { get; set; }
    public string? ShortName { get; set; }
    public string Display { get; set; } = "standalone";
    public string BackgroundColor { get; set; } = "#111723";
    public string ThemeColor { get; set; } = "#4f7cff";
}
