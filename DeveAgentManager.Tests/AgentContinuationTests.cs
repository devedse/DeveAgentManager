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
    [InlineData("working")]
    [InlineData("stopped")]
    [InlineData("native")]
    public async Task CancelsWhenTheOriginalWaitingStateChanges(string change)
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
                    a!.Working = change == "working";
                    if (change == "stopped") a.State = AgentState.Stopped;
                    await db.SaveChangesAsync(Ct);
                }
                break;
        }
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(3);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal(0, rig.Terminal.Sends);
        Assert.NotEqual("Scheduled", (await rig.Service.GetAsync(rig.Id, Ct))?.Status);
    }

    [Fact]
    public async Task UserInputCancelsAndUncertainDeliveryIsNeverRetried()
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        await rig.Service.OnUserInputAsync(rig.Id, Ct);
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
    public async Task DoesNotSendLongAfterMissedSchedule()
    {
        await using var rig = await Rig.Create();
        await rig.Service.ScheduleAsync(rig.Id, null, Ct);
        rig.Clock.Utc = rig.Clock.Utc.AddHours(1);
        await rig.Service.RefreshAsync(rig.Id, Ct);
        Assert.Equal("Cancelled", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
        Assert.Equal(0, rig.Terminal.Sends);
    }

    [Fact]
    public async Task StaleDialogCannotApproveANewLimit()
    {
        await using var rig = await Rig.Create();
        await rig.Service.RefreshAsync(rig.Id, Ct);
        var viewed = (await rig.Service.GetAsync(rig.Id, Ct))!;
        rig.Clock.Utc = rig.Clock.Utc.AddSeconds(5);
        rig.Terminal.Screen = rig.Terminal.Screen with { Screen = UsageLimitParserTests.Codex("3:00 PM") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.ScheduleAsync(rig.Id, null, Ct, viewed.DetectedAt));
        Assert.Equal("Detected", (await rig.Service.GetAsync(rig.Id, Ct))!.Status);
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

    [Fact]
    public async Task RelativeScheduleUsesManagerClockAndSendsAfterExactDuration()
    {
        await using var rig = await Rig.Create();
        var now = rig.Clock.Utc;
        await rig.Service.ScheduleAsync(rig.Id, null, Ct, continueAfterMinutes: 5);
        Assert.Equal(now.AddMinutes(5).UtcDateTime, (await rig.Service.GetAsync(rig.Id, Ct))!.ContinueAt);
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

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026, 9, 30, 14, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private sealed class FakeTerminal : ILimitTerminal
    {
        public LimitScreen Screen = new(UsageLimitParserTests.Codex("2:02 PM"), "pane:pid:start:boot", "UTC", true, "hash");
        public int Sends;
        public bool ThrowOnSend;
        public Task<LimitScreen> ReadAsync(Agent agent, CancellationToken ct) => Task.FromResult(Screen);
        public Task<bool> SendContinueAsync(Agent agent, LimitScreen expected, CancellationToken ct)
        {
            Sends++;
            if (ThrowOnSend) throw new IOException("Connection lost after sending");
            return Task.FromResult(true);
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
        public AgentContinuations NewService() => new(Factory, Terminal, new ManagerEvents(), Clock, NullLogger<AgentContinuations>.Instance);
        public static async Task<Rig> Create()
        {
            var rig = new Rig();
            rig.Factory = new Factory(rig._path);
            await using var db = rig.Factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync(Ct);
            var runner = new Runner { Id = Guid.NewGuid(), Name = "runner", State = RunnerState.Ready };
            db.Runners.Add(runner);
            db.Agents.Add(new Agent { Id = rig.Id, RunnerId = runner.Id, Name = "agent", HarnessId = "codex", HarnessSessionId = "session", State = AgentState.Running });
            await db.SaveChangesAsync(Ct);
            rig.Service = rig.NewService();
            return rig;
        }
        public ValueTask DisposeAsync() { Service.Dispose(); File.Delete(_path); return ValueTask.CompletedTask; }
    }
}
