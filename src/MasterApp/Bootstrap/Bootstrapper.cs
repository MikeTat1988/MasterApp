using MasterApp.Diagnostics;
using MasterApp.Models;
using MasterApp.Storage;
using System.Text.Json;

namespace MasterApp.Bootstrap;

public static class Bootstrapper
{
    public static BootstrapContext Initialize()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(localAppData, "MasterApp");
        var state = Path.Combine(root, "State");
        var logs = Path.Combine(root, "Logs");
        var temp = Path.Combine(root, "Temp");
        var apps = Path.Combine(root, "Apps");
        var appSpecs = Path.Combine(root, "AppSpecs");

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(apps);
        Directory.CreateDirectory(appSpecs);

        var paths = new AppPaths
        {
            RootDirectory = root,
            StateDirectory = state,
            LogsDirectory = logs,
            TempDirectory = temp,
            AppsDirectory = apps,
            AppSpecsDirectory = appSpecs,
            SettingsFile = Path.Combine(state, "settings.json"),
            SecretsFile = Path.Combine(state, "secrets.json"),
            RuntimeStateFile = Path.Combine(state, "runtime-state.json")
        };

        var log = new FileLogManager(paths.LogsDirectory);
        log.Info("Bootstrapper", $"MasterApp root: {paths.RootDirectory}");
        log.Info("Bootstrapper", $"Settings file: {paths.SettingsFile}");
        log.Info("Bootstrapper", $"Secrets file: {paths.SecretsFile}");

        var settings = LoadOrCreate(
            paths.SettingsFile,
            AppSettings.CreateDefault(),
            log,
            "settings.json");

        var secrets = LoadOrCreate(
            paths.SecretsFile,
            AppSecrets.CreateDefault(),
            log,
            "secrets.json");

        EnsureDirectory(settings.IncomingFolder, log, "incomingFolder");
        EnsureDirectory(settings.ProcessedFolder, log, "processedFolder");
        EnsureDirectory(settings.FailedFolder, log, "failedFolder");
        EnsureDirectory(settings.PublishedFolder, log, "publishedFolder");

        var runtimeStateStore = new RuntimeStateStore(paths.RuntimeStateFile, log);

        var issues = Validate(settings, secrets, log);

        return new BootstrapContext
        {
            Paths = paths,
            Settings = settings,
            Secrets = secrets,
            RuntimeStateStore = runtimeStateStore,
            Log = log,
            ValidationIssues = issues
        };
    }

    private static T LoadOrCreate<T>(string path, T defaultValue, FileLogManager log, string friendlyName)
        where T : class, new()
    {
        try
        {
            if (!File.Exists(path))
            {
                Save(path, defaultValue);
                log.Warn("Bootstrapper", $"{friendlyName} not found. Created default file at {path}");
                return defaultValue;
            }

            var json = File.ReadAllText(path);
            var value = JsonSerializer.Deserialize<T>(json, JsonOptions.Default);

            if (value is null)
            {
                Save(path, defaultValue);
                log.Warn("Bootstrapper", $"{friendlyName} was empty or invalid. Replaced with defaults.");
                return defaultValue;
            }

            if (typeof(T) == typeof(AppSecrets) && value is AppSecrets secrets)
            {
                value = (T)(object)NormalizeSecrets(json, secrets);
            }

            Save(path, value);
            log.Info("Bootstrapper", $"{friendlyName} loaded successfully.");
            return value;
        }
        catch (Exception ex)
        {
            Save(path, defaultValue);
            log.Error("Bootstrapper", $"{friendlyName} failed to load. Replaced with defaults.", ex);
            return defaultValue;
        }
    }

    private static void Save<T>(string path, T value)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions.DefaultIndented));
    }

    private static AppSecrets NormalizeSecrets(string json, AppSecrets secrets)
    {
        var tokenMissing = string.IsNullOrWhiteSpace(secrets.CloudflareTunnelToken) ||
                           secrets.CloudflareTunnelToken.Contains("PASTE_TOKEN_HERE", StringComparison.OrdinalIgnoreCase);

        if (!tokenMissing)
        {
            return secrets;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("tunnelToken", out var legacyToken) &&
                legacyToken.ValueKind == JsonValueKind.String)
            {
                var token = legacyToken.GetString();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    secrets.CloudflareTunnelToken = token;
                }
            }
        }
        catch
        {
            // Ignore legacy-shape probing and keep the deserialized value.
        }

        return secrets;
    }


    private static void EnsureDirectory(string path, FileLogManager log, string label)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                Directory.CreateDirectory(path);
            }
        }
        catch (Exception ex)
        {
            log.Warn("Bootstrapper", $"Could not create directory for {label}: {path}. {ex.Message}");
        }
    }

    private static List<string> Validate(AppSettings settings, AppSecrets secrets, FileLogManager log)
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(secrets.CloudflareTunnelToken) ||
            secrets.CloudflareTunnelToken.Contains("PASTE_TOKEN_HERE", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("TOKEN_MISSING: secrets.json does not contain a real Cloudflare tunnel token.");
        }

        if (secrets.LocalPort is <= 0 or > 65535)
        {
            issues.Add("LOCAL_PORT_INVALID: localPort must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(secrets.PublicHostname))
        {
            issues.Add("PUBLIC_HOSTNAME_MISSING: publicHostname is empty.");
        }

        if (!File.Exists(settings.CloudflaredPath))
        {
            issues.Add($"CLOUDFLARED_NOT_FOUND: {settings.CloudflaredPath}");
        }

        if (string.IsNullOrWhiteSpace(settings.IncomingFolder))
        {
            issues.Add("INCOMING_FOLDER_MISSING: incomingFolder is empty.");
        }

        if (string.IsNullOrWhiteSpace(settings.ProcessedFolder))
        {
            issues.Add("PROCESSED_FOLDER_MISSING: processedFolder is empty.");
        }

        if (string.IsNullOrWhiteSpace(settings.FailedFolder))
        {
            issues.Add("FAILED_FOLDER_MISSING: failedFolder is empty.");
        }

        if (string.IsNullOrWhiteSpace(settings.PublishedFolder))
        {
            issues.Add("PUBLISHED_FOLDER_MISSING: publishedFolder is empty.");
        }

        foreach (var issue in issues)
        {
            log.Warn("Bootstrapper", issue);
        }

        if (issues.Count == 0)
        {
            log.Info("Bootstrapper", "Configuration validation passed.");
        }

        return issues;
    }
}
