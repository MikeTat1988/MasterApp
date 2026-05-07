using MasterApp.Models;
using System.Text;
using System.Text.Json;

namespace MasterApp.Hosting;

public static class CodexSessionLogReader
{
    public static CodexBrokerService.CodexRecentChat? ReadRecentChat(
        string? sessionPath,
        string id,
        string title,
        DateTimeOffset updatedAtUtc)
    {
        var recent = new CodexBrokerService.CodexRecentChat
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled chat" : title,
            UpdatedAtUtc = updatedAtUtc,
            Source = "Codex",
            SessionPath = sessionPath
        };

        if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
        {
            recent.Preview = recent.Title;
            return recent;
        }

        try
        {
            if (new FileInfo(sessionPath).Length == 0)
            {
                recent.Preview = recent.Title;
                return recent;
            }

            string? firstUser = null;
            string? lastAssistant = null;
            var messages = new List<CodexChatMessage>();

            foreach (var line in ReadSharedLines(sessionPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

                    if (string.Equals(type, "session_meta", StringComparison.OrdinalIgnoreCase) &&
                        root.TryGetProperty("payload", out var metaPayload) &&
                        metaPayload.TryGetProperty("cwd", out var cwdProp))
                    {
                        recent.Cwd = cwdProp.GetString() ?? string.Empty;
                        continue;
                    }

                    if (!string.Equals(type, "response_item", StringComparison.OrdinalIgnoreCase) ||
                        !root.TryGetProperty("payload", out var payload) ||
                        !string.Equals(payload.TryGetProperty("type", out var itemType) ? itemType.GetString() : null, "message", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var role = payload.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : null;
                    var phase = payload.TryGetProperty("phase", out var phaseProp) ? phaseProp.GetString() : null;
                    var text = ExtractMessageText(payload);
                    if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        text = NormalizeUserMessageText(text);
                    }

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) && !LooksLikeMetaMessage(text))
                    {
                        firstUser ??= TrimForLog(text, 220);
                        messages.Add(new CodexChatMessage
                        {
                            Role = "user",
                            Text = TrimForLog(text, 4_000),
                            Status = "completed",
                            CreatedAtUtc = updatedAtUtc
                        });
                    }
                    else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(phase, "commentary", StringComparison.OrdinalIgnoreCase))
                    {
                        lastAssistant = TrimForLog(text, 320);
                        messages.Add(new CodexChatMessage
                        {
                            Role = "assistant",
                            Text = TrimForLog(text, 6_000),
                            Status = "completed",
                            CreatedAtUtc = updatedAtUtc
                        });
                    }
                }
                catch
                {
                    // Active Codex JSONL files can contain partial lines while a turn is still running.
                }
            }

            recent.UserPreview = firstUser ?? string.Empty;
            recent.AssistantPreview = lastAssistant ?? string.Empty;
            recent.Messages = messages.TakeLast(24).ToList();
            recent.Preview = !string.IsNullOrWhiteSpace(lastAssistant)
                ? lastAssistant
                : (!string.IsNullOrWhiteSpace(firstUser) ? firstUser : recent.Title);
            return IsUsefulRecentChat(recent) ? recent : null;
        }
        catch
        {
            recent.Preview = recent.Title;
            return IsUsefulRecentChat(recent) ? recent : null;
        }
    }

    private static IEnumerable<string> ReadSharedLines(string sessionPath)
    {
        using var stream = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static string ExtractMessageText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            var text = TryGetContentText(item);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(text);
        }

        return builder.ToString().Trim();
    }

    private static string? TryGetContentText(JsonElement item)
    {
        if (item.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
        {
            return textProp.GetString();
        }

        return null;
    }

    private static bool LooksLikeMetaMessage(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("<", StringComparison.Ordinal) ||
               trimmed.StartsWith("# AGENTS.md instructions", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<environment_context>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<permissions instructions>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<app-context>", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeUserMessageText(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        const string marker = "User request:";
        if (trimmed.StartsWith("You are running inside the MasterApp Codex panel.", StringComparison.OrdinalIgnoreCase))
        {
            var markerIndex = trimmed.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                return trimmed[(markerIndex + marker.Length)..].Trim();
            }
        }

        return trimmed;
    }

    private static bool IsUsefulRecentChat(CodexBrokerService.CodexRecentChat recent)
    {
        if (!IsRecentTimestampPlausible(recent.UpdatedAtUtc))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(recent.UserPreview) || !string.IsNullOrWhiteSpace(recent.AssistantPreview))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(recent.Cwd) || !string.Equals(recent.Title, "Untitled chat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecentTimestampPlausible(DateTimeOffset value)
    {
        if (value == default)
        {
            return false;
        }

        var utc = value.ToUniversalTime();
        return utc.Year >= 2024 && utc <= DateTimeOffset.UtcNow.AddDays(1);
    }

    private static string TrimForLog(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + " ...";
    }
}
