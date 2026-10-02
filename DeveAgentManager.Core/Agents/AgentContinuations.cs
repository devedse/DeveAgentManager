using System.Collections.Concurrent;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

/// <summary>Durable, opt-in, one-shot continuations. No browser timer and no automatic rearming after another limit.</summary>
public sealed class AgentContinuations(IDbContextFactory<ManagerDbContext> factory, ILimitTerminal terminal,
    ManagerEvents events, TimeProvider clock, ILogger<AgentContinuations> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private SemaphoreSlim Gate(Guid id) => _gates.GetOrAdd(id, _ => new(1, 1));
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Session(Agent a) => UsageLimitState.Session(a);

    public async Task<AgentLimitDto?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AgentLimits.AsNoTracking().FirstOrDefaultAsync(l => l.AgentId == id, ct);
        return row is null || row.Status == "Cleared" ||
            (row.Status is not ("Scheduled" or "Sending" or "Uncertain") && UsageLimitState.HasExpired(row, Now)) ? null : Dto(row);
    }

    private static AgentLimitDto Dto(AgentLimit l)
    {
        // Re-read the original message at its original observation time. ResetAt is the user's
        // selected reset and may have been overridden; it must never masquerade as detection.
        var detected = l.Manual ? null : UsageLimitState.DetectedReset(l);
        return new(l.Manual ? "Manual continuation — no usage limit is required." : l.Message, l.TimeZoneId, l.DetectedAt, l.ResetAt, l.ContinueAt, l.Status, l.Detail, l.NativeAutoContinue, l.Manual, detected);
    }

    public async Task ScheduleAsync(Guid id, DateTime? resetOverride, CancellationToken ct, DateTime? expectedDetectedAt = null, double? continueAfterMinutes = null, bool manual = false)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            await RefreshLockedAsync(id, false, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var agent = await db.Agents.Include(a => a.Runner).FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new KeyNotFoundException("Agent not found.");
            var limit = await db.AgentLimits.FindAsync([id], ct);
            if (manual)
            {
                if (limit is { Manual: false } && limit.Status != "Cleared" && !UsageLimitState.HasExpired(limit, Now))
                    throw new InvalidOperationException("A usage limit was detected. Reopen the dialog to review it.");
                if (limit?.Status is "Scheduled" or "Sending" or "Uncertain")
                    throw new InvalidOperationException("Review or cancel the existing continuation first.");
                if (continueAfterMinutes is null || resetOverride is not null)
                    throw new ArgumentException("Choose a duration for a manual continuation.");
                if (agent.State != AgentState.Running || agent.Runner?.State != RunnerState.Ready || agent.HarnessId is not ("codex" or "claude"))
                    throw new InvalidOperationException("Manual continuation requires a running Codex or Claude agent on a ready Runner.");
                var initial = await terminal.ReadAsync(agent, ct);
                if (limit is null) { limit = new AgentLimit { AgentId = id }; db.AgentLimits.Add(limit); }
                var preserveExpiredMessage = UsageLimitState.HasExpired(limit, Now)
                    && limit.SessionId == Session(agent) && limit.PaneIdentity == initial.Identity;
                limit.Manual = true;
                limit.PaneIdentity = initial.Identity;
                limit.SessionId = Session(agent);
                if (!preserveExpiredMessage)
                {
                    limit.Message = "Manual continuation — no usage limit is required.";
                    limit.DetectedAt = Now;
                }
                limit.ResetAt = null;
                limit.Status = "Detected";
                limit.NativeAutoContinue = false;
            }
            if (limit is null || limit.Status == "Cleared") throw new InvalidOperationException("No current usage-limit message was found.");
            if (!manual && expectedDetectedAt is { } expected && expected != limit.DetectedAt) throw new InvalidOperationException("The usage limit changed while the dialog was open. Close it and review the new reset time.");
            if (limit.NativeAutoContinue) throw new InvalidOperationException("Claude is already continuing automatically; no second continuation will be scheduled.");
            if (limit.Status is "Scheduled" or "Sending" or "Sent" or "Uncertain") throw new InvalidOperationException("This limit already has a continuation. Cancel a pending one before changing its time.");
            var now = Now;
            var reset = resetOverride ?? limit.ResetAt;
            DateTime continueAt;
            if (continueAfterMinutes is { } duration)
            {
                if (!double.IsFinite(duration) || duration < 1 || duration > 46080 || resetOverride is not null)
                    throw new ArgumentException("Choose either a reset time or a continuation duration from 1 minute to 32 days.");
                continueAt = now.AddMinutes(duration);
            }
            else
            {
                if (reset is null) throw new ArgumentException("Choose the reset date and time.");
                if (reset.Value.Kind != DateTimeKind.Utc || reset.Value <= now.AddMinutes(-1) || reset > now.AddDays(32))
                    throw new ArgumentException("The reset must be UTC, with its continuation in the future and within 32 days.");
                continueAt = reset.Value.AddMinutes(1);
            }
            limit.ResetAt = reset;
            limit.ContinueAt = continueAt;
            limit.Status = "Scheduled";
            limit.Detail = continueAfterMinutes is not null ? "Continue scheduled after the chosen duration; the detected allowance reset is unchanged."
                : resetOverride is null ? null : "Reset time confirmed manually.";
            await db.SaveChangesAsync(ct);
            events.AgentChanged(agent.RunnerId, id);
        }
        finally { gate.Release(); }
    }

    public async Task CancelAsync(Guid id, string reason, CancellationToken ct)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var changed = await db.AgentLimits.Where(l => l.AgentId == id && l.Status == "Scheduled")
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "Cancelled").SetProperty(l => l.ContinueAt, (DateTime?)null).SetProperty(l => l.Detail, reason), ct);
            if (changed > 0 && await db.Agents.FindAsync([id], ct) is { } a) events.AgentChanged(a.RunnerId, id);
        }
        finally { gate.Release(); }
    }

    public async Task RefreshAsync(Guid id, CancellationToken ct)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try { await RefreshLockedAsync(id, true, ct); }
        finally { gate.Release(); }
    }

    private async Task RefreshLockedAsync(Guid id, bool dispatch, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var agent = await db.Agents.Include(a => a.Runner).FirstOrDefaultAsync(a => a.Id == id, ct);
        var row = await db.AgentLimits.FindAsync([id], ct);
        if (agent is null) return;
        LimitScreen? screen = null;
        ParsedUsageLimit? parsed = null;
        if (agent.State == AgentState.Running && agent.Runner?.State == RunnerState.Ready && agent.HarnessId is "codex" or "claude")
        {
            screen = await terminal.ReadAsync(agent, ct);
            parsed = UsageLimitParser.Parse(agent.HarnessId, screen.Screen, screen.TimeZone, Now);
        }

        // An explicitly scheduled attempt is independent of later screen text or usage-limit detection.
        // Only the user cancels it. If it is due but cannot safely submit, leave it armed and explain why.
        if (row?.Status == "Sending")
        {
            row.Status = "Uncertain";
            row.Detail = "The Manager restarted during sending. Check the terminal; it will not send again automatically.";
        }
        else if (row?.Status == "Scheduled")
        {
            if (dispatch && row.ContinueAt <= Now)
            {
                var activity = screen is null ? AgentActivityStatus.Unknown
                    : AgentScreenDetector.Detect(agent.HarnessId, screen.Screen, Now).Status;
                row.Detail = Session(agent) != row.SessionId ? "Waiting: the agent's harness or session changed. Cancel this schedule to replace it."
                    : screen is null ? "Waiting for the agent and Runner to be available."
                    : !screen.InputEmpty ? "Waiting for an empty prompt. Your draft will not be submitted."
                    : parsed?.NativeAutoContinue == true ? "Waiting while Claude handles its own automatic continuation."
                    : agent.Working || activity is not (AgentActivityStatus.Ready or AgentActivityStatus.UsageLimited)
                        ? "Waiting for the agent to be idle and ready for input." : null;
                if (row.Detail is null)
                {
                    // Claim before the network side effect. Ambiguous delivery must never cause a duplicate send.
                    var claimed = await db.AgentLimits.Where(l => l.AgentId == id && l.Status == "Scheduled")
                        .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "Sending"), ct);
                    if (claimed == 0) return;
                    try
                    {
                        var sent = await terminal.SendContinueAsync(agent, screen!, ct);
                        row.Status = sent ? "Sent" : "Scheduled";
                        // Force Scheduled to be written back after the database-only claim above.
                        db.Entry(row).Property(l => l.Status).IsModified = true;
                        row.Detail = sent ? "Submitted continue once. If the harness reaches its limit again, review the new reset before scheduling another attempt."
                            : "Waiting because the terminal changed during submission. Check the prompt; inserted text may still be present.";
                    }
                    catch (Exception ex)
                    {
                        row.Status = "Uncertain";
                        row.Detail = "Could not confirm delivery. Check the terminal; it will not send again automatically.";
                        logger.LogWarning(ex, "Could not confirm scheduled continuation for agent {Agent}", id);
                    }
                }
            }
        }
        else if (row is not null && row.Status != "Uncertain" && UsageLimitState.HasExpired(row, Now)
            && (parsed is null || (parsed.Message == row.Message && Session(agent) == row.SessionId && screen?.Identity == row.PaneIdentity)))
        {
            row.Status = "Expired";
            row.ContinueAt = null;
            row.Detail = "The detected reset has passed. The prompt was paused by a usage limit.";
        }
        else if (row?.Status == "Sent" && parsed is null && screen is not null &&
            AgentScreenDetector.Detect(agent.HarnessId, screen.Screen, Now).Status is AgentActivityStatus.Ready or AgentActivityStatus.Working)
        {
            // A successful continuation is no longer an active limit once normal activity resumes.
            // Require positive live evidence: a missing connection or unrecognized screen is not recovery.
            row.Status = "Cleared";
            row.ContinueAt = null;
            row.Detail = null;
        }
        else if (row?.Status is "Sent" or "Uncertain" && (parsed is null || parsed.Message == row.Message))
        {
            // Retain delivery uncertainty, or a sent result until the limit clears and normal activity is observed.
        }
        else if (parsed is null)
        {
            if (row is null || row.Status == "Cleared" || row.Manual) return;
            row.Status = "Cleared";
            row.ContinueAt = null;
        }
        else
        {
            if (row is null) { row = new AgentLimit { AgentId = id }; db.AgentLimits.Add(row); }
            if (row.Manual || row.Message != parsed.Message || row.SessionId != Session(agent) || row.PaneIdentity != screen!.Identity || row.Status == "Cleared" || row.TimeZoneId != parsed.TimeZoneId)
            {
                row.Manual = false;
                row.Message = parsed.Message;
                row.SessionId = Session(agent);
                row.PaneIdentity = screen!.Identity;
                row.TimeZoneId = parsed.TimeZoneId;
                row.DetectedAt = Now;
                row.ResetAt = parsed.ResetAt;
                row.ContinueAt = null;
                row.Status = "Detected";
                row.Detail = parsed.Detail;
            }
            // A parser update can resolve an already-detected message without changing its text.
            // Repair only an unresolved detection, never a chosen reset or an armed schedule.
            if (row.Status == "Detected" && row.ResetAt is null && parsed.ResetAt is not null)
            {
                row.ResetAt = parsed.ResetAt;
                row.Detail = parsed.Detail;
            }
            row.NativeAutoContinue = parsed.NativeAutoContinue;
        }
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            events.AgentChanged(agent.RunnerId, id);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var ids = await db.Agents.Where(a => a.HarnessId == "codex" || a.HarnessId == "claude").Select(a => a.Id).ToListAsync(stoppingToken);
                await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken }, async (id, ct) =>
                {
                    try { await RefreshAsync(id, ct); }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogDebug(ex, "Could not read usage limit for agent {Agent}", id); }
                });
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(ex, "Usage-limit monitor pass failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
