using DeveAgentManager.Core.Agents;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class UsageLimitParserTests
{
    internal static string Codex(string reset) => $"■ You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro),\nvisit https://chatgpt.com/codex/settings/usage to purchase more credits or try again\nat {reset}.\n\n› Ask Codex to do anything\n  ? for shortcuts";
    internal static string Claude(string message) => $"{message}\n\n❯ \n  bypass permissions on";
    internal static string PiQuotaScreen => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Screens", "pi-quota-exceeded.txt"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PiQuotaErrorHasNoResetTime(bool wrapped)
    {
        var screen = PiQuotaScreen;
        if (wrapped) screen = screen.Replace("quota exceeded", "quota\n exceeded")
            .Replace("/bug sends a report", "/bug sends\n a report");
        var parsed = UsageLimitParser.Parse("pi", screen, "UTC", DateTime.UtcNow);
        Assert.NotNull(parsed);
        Assert.Equal("Error: 429 quota exceeded", parsed.Message);
        Assert.Null(parsed.ResetAt);
        Assert.False(parsed.NativeAutoContinue);
        Assert.Equal("Pi did not provide a reset time.", parsed.Detail);
    }

    [Fact]
    public void PiQuotaErrorDoesNotRequireBugHintOrAnEmptyDraft()
    {
        var screen = PiQuotaScreen.Replace(" If this looks like a pi bug, /bug sends a report to the developers.", "")
            .Replace("─\n\n─", "─\nmy draft\n─");
        Assert.NotNull(UsageLimitParser.Parse("pi", screen, "UTC", DateTime.UtcNow));
    }

    [Theory]
    [InlineData("\nThe operation succeeded.")]
    [InlineData("\n$ echo done\ndone\nTook 1.5s")]
    [InlineData("\n── ⠼ Working (medium effort)... ─────")]
    public void PiHistoricalQuotaErrorIsNotAnActiveLimit(string laterOutput)
    {
        var screen = PiQuotaScreen.Replace("to the developers.", "to the developers." + laterOutput);
        Assert.Null(UsageLimitParser.Parse("pi", screen, "UTC", DateTime.UtcNow));
        Assert.NotEqual(DeveAgentManager.Contracts.AgentActivityStatus.UsageLimited,
            AgentScreenDetector.Detect("pi", screen, DateTime.UtcNow).Status);
    }

    [Theory]
    [InlineData("Error: 500 server error")]
    [InlineData("Error: 429 Too Many Requests")]
    [InlineData("The example says Error: 429 quota exceeded")]
    public void PiOnlyRecognizesTheQuotaError(string message)
    {
        var screen = PiQuotaScreen.Replace("Error: 429 quota exceeded", message);
        Assert.Null(UsageLimitParser.Parse("pi", screen, "UTC", DateTime.UtcNow));
    }

    [Fact]
    public void PiQuotaTextNeedsAComposerAndCannotComeFromItsDraft()
    {
        Assert.Null(UsageLimitParser.Parse("pi", "Error: 429 quota exceeded", "UTC", DateTime.UtcNow));
        var screen = PiQuotaScreen.Replace("Error: 429 quota exceeded", "Done.")
            .Replace("─\n\n─", "─\nError: 429 quota exceeded\n─");
        Assert.Null(UsageLimitParser.Parse("pi", screen, "UTC", DateTime.UtcNow));
    }

    [Theory]
    [InlineData("⚠ 5h limit: 48% left · /status")]
    [InlineData("⚠ Weekly 48% left")]
    public void CodexQuotaFooterDoesNotPollutePastReset(string footer)
    {
        var screen = Codex("7:23 PM").Replace("■ You've", "■ Error running remote compact task: You've")
            .Replace("\n\n›", "\n" + footer + "\n\n›");
        var now = new DateTime(2026, 10, 3, 19, 1, 0, DateTimeKind.Utc);
        var parsed = UsageLimitParser.Parse("codex", screen, "Europe/Amsterdam", now)!;
        Assert.Equal(new DateTime(2026, 10, 3, 17, 23, 0, DateTimeKind.Utc), parsed.ResetAt);
        Assert.DoesNotContain("48%", parsed.Message);
    }

    [Fact]
    public void DetectsRemoteCompactionUsageErrorAndItsReset()
    {
        var screen = Codex("7:23 PM").Replace("■ You've", "■ Error running remote compact task: You've");
        var now = new DateTime(2026, 10, 3, 16, 12, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 10, 3, 17, 23, 0, DateTimeKind.Utc), UsageLimitParser.Parse("codex", screen, "Europe/Amsterdam", now)!.ResetAt);
        Assert.Equal(DeveAgentManager.Contracts.AgentActivityStatus.UsageLimited, AgentScreenDetector.Detect("codex", screen, now).Status);
    }

    [Theory]
    [InlineData("1 background terminal running · /ps to view · /stop to close")]
    [InlineData("1 background terminal\nrunning · /ps to view · /stop to close")]
    [InlineData("2 background terminals running · /ps to view · /stop to close")]
    public void CodexResetExcludesBackgroundTerminalFooter(string footer)
    {
        var screen = Codex("Oct 2nd, 2026 12:08 AM").Replace("\n\n›", "\n\n" + footer + "\n\n›");
        var parsed = UsageLimitParser.Parse("codex", screen, "Europe/Amsterdam", new DateTime(2026, 10, 1, 20, 16, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 10, 1, 22, 8, 0, DateTimeKind.Utc), parsed!.ResetAt);
        Assert.DoesNotContain("background terminal", parsed.Message);
    }

    [Theory]
    [InlineData("codex", "Oct 1st, 2026 12:30 AM", "Europe/Amsterdam", "2026-09-30T21:26:00Z", "2026-09-30T22:30:00Z")]
    [InlineData("codex", "October 2nd, 2026 1:30 PM", "UTC", "2026-09-30T21:26:00Z", "2026-10-02T13:30:00Z")]
    [InlineData("codex", "9:37 PM", "UTC", "2026-09-29T20:47:00Z", "2026-09-29T21:37:00Z")]
    [InlineData("codex", "9:37 PM", "Europe/Amsterdam", "2026-09-29T18:47:00Z", "2026-09-29T19:37:00Z")]
    [InlineData("codex", "12:15 AM", "Europe/Amsterdam", "2026-09-29T21:50:00Z", "2026-09-29T22:15:00Z")]
    [InlineData("claude", "└ You've hit your weekly limit · resets Oct 4, 3am (Europe/Amsterdam) /\nupgrade to increase your usage limit.", "UTC", "2026-10-01T19:45:00Z", "2026-10-04T01:00:00Z")]
    [InlineData("claude", "└ You've hit your weekly limit · resets Oct 4, 3am (Europe/Amsterdam) /upgrade to increase your usage limit.", "UTC", "2026-10-01T19:45:00Z", "2026-10-04T01:00:00Z")]
    [InlineData("claude", "└ You've hit your weekly limit · resets Oct 4, 3am (Europe/Amsterdam)\n  /upgrade to increase your usage limit.", "Europe/Madrid", "2026-10-01T19:45:00Z", "2026-10-04T01:00:00Z")]
    [InlineData("claude", "You've hit your limit · resets 7am (UTC)", "Europe/Amsterdam", "2026-09-29T05:00:00Z", "2026-09-29T07:00:00Z")]
    [InlineData("claude", "⎿ Claude usage limit reached. Your limit will reset at 3pm (America/Santiago).", "UTC", "2026-09-29T16:00:00Z", "2026-09-29T18:00:00Z")]
    [InlineData("claude", "You've hit your weekly limit · resets Oct 2 at 12am (Asia/Tokyo)", "UTC", "2026-09-29T16:00:00Z", "2026-10-01T15:00:00Z")]
    [InlineData("claude", "You've hit your limit · resets Jan 1, 2027, 9am (UTC)", "UTC", "2026-12-30T16:00:00Z", "2027-01-01T09:00:00Z")]
    [InlineData("claude", "You've hit your limit · resets Jan 1, 9am (UTC)", "UTC", "2026-12-30T16:00:00Z", "2027-01-01T09:00:00Z")]
    public void ResolvesResetInMessageOrProcessTimezone(string harness, string message, string zone, string now, string expected)
    {
        var parsed = UsageLimitParser.Parse(harness, harness == "codex" ? Codex(message) : Claude(message), zone, DateTime.Parse(now).ToUniversalTime());
        Assert.NotNull(parsed);
        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), parsed.ResetAt);
    }

    [Theory]
    [InlineData("You've hit your limit · resets 2:30am (America/New_York)", "2026-03-08T05:00:00Z")]
    [InlineData("You've hit your limit · resets 1:30am (America/New_York)", "2026-11-01T04:00:00Z")]
    [InlineData("You've hit your limit · resets 3pm (CST)", "2026-09-29T13:00:00Z")]
    [InlineData("You've hit your limit · resets 3pm (Not/AZone)", "2026-09-29T13:00:00Z")]
    [InlineData("You've hit your weekly limit · resets next week", "2026-09-29T13:00:00Z")]
    [InlineData("You've hit your limit · resets 29:99", "2026-09-29T13:00:00Z")]
    public void RequiresConfirmationWhenDateOrTimezoneIsAmbiguous(string message, string now)
    {
        var parsed = UsageLimitParser.Parse("claude", Claude(message), "UTC", DateTime.Parse(now).ToUniversalTime());
        Assert.NotNull(parsed);
        Assert.Null(parsed.ResetAt);
        Assert.NotNull(parsed.Detail);
    }

    [Fact]
    public void RecognizesNativeAutoContinueSoManagerDoesNotDuplicateIt()
    {
        var parsed = UsageLimitParser.Parse("claude", Claude("Usage limit reached · continuing automatically at 3:45pm · esc to cancel"), "UTC", new DateTime(2026, 9, 29, 14, 0, 0, DateTimeKind.Utc));
        Assert.True(parsed?.NativeAutoContinue);
        Assert.NotNull(parsed?.ResetAt);
    }

    [Fact]
    public void RecognizesNativeWaitBelowThePrompt()
    {
        var screen = Claude("You've hit your limit · resets 3pm (UTC)") + "\nUsage limit reached · continuing automatically at 3pm · esc to cancel";
        Assert.True(UsageLimitParser.Parse("claude", screen, "UTC", new DateTime(2026, 9, 29, 14, 0, 0, DateTimeKind.Utc))?.NativeAutoContinue);
    }

    [Theory]
    [InlineData("› Explain You've hit your usage limit and try again at 9pm\n› Ask Codex to do anything")]
    [InlineData("• The status says You've hit your usage limit. Try again at 9pm.\n› Ask Codex to do anything")]
    [InlineData("⚠ 5h 10% left\n› Ask Codex to do anything")]
    [InlineData("■ You've hit your usage limit. Try again at 9pm.\n› continue\n• Done.\n› Ask Codex to do anything")]
    [InlineData("■ You've hit your usage limit. Try again at 9pm.\n• Working (1s • esc to interrupt)\n› Ask Codex to do anything")]
    public void IgnoresConversationPromptsWarningsAndOldErrors(string screen) =>
        Assert.Null(UsageLimitParser.Parse("codex", screen, "UTC", new DateTime(2026, 9, 29, 20, 0, 0, DateTimeKind.Utc)));
}
