using System.Text.Json.Nodes;
using DeveAgentManager.Contracts.Pets;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Pets;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Users;
using DeveAgentManager.Harnesses.Claude;
using DeveAgentManager.Harnesses.Codex;
using DeveAgentManager.Harnesses.Pi;
using DeveAgentManager.Pets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class PetTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string path = Path.Combine(Path.GetTempPath(), "dam-pets-" + Guid.NewGuid().ToString("N") + ".db");
    private Factory factory = null!;
    private PetService pets = null!;
    private readonly Guid person = Guid.NewGuid();
    private readonly Guid session = Guid.NewGuid();
    private CurrentUser User => TestUsers.Plain(person);
    public async ValueTask InitializeAsync() {
        factory = new(path); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync(Ct);
        db.Users.Add(new() { Id = person, Username = "pet-test", DisplayName = "Pet test", CreatedAt = DateTime.UtcNow.AddDays(-1) }); await db.SaveChangesAsync(Ct);
        pets = Service();
    }
    private PetService Service() => new(factory, new(), new(), new(factory, NullLogger<AuditLog>.Instance), TimeProvider.System);
    public ValueTask DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); return ValueTask.CompletedTask; }

    [Fact] public async Task SettingsAndPetsHaveSeparatePermissionsAndDefaults() {
        Assert.False((await pets.MineAsync(User, Ct)).CanEdit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pets.SaveOptionsAsync(User, new(true, true), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pets.PeopleAsync(User, Ct));
        await pets.SaveOptionsAsync(TestUsers.Admin, new(true, true), Ct);
        Assert.False((await pets.MineAsync(User, Ct)).Pet.Enabled); // existing users keep their switch
        var newcomer = Guid.NewGuid();
        await using (var db = factory.CreateDbContext()) { db.Users.Add(new() { Id = newcomer, Username = "new-pet", DisplayName = "New", CreatedAt = DateTime.UtcNow.AddSeconds(1) }); await db.SaveChangesAsync(Ct); }
        await pets.InitializeAsync(newcomer, Ct);
        Assert.True((await pets.MineAsync(TestUsers.Plain(newcomer), Ct)).Pet.Enabled);
        await pets.SaveOptionsAsync(TestUsers.Admin, new(false, true), Ct);
        Assert.True((await pets.MineAsync(TestUsers.Plain(newcomer), Ct)).Pet.Enabled);
        Assert.False((await pets.MineAsync(TestUsers.Plain(newcomer), Ct)).CanEdit);
        await pets.BulkAsync(TestUsers.Admin, false, Ct);
        Assert.False((await pets.MineAsync(TestUsers.Plain(newcomer), Ct)).Pet.Enabled);
    }
    [Fact] public async Task TypeChangesKeepGrowthAndByteIsTheChickenDefault() {
        Assert.Equal("Byte", new PetCatalog().Require("silkie").DefaultName);
        await pets.AdjustAsync(TestUsers.Admin, person, new(true, "Byte", "silkie", 10, 90), Ct);
        var before = (await pets.MineAsync(User, Ct)).Pet;
        await pets.SaveOptionsAsync(TestUsers.Admin, new(true), Ct);
        await pets.SavePreferencesAsync(User, new(true, "Snek", "snek"), Ct);
        var after = (await Service().MineAsync(User, Ct)).Pet;
        Assert.Equal(before.Experience, after.Experience); Assert.Equal(3, after.Stage); Assert.Equal(90, after.Happiness);
        await Assert.ThrowsAsync<ArgumentException>(() => pets.AdjustAsync(TestUsers.Admin, person, new(true, "X", "unknown", 10, 90), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => pets.AdjustAsync(TestUsers.Admin, person, new(true, "X", "snek", 0, 90), Ct));
    }
    [Fact] public async Task UsageIsAttributedOnceAndSurvivesCopiesRestartsAndSnoozing() {
        await pets.SaveOptionsAsync(TestUsers.Admin, new(true), Ct);
        await pets.SavePreferencesAsync(User, new(true, "Snek", "snek"), Ct);
        var at = DateTime.UtcNow;
        await using (var db = factory.CreateDbContext()) { db.PetInputClaims.Add(new() { AgentId = Guid.NewGuid(), SessionId = session, UserId = person, At = at }); await db.SaveChangesAsync(Ct); }
        var turn = new SessionUsageTurn { PromptId = "prompt-1", StartedAt = at, Tokens = 200_000 };
        await pets.CreditAsync(session, "codex:native-session", [turn], Ct);
        Assert.Equal(200_000, (await pets.MineAsync(User, Ct)).Pet.Experience);
        await Service().CreditAsync(Guid.NewGuid(), "codex:native-session", [turn], Ct);
        Assert.Equal(200_000, (await pets.MineAsync(User, Ct)).Pet.Experience);
        await pets.InteractAsync(User, "snooze", 5, Ct); turn.Tokens += 100_000;
        await pets.CreditAsync(session, "codex:native-session", [turn], Ct);
        Assert.Equal(300_000, (await pets.MineAsync(User, Ct)).Pet.Experience);
        await pets.SavePreferencesAsync(User, new(false, "Snek", "snek"), Ct); turn.Tokens += 100_000;
        await pets.CreditAsync(session, "codex:native-session", [turn], Ct);
        await pets.SavePreferencesAsync(User, new(true, "Snek", "snek"), Ct);
        await pets.CreditAsync(session, "codex:native-session", [turn], Ct);
        Assert.Equal(300_000, (await pets.MineAsync(User, Ct)).Pet.Experience);
        await pets.CreditAsync(session, "codex:native-session", [new() { PromptId = "old-import", StartedAt = at.AddDays(-30), Tokens = 9_000_000 }], Ct);
        Assert.Equal(300_000, (await pets.MineAsync(User, Ct)).Pet.Experience);
    }
    [Fact] public async Task ReEnablingDoesNotCreditWorkWhoseHistoryArrivedAfterItWasDisabled() {
        await pets.SaveOptionsAsync(TestUsers.Admin, new(true), Ct);
        await pets.SavePreferencesAsync(User, new(true, "Snek", "snek"), Ct);
        var at = DateTime.UtcNow;
        await using (var db = factory.CreateDbContext()) { db.PetInputClaims.Add(new() { AgentId = Guid.NewGuid(), SessionId = session, UserId = person, At = at }); await db.SaveChangesAsync(Ct); }
        await pets.SavePreferencesAsync(User, new(false, "Snek", "snek"), Ct);
        await pets.SavePreferencesAsync(User, new(true, "Snek", "snek"), Ct);
        await pets.CreditAsync(session, "claude:delayed", [new() { PromptId = "delayed", StartedAt = at, Tokens = 1_000_000 }], Ct);
        Assert.Equal(0, (await pets.MineAsync(User, Ct)).Pet.Experience);
    }
    [Fact] public async Task InteractionsHaveCooldownAndHidePreservesProgress() {
        await pets.SaveOptionsAsync(TestUsers.Admin, new(true), Ct);
        await pets.AdjustAsync(TestUsers.Admin, person, new(true, "Snek", "snek", 6, 70), Ct);
        for (var i = 0; i < 10; i++) await pets.InteractAsync(User, "pet", 0, Ct);
        Assert.Equal(73, (await pets.MineAsync(User, Ct)).Pet.Happiness);
        var before = (await pets.MineAsync(User, Ct)).Pet.Experience;
        await pets.InteractAsync(User, "hide", 0, Ct);
        var p = (await pets.MineAsync(User, Ct)).Pet;
        Assert.False(p.Enabled); Assert.Equal(before, p.Experience);
    }
    [Fact] public void CodexUsesCumulativeUsageWithoutCountingCachedAndReasoningSubsetsAgain() {
        var format = new CodexSessionFormat(); var cursor = new SessionReadCursor();
        var lines = new[] {
            """{"type":"response_item","timestamp":"2026-01-01T00:00:00Z","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}}""",
            """{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1000,"cached_input_tokens":900,"output_tokens":200,"reasoning_output_tokens":100,"total_tokens":1200}}}}""",
            """{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":1200}}}}""",
            """{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":1500}}}}"""
        };
        foreach (var line in lines) { var from = cursor.Session.Entries.Count; format.AppendLines(cursor, [(JsonObject)JsonNode.Parse(line)!]); cursor.Compact(from); }
        Assert.Equal(1500, Assert.Single(cursor.UsageTurns).Tokens); Assert.Null(cursor.Session.Entries[0].Content);
    }
    [Fact] public void ClaudeStreamingCopiesOfTheSameMessageOnlyCreditTheUsageIncrease() {
        var cursor = new SessionReadCursor(); var format = new ClaudeSessionFormat();
        foreach (var line in new[] {
            """{"type":"user","uuid":"u1","timestamp":"2026-01-01T00:00:00Z","message":{"role":"user","content":"hello"}}""",
            """{"type":"assistant","uuid":"a1","message":{"id":"msg-1","content":[],"usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":100}}}""",
            """{"type":"assistant","uuid":"a2","message":{"id":"msg-1","content":[],"usage":{"input_tokens":10,"output_tokens":15,"cache_read_input_tokens":100}}}"""
        }) format.AppendLines(cursor, [(JsonObject)JsonNode.Parse(line)!]);
        Assert.Equal(125, Assert.Single(cursor.UsageTurns).Tokens);
    }
    [Theory] [InlineData(1, 0)] [InlineData(3, 1)] [InlineData(6, 2)] [InlineData(10, 3)] [InlineData(1000, 3)]
    public void LevelsAndStagesRoundTrip(int level, int stage) { var experience = PetRules.LevelStart(level); Assert.Equal(level, PetRules.Level(experience)); Assert.Equal(stage, PetRules.Stage(level)); }
    private sealed class Factory(string file) : IDbContextFactory<ManagerDbContext> { public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite("Data Source=" + file + ";Pooling=False").Options); }
}
