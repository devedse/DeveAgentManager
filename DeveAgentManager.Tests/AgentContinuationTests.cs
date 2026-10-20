using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class AgentContinuationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AutomaticScheduleFollowsNewerResetAndRemainsVisibleUntilRecovery()
    {
        await using var rig = await Rig.Create();
        rig.Clock.Utc = new(2026, 10, 16, 16, 5, 0, TimeSpan.Zero);
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Codex("Oct 22nd, 2026 8:41 AM"), TimeZone = "Europe/Amsterdam" };
        await rig.Service.SetAutomaticAsync(rig.Id, true, 2, Ct);
        Assert.Equal(new DateTime(2026, 10, 22, 6, 42, 0, DateTimeKind.Utc), (await rig.Service.GetAsync(rig.Id, Ct))!.ContinueAt);

        // A manual Continue gets a different quota window. Only automatic schedules follow it.
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.ChangedCodexReset };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var next = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal(new DateTime(2026, 10, 16, 16, 26, 0, DateTimeKind.Utc), next.DetectedResetAt);
        Assert.Equal(next.DetectedResetAt!.Value.AddMinutes(1), next.ContinueAt);
        rig.Service.Dispose(); rig.Service = rig.NewService();
        rig.Clock.Utc = new(2026, 10, 16, 16, 34, 0, TimeSpan.Zero);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        Assert.Equal("Sent", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
        for (var i = 0; i < 3; i++) await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        Assert.Equal("Sent", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
        ReadyWithoutLimit(rig);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct));
    }

    [Fact]
    public async Task AutoContinueRepairsStoredWarningWithoutMovingResetOrSendingTwice()
    {
        await using var rig = await Rig.Create();
        rig.Clock.Utc = new(2026, 10, 16, 16, 5, 0, TimeSpan.Zero);
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.ChangedCodexReset, TimeZone = "Europe/Amsterdam" };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var firstObserved = rig.Clock.Utc.UtcDateTime;
        var withoutWarning = rig.Terminal.Screen.Screen[..rig.Terminal.Screen.Screen.IndexOf("\n⚠", StringComparison.Ordinal)] + "\n› Ask Codex to do anything";
        var cleanMessage = UsageLimitParser.Parse("codex", withoutWarning, "Europe/Amsterdam", firstObserved)!.Message;
        await using (var db = rig.Factory.CreateDbContext())
        {
            var row = (await db.AgentLimits.FindAsync([rig.Id], Ct))!;
            row.Message = cleanMessage + " ⚠ Heads up, you have less than 25% of your weekly limit left. Run /status for a breakdown.";
            row.ResetAt = null;
            row.Detail = "Previously unparsed";
            await db.SaveChangesAsync(Ct);
        }
        rig.Clock.Utc = rig.Clock.Utc.AddDays(1);
        await rig.Service.SetAutomaticAsync(rig.Id, true, null, Ct);
        for (var i = 0; i < 3; i++) await rig.Service.RefreshAsync(rig.Id, Ct);
        var repaired = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal(firstObserved, repaired.DetectedAt);
        Assert.Equal(new DateTime(2026, 10, 16, 16, 26, 0, DateTimeKind.Utc), repaired.DetectedResetAt);
        Assert.DoesNotContain("Heads up", repaired.Message);
        Assert.Equal(1, rig.Terminal.Sends);
        rig.Service.Dispose(); rig.Service = rig.NewService();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
    }

    [Fact]
    public async Task CurrentLimitOutweighsReplyTimestampAfterItsReset()
    {
        await using var rig = await Rig.Create();
        await rig.Service.SetAutomaticAsync(rig.Id, true, 2, Ct);
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await using (var db = rig.Factory.CreateDbContext())
        {
            (await db.Agents.FindAsync([rig.Id], Ct))!.LastAssistantMessageAt = rig.Clock.Utc.UtcDateTime;
            await db.SaveChangesAsync(Ct);
        }
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        Assert.Equal("Sent", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
    }

    [Fact]
    public async Task NewLimitWithUnknownResetReplacesAutomaticScheduleWithoutGuessing()
    {
        await using var rig = await Rig.Create();
        await rig.Service.SetAutomaticAsync(rig.Id, true, 2, Ct);
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Codex("an unknown time") };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var limit = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal("Detected", limit.Status);
        Assert.Null(limit.ContinueAt);
        Assert.Null(limit.DetectedResetAt);
        Assert.Contains("reset time is uncertain", (await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Detail);
        rig.Clock.Utc = rig.Clock.Utc.AddHours(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
    }

    [Fact]
    public async Task LegacyExpiredDetectionRetainsObservationDateAndCatchesUpOnlyOnce()
    {
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await using (var db = rig.Factory.CreateDbContext())
        {
            (await db.AgentLimits.FindAsync([rig.Id], Ct))!.Status = "Expired";
            await db.SaveChangesAsync(Ct);
        }
        var observedAt = rig.Clock.Utc.UtcDateTime;
        rig.Clock.Utc = rig.Clock.Utc.AddDays(1);
        await rig.Service.SetAutomaticAsync(rig.Id, true, null, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var limit = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal(observedAt, limit.DetectedAt);
        Assert.Equal(observedAt.AddMinutes(2), limit.DetectedResetAt);
        Assert.Equal(1, rig.Terminal.Sends);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
    }

    [Fact]
    public async Task UnrecognizedScreenDoesNotClearKnownLimit()
    {
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = "unrecognized screen" };
        rig.Clock.Utc = rig.Clock.Utc.AddHours(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Detected", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
        ReadyWithoutLimit(rig);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryBeforeResetClearsAutomaticSchedule(bool working)
    {
        await using var rig = await Rig.Create();
        await rig.Service.SetAutomaticAsync(rig.Id, true, 2, Ct);
        ReadyWithoutLimit(rig);
        if (working) rig.Terminal.Screen = rig.Terminal.Screen with { Screen = "• Working (2s • esc to interrupt)\n" + rig.Terminal.Screen.Screen };
        else
        {
            await using var db = rig.Factory.CreateDbContext();
            (await db.Agents.FindAsync([rig.Id], Ct))!.LastAssistantMessageAt = rig.Clock.Utc.UtcDateTime.AddSeconds(1);
            await db.SaveChangesAsync(Ct);
        }
        rig.Clock.Utc = rig.Clock.Utc.AddSeconds(5);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct));
        Assert.True((await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Enabled);
        Assert.Equal(0, rig.Terminal.Sends);
    }

    [Fact]
    public async Task ChangedWordingWithSameResetDoesNotLoseUnsentAutomaticReservation()
    {
        await using var rig = await Rig.Create();
        await rig.Service.SetAutomaticAsync(rig.Id, true, 2, Ct);
        var due = (await rig.Service.GetAsync(rig.Id, Ct))!.ContinueAt;
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = rig.Terminal.Screen.Screen.Replace("Upgrade to Pro", "Upgrade your plan") };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(due, (await rig.Service.GetAsync(rig.Id, Ct))!.ContinueAt);
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        // A changed rejection after a confirmed send must not release that send's key.
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = rig.Terminal.Screen.Screen.Replace("Upgrade your plan", "Upgrade to Pro") };
        for (var i = 0; i < 3; i++) await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        Assert.Contains("already attempted", (await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacedAutomaticScheduleRecordsCurrentPaneAndTimezone(bool timezoneChanged)
    {
        await using var rig = await Rig.Create();
        await rig.Service.SetAutomaticAsync(rig.Id, true, 2, Ct);
        rig.Terminal.Screen = timezoneChanged ? rig.Terminal.Screen with { TimeZone = "Etc/UTC" }
            : rig.Terminal.Screen with { Identity = "replacement-pane" };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await using (var db = rig.Factory.CreateDbContext())
        {
            var limit = (await db.AgentLimits.FindAsync([rig.Id], Ct))!;
            Assert.Equal(rig.Terminal.Screen.Identity, limit.PaneIdentity);
            Assert.Equal(rig.Terminal.Screen.TimeZone, limit.TimeZoneId);
        }
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        for (var i = 0; i < 3; i++) await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
    }

    [Fact]
    public async Task FailedFirstObservationIsVisibleWithoutInventingADurableLimit()
    {
        await using var rig = await Rig.Create();
        var reader = new FaultingLimitReader { FailScreen = true };
        rig.Reader = reader;
        rig.Service.Dispose(); rig.Service = rig.NewService();
        await rig.Service.SetAutomaticAsync(rig.Id, true, null, Ct);
        var status = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.NotNull(status.DetectionProblem);
        Assert.True(status.Manual);
        await using (var db = rig.Factory.CreateDbContext()) Assert.Empty(await db.AgentLimits.ToListAsync(Ct));
        Assert.True((await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Enabled);
        reader.FailScreen = false;
        ReadyWithoutLimit(rig);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct));
        Assert.Equal(0, rig.Terminal.Sends);
    }

    [Theory]
    [InlineData(false, "Detected")]
    [InlineData(false, "Scheduled")]
    [InlineData(false, "Uncertain")]
    [InlineData(true, "Detected")]
    [InlineData(true, "Scheduled")]
    [InlineData(true, "Uncertain")]
    public async Task ParsingFailurePreservesLimitsOverridesPoliciesAndDeliveryEvidence(bool stored, string status)
    {
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await rig.Service.SetAutomaticAsync(rig.Id, true, 2, Ct);
        var chosen = rig.Clock.Utc.AddHours(1).UtcDateTime;
        var scheduled = rig.Clock.Utc.AddMinutes(3).UtcDateTime;
        await using (var db = rig.Factory.CreateDbContext())
        {
            var row = (await db.AgentLimits.FindAsync([rig.Id], Ct))!;
            row.Status = status;
            row.ResetAt = chosen;
            row.ContinueAt = scheduled;
            row.Detail = "Keep this delivery evidence";
            await db.SaveChangesAsync(Ct);
        }
        var reader = new FaultingLimitReader { FailScreen = !stored, FailStored = stored };
        rig.Reader = reader;
        rig.Service.Dispose();
        rig.Service = rig.NewService();
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(4);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var limit = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.NotNull(limit);
        Assert.NotNull(limit.DetectionProblem);
        Assert.Equal(status, limit.Status);
        Assert.Equal(chosen, limit.ResetAt);
        Assert.Equal(scheduled, limit.ContinueAt);
        Assert.Equal("Keep this delivery evidence", limit.Detail);
        Assert.Equal(0, rig.Terminal.Sends);
        var automatic = await rig.Service.GetAutomaticAsync(rig.Id, Ct);
        Assert.True(automatic.Enabled);
        Assert.Equal(2, automatic.Remaining);
        Assert.Equal(0, automatic.Sent);
        reader.FailScreen = reader.FailStored = false;
        await rig.Service.RefreshAsync(rig.Id, Ct);
        if (status == "Scheduled") Assert.Equal(1, rig.Terminal.Sends);
        else if (status == "Uncertain") Assert.Equal("Uncertain", await rig.StatusAsync());
        var recovered = await rig.Service.GetAsync(rig.Id, Ct);
        Assert.Null(recovered?.DetectionProblem);
    }

    [Fact]
    public async Task ExplicitScheduleCanUseHealthyReadinessWhenOnlyOldResetParsingFails()
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, rig.Clock.Utc.AddMinutes(2).UtcDateTime, Ct);
        rig.Reader = new FaultingLimitReader { FailStored = true };
        rig.Service.Dispose();
        rig.Service = rig.NewService();
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        Assert.Equal("Sent", await rig.StatusAsync());
    }

    [Fact]
    public async Task SendsOnceOnlyAtResetPlusMinute_AndSurvivesServiceRestart()
    {
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends); // Detecting never grants permission to send.
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        var scheduled = await rig.Service.GetAsync(rig.Id, Ct);
        Assert.Equal(rig.Clock.Utc.AddMinutes(3).UtcDateTime, scheduled!.ContinueAt);
        rig.Service.Dispose();
        rig.Service = rig.NewService();
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(2).AddSeconds(59);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        rig.Clock.Utc = rig.Clock.Utc.AddSeconds(1);
        await Task.WhenAll(rig.Service.RefreshAsync(rig.Id, Ct), rig.Service.RefreshAsync(rig.Id, Ct));
        Assert.Equal(1, rig.Terminal.Sends);
        Assert.Equal("Sent", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
    }

    [Theory]
    [InlineData("typing")]
    [InlineData("restarted")]
    [InlineData("changed-limit")]
    [InlineData("stopped")]
    [InlineData("native")]
    public async Task ScheduleSurvivesChangesAndWaitsWhenNotReady(string change)
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        switch (change)
        {
            case "typing": rig.Terminal.Screen = rig.Terminal.Screen with { InputEmpty = false }; break;
            case "restarted": rig.Terminal.Screen = rig.Terminal.Screen with { Identity = "new-process" }; break;
            case "changed-limit": rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Codex("3:00 PM") }; break;
            case "native":
                await using (var db = rig.Factory.CreateDbContext())
                {
                    var a = await db.Agents.FindAsync([rig.Id], Ct);
                    a!.HarnessId = "claude";
                    await db.SaveChangesAsync(Ct);
                }
                rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Claude("Usage limit reached · continuing automatically at 2:02pm · esc to cancel") };
                break;
            default:
                await using (var db = rig.Factory.CreateDbContext())
                {
                    var a = await db.Agents.FindAsync([rig.Id], Ct);
                    a!.State = AgentState.Stopped;
                    await db.SaveChangesAsync(Ct);
                }
                break;
        }
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var ready = change is "restarted" or "changed-limit";
        Assert.Equal(ready ? 1 : 0, rig.Terminal.Sends);
        Assert.Equal(ready ? "Sent" : "Scheduled", await rig.StatusAsync());
    }

    [Fact]
    public async Task ExplicitCancelAndUncertainDeliveryIsNeverRetried()
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        await rig.Service.CancelAsync(rig.Id, "Cancelled by you.", Ct);
        Assert.Equal("Cancelled", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        rig.Terminal.ThrowOnSend = true;
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Uncertain", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
        Assert.Equal(1, rig.Terminal.Sends);
    }

    [Fact]
    public async Task UnknownResetRequiresExplicitUtcTimeAndDoesNotGuess()
    {
        await using var rig = await Rig.Create();
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Codex("an unknown time") };
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.ScheduleAsync(rig.Id, null, Ct));
        await rig.Service.ScheduleAsync(rig.Id, rig.Clock.Utc.AddHours(1).UtcDateTime, Ct);
        Assert.Equal(rig.Clock.Utc.AddHours(1).AddMinutes(1).UtcDateTime, (await rig.Service.GetAsync(rig.Id, Ct))!.ContinueAt);
    }

    [Fact]
    public async Task OverdueScheduleRemainsArmedAndSendsWhenReady()
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        rig.Clock.Utc = rig.Clock.Utc.AddHours(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Sent", await rig.StatusAsync());
        Assert.Equal(1, rig.Terminal.Sends);
    }

    [Fact]
    public async Task ExplicitTimeFromDialogSurvivesChangedDetection()
    {
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var viewed = (await rig.Service.GetAsync(rig.Id, Ct))!;
        rig.Clock.Utc = rig.Clock.Utc.AddSeconds(5);
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Codex("3:00 PM") };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await rig.Service.ScheduleAsync(rig.Id, viewed.ResetAt, Ct, viewed.DetectedAt);
        Assert.Equal(viewed.ResetAt!.Value.AddMinutes(1), (await rig.Service.GetAsync(rig.Id, Ct))!.ContinueAt);
    }

    [Fact]
    public async Task RestartDuringDeliveryLeavesAnUncertainResultWithoutResending()
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        await using (var db = rig.Factory.CreateDbContext())
            await db.AgentLimits.Where(l => l.AgentId == rig.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "Sending"), Ct);
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        rig.Service.Dispose();
        rig.Service = rig.NewService();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Uncertain", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
        Assert.Equal(0, rig.Terminal.Sends);
    }

    [Fact]
    public async Task ClaudeWeeklyLimitOffersSchedulingAfterCompletedFooter()
    {
        await using var rig = await Rig.Create();
        await using (var db = rig.Factory.CreateDbContext())
            await db.Agents.Where(a => a.Id == rig.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.HarnessId, "claude"), Ct);
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = AgentActivityTests.Screen("claude-weekly-limit") };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var limit = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal("Detected", limit.Status);
        Assert.Equal("Europe/Amsterdam", limit.TimeZoneId);
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        Assert.Equal(new DateTime(2026, 10, 4, 1, 1, 0, DateTimeKind.Utc), (await rig.Service.GetAsync(rig.Id, Ct))!.ContinueAt);
        Assert.Equal(0, rig.Terminal.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WeeklyResetAcrossClockOverlapRepairsAndSendsOnceAtTheLaterOccurrence(bool alreadyStored)
    {
        await using var rig = await Rig.Create();
        rig.Clock.Utc = new(2026, 10, 20, 1, 22, 0, TimeSpan.Zero);
        await using (var db = rig.Factory.CreateDbContext())
            await db.Agents.Where(a => a.Id == rig.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.HarnessId, "claude"), Ct);
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Screens", "claude-weekly-limit-dst.txt")) };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var detected = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal(new DateTime(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc), detected.DetectedResetAt);
        Assert.Contains("later occurrence", detected.Detail!);
        if (alreadyStored)
        {
            await using var db = rig.Factory.CreateDbContext();
            await db.AgentLimits.Where(l => l.AgentId == rig.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.ResetAt, (DateTime?)null)
                .SetProperty(l => l.Detail, "This reset falls in a daylight-saving clock change."), Ct);
            rig.Clock.Utc = rig.Clock.Utc.AddDays(1);
            rig.Service.Dispose(); rig.Service = rig.NewService();
        }
        await rig.Service.SetAutomaticAsync(rig.Id, true, 2, Ct);
        var scheduled = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal(detected.DetectedAt, scheduled.DetectedAt);
        Assert.Equal(new DateTime(2026, 10, 25, 1, 1, 0, DateTimeKind.Utc), scheduled.ContinueAt);
        rig.Clock.Utc = new(2026, 10, 25, 0, 1, 0, TimeSpan.Zero); // The earlier 02:01 must not send.
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        rig.Clock.Utc = new(2026, 10, 25, 1, 1, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++) await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        Assert.Equal("Sent", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
        Assert.NotNull((await rig.Service.GetAsync(rig.Id, Ct))!.DetectedResetAt); // Passing the reset is not proof of recovery.
    }

    [Fact]
    public async Task RefreshRepairsPreviouslyUnparsedResetWithoutANewLimitMessage()
    {
        await using var rig = await Rig.Create();
        await using (var db = rig.Factory.CreateDbContext())
            await db.Agents.Where(a => a.Id == rig.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.HarnessId, "claude"), Ct);
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Claude(
            "└ You've hit your weekly limit · resets Oct 4, 3am (Europe/Amsterdam) /upgrade to increase your usage limit.") };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var detected = (await rig.Service.GetAsync(rig.Id, Ct))!;
        await using (var db = rig.Factory.CreateDbContext())
            await db.AgentLimits.Where(l => l.AgentId == rig.Id).ExecuteUpdateAsync(s =>
                s.SetProperty(l => l.ResetAt, (DateTime?)null).SetProperty(l => l.Detail, "Previously unparsed"), Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var repaired = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal(detected.DetectedAt, repaired.DetectedAt);
        Assert.Equal(new DateTime(2026, 10, 4, 1, 0, 0, DateTimeKind.Utc), repaired.ResetAt);
        Assert.Null(repaired.Detail);
    }

    [Fact]
    public async Task DetectedResetRemainsAvailableAfterManualOverrideAndCancellation()
    {
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var detectedReset = rig.Clock.Utc.AddMinutes(2).UtcDateTime;
        var selectedReset = rig.Clock.Utc.AddHours(1).UtcDateTime;
        await rig.Service.ScheduleAsync(rig.Id, selectedReset, Ct);
        await rig.Service.CancelAsync(rig.Id, "Cancelled by you.", Ct);
        var limit = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal(selectedReset, limit.ResetAt);
        Assert.Equal(detectedReset, limit.DetectedResetAt);
        await rig.Service.ScheduleAsync(rig.Id, limit.DetectedResetAt, Ct, limit.DetectedAt);
        Assert.Equal(detectedReset.AddMinutes(1), (await rig.Service.GetAsync(rig.Id, Ct))!.ContinueAt);
    }

    [Fact]
    public async Task RelativeScheduleUsesManagerClockAndSendsAfterExactDuration()
    {
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var now = rig.Clock.Utc;
        await rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: 5);
        Assert.Equal(now.AddMinutes(5).UtcDateTime, (await rig.Service.GetAsync(rig.Id, Ct))!.ContinueAt);
        Assert.Equal(now.AddMinutes(2).UtcDateTime, (await rig.Service.GetAsync(rig.Id, Ct))!.ResetAt);
        rig.Clock.Utc = now.AddMinutes(4);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        rig.Clock.Utc = now.AddMinutes(5);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(46081)]
    [InlineData(double.NaN)]
    public async Task RejectsInvalidRelativeSchedule(double minutes)
    {
        await using var rig = await Rig.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: minutes));
        Assert.Equal(0, rig.Terminal.Sends);
    }

    [Theory]
    [InlineData(ContinueDelivery.NotTyped, "nothing was typed")]
    [InlineData(ContinueDelivery.Unconfirmed, "Enter was not sent")]
    public async Task ChangedScreenDuringSubmissionWaitsAndRetriesOnlyWhilePromptIsEmpty(ContinueDelivery outcome, string detail)
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        rig.Terminal.SendResult = outcome;
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var waiting = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal("Scheduled", waiting.Status);
        Assert.Contains(detail, waiting.Detail);
        rig.Terminal.Screen = rig.Terminal.Screen with { InputEmpty = false };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        rig.Terminal.Screen = rig.Terminal.Screen with { InputEmpty = true };
        rig.Terminal.SendResult = ContinueDelivery.Sent;
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Sent", await rig.StatusAsync());
        Assert.Equal(2, rig.Terminal.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SentLimitClearsWhenNormalActivityResumesWithoutSendingAgain(bool working)
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Sent", await rig.StatusAsync());
        ReadyWithoutLimit(rig);
        if (working) rig.Terminal.Screen = rig.Terminal.Screen with { Screen = "• Working (2s • esc to interrupt)\n" + rig.Terminal.Screen.Screen };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct));
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        // The normal manual scheduler is available again.
        await rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: 1, manual: true);
        Assert.True((await rig.Service.GetAsync(rig.Id, Ct))!.Manual);
    }

    [Fact]
    public async Task SentResultIsNotClearedByAnUnrecognizedScreenBeforeDetectedReset()
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: 1);
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = "unrecognized screen" };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Sent", await rig.StatusAsync());
        Assert.Equal(1, rig.Terminal.Sends);
    }

    [Fact]
    public async Task PassedResetStaysVisibleWithoutAutomaticPermissionAndAllowsManualSchedule()
    {
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.NotNull(await rig.Service.GetAsync(rig.Id, Ct));
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(2);
        Assert.NotNull(await rig.Service.GetAsync(rig.Id, Ct));
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Detected", await rig.StatusAsync());
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.NotNull(await rig.Service.GetAsync(rig.Id, Ct));
        Assert.Equal(0, rig.Terminal.Sends);
        await rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: 1, manual: true);
        var scheduled = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.True(scheduled.Manual);
        Assert.DoesNotContain("You've hit", scheduled.Message);
        await using (var db = rig.Factory.CreateDbContext())
        {
            var row = (await db.AgentLimits.FindAsync([rig.Id], Ct))!;
            Assert.True(UsageLimitState.HasExpired(row, rig.Clock.Utc.UtcDateTime));
        }
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        ReadyWithoutLimit(rig);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct));
    }

    private static void ReadyWithoutLimit(Rig rig) => rig.Terminal.Screen = rig.Terminal.Screen with
    { Screen = "› Ask Codex to do anything\nGPT-6 medium · /work/example\n? for shortcuts" };

    [Fact]
    public async Task ManualScheduleNeedsNoLimit_SurvivesRestart_AndSendsOnce()
    {
        await using var rig = await Rig.Create();
        ReadyWithoutLimit(rig);
        await rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: 1, manual: true);
        Assert.True((await rig.Service.GetAsync(rig.Id, Ct))!.Manual);
        rig.Service.Dispose(); rig.Service = rig.NewService();
        rig.Clock.Utc = rig.Clock.Utc.AddSeconds(59);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        rig.Clock.Utc = rig.Clock.Utc.AddSeconds(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct));
        // A new explicit click may start another manual test.
        await rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: 1, manual: true);
        Assert.Equal("Scheduled", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("screen")]
    [InlineData("process")]
    [InlineData("stopped")]
    [InlineData("limit")]
    public async Task ManualScheduleSurvivesChangesAndWaitsForAnEmptyIdlePrompt(string change)
    {
        await using var rig = await Rig.Create();
        ReadyWithoutLimit(rig);
        await rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: 1, manual: true);
        switch (change)
        {
            case "input": rig.Terminal.Screen = rig.Terminal.Screen with { InputEmpty = false }; break;
            case "screen": rig.Terminal.Screen = rig.Terminal.Screen with { Hash = "changed" }; break;
            case "process": rig.Terminal.Screen = rig.Terminal.Screen with { Identity = "new" }; break;
            case "limit": rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Codex("2:02 PM") }; break;
            default:
                await using (var db = rig.Factory.CreateDbContext())
                {
                    var agent = await db.Agents.FindAsync([rig.Id], Ct);
                    agent!.State = AgentState.Stopped;
                    await db.SaveChangesAsync(Ct);
                }
                break;
        }
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var ready = change is "screen" or "process" or "limit";
        Assert.Equal(ready ? 1 : 0, rig.Terminal.Sends);
        Assert.Equal(ready ? "Sent" : "Scheduled", await rig.StatusAsync());
        if (change == "input")
        {
            rig.Terminal.Screen = rig.Terminal.Screen with { InputEmpty = true };
            await rig.Service.RefreshAsync(rig.Id, Ct);
            Assert.Equal(1, rig.Terminal.Sends);
        }
    }

    [Theory]
    [InlineData(false, "working")]
    [InlineData(false, "working-screen")]
    [InlineData(false, "replied")]
    [InlineData(true, "working")]
    [InlineData(true, "working-screen")]
    [InlineData(true, "replied")]
    public async Task OneTimeContinueIsDoneWhenTheAgentAlreadyContinued(bool manual, string how)
    {
        await using var rig = await Rig.Create();
        if (manual) ReadyWithoutLimit(rig);
        await rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: manual ? 3 : null, manual: manual);
        var due = (await rig.Service.GetAsync(rig.Id, Ct))!.ContinueAt!.Value;
        await using (var db = rig.Factory.CreateDbContext())
        {
            var agent = (await db.Agents.FindAsync([rig.Id], Ct))!;
            agent.Working = how == "working";
            if (how == "replied") agent.LastAssistantMessageAt = due.AddSeconds(-30);
            await db.SaveChangesAsync(Ct);
        }
        rig.Terminal.Screen = rig.Terminal.Screen with
        {
            Screen = (how == "working-screen" ? "• Working (2s • esc to interrupt)\n" : "") + "› Ask Codex to do anything\nGPT-6 medium · /work/example\n? for shortcuts",
        };
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct));

        // Idle again at the next limit: that one is only detected, the earlier schedule doesn't send into it.
        await using (var db = rig.Factory.CreateDbContext())
        {
            (await db.Agents.FindAsync([rig.Id], Ct))!.Working = false;
            await db.SaveChangesAsync(Ct);
        }
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Codex("7:00 PM"), Hash = "next-limit" };
        rig.Clock.Utc = rig.Clock.Utc.AddHours(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        Assert.Equal("Detected", await rig.StatusAsync());
    }

    [Fact]
    public async Task OneTimeContinueStillSendsAfterAReplyFromBeforeTheReset()
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        await using (var db = rig.Factory.CreateDbContext())
        {
            // The reply that ran into the limit.
            (await db.Agents.FindAsync([rig.Id], Ct))!.LastAssistantMessageAt = rig.Clock.Utc.UtcDateTime.AddMinutes(-1);
            await db.SaveChangesAsync(Ct);
        }
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        Assert.Equal("Sent", await rig.StatusAsync());
    }

    [Fact]
    public async Task ClaudeContinuingByItselfAtTheResetEndsAChosenTimeSchedule()
    {
        // Claude waits for the reset itself ("continuing automatically at 2:02pm"); a continue scheduled for a minute
        // later finds it working, and must not fire when it stops at its next limit.
        await using var rig = await Rig.Create();
        await using (var db = rig.Factory.CreateDbContext())
        {
            (await db.Agents.FindAsync([rig.Id], Ct))!.HarnessId = "claude";
            await db.SaveChangesAsync(Ct);
        }
        ReadyWithoutLimit(rig);
        await rig.Service.ScheduleAsync(rig.Id, rig.Clock.Utc.AddMinutes(2).UtcDateTime, Ct, manual: true);
        rig.Terminal.Screen = rig.Terminal.Screen with
        {
            Screen = "● Usage limit reset\n\n✶ Deliberating… (1m 12s · thinking)\n\n─────\n❯ \n─────\n  ⏵⏵ bypass permissions on",
        };
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct));

        rig.Terminal.Screen = rig.Terminal.Screen with
        {
            Screen = UsageLimitParserTests.Claude("Usage limit reached · continuing automatically at 7:02pm · esc to cancel"), Hash = "next-limit",
        };
        rig.Clock.Utc = rig.Clock.Utc.AddHours(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        Assert.True((await rig.Service.GetAsync(rig.Id, Ct))!.NativeAutoContinue);
    }

    [Fact]
    public async Task ManualScheduleWaitsForUnclearPromptEvenWithNoDetection()
    {
        await using var rig = await Rig.Create();
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = "some unrecognized screen" };
        await rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: 1, manual: true);
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Scheduled", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
        Assert.Equal(0, rig.Terminal.Sends);
    }

    [Fact]
    public async Task SharedObservationDetectsAndDispatchesWithoutAnotherRead()
    {
        await using var rig = await Rig.Create();
        await rig.Service.ObserveAsync(rig.Id, rig.Terminal.Screen, Ct);
        Assert.Equal(0, rig.Terminal.Reads);
        Assert.NotNull(await rig.Service.GetAsync(rig.Id, Ct));
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        var reads = rig.Terminal.Reads;
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.ObserveAsync(rig.Id, rig.Terminal.Screen, Ct);
        Assert.Equal(reads, rig.Terminal.Reads);
        Assert.Equal(1, rig.Terminal.Sends);
    }

    [Fact]
    public async Task AutomaticRepeatsAcrossLimitsAndRestartUntilCountIsExhausted()
    {
        await using var rig = await Rig.Create();
        await rig.Service.SetAutomaticAsync(rig.Id, true, 2, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct); // detect
        await rig.Service.RefreshAsync(rig.Id, Ct); // arm
        Assert.Equal(0, rig.Terminal.Sends);
        Assert.Equal(rig.Clock.Utc.AddMinutes(3).UtcDateTime, (await rig.Service.GetAsync(rig.Id, Ct))!.ContinueAt);
        rig.Service.Dispose(); rig.Service = rig.NewService();
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        Assert.Equal(1, (await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Remaining);
        for (var i = 0; i < 3; i++) await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends); // the old error still on screen cannot trigger another send
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Codex("3:00 PM") };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(58);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var policy = await rig.Service.GetAutomaticAsync(rig.Id, Ct);
        Assert.False(policy.Enabled);
        Assert.Equal(0, policy.Remaining);
        Assert.Equal(2, policy.Sent);
        Assert.Equal(2, rig.Terminal.Sends);
    }

    [Fact]
    public async Task EnablingAutomatic_SchedulesALimitAlreadyOnScreenAtOnce()
    {
        await using var rig = await Rig.Create();
        await rig.Service.SetAutomaticAsync(rig.Id, true, null, Ct);
        // No background check yet: the dialog that switched it on shows the next continue straight away.
        var limit = await rig.Service.GetAsync(rig.Id, Ct);
        Assert.Equal("Scheduled", limit!.Status);
        Assert.Equal(rig.Clock.Utc.AddMinutes(3).UtcDateTime, limit.ContinueAt);
        Assert.Equal("Waiting until one minute after the detected reset.", (await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Detail);
        Assert.Equal(0, rig.Terminal.Sends);
    }

    [Fact]
    public async Task EnablingAutomatic_SaysAtOnceWhenTheLimitOnScreenHasNoResetTime()
    {
        await using var rig = await Rig.Create();
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Codex("an unknown time") };
        await rig.Service.SetAutomaticAsync(rig.Id, true, null, Ct);
        Assert.Contains("reset time is uncertain", (await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Detail);
        Assert.NotEqual("Scheduled", await rig.StatusAsync());
        Assert.Equal(0, rig.Terminal.Sends);
    }

    [Fact]
    public async Task AutomaticUnlimitedWaitsForDraftAndCanBeTurnedOff()
    {
        await using var rig = await Rig.Create();
        await rig.Service.SetAutomaticAsync(rig.Id, true, null, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        rig.Terminal.Screen = rig.Terminal.Screen with { InputEmpty = false };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        Assert.True((await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Enabled);
        await rig.Service.SetAutomaticAsync(rig.Id, false, null, Ct);
        rig.Terminal.Screen = rig.Terminal.Screen with { InputEmpty = true };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        await rig.Service.SetAutomaticAsync(rig.Id, true, null, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        Assert.True((await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Enabled);
        Assert.Null((await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Remaining);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("native")]
    public async Task AutomaticDoesNotGuessOrDuplicateNativeContinuation(string kind)
    {
        await using var rig = await Rig.Create();
        if (kind == "native")
        {
            await using var db = rig.Factory.CreateDbContext();
            (await db.Agents.FindAsync([rig.Id], Ct))!.HarnessId = "claude";
            await db.SaveChangesAsync(Ct);
            rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Claude("Usage limit reached · continuing automatically at 2:02pm · esc to cancel") };
        }
        else rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Codex("an unknown time") };
        await rig.Service.SetAutomaticAsync(rig.Id, true, null, Ct);
        for (var i = 0; i < 3; i++) await rig.Service.RefreshAsync(rig.Id, Ct);
        rig.Clock.Utc = rig.Clock.Utc.AddHours(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        Assert.NotEqual("Scheduled", await rig.StatusAsync());
    }

    [Theory]
    [InlineData("session")]
    [InlineData("uncertain")]
    [InlineData("restart-sending")]
    public async Task AutomaticStopsForChangedConversationOrUncertainDelivery(string change)
    {
        await using var rig = await Rig.Create();
        await rig.Service.SetAutomaticAsync(rig.Id, true, 1, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await using (var db = rig.Factory.CreateDbContext())
        {
            if (change == "session") (await db.Agents.FindAsync([rig.Id], Ct))!.HarnessSessionId = "different-chat";
            if (change == "restart-sending") (await db.AgentLimits.FindAsync([rig.Id], Ct))!.Status = "Sending";
            await db.SaveChangesAsync(Ct);
        }
        rig.Terminal.ThrowOnSend = change == "uncertain";
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var policy = await rig.Service.GetAutomaticAsync(rig.Id, Ct);
        Assert.False(policy.Enabled);
        Assert.Equal(1, policy.Remaining);
        Assert.Equal(change == "uncertain" ? 1 : 0, rig.Terminal.Sends);
    }

    [Fact]
    public async Task AutomaticAndOneTimeModesCannotCompete()
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.SetAutomaticAsync(rig.Id, true, null, Ct));
        await rig.Service.CancelAsync(rig.Id, "Cancelled", Ct);
        await rig.Service.SetAutomaticAsync(rig.Id, true, null, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.ScheduleAsync(rig.Id, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.SetAutomaticAsync(rig.Id, true, 0, Ct));
    }

    [Fact]
    public async Task ChosenDateSchedulesEvenIfLimitClearedWhileDialogWasOpen()
    {
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var viewed = (await rig.Service.GetAsync(rig.Id, Ct))!;
        ReadyWithoutLimit(rig);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct));
        var reads = rig.Terminal.Reads;
        await rig.Service.ScheduleAsync(rig.Id, viewed.ResetAt, Ct, viewed.DetectedAt);
        Assert.Equal(reads, rig.Terminal.Reads); // no new detection gate for an explicit time
        Assert.Equal(viewed.ResetAt!.Value.AddMinutes(1), (await rig.Service.GetAsync(rig.Id, Ct))!.ContinueAt);
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
    }

    [Fact]
    public async Task ExplicitScheduleRemainsCancellableWhenNativeWaitingWasPreviouslyDetected()
    {
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await using (var db = rig.Factory.CreateDbContext())
        {
            (await db.AgentLimits.FindAsync([rig.Id], Ct))!.NativeAutoContinue = true;
            await db.SaveChangesAsync(Ct);
        }
        await rig.Service.ScheduleAsync(rig.Id, rig.Clock.Utc.AddHours(1).UtcDateTime, Ct);
        Assert.False((await rig.Service.GetAsync(rig.Id, Ct))!.NativeAutoContinue);
        await rig.Service.CancelAsync(rig.Id, "Cancelled by you.", Ct);
        Assert.Equal("Cancelled", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoContinueCatchesUpPastResetWithQuotaFooter(bool alreadyStored)
    {
        await using var rig = await Rig.Create();
        var screen = UsageLimitParserTests.Codex("2:02 PM").Replace("■ You've", "■ Error running remote compact task: You've")
            .Replace("\n\n›", "\n⚠ 5h limit: 48% left · /status\n\n›");
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = screen };
        if (alreadyStored)
        {
            await rig.Service.RefreshAsync(rig.Id, Ct);
            await using var db = rig.Factory.CreateDbContext();
            var row = (await db.AgentLimits.FindAsync([rig.Id], Ct))!;
            row.Message += " ⚠ 5h limit: 48% left · /status";
            row.ResetAt = null;
            await db.SaveChangesAsync(Ct);
        }
        rig.Clock.Utc = rig.Clock.Utc.AddHours(2);
        await rig.Service.SetAutomaticAsync(rig.Id, true, null, Ct);
        for (var i = 0; i < 4; i++) await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        Assert.True((await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Enabled);
        Assert.Equal("Sent", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
    }

    [Fact]
    public async Task AutoContinueRepairsStoredRecapWithoutMovingObservationOrSendingTwice()
    {
        await using var rig = await Rig.Create();
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = rig.Terminal.Screen.Screen
            .Replace("\n\n›", "\n\n  ↳ Recap: Tests passed. Next: Update the report.\n\n›") };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var observedAt = rig.Clock.Utc.UtcDateTime;
        await using (var db = rig.Factory.CreateDbContext())
        {
            var row = (await db.AgentLimits.FindAsync([rig.Id], Ct))!;
            row.Message += " ↳ Recap: Tests passed. Next: Update the report.";
            row.ResetAt = null;
            row.Detail = "Previously unparsed";
            await db.SaveChangesAsync(Ct);
        }
        // A deployment on a later day must not reinterpret yesterday's paused error as a new limit.
        rig.Clock.Utc = rig.Clock.Utc.AddDays(1);
        await rig.Service.SetAutomaticAsync(rig.Id, true, 2, Ct);
        await using (var db = rig.Factory.CreateDbContext())
        {
            var row = (await db.AgentLimits.FindAsync([rig.Id], Ct))!;
            Assert.Equal(observedAt, row.DetectedAt);
            Assert.Equal(observedAt.AddMinutes(2), row.ResetAt);
            Assert.Equal(observedAt.AddMinutes(3), row.ContinueAt);
            Assert.DoesNotContain("Recap:", row.Message);
        }
        for (var i = 0; i < 4; i++) await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends);
        var automatic = await rig.Service.GetAutomaticAsync(rig.Id, Ct);
        Assert.True(automatic.Enabled);
        Assert.Equal(1, automatic.Sent);
        Assert.Equal(1, automatic.Remaining);
    }

    [Fact]
    public async Task RecapRepairKeepsChosenResetAndArmedSchedule()
    {
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var chosen = rig.Clock.Utc.AddHours(1).UtcDateTime;
        await rig.Service.ScheduleAsync(rig.Id, chosen, Ct);
        var before = (await rig.Service.GetAsync(rig.Id, Ct))!;
        await using (var db = rig.Factory.CreateDbContext())
        {
            var row = (await db.AgentLimits.FindAsync([rig.Id], Ct))!;
            row.Message += " ↳ Recap: Tests passed. Next: Update the report.";
            await db.SaveChangesAsync(Ct);
        }
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var after = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal(before.DetectedAt, after.DetectedAt);
        Assert.Equal(before.DetectedResetAt, after.DetectedResetAt);
        Assert.Equal(chosen, after.ResetAt);
        Assert.Equal(before.ContinueAt, after.ContinueAt);
        Assert.Equal("Scheduled", after.Status);
        Assert.Equal(0, rig.Terminal.Sends);
        Assert.DoesNotContain("Recap:", after.Message);
    }

    private static string PiScreen => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Screens", "pi-idle-cursor-line.txt"));

    [Fact]
    public async Task PiQuotaLimitIsReportedWithoutAResetOrAutomaticContinuation()
    {
        await using var rig = await Rig.Create();
        await SwitchAsync(rig, "harness");
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.PiQuotaScreen };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var limit = await rig.Service.GetAsync(rig.Id, Ct);
        Assert.NotNull(limit);
        Assert.Equal("Detected", limit.Status);
        Assert.Equal("Error: 429 quota exceeded", limit.Message);
        Assert.Null(limit.ResetAt);
        Assert.Null(limit.DetectedResetAt);
        Assert.Null(limit.ContinueAt);
        Assert.False(limit.Manual);
        Assert.False((await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Available);

        rig.Clock.Utc = rig.Clock.Utc.AddDays(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(limit, await rig.Service.GetAsync(rig.Id, Ct));
        Assert.Equal(0, rig.Terminal.Sends);

        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = PiScreen };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct));
    }

    private static string CodexIdle => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Screens", "codex-idle.txt"));

    /// <summary>Continues the agent in another harness, with another login or in a new conversation.</summary>
    private static async Task SwitchAsync(Rig rig, string change)
    {
        await using var db = rig.Factory.CreateDbContext();
        var agent = (await db.Agents.FindAsync([rig.Id], Ct))!;
        switch (change)
        {
            case "harness": agent.HarnessId = "pi"; agent.HarnessSessionId = "pi-session"; rig.Terminal.Screen = rig.Terminal.Screen with { Screen = PiScreen }; break;
            case "login":
                // As if the limit was seen with another login of this harness.
                (await db.AgentLimits.FindAsync([rig.Id], Ct))!.SessionId = $"codex|session|{Guid.NewGuid()}";
                rig.Terminal.Screen = rig.Terminal.Screen with { Screen = CodexIdle };
                break;
            default: agent.HarnessSessionId = "new-conversation"; rig.Terminal.Screen = rig.Terminal.Screen with { Screen = CodexIdle }; break;
        }
        await db.SaveChangesAsync(Ct);
    }

    [Theory]
    [InlineData("harness", "Detected")]
    [InlineData("harness", "Sent")]
    [InlineData("harness", "Uncertain")]
    [InlineData("login", "Detected")]
    [InlineData("conversation", "Sent")]
    public async Task LimitFromBeforeASwitch_IsGoneAtOnce_AndClearedWithoutSending(string change, string status)
    {
        // The reported case: Claude's weekly limit kept showing on the agent after it continued in pi.
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        await using (var db = rig.Factory.CreateDbContext())
        {
            (await db.AgentLimits.FindAsync([rig.Id], Ct))!.Status = status;
            await db.SaveChangesAsync(Ct);
        }
        Assert.NotNull(await rig.Service.GetAsync(rig.Id, Ct));

        await SwitchAsync(rig, change);
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct)); // Before any observation of the new harness.
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Cleared", await rig.StatusAsync());

        // It stays cleared after its old reset passed, rather than coming back as Expired.
        rig.Clock.Utc = rig.Clock.Utc.AddHours(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Cleared", await rig.StatusAsync());
        Assert.Null(await rig.Service.GetAsync(rig.Id, Ct));
        Assert.Equal(0, rig.Terminal.Sends);
    }

    [Fact]
    public async Task YourScheduleSurvivesASwitch_WaitsVisibly_AndCanBeCancelled()
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        await SwitchAsync(rig, "harness");
        Assert.Equal("Scheduled", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);

        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(5);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var waiting = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal("Scheduled", waiting.Status);
        Assert.Contains("harness or session changed", waiting.Detail);
        Assert.Equal(0, rig.Terminal.Sends);

        await rig.Service.CancelAsync(rig.Id, "Cancelled by you.", Ct);
        Assert.Equal("Cancelled", await rig.StatusAsync());
        // A one-time schedule for the new harness replaces it.
        await rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: 1, manual: true);
        Assert.Equal("Scheduled", await rig.StatusAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pi_GetsOneTimeSchedules_ButNotAutomaticMode(bool chosenTime)
    {
        await using var rig = await Rig.Create();
        await SwitchAsync(rig, "harness");
        Assert.False((await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Available);
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.SetAutomaticAsync(rig.Id, true, null, Ct));

        if (chosenTime) await rig.Service.ScheduleAsync(rig.Id, rig.Clock.Utc.AddMinutes(10).UtcDateTime, Ct);
        else await rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: 11, manual: true);
        var scheduled = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal(rig.Clock.Utc.AddMinutes(11).UtcDateTime, scheduled.ContinueAt);

        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(10);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(1, rig.Terminal.Sends); // pi's empty prompt box is ready.
        Assert.Equal("Sent", await rig.StatusAsync());

        // pi's prompt is ready again afterwards: done.
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Cleared", await rig.StatusAsync());
        Assert.Equal(1, rig.Terminal.Sends);
    }

    [Fact]
    public async Task ClaudeMessageStoredWithItsConnector_IsRepairedInPlace()
    {
        // Limits stored before the ⎿ connector was dropped keep their schedule and detection.
        await using var rig = await Rig.Create();
        var screen = UsageLimitParserTests.Claude("  ⎿  You've hit your weekly limit · resets Oct 2, 3pm (UTC)");
        await using (var db = rig.Factory.CreateDbContext())
        {
            (await db.Agents.FindAsync([rig.Id], Ct))!.HarnessId = "claude";
            await db.SaveChangesAsync(Ct);
        }
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = screen };
        await rig.Service.RefreshAsync(rig.Id, Ct);
        DateTime detectedAt;
        await using (var db = rig.Factory.CreateDbContext())
        {
            var row = (await db.AgentLimits.FindAsync([rig.Id], Ct))!;
            row.Message = "⎿ " + row.Message;
            detectedAt = row.DetectedAt;
            await db.SaveChangesAsync(Ct);
        }
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var limit = (await rig.Service.GetAsync(rig.Id, Ct))!;
        Assert.Equal("You've hit your weekly limit · resets Oct 2, 3pm (UTC)", limit.Message);
        Assert.Equal(detectedAt, limit.DetectedAt); // Repaired, not detected again.
        Assert.Equal(new DateTime(2026, 10, 2, 15, 0, 0, DateTimeKind.Utc), limit.DetectedResetAt);
    }

    [Fact]
    public async Task CodexAndClaude_OfferAutomaticMode()
    {
        await using var rig = await Rig.Create();
        Assert.True((await rig.Service.GetAutomaticAsync(rig.Id, Ct)).Available);
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026, 9, 30, 14, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private sealed class FakeTerminal : ILimitTerminal
    {
        public LimitScreen Screen = new(UsageLimitParserTests.Codex("2:02 PM"), "pane:pid:start:boot", "UTC", true, "hash");
        public int Sends;
        public int Reads;
        public bool ThrowOnSend;
        public ContinueDelivery SendResult = ContinueDelivery.Sent;
        public Task<LimitScreen> ReadAsync(Agent agent, CancellationToken ct) { Reads++; return Task.FromResult(Screen); }
        public Task<ContinueDelivery> SendContinueAsync(Agent agent, LimitScreen expected, CancellationToken ct)
        {
            Sends++;
            if (ThrowOnSend) throw new IOException("Connection lost after sending");
            return Task.FromResult(SendResult);
        }
    }

    private sealed class Factory(string path) : IDbContextFactory<ManagerDbContext>
    {
        public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"dam-continue-{Guid.NewGuid():N}.db");
        public Guid Id = Guid.NewGuid();
        public Factory Factory = null!;
        public FakeClock Clock = new();
        public FakeTerminal Terminal = new();
        public AgentContinuations Service = null!;
        public UsageLimitReader? Reader;
        public async Task<string?> StatusAsync()
        {
            await using var db = Factory.CreateDbContext();
            return (await db.AgentLimits.FindAsync([Id], Ct))?.Status;
        }
        public AgentContinuations NewService() => new(Factory, Terminal, new ManagerEvents(), Clock, NullLogger<AgentContinuations>.Instance, limitReader: Reader);
        public static async Task<Rig> Create()
        {
            var rig = new Rig();
            rig.Factory = new Factory(rig._path);
            await using var db = rig.Factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync(Ct);
            var runner = new Runner { OwnerId = User.BootstrapId, Id = Guid.NewGuid(), Name = "runner", State = RunnerState.Ready };
            db.Runners.Add(runner);
            db.Agents.Add(new Agent { OwnerId = User.BootstrapId, Id = rig.Id, RunnerId = runner.Id, Name = "agent", HarnessId = "codex", HarnessSessionId = "session", State = AgentState.Running });
            await db.SaveChangesAsync(Ct);
            rig.Service = rig.NewService();
            return rig;
        }
        public ValueTask DisposeAsync() { Service.Dispose(); File.Delete(_path); return ValueTask.CompletedTask; }
    }
}
