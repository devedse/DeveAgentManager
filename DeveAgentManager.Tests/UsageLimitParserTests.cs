using DeveAgentManager.Core.Agents;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class UsageLimitParserTests
{
    internal static string Codex(string reset) => $"■ You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro),\nvisit https://chatgpt.com/codex/settings/usage to purchase more credits or try again\nat {reset}.\n\n› Ask Codex to do anything\n  ? for shortcuts";
    internal static string Claude(string message) => $"{message}\n\n❯ \n  bypass permissions on";

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
    [InlineData("You've hit your limit · resets 3pm", "2026-09-29T20:00:00Z")]
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
