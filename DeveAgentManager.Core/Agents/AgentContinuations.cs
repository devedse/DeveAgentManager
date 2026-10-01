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
    private readonly ConcurrentDictionary<Guid, byte> _armed = new();
    private SemaphoreSlim Gate(Guid id) => _gates.GetOrAdd(id, _ => new(1, 1));
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Session(Agent a) => $"{a.HarnessId}|{a.HarnessSessionId}|{a.AccountId}";

    public async Task<AgentLimitDto?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AgentLimits.AsNoTracking().FirstOrDefaultAsync(l => l.AgentId == id, ct);
        return row is null || row.Status == "Cleared" ? null : Dto(row);
    }

    private static AgentLimitDto Dto(AgentLimit l) => new(l.Message, l.TimeZoneId, l.DetectedAt, l.ResetAt, l.ContinueAt, l.Status, l.Detail, l.NativeAutoContinue);

    public async Task ScheduleAsync(Guid id, DateTime? resetOverride, CancellationToken ct, DateTime? expectedDetectedAt = null, double? continueAfterMinutes = null)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            await RefreshLockedAsync(id, false, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var agent = await db.Agents.Include(a => a.Runner).FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new KeyNotFoundException("Agent not found.");
            var limit = await db.AgentLimits.FindAsync([id], ct);
            if (limit is null || limit.Status == "Cleared") throw new InvalidOperationException("No current usage-limit message was found.");
            if (expectedDetectedAt is { } expected && expected != limit.DetectedAt) throw new InvalidOperationException("The usage limit changed while the dialog was open. Close it and review the new reset time.");
            if (limit.NativeAutoContinue) throw new InvalidOperationException("Claude is already continuing automatically; no second continuation will be scheduled.");
            if (limit.Status is "Scheduled" or "Sending" or "Sent" or "Uncertain") throw new InvalidOperationException("This limit already has a continuation. Cancel a pending one before changing its time.");
            var screen = await terminal.ReadAsync(agent, ct);
            if (!screen.InputEmpty || screen.Identity != limit.PaneIdentity || agent.Working)
                throw new InvalidOperationException("The harness must be idle with an empty prompt before scheduling.");
            if (continueAfterMinutes is { } duration)
            {
                if (!double.IsFinite(duration) || duration < 1 || duration > 46080 || resetOverride is not null)
                    throw new ArgumentException("Choose either a reset time or a continuation duration from 1 minute to 32 days.");
                resetOverride = Now.AddMinutes(duration - 1);
            }
            var reset = resetOverride ?? limit.ResetAt ?? throw new ArgumentException("Enter the exact reset time in UTC.");
            if (reset.Kind != DateTimeKind.Utc || reset.AddMinutes(1) <= Now || reset > Now.AddDays(32))
                throw new ArgumentException("The reset must be UTC, with its continuation in the future and within 32 days.");
            limit.ResetAt = reset;
            limit.ContinueAt = reset.AddMinutes(1);
            limit.Status = "Scheduled";
            limit.Detail = resetOverride is null ? null : "Reset time confirmed manually in UTC.";
            await db.SaveChangesAsync(ct);
            _armed[id] = 0;
            events.AgentChanged(agent.RunnerId, id);
        }
        finally { gate.Release(); }
    }

    public Task OnUserInputAsync(Guid id, CancellationToken ct) => _armed.ContainsKey(id)
        ? CancelAsync(id, "Cancelled because you used the terminal.", ct) : Task.CompletedTask;

    public async Task CancelAsync(Guid id, string reason, CancellationToken ct)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var changed = await db.AgentLimits.Where(l => l.AgentId == id && l.Status == "Scheduled")
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "Cancelled").SetProperty(l => l.ContinueAt, (DateTime?)null).SetProperty(l => l.Detail, reason), ct);
            _armed.TryRemove(id, out _);
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
        if (agent is null) { _armed.TryRemove(id, out _); return; }
        LimitScreen? screen = null;
        ParsedUsageLimit? parsed = null;
        if (agent.State == AgentState.Running && agent.Runner?.State == RunnerState.Ready && agent.HarnessId is "codex" or "claude")
        {
            screen = await terminal.ReadAsync(agent, ct);
            parsed = UsageLimitParser.Parse(agent.HarnessId, screen.Screen, screen.TimeZone, Now);
        }

        if (parsed is null)
        {
            if (row is null || row.Status == "Cleared") return;
            row.Status = "Cleared";
            row.ContinueAt = null;
            _armed.TryRemove(id, out _);
        }
        else
        {
            if (row is null)
            {
                row = new AgentLimit { AgentId = id };
                db.AgentLimits.Add(row);
            }
            if (row.Message != parsed.Message || row.SessionId != Session(agent) || row.PaneIdentity != screen!.Identity || row.Status == "Cleared" || row.TimeZoneId != parsed.TimeZoneId)
            {
                row.Message = parsed.Message;
                row.SessionId = Session(agent);
                row.PaneIdentity = screen!.Identity;
                row.TimeZoneId = parsed.TimeZoneId;
                row.DetectedAt = Now;
                row.ResetAt = parsed.ResetAt;
                row.ContinueAt = null;
                row.Status = "Detected";
                row.Detail = parsed.Detail;
                _armed.TryRemove(id, out _);
            }
            row.NativeAutoContinue = parsed.NativeAutoContinue;
            if (row.Status == "Sending")
            {
                row.Status = "Uncertain";
                row.Detail = "The Manager restarted during sending. Check the terminal; it will not send again automatically.";
            }
            if (row.Status == "Scheduled" && (!screen!.InputEmpty || agent.Working || parsed.NativeAutoContinue))
            {
                row.Status = "Cancelled";
                row.Detail = "Cancelled because the prompt changed or the harness resumed.";
                row.ContinueAt = null;
                _armed.TryRemove(id, out _);
            }
            if (dispatch && row.Status == "Scheduled" && row.ContinueAt <= Now)
            {
                if (row.ContinueAt < Now.AddMinutes(-15))
                {
                    row.Status = "Cancelled";
                    row.Detail = "The scheduled time was missed by more than 15 minutes. Continue manually.";
                }
                else
                {
                    // Claim before the network side effect. A crash or uncertain SSH result must never cause a duplicate send.
                    var claimed = await db.AgentLimits.Where(l => l.AgentId == id && l.Status == "Scheduled")
                        .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "Sending"), ct);
                    if (claimed == 0) return;
                    try
                    {
                        var sent = await terminal.SendContinueAsync(agent, screen!, ct);
                        row.Status = sent ? "Sent" : "Cancelled";
                        row.Detail = sent ? "Sent continue once." : "The terminal changed before sending; nothing was typed.";
                    }
                    catch (Exception ex)
                    {
                        row.Status = "Uncertain";
                        row.Detail = "Could not confirm delivery. Check the terminal; it will not send again automatically.";
                        logger.LogWarning(ex, "Could not confirm scheduled continuation for agent {Agent}", id);
                    }
                }
                _armed.TryRemove(id, out _);
            }
        }
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            events.AgentChanged(agent.RunnerId, id);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var db = await factory.CreateDbContextAsync(stoppingToken))
            foreach (var id in await db.AgentLimits.Where(l => l.Status == "Scheduled").Select(l => l.AgentId).ToListAsync(stoppingToken)) _armed[id] = 0;
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
