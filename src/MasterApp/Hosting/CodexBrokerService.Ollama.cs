using MasterApp.Models;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MasterApp.Storage;

namespace MasterApp.Hosting;

public sealed partial class CodexBrokerService
{
    private const string CodexProvider = "codex";
    private const string OllamaProvider = "ollama";
    private const string DefaultOllamaModel = "gemma4:e2b";
    private const string DefaultOllamaEndpoint = "http://localhost:11434";
    private static readonly Regex SearchIntentRegex = new(@"(\b(search( the)? web|web search|internet search|latest|current|today|news|weather|forecast|temperature)\b|найди|поиск|интернет|веб|свеж|последн|новост|сегодня|погод|температур|прогноз)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlLinkRegex = new(@"<a[^>]+href=""(?<href>[^""]+)""[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _ollamaExecutablePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "Ollama",
        "ollama.exe");

    private static string NormalizeProvider(string? provider, string? model)
    {
        if (!string.IsNullOrWhiteSpace(provider))
        {
            return provider.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(model) &&
            string.Equals(model.Trim(), DefaultOllamaModel, StringComparison.OrdinalIgnoreCase))
        {
            return OllamaProvider;
        }

        return CodexProvider;
    }

    private static bool IsOllamaProvider(string? provider)
        => string.Equals(provider, OllamaProvider, StringComparison.OrdinalIgnoreCase);

    private string ResolveCurrentProvider(CodexRuntimeState runtime)
    {
        return string.IsNullOrWhiteSpace(runtime.CurrentProvider)
            ? CodexProvider
            : NormalizeProvider(runtime.CurrentProvider, runtime.CurrentModel);
    }

    private string ResolveCurrentModel(CodexRuntimeState runtime)
    {
        if (!string.IsNullOrWhiteSpace(runtime.CurrentModel))
        {
            return runtime.CurrentModel;
        }

        return IsOllamaProvider(runtime.CurrentProvider)
            ? DefaultOllamaModel
            : (TryReadCurrentCodexModel() ?? string.Empty);
    }

    private OllamaStatusSnapshot GetOllamaStatus()
    {
        var executablePath = File.Exists(_ollamaExecutablePath) ? _ollamaExecutablePath : null;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new OllamaStatusSnapshot
            {
                Status = "missing",
                IsReady = false,
                Endpoint = DefaultOllamaEndpoint,
                LastError = "Ollama executable was not found in the default Windows location."
            };
        }

        try
        {
            var capture = RunProcessCapture(executablePath, new[] { "list" }, Directory.GetCurrentDirectory(), 8_000);
            if (capture.ExitCode == 0)
            {
                return new OllamaStatusSnapshot
                {
                    Status = "ready",
                    IsReady = true,
                    Endpoint = DefaultOllamaEndpoint,
                    ExecutablePath = executablePath
                };
            }

            var error = string.IsNullOrWhiteSpace(capture.StandardError)
                ? capture.StandardOutput.Trim()
                : capture.StandardError.Trim();

            return new OllamaStatusSnapshot
            {
                Status = "failed",
                IsReady = false,
                Endpoint = DefaultOllamaEndpoint,
                ExecutablePath = executablePath,
                LastError = string.IsNullOrWhiteSpace(error) ? $"ollama list exited with code {capture.ExitCode}." : error
            };
        }
        catch (Exception ex)
        {
            return new OllamaStatusSnapshot
            {
                Status = "failed",
                IsReady = false,
                Endpoint = DefaultOllamaEndpoint,
                ExecutablePath = executablePath,
                LastError = ex.Message
            };
        }
    }

    private IReadOnlyList<CodexModelInfo> GetAvailableOllamaModels()
    {
        if (!File.Exists(_ollamaExecutablePath))
        {
            return Array.Empty<CodexModelInfo>();
        }

        try
        {
            var capture = RunProcessCapture(_ollamaExecutablePath, new[] { "list" }, Directory.GetCurrentDirectory(), 8_000);
            if (capture.ExitCode != 0)
            {
                return FallbackOllamaModelList();
            }

            var models = capture.StandardOutput
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length > 0 && !string.Equals(parts[0], "NAME", StringComparison.OrdinalIgnoreCase))
                .Select(parts => new CodexModelInfo
                {
                    Provider = OllamaProvider,
                    Slug = parts[0],
                    DisplayName = $"{parts[0]} (Ollama)"
                })
                .DistinctBy(model => $"{model.Provider}:{model.Slug}", StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return models.Length > 0 ? models : FallbackOllamaModelList();
        }
        catch
        {
            return FallbackOllamaModelList();
        }
    }

    private IReadOnlyList<CodexModelInfo> FallbackOllamaModelList()
    {
        return new[]
        {
            new CodexModelInfo
            {
                Provider = OllamaProvider,
                Slug = DefaultOllamaModel,
                DisplayName = $"{DefaultOllamaModel} (Ollama)"
            }
        };
    }

    private async Task<string> RequestOllamaResponseAsync(
        string model,
        string prompt,
        IReadOnlyList<CodexChatMessage> conversation,
        CancellationToken cancellationToken)
    {
        var searchContext = await TryBuildWebSearchContextAsync(prompt, cancellationToken);
        var finalPrompt = BuildOllamaPrompt(prompt, conversation, searchContext);

        using var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var payload = JsonSerializer.Serialize(new
        {
            model,
            prompt = finalPrompt,
            stream = false
        }, JsonOptions.Default);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{DefaultOllamaEndpoint}/api/generate")
        {
            Content = new StringContent(payload, Encoding.UTF8)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {content}".Trim());
        }

        var parsed = JsonSerializer.Deserialize<OllamaGenerateResponse>(content, JsonOptions.Default);
        if (parsed is null)
        {
            throw new InvalidOperationException("Ollama returned an empty response.");
        }

        return (parsed.Response ?? string.Empty).Trim();
    }

    private async Task<string?> TryBuildWebSearchContextAsync(string prompt, CancellationToken cancellationToken)
    {
        if (!ShouldUseWebSearch(prompt))
        {
            return null;
        }

        try
        {
            var results = await SearchWebAsync(prompt, cancellationToken);
            if (results.Count == 0)
            {
                return "Web search was requested, but no search results were available.";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Fresh web search results captured at {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}:");
            foreach (var item in results.Take(5))
            {
                builder.AppendLine($"- {item.Title}");
                builder.AppendLine($"  URL: {item.Url}");
                if (!string.IsNullOrWhiteSpace(item.Snippet))
                {
                    builder.AppendLine($"  Snippet: {item.Snippet}");
                }
            }

            return builder.ToString().Trim();
        }
        catch (Exception ex)
        {
            return $"Web search was requested, but fetching results failed: {ex.Message}";
        }
    }

    private static bool ShouldUseWebSearch(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        if (SearchIntentRegex.IsMatch(prompt))
        {
            return true;
        }

        var text = prompt.Trim();
        return text.Contains("web", StringComparison.OrdinalIgnoreCase)
            || text.Contains("internet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("browse", StringComparison.OrdinalIgnoreCase)
            || text.Contains("look it up", StringComparison.OrdinalIgnoreCase)
            || text.Contains("weather", StringComparison.OrdinalIgnoreCase)
            || text.Contains("forecast", StringComparison.OrdinalIgnoreCase)
            || text.Contains("temperature", StringComparison.OrdinalIgnoreCase)
            || text.Contains("погода", StringComparison.OrdinalIgnoreCase)
            || text.Contains("прогноз", StringComparison.OrdinalIgnoreCase)
            || text.Contains("температура", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchWebAsync(string query, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MasterApp/1.0");
        var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
        var html = await client.GetStringAsync(url, cancellationToken);

        var results = new List<WebSearchResult>();
        foreach (Match match in HtmlLinkRegex.Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            var text = StripHtml(WebUtility.HtmlDecode(match.Groups["text"].Value));
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var normalized = NormalizeSearchUrl(href);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (results.Any(item => string.Equals(item.Url, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(new WebSearchResult
            {
                Title = text,
                Url = normalized
            });

            if (results.Count >= 5)
            {
                break;
            }
        }

        return results;
    }

    private static string BuildOllamaPrompt(string prompt, IReadOnlyList<CodexChatMessage> conversation, string? searchContext)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the MasterApp assistant.");
        builder.AppendLine("Answer clearly and directly.");
        builder.AppendLine("When prior conversation is provided, continue the same chat naturally.");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(searchContext))
        {
            builder.AppendLine("Web context:");
            builder.AppendLine(searchContext);
            builder.AppendLine();
            builder.AppendLine("The web context above was fetched by the app just now.");
            builder.AppendLine("When that section is present, do not say that you cannot browse or search the web.");
            builder.AppendLine("Use the supplied web results when they are relevant. If the web context is incomplete or failed, say that plainly.");
            builder.AppendLine();
        }

        if (conversation.Count > 0)
        {
            builder.AppendLine("Conversation so far:");
            foreach (var message in conversation)
            {
                var role = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "User" : "Assistant";
                builder.AppendLine($"{role}: {message.Text.Trim()}");
            }
            builder.AppendLine();
            builder.AppendLine("Continue the conversation and answer the latest user message.");
        }
        else
        {
            builder.AppendLine("User request:");
            builder.AppendLine(prompt.Trim());
        }

        return builder.ToString();
    }

    private static string NormalizeSearchUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + url;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            var uddg = absolute.Query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(part => part.Length == 2 && string.Equals(part[0], "uddg", StringComparison.OrdinalIgnoreCase));
            if (uddg is not null)
            {
                return Uri.UnescapeDataString(uddg[1]);
            }

            return absolute.ToString();
        }

        return url;
    }

    private static string StripHtml(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutTags = Regex.Replace(value, "<.*?>", " ");
        return Regex.Replace(withoutTags, @"\s+", " ").Trim();
    }

    private void TryStopOllamaModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(_ollamaExecutablePath))
        {
            return;
        }

        try
        {
            RunProcessCapture(_ollamaExecutablePath, new[] { "stop", model.Trim() }, Directory.GetCurrentDirectory(), 30_000);
        }
        catch
        {
            // best effort only
        }
    }

    private sealed class OllamaGenerateResponse
    {
        public string? Response { get; set; }
    }

    private sealed class WebSearchResult
    {
        public string Title { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public string? Snippet { get; init; }
    }

    private sealed class OllamaStatusSnapshot
    {
        public string Status { get; init; } = "idle";
        public bool IsReady { get; init; }
        public string Endpoint { get; init; } = DefaultOllamaEndpoint;
        public string? ExecutablePath { get; init; }
        public string? LastError { get; init; }
    }
}
