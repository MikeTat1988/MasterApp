using System.Text.RegularExpressions;

namespace MasterApp.Hosting;

public sealed partial class CodexBrokerService
{
    private const string TaskModeAuto = "auto";
    private const string TaskModeAction = "action";
    private const string TaskModeInvestigate = "investigate";
    private const string TaskModeCode = "code";
    private const string TaskModeAsk = "ask";

    private static readonly Regex ActionIntentPattern = new(
        @"\b(run|execute|launch|start|open|show|display|restart|relaunch|pop(?:\s|-)?up|bring up)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ActionTargetPattern = new(
        @"\b(script|bat|window|dialog|message box|browser|masterapp|hello world|app|desktop|computer)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InvestigateIntentPattern = new(
        @"\b(why|investigate|debug|diagnose|inspect|find|search|what happened|check why|root cause|analy[sz]e|failed|failure|broken|not working|didn(?:'|’)t work|did not work)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CodeIntentPattern = new(
        @"\b(fix|change|edit|update|modify|implement|add|remove|refactor|rewrite|patch|create)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CodeArtifactPattern = new(
        @"\b(file|files|repo|repository|workspace|component|function|class|endpoint|api|bug|prompt|diff|logic|code)\b|\.cs\b|\.js\b|\.ts\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AskIntentPattern = new(
        @"\b(explain|answer|tell me|what is|how does|summari[sz]e|status|help)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex QuestionPattern = new(
        @"[?]|(?:^|\s)(why|what|how|when|where)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ShortImperativePattern = new(
        @"^\s*(run|open|show|start|launch|restart|execute)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] SupportedTaskModes =
    {
        TaskModeAuto,
        TaskModeAction,
        TaskModeInvestigate,
        TaskModeCode,
        TaskModeAsk
    };

    private static IReadOnlyList<object> GetAvailableTaskModes()
    {
        return new object[]
        {
            new { slug = TaskModeAuto, displayName = "Auto" },
            new { slug = TaskModeAction, displayName = "Action" },
            new { slug = TaskModeInvestigate, displayName = "Investigate" },
            new { slug = TaskModeCode, displayName = "Code" },
            new { slug = TaskModeAsk, displayName = "Ask" }
        };
    }

    private static string NormalizeRequestedMode(string? requestedMode)
    {
        var normalized = (requestedMode ?? string.Empty).Trim().ToLowerInvariant();
        return SupportedTaskModes.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : TaskModeAuto;
    }

    public static CodexTaskModePreview PreviewTaskMode(string prompt, string? requestedMode = null)
    {
        var normalizedMode = NormalizeRequestedMode(requestedMode);
        var selection = ResolveTaskModeSelection(prompt, normalizedMode);
        return new CodexTaskModePreview(
            normalizedMode,
            selection.Mode,
            selection.Source,
            selection.Confidence,
            selection.Reason);
    }

    private static CodexTaskModeSelection ResolveTaskModeSelection(string prompt, string requestedMode)
    {
        var normalizedMode = NormalizeRequestedMode(requestedMode);
        if (!string.Equals(normalizedMode, TaskModeAuto, StringComparison.OrdinalIgnoreCase))
        {
            return new CodexTaskModeSelection(
                normalizedMode,
                "manual",
                1.0,
                $"Manual mode override selected: {normalizedMode}.");
        }

        var text = (prompt ?? string.Empty).Trim();
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [TaskModeAction] = 0,
            [TaskModeInvestigate] = 0,
            [TaskModeCode] = 0,
            [TaskModeAsk] = 0
        };

        var reasons = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [TaskModeAction] = new(),
            [TaskModeInvestigate] = new(),
            [TaskModeCode] = new(),
            [TaskModeAsk] = new()
        };

        AddScoreIfMatch(scores, reasons, TaskModeAction, ActionIntentPattern, text, 5, "contains explicit action verbs");
        AddScoreIfMatch(scores, reasons, TaskModeAction, ActionTargetPattern, text, 2, "mentions a local thing to open or run");
        AddScoreIfMatch(scores, reasons, TaskModeInvestigate, InvestigateIntentPattern, text, 5, "contains investigation wording");
        AddScoreIfMatch(scores, reasons, TaskModeCode, CodeIntentPattern, text, 4, "contains code-change wording");
        AddScoreIfMatch(scores, reasons, TaskModeCode, CodeArtifactPattern, text, 2, "mentions files or code artifacts");
        AddScoreIfMatch(scores, reasons, TaskModeAsk, AskIntentPattern, text, 4, "contains ask/explain wording");

        if (QuestionPattern.IsMatch(text))
        {
            AddScore(scores, reasons, TaskModeAsk, 2, "looks like a question");
            AddScore(scores, reasons, TaskModeInvestigate, 1, "question form often implies diagnosis");
        }

        if (ShortImperativePattern.IsMatch(text) && CountWords(text) <= 12)
        {
            AddScore(scores, reasons, TaskModeAction, 2, "short imperative request");
        }

        var ranked = scores
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var top = ranked[0];
        var second = ranked.Length > 1 ? ranked[1] : default;
        if (top.Value <= 0)
        {
            var fallbackMode = QuestionPattern.IsMatch(text) || CountWords(text) <= 3
                ? TaskModeAsk
                : TaskModeCode;

            return new CodexTaskModeSelection(
                fallbackMode,
                "auto",
                0.55,
                fallbackMode == TaskModeAsk
                    ? "No strong code or action signal; defaulted to ask mode."
                    : "No strong action or investigation signal; defaulted to code mode for the workspace.");
        }

        var confidence = ComputeTaskModeConfidence(top.Value, second.Value);
        var reason = reasons[top.Key].Count > 0
            ? string.Join("; ", reasons[top.Key].Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
            : "Best heuristic match.";

        return new CodexTaskModeSelection(top.Key, "auto", confidence, reason);
    }

    private static double ComputeTaskModeConfidence(int topScore, int secondScore)
    {
        var gap = Math.Max(0, topScore - secondScore);
        if (topScore >= 7 && gap >= 3)
        {
            return 0.97;
        }

        if (topScore >= 5 && gap >= 2)
        {
            return 0.88;
        }

        if (topScore >= 4)
        {
            return 0.78;
        }

        if (topScore >= 2)
        {
            return 0.64;
        }

        return 0.55;
    }

    private static void AddScoreIfMatch(
        IDictionary<string, int> scores,
        IDictionary<string, List<string>> reasons,
        string mode,
        Regex pattern,
        string text,
        int points,
        string reason)
    {
        if (pattern.IsMatch(text))
        {
            AddScore(scores, reasons, mode, points, reason);
        }
    }

    private static void AddScore(
        IDictionary<string, int> scores,
        IDictionary<string, List<string>> reasons,
        string mode,
        int points,
        string reason)
    {
        scores[mode] += points;
        reasons[mode].Add(reason);
    }

    private static int CountWords(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private sealed record CodexTaskModeSelection(string Mode, string Source, double Confidence, string Reason);

    public sealed record CodexTaskModePreview(
        string RequestedMode,
        string Mode,
        string Source,
        double Confidence,
        string Reason);
}
