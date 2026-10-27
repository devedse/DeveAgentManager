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
    [Fact] public async Task OnlyManagersCanChangeSpeciesAndNamesStartEmpty() {
        Assert.All(new PetCatalog().All, definition => Assert.Equal("", definition.DefaultName));
        await pets.AdjustAsync(TestUsers.Admin, person, new(true, "Byte", "silkie", 10, 90), Ct);
        var before = (await pets.MineAsync(User, Ct)).Pet;
        await pets.SaveOptionsAsync(TestUsers.Admin, new(true), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pets.SavePreferencesAsync(User, new(true, "Snek", "snek", Generation: 1), Ct));
        await pets.AdjustAsync(TestUsers.Admin, person, new(true, "Snek", "snek", 10, 90), Ct);
        var after = (await Service().MineAsync(User, Ct)).Pet;
        Assert.Equal(before.Experience, after.Experience); Assert.Equal(3, after.Stage); Assert.Equal(90, after.Happiness);
        await Assert.ThrowsAsync<ArgumentException>(() => pets.AdjustAsync(TestUsers.Admin, person, new(true, "X", "unknown", 10, 90), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => pets.AdjustAsync(TestUsers.Admin, person, new(true, "X", "snek", 0, 90), Ct));
    }
    [Fact] public async Task UsageIsAttributedOnceAndSurvivesCopiesRestartsAndSnoozing() {
        await pets.SaveOptionsAsync(TestUsers.Admin, new(true), Ct);
        await pets.AdoptAsync(User, new("Snek", "snek", 0), Ct);
        var at = DateTime.UtcNow;
        await using (var db = factory.CreateDbContext()) { db.PetInputClaims.Add(new() { AgentId = Guid.NewGuid(), SessionId = session, UserId = person, Generation = 1, At = at }); await db.SaveChangesAsync(Ct); }
        var turn = new SessionUsageTurn { PromptId = "prompt-1", StartedAt = at, Tokens = 200_000 };
        await pets.CreditAsync(session, "codex:native-session", [turn], Ct);
        Assert.Equal(200_000, (await pets.MineAsync(User, Ct)).Pet.Experience);
        await Service().CreditAsync(Guid.NewGuid(), "codex:native-session", [turn], Ct);
        Assert.Equal(200_000, (await pets.MineAsync(User, Ct)).Pet.Experience);
        await pets.InteractAsync(User, "snooze", 5, Ct); turn.Tokens += 100_000;
        await pets.CreditAsync(session, "codex:native-session", [turn], Ct);
        Assert.Equal(300_000, (await pets.MineAsync(User, Ct)).Pet.Experience);
        await pets.SavePreferencesAsync(User, new(false, "Snek", "snek", Generation: 1), Ct); turn.Tokens += 100_000;
        await pets.CreditAsync(session, "codex:native-session", [turn], Ct);
        await pets.SavePreferencesAsync(User, new(true, "Snek", "snek", Generation: 1), Ct);
        await pets.CreditAsync(session, "codex:native-session", [turn], Ct);
        Assert.Equal(300_000, (await pets.MineAsync(User, Ct)).Pet.Experience);
        Assert.Equal(400_000, (await pets.MineAsync(User, Ct)).Pet.LifetimeTokens);
        await pets.CreditAsync(session, "codex:native-session", [new() { PromptId = "old-import", StartedAt = at.AddDays(-30), Tokens = 9_000_000 }], Ct);
        Assert.Equal(300_000, (await pets.MineAsync(User, Ct)).Pet.Experience);
    }
    [Fact] public async Task ReEnablingDoesNotCreditWorkWhoseHistoryArrivedAfterItWasDisabled() {
        await pets.SaveOptionsAsync(TestUsers.Admin, new(true), Ct);
        await pets.AdoptAsync(User, new("Snek", "snek", 0), Ct);
        var at = DateTime.UtcNow;
        await using (var db = factory.CreateDbContext()) { db.PetInputClaims.Add(new() { AgentId = Guid.NewGuid(), SessionId = session, UserId = person, Generation = 1, At = at }); await db.SaveChangesAsync(Ct); }
        await pets.SavePreferencesAsync(User, new(false, "Snek", "snek", Generation: 1), Ct);
        await pets.SavePreferencesAsync(User, new(true, "Snek", "snek", Generation: 1), Ct);
        await pets.CreditAsync(session, "claude:delayed", [new() { PromptId = "delayed", StartedAt = at, Tokens = 1_000_000 }], Ct);
        Assert.Equal(0, (await pets.MineAsync(User, Ct)).Pet.Experience);
        Assert.Equal(1_000_000, (await pets.MineAsync(User, Ct)).Pet.LifetimeTokens);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AccountUsageCountsIndependentlyOfModuleAndPersonalGrowthSwitches(bool moduleEnabled, bool personalEnabled) {
        await pets.SaveOptionsAsync(TestUsers.Admin, new(moduleEnabled), Ct);
        await pets.AdjustAsync(TestUsers.Admin, person, new(personalEnabled, "Byte", "silkie", 1, 70), Ct);
        var agent = Guid.NewGuid(); var runner = Guid.NewGuid();
        await using (var db = factory.CreateDbContext()) {
            db.Runners.Add(new() { Id = runner, OwnerId = person });
            db.Agents.Add(new() { Id = agent, RunnerId = runner, OwnerId = person });
            db.Sessions.Add(new() { Id = session, AgentId = agent, OwnerId = person, HarnessId = "codex" });
            await db.SaveChangesAsync(Ct);
        }
        await pets.ClaimInputAsync(agent, person, Ct);
        DateTime at;
        await using (var db = factory.CreateDbContext()) {
            var claim = await db.PetInputClaims.SingleAsync(Ct);
            Assert.Equal(moduleEnabled && personalEnabled, claim.PetEligible); at = claim.At;
        }
        var turn = new SessionUsageTurn { PromptId = "account-work", StartedAt = at, Tokens = 200_000 };
        await pets.CreditAsync(session, "codex:account", [turn], Ct);
        await Service().CreditAsync(Guid.NewGuid(), "codex:account", [turn], Ct);
        var state = (await Service().MineAsync(User, Ct)).Pet;
        Assert.Equal(200_000, state.LifetimeTokens);
        Assert.Equal(moduleEnabled && personalEnabled ? 200_000 : 0, state.Experience);
        if (!moduleEnabled || !personalEnabled) {
            await pets.SaveOptionsAsync(TestUsers.Admin, new(true), Ct);
            await pets.SavePreferencesAsync(User, new(true, "Byte", "silkie", Generation: state.Generation), Ct);
            turn.Tokens += 100_000;
            await pets.CreditAsync(session, "codex:account", [turn], Ct);
            state = (await Service().MineAsync(User, Ct)).Pet;
            Assert.Equal(300_000, state.LifetimeTokens); Assert.Equal(0, state.Experience);
        }
    }
    [Theory]
    [InlineData("pet", 0, "happy")]
    [InlineData("play", 0, "hop")]
    [InlineData("wake", 0, "happy")]
    [InlineData("snooze", 5, "sad")]
    [InlineData("snooze", 15, "sad")]
    [InlineData("snooze", 60, "sad")]
    [InlineData("hide", 0, "sad")]
    public async Task InteractionReactionsMatchTheAction(string action, int minutes, string reaction) {
        await pets.SaveOptionsAsync(TestUsers.Admin, new(true), Ct);
        await pets.AdjustAsync(TestUsers.Admin, person, new(true, "Byte", "silkie", 1, 70), Ct);
        await pets.InteractAsync(User, action, minutes, Ct);
        Assert.Equal(reaction, (await pets.MineAsync(User, Ct)).Pet.Reaction);
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
    [Fact] public async Task BulkEnablingStartsWithAnUnchosenUnnamedEggAndAdoptionStartsAtZero() {
        await pets.SaveOptionsAsync(TestUsers.Admin, new(true), Ct);
        await pets.BulkAsync(TestUsers.Admin, true, Ct);
        var egg = (await pets.MineAsync(User, Ct)).Pet;
        Assert.True(egg.NeedsAdoption); Assert.Equal("", egg.Name); Assert.Equal(0, egg.Stage); Assert.Equal(0, egg.Level);
        await Assert.ThrowsAsync<ArgumentException>(() => pets.AdoptAsync(User, new(" ", "silkie", 0), Ct));
        var at = DateTime.UtcNow;
        await using (var db = factory.CreateDbContext()) { db.PetInputClaims.Add(new() { SessionId = session, UserId = person, At = at }); await db.SaveChangesAsync(Ct); }
        await pets.CreditAsync(session, "codex:egg", [new() { PromptId = "egg-work", StartedAt = at, Tokens = 4_000_000 }], Ct);
        egg = (await pets.MineAsync(User, Ct)).Pet;
        Assert.Equal(4_000_000, egg.LifetimeTokens); Assert.Equal(0, egg.Experience);
        await pets.AdoptAsync(User, new("Byte", "silkie", 0), Ct);
        var pet = (await pets.MineAsync(User, Ct)).Pet;
        Assert.False(pet.NeedsAdoption); Assert.Equal(1, pet.Level); Assert.Equal(1, pet.Stage); Assert.Equal(0, pet.Experience);
        Assert.Equal(4_000_000, pet.LifetimeTokens);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pets.AdoptAsync(User, new("Other", "snek", pet.Generation), Ct));
    }
    [Fact] public async Task RetirementPreservesLifetimeAndDelayedUsageCannotGrowTheReplacement() {
        await pets.SaveOptionsAsync(TestUsers.Admin, new(true), Ct);
        await pets.AdoptAsync(User, new("Byte", "silkie", 0), Ct);
        var at = DateTime.UtcNow;
        await using (var db = factory.CreateDbContext()) { db.PetInputClaims.Add(new() { SessionId = session, UserId = person, Generation = 1, At = at }); await db.SaveChangesAsync(Ct); }
        var turn = new SessionUsageTurn { PromptId = "first-pet", StartedAt = at, Tokens = 4_000_000 };
        await pets.CreditAsync(session, "codex:reset", [turn], Ct);
        await pets.RetireAsync(User, new(1), Ct);
        var egg = (await Service().MineAsync(User, Ct)).Pet;
        Assert.True(egg.NeedsAdoption); Assert.Equal(4_000_000, egg.LifetimeTokens); Assert.Equal(0, egg.Experience);
        await pets.AdoptAsync(User, new("Snek", "snek", egg.Generation), Ct);
        turn.Tokens += 2_000_000;
        await pets.CreditAsync(session, "codex:reset", [turn, new() { PromptId = "delayed-old-work", StartedAt = at, Tokens = 100_000 }], Ct);
        var pet = (await pets.MineAsync(User, Ct)).Pet;
        Assert.Equal(6_100_000, pet.LifetimeTokens); Assert.Equal(0, pet.Experience); Assert.Equal(1, pet.Level);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pets.RetireAsync(User, new(1), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pets.SavePreferencesAsync(User, new(true, "Old", "silkie", Generation: 1), Ct));
        await using (var db = factory.CreateDbContext()) { db.PetInputClaims.Add(new() { SessionId = session, UserId = person, Generation = pet.Generation, At = at.AddSeconds(10) }); await db.SaveChangesAsync(Ct); }
        await pets.CreditAsync(session, "codex:reset", [new() { PromptId = "new-work", StartedAt = at.AddSeconds(10), Tokens = 500_000 }], Ct);
        pet = (await Service().MineAsync(User, Ct)).Pet;
        Assert.Equal(6_600_000, pet.LifetimeTokens); Assert.Equal(500_000, pet.Experience);
    }
    [Fact] public async Task ManagerCanSetExactCountersAndReturnAPetToAnEgg() {
        await pets.AdjustAsync(TestUsers.Admin, person, new(true, "Byte", "silkie", 1, 40, 4_000_000, 12_000_000), Ct);
        var pet = (await pets.MineAsync(User, Ct)).Pet;
        Assert.Equal(1, pet.Level); Assert.Equal(4_000_000, pet.Experience); Assert.Equal(12_000_000, pet.LifetimeTokens);
        await pets.AdjustAsync(TestUsers.Admin, person, new(true, "", "", 1, 70), Ct);
        pet = (await pets.MineAsync(User, Ct)).Pet;
        Assert.True(pet.NeedsAdoption); Assert.Equal(12_000_000, pet.LifetimeTokens); Assert.Equal(0, pet.Experience);
    }
    [Theory]
    [InlineData(4_000_000, 1, 1)]
    [InlineData(64_203_424, 2, 1)]
    [InlineData(749_999_999, 5, 1)]
    [InlineData(750_000_000, 6, 2)]
    [InlineData(2_249_999_999, 9, 2)]
    [InlineData(2_250_000_000, 10, 3)]
    [InlineData(long.MaxValue, PetRules.MaxLevel, 3)]
    public void GrowthTakesDaysOfHeavyUsageAndHandlesLargeBalances(long tokens, int level, int stage) {
        Assert.Equal(level, PetRules.Level(tokens)); Assert.Equal(stage, PetRules.Stage(PetRules.Level(tokens)));
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
    [Theory] [InlineData(1, 1)] [InlineData(3, 1)] [InlineData(6, 2)] [InlineData(10, 3)] [InlineData(1000, 3)]
    public void LevelsAndStagesRoundTrip(int level, int stage) { var experience = PetRules.LevelStart(level); Assert.Equal(level, PetRules.Level(experience)); Assert.Equal(stage, PetRules.Stage(level)); }
    private sealed class Factory(string file) : IDbContextFactory<ManagerDbContext> { public ManagerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite("Data Source=" + file + ";Pooling=False").Options); }
}
