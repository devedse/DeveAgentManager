using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using DeveAgentManager.Contracts;

namespace DeveAgentManager.Core.Agents;

public sealed record AgentScreenDetection(AgentActivityStatus Status, string Reason, string Rule,
    string? Evidence = null, bool QuestionWaiting = false, string? CompletionEvidence = null, int? BackgroundTaskCount = null);

/// <summary>Named rules examine live controls near the current prompt, rather than arbitrary conversation text.</summary>
public static class AgentScreenDetector
{
    private sealed record Rule(string Id, int Priority, string Region, Func<Context, AgentScreenDetection?> Match);
    private sealed record Context(string Harness, string[] Lines, int Prompt, string Before, string After, string Tail, DateTime Now);
    // Rule patterns are fixed. Keep them instead of thrashing the process-wide 15-entry Regex cache every poll.
    private static readonly ConcurrentDictionary<string, Regex> Patterns = new();
    private static Regex Pattern(string pattern) => Patterns.GetOrAdd(pattern,
        p => new Regex(p, RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
    private static bool Has(string text, string pattern) => Pattern(pattern).IsMatch(text);
    private static string? Find(string text, string pattern) => Pattern(pattern).Match(text) is { Success: true } m ? m.Value.Trim() : null;
    private static string Evidence(string value) => value.Length <= 240 ? value : value[..240] + "…";
    private const string PromptPattern = @"^\s*(?:[›❯](?:\s|$)|›[⠁⠂⠄⠈⠐⠠⡀⢀])";
    private static string? CodexCompletion(string text) => text.Split('\n').LastOrDefault(line =>
        Has(line, @"^\s*Worked for [0-9][^\n]*[·•]\s*(?:[0-9]{1,2}:[0-9]{2}|[A-Z][a-z]{2}\s+[0-9]{1,2}\s+at\s+[0-9]{1,2}:[0-9]{2})[^\n]*$"))?.Trim();
    private static readonly Rule[] Rules =
    [
        new("codex-transcript-viewer", 110, "viewer controls", c => c.Harness == "codex"
            && (Has(c.After, "↑/↓ to scroll") && Has(c.After, "q to quit") && Has(c.After, "pgup/pgdn|home/end")
                || Has(c.Tail, "↓ Back to bottom") && Has(c.Tail, "enter/esc latest"))
            ? new(AgentActivityStatus.Unknown, "Codex is showing conversation history; this view does not establish live activity.", "") : null),
        new("usage-limit", 100, "current error", c => Limit(c)),
        new("live-input-controls", 90, "below current prompt / modal controls", Blocked),
        new("codex-live-activity", 80, "last activity above current prompt", CodexWorking),
        new("claude-live-activity", 80, "live footer", ClaudeWorking),
        new("pi-live-activity", 80, "live footer", c => c.Harness == "pi" && Find(c.Tail, @"^\s*─+\s+[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏] Working[^\n]*") is { } line
            ? new(AgentActivityStatus.Working, "Pi is working.", "", Evidence(line)) : null),
        new("codex-ready-prompt", 50, "composer and latest response", CodexReady),
        new("claude-ready-prompt", 50, "prompt box and completion footer", ClaudeReady),
        new("pi-ready-prompt", 50, "empty prompt box and usage footer", PiReady),
    ];

    private static readonly Rule[] OrderedRules = Rules.OrderByDescending(r => r.Priority).ToArray();

    private static AgentScreenDetection? Limit(Context context)
    {
        var read = UsageLimitParser.Inspect(context.Harness, string.Join('\n', context.Lines), "UTC", context.Now);
        return !read.Available ? new(AgentActivityStatus.Unknown, read.Problem!, "usage-limit-unavailable")
            : read.Limit is { } limit ? new(AgentActivityStatus.UsageLimited, "The harness has reached its usage allowance.", "", Evidence(limit.Message)) : null;
    }

    public static AgentScreenDetection Detect(string harness, string screen, DateTime now)
    {
        try { return DetectScreen(harness, screen, now); }
        catch (RegexMatchTimeoutException)
        {
            // A pathological screen must not discard the other agents in the Runner's observation batch.
            return new(AgentActivityStatus.Unknown, "Status matching timed out for this screen; it will be checked again.", $"{harness}/matching-timeout");
        }
    }

    private static AgentScreenDetection DetectScreen(string harness, string screen, DateTime now)
    {
        var lines = screen.Replace('\u00a0', ' ').Split('\n').TakeLast(100).ToArray();
        var prompt = Array.FindLastIndex(lines, l => Has(l, PromptPattern));
        var before = string.Join('\n', prompt >= 0 ? lines.Take(prompt) : lines);
        var after = prompt >= 0 ? string.Join('\n', lines.Skip(prompt + 1)) : "";
        var tail = string.Join('\n', lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(15));
        var c = new Context(harness, lines, prompt, before, after, tail, now);
        foreach (var rule in OrderedRules)
            if (rule.Match(c) is { } match) return match with { Rule = $"{harness}/{rule.Id} · {rule.Region}", BackgroundTaskCount = BackgroundCount(c) };
        return new(AgentActivityStatus.Unknown, "The current screen has no recognized live status. No completion is inferred.", $"{harness}/unrecognized-screen");
    }

    // Only the live controls below the composer establish background activity. Historical
    // "shell still running" completion text and commands typed into the composer are not evidence.
    private static int? BackgroundCount(Context c)
    {
        if (c.Prompt < 0) return null;
        var controls = c.After;
        if (c.Harness == "claude")
        {
            var border = Array.FindIndex(c.Lines, c.Prompt + 1, l => Has(l, @"^\s*[─━]{3}"));
            if (border < 0) return null;
            controls = string.Join('\n', c.Lines.Skip(border + 1));
        }
        var pattern = c.Harness switch
        {
            "claude" => @"(?:^|[·])\s*([1-9][0-9]*)\s+shells?\b",
            "codex" => @"^\s*([1-9][0-9]*)\s+background terminals? running\b",
            _ => null,
        };
        if (pattern is null) return null;
        var match = Regex.Match(controls, pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : null;
    }

    private static AgentScreenDetection? Blocked(Context c)
    {
        // Only control hints outside the editable prompt. A quoted approval in history is not a blocker.
        var controls = c.Prompt >= 0 ? c.After : c.Tail;
        // Approval option cursors use the same marker as the composer. Bound this check to the current panel.
        var panelStart = Array.FindLastIndex(c.Lines, l => Has(l, @"^\s*[─━]{3}"));
        var panel = string.Join('\n', c.Lines.Skip(Math.Max(0, panelStart + 1)));
        if (c.Harness is "claude" or "codex" && Has(panel, @"(?im)^\s*(?:Do you want to proceed\?|Would you like to run the following command\?|Do you trust .+\?)")
            && Has(panel, @"(?im)^\s*[›❯]?\s*[123]\.\s*(?:Yes|No|Trust)\b")
            && (Has(panel, @"(?i)(esc to cancel|enter to confirm|tab to amend|ctrl\+e to explain)") || Has(panel, @"(?i)Bash command")))
            return new(AgentActivityStatus.NeedsInput, "The agent is waiting for command approval.", "", "Approval options and live controls are visible.");
        var hint = Find(controls, @"(?i)^\s*(?:[›❯?]\s*)?(?:press )?(?:enter to (?:submit (?:all|answer)|confirm|select)|.*(?:esc to cancel|tab/arrow keys to navigate))[^\n]*$");
        var question = Has(controls, @"(?i)enter to submit (?:answer|all)");
        var permission = Has(c.Tail, @"(?i)(allow (?:this |the )?command\?|do you (?:trust|want to (?:allow|proceed))|waiting for permission|permission required)");
        if (hint is not null && (question || permission || Has(controls, @"(?i)(enter to confirm|enter to select|tab/arrow keys to navigate)")))
            return new(AgentActivityStatus.NeedsInput, question ? "The agent is waiting for your answer." : "The agent is waiting for a selection or approval.", "", Evidence(hint));
        if (Has(c.Tail, @"(?i)Update available!") && Has(c.Tail, @"(?i)Update now") && Has(c.Tail, @"(?i)Press enter to continue\s*$"))
            return new(AgentActivityStatus.NeedsInput, "A harness update prompt needs a selection.", "", "Update now / Skip; press Enter to continue.");
        return null;
    }

    private static AgentScreenDetection? CodexWorking(Context c)
    {
        if (c.Harness != "codex" || c.Prompt < 0) return null;
        var lines = c.Before.Split('\n');
        var start = Array.FindLastIndex(lines, l => Has(l, @"^\s*(?:[•◦]\s+)?[^\s›❯•◦■✗✓─][^\n]+ \([0-9]"));
        if (start < 0) return null;
        var line = lines[start].Trim();
        // A later tool result, response or submitted prompt invalidates a historical activity line.
        if (lines.Skip(start + 1).Any(l => Has(l, @"^\s*(?:[›❯](?![⠁⠂⠄⠈⠐⠠⡀⢀])|[■✗✓])|^\s*Worked for [0-9]|^\s*[•◦] (?!Queued follow-up inputs|Messages to be submitted)"))) return null;
        var knownTruncated = Has(line, @"^(?:[•◦]\s+)?(?:Working|Compacting context|Waiting for background terminal) \([0-9]");
        var timed = Has(line, @"^(?:[•◦]\s+)?[^\n]+ \((?:[0-9]+[hm] )*[0-9]+s(?: • [^\n]*to interrupt)?\)(?: · [^\n]*)?$");
        if (!knownTruncated && !timed) return null;
        if (Has(line, @"^(?:[•◦]\s+)?(?:Reconnect failed|Compacted context|Done\b|Completed\b|Finished\b|Ran |Edited |Added |Deleted |Updated |Failed \(exit)")) return null;
        var waiting = Has(string.Join('\n', lines.Skip(start)), @"(?i)(Queued follow-up inputs|shift\+← to answer|shift\+left to answer)");
        return new(AgentActivityStatus.Working, line.Contains("Compacting context") ? "Codex is compacting context."
            : line.Contains("Waiting for background terminal") ? "Codex is waiting for a background command." : "Codex is working.", "", Evidence(line), waiting,
            CompletionEvidence: CodexCompletion(c.Before));
    }

    private static AgentScreenDetection? ClaudeWorking(Context c)
    {
        if (c.Harness != "claude") return null;
        var recent = AfterLastSubmitted(c).Split('\n');
        var spinner = Array.FindLastIndex(recent, l => Has(l, @"^\s*[✢✳✶✻✽*·] \S[^\n]*… (?:\([0-9]+[smh]|$)[^\n]*")
            || Has(l, @"^\s*[✢✳✶✻✽*·] Waiting for [1-9][0-9]* background agents? to finish\s*$"));
        // The optional feedback survey shares the assistant's bullet, but can appear while
        // work continues. Exclude only its complete panel, including wrapped choices;
        // real responses, submitted prompts and completion footers still invalidate a spinner.
        var following = spinner < 0 ? "" : string.Join('\n', recent.Skip(spinner + 1));
        following = Regex.Replace(following,
            @"^\s*●\s+How\s+is\s+Claude\s+doing\s+this\s+session\?\s*\(optional\)\s+1:\s*Bad\s+2:\s*Fine\s+3:\s*Good\s+0:\s*Dismiss[^\S\n]*(?=\n|$)", "",
            RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var stale = spinner >= 0 && Has(following, @"^\s*[●❯]|^\s*[✢✳✶✻✽*·] .+ for [0-9]+[smh]");
        var line = spinner >= 0 && !stale ? recent[spinner].Trim()
            : Find(c.After, @"^\s*[⏵⏸][^\n]*esc to interrupt[^\n]*");
        return line is null ? null : new(AgentActivityStatus.Working, "Claude Code is working.", "", Evidence(line));
    }

    private static string AfterLastSubmitted(Context c)
    {
        var lines = c.Before.Split('\n');
        var submitted = Array.FindLastIndex(lines, l => Has(l, PromptPattern));
        return string.Join('\n', lines.Skip(submitted + 1));
    }

    private static AgentScreenDetection? CodexReady(Context c)
    {
        if (c.Harness != "codex" || c.Prompt < 0) return null;
        var content = AfterLastSubmitted(c);
        var completed = CodexCompletion(content);
        // The shortcuts hint can disappear while editing or be replaced by warning counters.
        var composerFooter = Has(c.After, @"\? for shortcuts")
            || Has(c.After, @"(?i)^\s*(?:GPT-\S+|o[134](?:-\S+)?)\s+(?:low|medium|high|xhigh|minimal|default)\b");
        if (!composerFooter && completed is null) return null;
        var current = c.Lines[c.Prompt].Trim();
        if (completed is not null)
            return new(AgentActivityStatus.Ready, "Codex finished its turn and its prompt is ready for input.", "", Evidence(completed), CompletionEvidence: completed);
        if (current != "›" && current != "› Ask Codex to do anything")
            return new(AgentActivityStatus.Ready, "Codex's prompt contains a draft; it is ready for input.", "", Evidence(current));
        var response = Find(content, @"^\s*[•] (?!Ran |Running |Edited |Added |Deleted |Updated |Waiting |Working |Compacting |Queued |The status says |You)[^\n]+$");
        var lastMarker = content.Split('\n').LastOrDefault(l => Has(l, @"^\s*[•›■]"));
        if (response is not null && lastMarker is not null && Has(lastMarker, @"^\s*• (?!Ran |Running |Edited |Added |Deleted |Updated |Waiting |Working |Compacting |Queued |The status says )"))
            return new(AgentActivityStatus.Ready, "Codex's prompt is ready for input; the visible text alone does not establish a finished turn.", "", Evidence(lastMarker));
        // The initial empty composer is ready, but a submitted prompt without a settled response is ambiguous.
        if (!Has(c.Before, @"^\s*› ") && !Has(c.Before, @"^\s*[•◦■] "))
            return new(AgentActivityStatus.Ready, "Codex's initial prompt is ready.", "", current);
        return new(AgentActivityStatus.Ready, "Codex's prompt is ready for input; no new completion is inferred.", "", Evidence(current));
    }

    private static AgentScreenDetection? ClaudeReady(Context c)
    {
        if (c.Harness != "claude" || c.Prompt < 0 || !Has(c.After, @"(?i)(shift\+tab|bypass permissions|accept edits|\? for shortcuts)")) return null;
        if (!Has(c.Before, @"^\s*[─━]{3}")) return null;
        var completion = Find(AfterLastSubmitted(c), @"^\s*[✢✳✶✻✽*·] .+ for [0-9]+[smh][^\n]*$");
        return new(AgentActivityStatus.Ready, "Claude Code's prompt is ready for input.", "", completion is null ? "Prompt box and keyboard controls are visible." : Evidence(completion),
            CompletionEvidence: completion);
    }

    private static AgentScreenDetection? PiReady(Context c)
    {
        if (c.Harness != "pi" || !(Has(c.Tail, @"^↑[0-9]+ .*↓[0-9]+") && Has(c.Tail, @"^think:")
            || Has(c.Tail, @"[0-9]+(?:\.[0-9]+)?%/[0-9].* ·|[0-9]+(?:\.[0-9]+)?%/[0-9].* •"))) return null;
        var separator = Array.FindLastIndex(c.Lines, l => Has(l, @"^─{4,}\s*$"));
        // The empty prompt box: two separators with nothing but blank (cursor) lines between them.
        var open = separator - 1;
        while (open >= 0 && string.IsNullOrWhiteSpace(c.Lines[open])) open--;
        if (separator < 1 || open < 0 || !Has(c.Lines[open], @"^─{4,}\s*$")) return null;
        var body = c.Lines.Take(open).ToArray();
        var end = Array.FindLastIndex(body, l => Has(l, @"^─{4,}\s*$"));
        var response = string.Join('\n', body.Skip(end + 1)).Trim();
        return new(AgentActivityStatus.Ready, "Pi's prompt is ready for input.", "", "Empty prompt box and usage footer are visible.",
            CompletionEvidence: response.Length > 0 ? response : null);
    }
}
