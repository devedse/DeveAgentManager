using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Contracts.Pets;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Users;
using DeveAgentManager.Pets;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Pets;

/// <summary>Manager adapter for the pet module. All growth and settings writes are serialized and durable.</summary>
public sealed class PetService(IDbContextFactory<ManagerDbContext> factory, PetCatalog catalog, ManagerEvents events, AuditLog audit, TimeProvider clock)
{
    private readonly Dictionary<Guid, (AgentActivityStatus Status, long Completion)> activity = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private const string OptionsKey = "damagochi";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private async Task<PetOptions> Options(ManagerDbContext db, CancellationToken ct) =>
        await db.Settings.FindAsync([OptionsKey], ct) is { Value: { } value } ? JsonSerializer.Deserialize<PetOptions>(value, Json)! : new();
    public async Task<PetOptions> GetOptionsAsync(CancellationToken ct) { await using var db = await factory.CreateDbContextAsync(ct); return await Options(db, ct); }
    private static void RequireManager(CurrentUser actor) { if (!actor.Has(UserRoles.DamagochiManager)) throw new UnauthorizedAccessException("The Damagochi manager role is required."); }
    private async Task<UserPet> Pet(ManagerDbContext db, Guid userId, CancellationToken ct)
    {
        if (await db.UserPets.FindAsync([userId], ct) is { } pet) return pet;
        var user = await db.Users.FindAsync([userId], ct) ?? throw new KeyNotFoundException("User not found.");
        var options = await Options(db, ct);
        // The default is fixed when the user is created, never retroactively applied to existing accounts.
        var petDefault = await db.Settings.FindAsync([OptionsKey + "-default-since"], ct);
        var since = DateTime.TryParse(petDefault?.Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.MaxValue;
        pet = new UserPet { UserId = userId, Enabled = options.DefaultForNewUsers && user.CreatedAt >= since };
        db.UserPets.Add(pet); return pet;
    }
    private PetStateDto Dto(UserPet p)
    {
        var waiting = p.Species.Length == 0;
        var level = waiting ? 0 : PetRules.Level(p.Experience); var stage = waiting ? 0 : PetRules.Stage(level);
        return new(p.UserId, p.Name, p.Species, p.Enabled, p.Experience, level, stage, waiting ? "Unchosen egg" : catalog.Require(p.Species).Stages[stage],
            PetRules.LevelStart(level), waiting ? 0 : PetRules.LevelStart(Math.Min(PetRules.MaxLevel, level + 1)), waiting ? 0 : PetRules.StageProgress(p.Experience), p.Happiness,
            p.SnoozedUntil, p.ReducedMotion, p.Size, p.Revision, p.Reaction, p.ReactionAt, p.LifetimeTokens, p.Generation);
    }
    private void Changed(UserPet p, string reaction = "idle")
    {
        p.Revision++; p.Reaction = reaction; p.ReactionAt = Now;
    }
    private static async Task PauseUsage(ManagerDbContext db, Guid? userId, CancellationToken ct)
    {
        // Closing eligibility also closes in-flight turns, so delayed history cannot grant catch-up growth.
        await db.PetInputClaims.Where(c => userId == null || c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.PetUsageCredits.Where(c => userId == null || c.UserId == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(c => c.UserId, (Guid?)null), ct);
    }
    private void Notify(Guid? userId = null) => events.Publish(new ManagerEvent(ManagerEventKind.PetChanged, UserId: userId));
    public async Task InitializeAsync(Guid userId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); var p = await Pet(db, userId, ct); p.Enabled = (await Options(db, ct)).DefaultForNewUsers; await db.SaveChangesAsync(ct); }
        finally { gate.Release(); }
    }
    public async Task<MyPetDto> MineAsync(CurrentUser actor, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); var p = await Pet(db, actor.Id, ct); var options = await Options(db, ct);
            await db.SaveChangesAsync(ct); return new(options, actor.Has(UserRoles.DamagochiManager), options.Enabled || actor.Has(UserRoles.DamagochiManager), Dto(p), catalog.All); }
        finally { gate.Release(); }
    }
    public async Task SaveOptionsAsync(CurrentUser actor, PetOptions request, CancellationToken ct)
    {
        RequireManager(actor); await gate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct); var before = await Options(db, ct);
            foreach (var id in await db.Users.Select(u => u.Id).ToListAsync(ct)) await Pet(db, id, ct);
            var setting = await db.Settings.FindAsync([OptionsKey], ct);
            if (setting is null) { setting = new SettingEntry { Key = OptionsKey }; db.Settings.Add(setting); }
            if (before.Enabled && !request.Enabled) await PauseUsage(db, null, ct);
            setting.Value = JsonSerializer.Serialize(request, Json); setting.UpdatedAt = Now;
            if (request.DefaultForNewUsers && !before.DefaultForNewUsers) {
                // Materialize existing preferences before changing the new-account default.
                foreach (var id in await db.Users.Select(u => u.Id).ToListAsync(ct)) await Pet(db, id, ct);
                var since = await db.Settings.FindAsync([OptionsKey + "-default-since"], ct);
                if (since is null) { since = new SettingEntry { Key = OptionsKey + "-default-since" }; db.Settings.Add(since); }
                since.Value = Now.ToString("O"); since.UpdatedAt = Now;
            }
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        } finally { gate.Release(); }
        await audit.RecordAsync(actor, "PetSettingsChanged", "Damagochi", $"Enabled: {request.Enabled}; new users: {request.DefaultForNewUsers}", ct); Notify();
    }
    public async Task SavePreferencesAsync(CurrentUser actor, PetPreferences request, CancellationToken ct)
    {
        if (request.Size is < 90 or > 240) throw new ArgumentException("Damagochi size must be between 90 and 240.");
        await gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!(await Options(db, ct)).Enabled && !actor.Has(UserRoles.DamagochiManager)) throw new UnauthorizedAccessException("Damagochi is disabled by a Damagochi manager.");
            var p = await Pet(db, actor.Id, ct);
            RequireGeneration(p, request.Generation);
            if (p.Species != request.Species) throw new InvalidOperationException("Kill your current Damagochi before choosing a new one. Damagochi managers can change types in Manager settings.");
            var name = PetRules.CleanName(request.Name, p.Species.Length == 0);
            if (p.Species.Length == 0 && name.Length != 0) throw new InvalidOperationException("Choose a Damagochi and give it a name to hatch your egg.");
            if (p.Enabled && !request.Enabled) await PauseUsage(db, actor.Id, ct);
            p.Enabled = request.Enabled; p.Name = name; p.Species = request.Species;
            p.ReducedMotion = request.ReducedMotion; p.Size = request.Size; p.SnoozedUntil = null; Changed(p); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        } finally { gate.Release(); }
        Notify(actor.Id);
    }
    private static void RequireGeneration(UserPet pet, long generation)
    {
        if (pet.Generation != generation) throw new InvalidOperationException("Your Damagochi changed in another tab. Refresh and try again.");
    }
    public async Task AdoptAsync(CurrentUser actor, PetAdoption request, CancellationToken ct)
    {
        catalog.Require(request.Species); var name = PetRules.CleanName(request.Name);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!(await Options(db, ct)).Enabled && !actor.Has(UserRoles.DamagochiManager)) throw new UnauthorizedAccessException("Damagochi is disabled by a Damagochi manager.");
            var p = await Pet(db, actor.Id, ct); RequireGeneration(p, request.Generation);
            if (p.Species.Length != 0) throw new InvalidOperationException("Kill your current Damagochi before choosing a new one.");
            p.Generation++; p.Species = request.Species; p.Name = name; p.Experience = 0; p.Happiness = 70;
            p.Enabled = true; p.SnoozedUntil = null; p.LastInteractionAt = null; Changed(p, "adopted");
            await db.SaveChangesAsync(ct);
        }
        finally { gate.Release(); }
        Notify(actor.Id);
    }
    public async Task RetireAsync(CurrentUser actor, PetRetirement request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!(await Options(db, ct)).Enabled && !actor.Has(UserRoles.DamagochiManager)) throw new UnauthorizedAccessException("Damagochi is disabled by a Damagochi manager.");
            var p = await Pet(db, actor.Id, ct); RequireGeneration(p, request.Generation);
            if (p.Species.Length == 0) throw new InvalidOperationException("Choose a Damagochi first.");
            // Credits retain lifetime attribution. Their generation prevents delayed work growing the next pet.
            p.Generation++; p.Species = ""; p.Name = ""; p.Experience = 0; p.Happiness = 70;
            p.SnoozedUntil = null; p.LastInteractionAt = null; Changed(p, "retired");
            await db.SaveChangesAsync(ct);
        }
        finally { gate.Release(); }
        await audit.RecordAsync(actor, "PetRetired", "Damagochi", "Damagochi progress reset; lifetime tokens preserved.", ct);
        Notify(actor.Id);
    }
    public async Task InteractAsync(CurrentUser actor, string action, int minutes, CancellationToken ct)
    {
        if (action is not ("pet" or "play" or "snooze" or "wake" or "hide")) throw new ArgumentException("Unknown Damagochi interaction.");
        if (action == "snooze" && minutes is not (5 or 15 or 60)) throw new ArgumentException("Choose 5, 15 or 60 minutes.");
        await gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct); var p = await Pet(db, actor.Id, ct);
            if (!(await Options(db, ct)).Enabled || !p.Enabled) throw new InvalidOperationException("Enable your Damagochi first.");
            if (p.Species.Length == 0 && action is "pet" or "play") throw new InvalidOperationException("Choose and name your Damagochi first.");
            if (action == "hide") { p.Enabled = false; await PauseUsage(db, actor.Id, ct); }
            if (action == "snooze") p.SnoozedUntil = Now.AddMinutes(minutes);
            if (action == "wake") p.SnoozedUntil = null;
            if (action is "pet" or "play" && (p.LastInteractionAt is null || Now - p.LastInteractionAt >= TimeSpan.FromSeconds(30))) {
                p.Happiness = Math.Min(100, p.Happiness + 3); p.LastInteractionAt = Now;
            }
            Changed(p, action == "play" ? "hop" : "happy"); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        } finally { gate.Release(); }
        Notify(actor.Id);
    }
    public async Task<IReadOnlyList<PetPersonDto>> PeopleAsync(CurrentUser actor, CancellationToken ct)
    {
        RequireManager(actor); await gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
            var result = new List<PetPersonDto>(); foreach (var user in users) result.Add(new(user.Id, user.DisplayName, user.Username, Dto(await Pet(db, user.Id, ct))));
            await db.SaveChangesAsync(ct); return result;
        } finally { gate.Release(); }
    }
    public async Task AdjustAsync(CurrentUser actor, Guid userId, PetAdjustment request, CancellationToken ct)
    {
        RequireManager(actor); if (request.Species.Length != 0) catalog.Require(request.Species); var name = PetRules.CleanName(request.Name, true);
        if (request.Level is < 1 or > PetRules.MaxLevel || request.Happiness is < 0 or > 100) throw new ArgumentException("Choose level 1–1000 and happiness 0–100.");
        if (request.Experience < 0 || request.LifetimeTokens < 0) throw new ArgumentException("Token counts cannot be negative.");
        await gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct); var p = await Pet(db, userId, ct);
            if (p.Enabled && !request.Enabled) await PauseUsage(db, userId, ct);
            if ((p.Species.Length == 0) != (request.Species.Length == 0)) p.Generation++;
            p.Name = name; p.Species = request.Species; p.Enabled = request.Enabled; p.Happiness = request.Happiness;
            if (p.Species.Length == 0) { p.Name = ""; p.Experience = 0; }
            else if (request.Experience is { } xp) p.Experience = xp;
            else if (PetRules.Level(p.Experience) != request.Level) p.Experience = PetRules.LevelStart(request.Level);
            if (request.LifetimeTokens is { } total) p.LifetimeTokens = total;
            Changed(p); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        } finally { gate.Release(); }
        await audit.RecordAsync(actor, "PetAdjusted", $"User {userId}", $"Species: {request.Species}; level: {request.Level}; Damagochi tokens: {request.Experience}; lifetime tokens: {request.LifetimeTokens}; happiness: {request.Happiness}; enabled: {request.Enabled}", ct); Notify(userId);
    }
    public async Task BulkAsync(CurrentUser actor, bool enabled, CancellationToken ct)
    {
        RequireManager(actor); await gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!enabled) await PauseUsage(db, null, ct);
            foreach (var id in await db.Users.Select(u => u.Id).ToListAsync(ct)) { var p = await Pet(db, id, ct); p.Enabled = enabled; Changed(p); }
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        } finally { gate.Release(); }
        await audit.RecordAsync(actor, "PetsBulkChanged", "All users", $"Enabled: {enabled}", ct); Notify();
    }
    // Input is only an attribution candidate; a real user message and real usage must subsequently appear in history.
    public async Task ClaimInputAsync(Guid agentId, Guid userId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); if (!(await Options(db, ct)).Enabled) return;
            var pet = await Pet(db, userId, ct); if (!pet.Enabled) return;
            var session = await db.Sessions.Where(s => s.AgentId == agentId).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            if (session is null) return;
            db.PetInputClaims.Add(new() { AgentId = agentId, SessionId = session.Value, UserId = userId, Generation = pet.Generation, At = Now });
            await db.PetInputClaims.Where(c => c.At < Now.AddDays(-7)).ExecuteDeleteAsync(ct); await db.SaveChangesAsync(ct);
        } finally { gate.Release(); }
    }
    public async Task ReactAsync(Guid userId, string reaction, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); var options = await Options(db, ct); if (!options.Enabled) return;
            var p = await Pet(db, userId, ct); if (!p.Enabled || p.Species.Length == 0 || p.ReactionAt is { } at && Now - at < TimeSpan.FromSeconds(2)) return;
            Changed(p, reaction); await db.SaveChangesAsync(ct);
        } finally { gate.Release(); }
        Notify(userId);
    }
    public async Task ObserveActivityAsync(Guid agentId, string harness, string? nativeId, AgentActivityDto state, CancellationToken ct)
    {
        if (nativeId is null) return;
        Guid? userId = null; string? reaction = null;
        await gate.WaitAsync(ct);
        try {
            var previous = activity.GetValueOrDefault(agentId);
            var known = activity.ContainsKey(agentId);
            activity[agentId] = (state.Status, state.CompletionSequence);
            if (state.Status == previous.Status && state.CompletionSequence == previous.Completion) return;
            reaction = state.Status switch {
                AgentActivityStatus.Working => "working",
                AgentActivityStatus.NeedsInput => "surprised",
                AgentActivityStatus.UsageLimited => "sleep",
                AgentActivityStatus.Ready when known && state.CurrentTurnCompleted && state.CompletionSequence > previous.Completion => "happy",
                AgentActivityStatus.Ready => "idle",
                _ => null
            };
            if (reaction is null) return;
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!(await Options(db, ct)).Enabled) return;
            var sessionId = await db.Sessions.Where(s => s.AgentId == agentId).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            var candidate = await db.PetInputClaims.Where(c => c.SessionId == sessionId).OrderByDescending(c => c.At).Select(c => (Guid?)c.UserId).FirstOrDefaultAsync(ct);
            if (candidate is { } id && await db.PetUsageCredits.AnyAsync(c => c.Source == harness + ":" + nativeId && c.UserId == id, ct)) userId = id;
        } finally { gate.Release(); }
        if (userId is { } user && reaction is not null) await ReactAsync(user, reaction, ct);
    }
    public async Task CreditAsync(Guid sessionId, string source, IReadOnlyList<SessionUsageTurn> turns, CancellationToken ct)
    {
        var notified = new HashSet<Guid>(); await gate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct); var options = await Options(db, ct);
            // No old history backfill: the first observation establishes a baseline, and unmatched work earns nothing.
            var claims = await db.PetInputClaims.Where(c => c.SessionId == sessionId).OrderBy(c => c.At).ToListAsync(ct);
            var existing = await db.PetUsageCredits.Where(c => c.Source == source).ToDictionaryAsync(c => c.PromptId, ct);
            var firstClaim = claims.Count == 0 ? DateTime.MaxValue : claims[0].At.AddSeconds(-5);
            foreach (var turn in turns.Where(t => t.Tokens > 0 && (existing.ContainsKey(t.PromptId) || t.StartedAt >= firstClaim))) {
                var credit = existing.GetValueOrDefault(turn.PromptId);
                if (credit is null) {
                    var claim = claims.LastOrDefault(c => c.At <= turn.StartedAt.AddSeconds(5) && c.At >= turn.StartedAt.AddMinutes(-2));
                    credit = new PetUsageCredit { Source = source, PromptId = turn.PromptId, UserId = claim?.UserId, Generation = claim?.Generation ?? 0 };
                    db.PetUsageCredits.Add(credit); existing[turn.PromptId] = credit;
                }
                var delta = Math.Max(0, turn.Tokens - credit.Tokens); credit.Tokens = Math.Max(credit.Tokens, turn.Tokens);
                if (delta == 0 || credit.UserId is not { } userId || !options.Enabled || !await db.Users.AnyAsync(u => u.Id == userId && !u.Disabled, ct)) continue;
                var p = await Pet(db, userId, ct); if (!p.Enabled) continue;
                p.LifetimeTokens = Math.Min(long.MaxValue - delta, p.LifetimeTokens) + delta;
                if (p.Species.Length != 0 && credit.Generation == p.Generation) {
                    var level = PetRules.Level(p.Experience); p.Experience = Math.Min(long.MaxValue - delta, p.Experience) + delta;
                    p.Happiness = Math.Min(100, p.Happiness + 1); Changed(p, PetRules.Level(p.Experience) > level ? "level" : "working");
                }
                else Changed(p);
                notified.Add(userId);
            }
            await db.SaveChangesAsync(ct);
        } finally { gate.Release(); }
        foreach (var id in notified) Notify(id);
    }
}
