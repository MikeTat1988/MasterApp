using MasterApp.Models;
using MasterApp.Storage;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MasterApp.Hosting;

public sealed partial class CodexBrokerService
{
    private IReadOnlyList<CodexRecentChat> GetRecentChats(int maxItems)
    {
        lock (_gate)
        {
            return _runtimeState.Sessions
                .Where(session => session.Messages.Count > 0 && IsRecentTimestampPlausible(session.UpdatedAtUtc))
                .OrderByDescending(session => session.UpdatedAtUtc)
                .Take(Math.Max(1, maxItems))
                .Select(session => new CodexRecentChat
                {
                    Id = session.Id,
                    Title = string.IsNullOrWhiteSpace(session.Title) ? "Untitled chat" : session.Title,
                    UpdatedAtUtc = session.UpdatedAtUtc,
                    Cwd = session.WorkspacePath,
                    Preview = session.Messages.LastOrDefault(message => string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))?.Text
                        ?? session.Messages.LastOrDefault()?.Text
                        ?? string.Empty,
                    UserPreview = session.Messages.FirstOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Text ?? string.Empty,
                    AssistantPreview = session.Messages.LastOrDefault(message => string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))?.Text ?? string.Empty,
                    Messages = session.Messages
                        .Select(message => new CodexChatMessage
                        {
                            Id = message.Id,
                            Role = message.Role,
                            Text = message.Text,
                            Status = message.Status,
                            RunId = message.RunId,
                            CreatedAtUtc = message.CreatedAtUtc
                        })
                        .ToList()
                })
                .ToArray();
        }
    }

    private void AppendUserMessageToCurrentSession(CodexChatRun run)
    {
        lock (_gate)
        {
            var session = EnsureCurrentSession_NoLock(run, createIfMissing: true);
            if (session is null)
            {
                return;
            }

            if (session.Messages.Count > 0)
            {
                var last = session.Messages[^1];
                if (string.Equals(last.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(last.RunId, run.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(last.Text, run.Prompt, StringComparison.Ordinal))
                {
                    return;
                }
            }

            session.Messages.Add(new CodexChatMessage
            {
                Role = "user",
                Text = run.Prompt,
                Status = "completed",
                RunId = run.Id,
                CreatedAtUtc = run.StartedAtUtc
            });
            TouchSession_NoLock(session, run);
            TrimSessions_NoLock();
            PersistRuntimeState_NoLock();
        }
    }

    private void AppendAssistantMessageToCurrentSession(CodexChatRun run, string text, string status = "completed")
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        lock (_gate)
        {
            var session = EnsureCurrentSession_NoLock(run, createIfMissing: true);
            if (session is null)
            {
                return;
            }

            if (session.Messages.Count > 0)
            {
                var last = session.Messages[^1];
                if (string.Equals(last.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(last.RunId, run.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(last.Text, trimmed, StringComparison.Ordinal))
                {
                    last.Status = status;
                    last.CreatedAtUtc = run.CompletedAtUtc ?? run.UpdatedAtUtc;
                    TouchSession_NoLock(session, run);
                    PersistRuntimeState_NoLock();
                    return;
                }
            }

            session.Messages.Add(new CodexChatMessage
            {
                Role = "assistant",
                Text = trimmed,
                Status = status,
                RunId = run.Id,
                CreatedAtUtc = run.CompletedAtUtc ?? run.UpdatedAtUtc
            });
            TouchSession_NoLock(session, run);
            TrimSessions_NoLock();
            PersistRuntimeState_NoLock();
        }
    }

    private string GetCurrentSessionId()
    {
        lock (_gate)
        {
            return _runtimeState.CurrentSessionId;
        }
    }

    private IReadOnlyList<CodexChatMessage> GetConversationMessages(CodexChatRun run, int maxMessages)
    {
        lock (_gate)
        {
            var session = EnsureCurrentSession_NoLock(run, createIfMissing: false);
            if (session is null || session.Messages.Count == 0)
            {
                return Array.Empty<CodexChatMessage>();
            }

            return session.Messages
                .TakeLast(Math.Max(1, maxMessages))
                .Select(message => new CodexChatMessage
                {
                    Id = message.Id,
                    Role = message.Role,
                    Text = message.Text,
                    Status = message.Status,
                    RunId = message.RunId,
                    CreatedAtUtc = message.CreatedAtUtc
                })
                .ToArray();
        }
    }

    private void ResetCurrentSession()
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(_runtimeState.CurrentSessionId))
            {
                var session = _runtimeState.Sessions.FirstOrDefault(item =>
                    string.Equals(item.Id, _runtimeState.CurrentSessionId, StringComparison.OrdinalIgnoreCase));
                if (session is not null && session.Messages.Count == 0)
                {
                    _runtimeState.Sessions.Remove(session);
                }
            }

            _runtimeState.CurrentSessionId = string.Empty;
            TrimSessions_NoLock();
            PersistRuntimeState_NoLock();
        }
    }

    private CodexChatSession? EnsureCurrentSession_NoLock(CodexChatRun run, bool createIfMissing)
    {
        CodexChatSession? session = null;
        if (!string.IsNullOrWhiteSpace(_runtimeState.CurrentSessionId))
        {
            session = _runtimeState.Sessions.FirstOrDefault(item =>
                string.Equals(item.Id, _runtimeState.CurrentSessionId, StringComparison.OrdinalIgnoreCase));
        }

        if (session is null && createIfMissing)
        {
            session = new CodexChatSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = BuildSessionTitle(run.Prompt),
                WorkspacePath = run.WorkspacePath,
                Provider = run.Provider,
                Model = run.Model,
                CreatedAtUtc = run.StartedAtUtc,
                UpdatedAtUtc = run.StartedAtUtc
            };
            _runtimeState.CurrentSessionId = session.Id;
            _runtimeState.Sessions.Add(session);
        }

        if (session is not null)
        {
            if (string.IsNullOrWhiteSpace(session.Title))
            {
                session.Title = BuildSessionTitle(run.Prompt);
            }

            if (string.IsNullOrWhiteSpace(session.WorkspacePath))
            {
                session.WorkspacePath = run.WorkspacePath;
            }

            session.Provider = run.Provider;
            session.Model = run.Model;
        }

        return session;
    }

    private void TouchSession_NoLock(CodexChatSession session, CodexChatRun run)
    {
        session.Title = string.IsNullOrWhiteSpace(session.Title)
            ? BuildSessionTitle(run.Prompt)
            : session.Title;
        session.WorkspacePath = string.IsNullOrWhiteSpace(session.WorkspacePath) ? run.WorkspacePath : session.WorkspacePath;
        session.Provider = run.Provider;
        session.Model = run.Model;
        session.UpdatedAtUtc = run.CompletedAtUtc ?? run.UpdatedAtUtc;
        session.Messages = session.Messages
            .OrderBy(message => message.CreatedAtUtc)
            .TakeLast(80)
            .ToList();
        _runtimeState.Sessions = _runtimeState.Sessions
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToList();
        _runtimeState.CurrentSessionId = session.Id;
    }

    private void TrimSessions_NoLock()
    {
        var limit = Math.Max(1, _context.Settings.CodexHistoryLimit);
        _runtimeState.Sessions = _runtimeState.Sessions
            .Where(session => session.Messages.Count > 0 || string.Equals(session.Id, _runtimeState.CurrentSessionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.UpdatedAtUtc)
            .Take(limit)
            .ToList();
    }

    private static string BuildSessionTitle(string prompt)
    {
        var text = (prompt ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Untitled chat";
        }

        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length <= 48 ? text : text[..48].TrimEnd() + " ...";
    }

    private CodexRecentChat? ReadRecentChat(SessionIndexEntry entry)
    {
        var sessionPath = FindSessionFile(entry.Id);
        var recent = new CodexRecentChat
        {
            Id = entry.Id,
            Title = string.IsNullOrWhiteSpace(entry.ThreadName) ? "Untitled chat" : entry.ThreadName!,
            UpdatedAtUtc = entry.UpdatedAt,
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

            foreach (var line in File.ReadLines(sessionPath))
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
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) && !LooksLikeMetaMessage(text))
                    {
                        firstUser ??= TrimForLog(text, 220);
                    }
                    else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(phase, "commentary", StringComparison.OrdinalIgnoreCase))
                    {
                        lastAssistant = TrimForLog(text, 320);
                    }
                }
                catch
                {
                    // ignore malformed session lines
                }
            }

            recent.UserPreview = firstUser ?? string.Empty;
            recent.AssistantPreview = lastAssistant ?? string.Empty;
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

    private string? FindSessionFile(string sessionId)
    {
        if (!Directory.Exists(_codexSessionsDirectory))
        {
            return null;
        }

        return Directory.GetFiles(_codexSessionsDirectory, $"*{sessionId}*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();
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
            if (!item.TryGetProperty("text", out var textProp))
            {
                continue;
            }

            var text = textProp.GetString();
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

    private static bool LooksLikeMetaMessage(string text)
    {
        return text.TrimStart().StartsWith("<", StringComparison.Ordinal);
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

    private static bool IsUsefulRecentChat(CodexRecentChat recent)
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

    private static string TrimForLog(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + " ...";
    }

    private static bool IsTerminal(string status)
    {
        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "restart-scheduled", StringComparison.OrdinalIgnoreCase);
    }

    private void RecoverInterruptedRun()
    {
        lock (_gate)
        {
            if (_runtimeState.ActiveRun is null)
            {
                if (_runtimeState.PendingApproval is not null)
                {
                    _pendingApprovalWaits.Remove(_runtimeState.PendingApproval.Id);
                    _runtimeState.PendingApproval = null;
                    PersistRuntimeState_NoLock();
                }

                return;
            }

            if (IsTerminal(_runtimeState.ActiveRun.Status))
            {
                if (_runtimeState.PendingApproval is not null &&
                    string.Equals(_runtimeState.PendingApproval.RunId, _runtimeState.ActiveRun.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingApprovalWaits.Remove(_runtimeState.PendingApproval.Id);
                    _runtimeState.PendingApproval = null;
                    PersistRuntimeState_NoLock();
                }

                return;
            }

            var wasAwaitingApproval = _runtimeState.PendingApproval is not null &&
                                      string.Equals(_runtimeState.PendingApproval.RunId, _runtimeState.ActiveRun.Id, StringComparison.OrdinalIgnoreCase);
            var message = wasAwaitingApproval
                ? "Previous session was interrupted while waiting for approval. Start a new session to continue."
                : "Previous session was interrupted before completion. Start a new session to continue.";

            var recoveredRun = MarkRunInterrupted(_runtimeState.ActiveRun, message);
            if (_runtimeState.PendingApproval is not null)
            {
                _pendingApprovalWaits.Remove(_runtimeState.PendingApproval.Id);
            }

            _runtimeState.ActiveRun = Clone(recoveredRun);
            _runtimeState.PendingApproval = null;
            PersistRuntimeState_NoLock();
        }
    }

    private CodexChatRun MarkRunInterrupted(CodexChatRun run, string message)
    {
        var recovered = Clone(run);
        recovered.Status = "stopped";
        recovered.Summary = "Stopped.";
        recovered.FailureMessage = message;
        recovered.UpdatedAtUtc = DateTimeOffset.UtcNow;
        recovered.CompletedAtUtc ??= DateTimeOffset.UtcNow;
        AppendLog(recovered, "system", message);
        return recovered;
    }

    private void AppendLog(CodexChatRun run, string kind, string line)
    {
        var formatted = $"[{DateTimeOffset.Now:HH:mm:ss}] [{kind}] {line}";
        run.LogLines.Add(formatted);
        if (run.LogLines.Count > MaxLiveLogLines)
        {
            run.LogLines.RemoveRange(0, run.LogLines.Count - MaxLiveLogLines);
        }

        _context.Log.Codex("CodexBrokerService", formatted);
    }

    private void UpdateRun(CodexChatRun run, string status, string logMessage)
    {
        run.Status = status;
        run.UpdatedAtUtc = DateTimeOffset.UtcNow;
        AppendLog(run, "system", logMessage);
        PublishRun(run);
    }

    private static void TrimForPersistence(CodexChatRun run)
    {
        run.LogLines = run.LogLines.TakeLast(MaxPersistedLogLines).ToList();
        foreach (var item in run.ApprovalHistory)
        {
            item.LogLines = item.LogLines.TakeLast(MaxApprovalLogLines).ToList();
            item.OutputSummary = TrimForLog(item.OutputSummary, 2_000);
        }

        run.ResponseText = TrimForLog(run.ResponseText, 20_000);
        run.Summary = TrimForLog(run.Summary, 1_000);
        if (run.BuildResult is not null)
        {
            run.BuildResult.LogLines = run.BuildResult.LogLines.TakeLast(MaxPersistedLogLines).ToList();
            run.BuildResult.Summary = TrimForLog(run.BuildResult.Summary, 2_000);
        }
    }

    private static CodexChatRun Clone(CodexChatRun run)
    {
        var json = JsonSerializer.Serialize(run, JsonOptions.Default);
        return JsonSerializer.Deserialize<CodexChatRun>(json, JsonOptions.Default) ?? new CodexChatRun();
    }

    private static CodexRuntimeState Clone(CodexRuntimeState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions.Default);
        return JsonSerializer.Deserialize<CodexRuntimeState>(json, JsonOptions.Default) ?? new CodexRuntimeState();
    }

    private static CodexApprovalRequest Clone(CodexApprovalRequest approval)
    {
        var json = JsonSerializer.Serialize(approval, JsonOptions.Default);
        return JsonSerializer.Deserialize<CodexApprovalRequest>(json, JsonOptions.Default) ?? new CodexApprovalRequest();
    }

    private sealed record PendingApprovalContext(CodexApprovalRequest Request, TaskCompletionSource<CodexApprovalDecisionRequest> Completion);

    private sealed record SessionIndexEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("thread_name")]
        public string? ThreadName { get; init; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; init; }
    }

    private sealed record CodexModelsCache
    {
        public List<CodexModelEntry>? Models { get; init; }
    }

    private sealed record CodexModelEntry
    {
        public string? Slug { get; init; }
        public string? DisplayName { get; init; }
        public List<CodexReasoningLevel>? SupportedReasoningLevels { get; init; }
    }

    private sealed record CodexReasoningLevel
    {
        public string? Effort { get; init; }
    }
}
