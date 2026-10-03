using System.Collections.Concurrent;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

/// <summary>Durable one-time schedules and opt-in recurring usage-limit continuation.</summary>
public sealed class AgentContinuations(IDbContextFactory<ManagerDbContext> factory, ILimitTerminal terminal,
    ManagerEvents events, TimeProvider clock, ILogger<AgentContinuations> logger, AgentDiagnostics? diagnostics = null) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private SemaphoreSlim Gate(Guid id) => _gates.GetOrAdd(id, _ => new(1, 1));
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Session(Agent a) => UsageLimitState.Session(a);

    public async Task<AutoContinueDto> GetAutomaticAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var policy = await db.AgentAutoContinues.AsNoTracking().FirstOrDefaultAsync(p => p.AgentId == id, ct);
        return policy is null ? new() : new(policy.Enabled, policy.Remaining, policy.Sent, policy.Detail);
    }

    public async Task SetAutomaticAsync(Guid id, bool enabled, int? remaining, CancellationToken ct)
    {
        if (remaining is < 1 or > 10000) throw new ArgumentException("Choose from 1 to 10,000 continuations, or unlimited.");
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var agent = await db.Agents.FindAsync([id], ct) ?? throw new KeyNotFoundException("Agent not found.");
            if (enabled && agent.HarnessId is not ("codex" or "claude")) throw new ArgumentException("Automatic continuation supports Codex and Claude.");
            var limit = await db.AgentLimits.FindAsync([id], ct);
            if (enabled && (limit?.Status is "Sending" or "Uncertain" || limit is { Automatic: false, Status: "Scheduled" }))
                throw new InvalidOperationException("Cancel or resolve the existing one-time continuation first.");
            var policy = await db.AgentAutoContinues.FindAsync([id], ct);
            if (policy is null) { policy = new() { AgentId = id }; db.AgentAutoContinues.Add(policy); }
            policy.Enabled = enabled;
            policy.Remaining = remaining;
            policy.SessionId = Session(agent);
            policy.Detail = enabled ? "Waiting for a detected usage limit." : "Automatic continuation is off.";
            if (!enabled && limit is { Automatic: true, Status: "Scheduled" })
            {
                limit.Status = "Cancelled";
                limit.ContinueAt = null;
                limit.Detail = "Automatic continuation switched off.";
                policy.LastLimitKey = null; // An unsent attempt may be re-enabled explicitly.
            }
            await db.SaveChangesAsync(ct);
            events.AgentChanged(agent.RunnerId, id);
        }
        finally { gate.Release(); }
    }

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
            if ((await GetAutomaticAsync(id, ct)).Enabled)
                throw new InvalidOperationException("Switch off automatic continuation before scheduling once.");
            await using var db = await factory.CreateDbContextAsync(ct);
            var agent = await db.Agents.Include(a => a.Runner).FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new KeyNotFoundException("Agent not found.");
            if (agent.HarnessId is not ("codex" or "claude")) throw new InvalidOperationException("Continuation supports Codex and Claude.");
            var limit = await db.AgentLimits.FindAsync([id], ct);
            // A chosen time is an explicit instruction, independent of current terminal detection.
            // Older clients may omit a reset when accepting detection; resolve that stored value once.
            if (resetOverride is null && continueAfterMinutes is null && (limit is null || limit.Status == "Cleared"))
            {
                await RefreshLockedAsync(id, false, ct);
                db.ChangeTracker.Clear();
                limit = await db.AgentLimits.FindAsync([id], ct);
            }
            if (limit?.Status is "Scheduled" or "Sending" or "Uncertain")
                throw new InvalidOperationException("Review or cancel the existing continuation first.");
            if (resetOverride is null && continueAfterMinutes is null && expectedDetectedAt is { } expected && limit?.DetectedAt != expected)
                throw new InvalidOperationException("The detected reset changed. Choose a time before scheduling.");
            if (limit is null) { limit = new AgentLimit { AgentId = id }; db.AgentLimits.Add(limit); }
            if (limit.Status == "Cleared" || limit.Message.Length == 0 || limit.SessionId != Session(agent))
            {
                limit.Manual = true;
                limit.Message = "One-time continuation at your chosen time.";
                limit.DetectedAt = Now;
                limit.ResetAt = null;
            }
            limit.Manual |= manual;
            limit.SessionId = Session(agent);
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
            limit.Automatic = false;
            // This is now the user's schedule; native waiting is rechecked at delivery time.
            limit.NativeAutoContinue = false;
            limit.ResetAt = reset;
            limit.ContinueAt = continueAt;
            limit.Status = "Scheduled";
            limit.Detail = continueAfterMinutes is not null ? "Continue scheduled after the chosen duration; the detected allowance reset is unchanged."
                : resetOverride is null ? null : "Reset time confirmed manually.";
            await db.SaveChangesAsync(ct);
            diagnostics?.Record(id, "Continuation", $"{limit.Status}: {limit.Detail ?? "Scheduled continuation"}");
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
            if (changed > 0 && await db.Agents.FindAsync([id], ct) is { } a)
            {
                diagnostics?.Record(id, "Continuation", "Cancelled by you.");
                events.AgentChanged(a.RunnerId, id);
            }
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

    public async Task ObserveAsync(Guid id, LimitScreen snapshot, CancellationToken ct, string? expectedSession = null)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try { await RefreshLockedAsync(id, true, ct, snapshot, expectedSession); }
        finally { gate.Release(); }
    }

    private async Task RefreshLockedAsync(Guid id, bool dispatch, CancellationToken ct, LimitScreen? observed = null, string? expectedSession = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var agent = await db.Agents.Include(a => a.Runner).FirstOrDefaultAsync(a => a.Id == id, ct);
        var row = await db.AgentLimits.FindAsync([id], ct);
        if (agent is null || expectedSession is not null && Session(agent) != expectedSession) return;
        if (observed?.ObservedAt is { } at && Now - at > TimeSpan.FromSeconds(30)) return;
        var policy = await db.AgentAutoContinues.FindAsync([id], ct);
        if (policy is { Enabled: true } && policy.SessionId != Session(agent))
        {
            policy.Enabled = false;
            policy.Detail = "Automatic continuation stopped because the conversation or harness changed.";
            if (row is { Automatic: true, Status: "Scheduled" }) { row.Status = "Cancelled"; row.ContinueAt = null; }
        }
        LimitScreen? screen = null;
        ParsedUsageLimit? parsed = null;
        if (agent.State == AgentState.Running && agent.Runner?.State == RunnerState.Ready && agent.HarnessId is "codex" or "claude")
        {
            screen = observed ?? await terminal.ReadAsync(agent, ct);
            parsed = UsageLimitParser.Parse(agent.HarnessId, screen.Screen, screen.TimeZone, screen.ObservedAt ?? Now);
        }

        // Re-arm only for a detected reset, never by repeatedly reacting to an old screen.
        // The reset key is persisted before sending and survives process/Manager restarts.
        if (policy is { Enabled: true } && parsed is not null && !parsed.NativeAutoContinue
            && row is not null && row.Status is not ("Scheduled" or "Sending" or "Uncertain" or "Sent")
            && row.Message == parsed.Message && row.SessionId == Session(agent) && !row.Manual
            && UsageLimitState.DetectedReset(row) is { } reset)
        {
            var key = Session(agent) + "|" + reset.ToString("O");
            if (policy.LastLimitKey != key)
            {
                row.Automatic = true;
                row.ContinueAt = reset.AddMinutes(1);
                row.Status = "Scheduled";
                row.Detail = null;
                policy.LastLimitKey = key;
                policy.Detail = "Waiting until one minute after the detected reset.";
                // The claim below must find Scheduled in the database even on an already-expired reset.
                await db.SaveChangesAsync(ct);
                events.AgentChanged(agent.RunnerId, id);
            }
        }

        // An explicitly scheduled attempt is independent of later screen text or usage-limit detection.
        // Only the user cancels it. If it is due but cannot safely submit, leave it armed and explain why.
        if (row?.Status == "Sending")
        {
            row.Status = "Uncertain";
            if (row.Automatic && policy is not null) { policy.Enabled = false; policy.Detail = "Delivery is uncertain after a restart. Check the terminal."; }
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
                        if (sent && row.Automatic && policy is not null)
                        {
                            policy.Sent++;
                            if (policy.Remaining is { } count) policy.Remaining = Math.Max(0, count - 1);
                            if (policy.Remaining == 0) policy.Enabled = false;
                            policy.Detail = policy.Enabled ? "Continue sent. Waiting for the next usage limit." : "The chosen number of continuations has been sent.";
                        }
                        // Force Scheduled to be written back after the database-only claim above.
                        db.Entry(row).Property(l => l.Status).IsModified = true;
                        row.Detail = sent ? (row.Automatic ? policy?.Detail : "Submitted continue once. If the harness reaches its limit again, review the new reset before scheduling another attempt.")
                            : "Waiting because the terminal changed during submission. Check the prompt; inserted text may still be present.";
                    }
                    catch (Exception ex)
                    {
                        row.Status = "Uncertain";
                        if (row.Automatic && policy is not null) { policy.Enabled = false; policy.Detail = "Delivery is uncertain. Check the terminal before enabling again."; }
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
            if (row is null || row.Status == "Cleared" || row.Manual)
            {
                if (db.ChangeTracker.HasChanges()) { await db.SaveChangesAsync(ct); events.AgentChanged(agent.RunnerId, id); }
                return;
            }
            row.Status = "Cleared";
            row.ContinueAt = null;
        }
        else
        {
            if (row is null) { row = new AgentLimit { AgentId = id }; db.AgentLimits.Add(row); }
            if (row.Manual || row.Message != parsed.Message || row.SessionId != Session(agent) || row.PaneIdentity != screen!.Identity || row.Status == "Cleared" || row.TimeZoneId != parsed.TimeZoneId)
            {
                row.Automatic = false;
                row.Manual = false;
                row.Message = parsed.Message;
                row.SessionId = Session(agent);
                row.PaneIdentity = screen!.Identity;
                row.TimeZoneId = parsed.TimeZoneId;
                row.DetectedAt = screen!.ObservedAt ?? Now;
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
        if (policy is { Enabled: true })
        {
            if (parsed?.NativeAutoContinue == true) policy.Detail = "Waiting while Claude continues automatically.";
            else if (parsed is not null && row?.Status == "Detected" && UsageLimitState.DetectedReset(row) is null)
                policy.Detail = "The reset time is uncertain. Switch to a one-time schedule to choose it manually.";
            else if (row is { Automatic: true, Status: "Scheduled", Detail: not null }) policy.Detail = row.Detail;
        }
        if (db.ChangeTracker.HasChanges())
        {
            diagnostics?.Record(id, "Continuation", $"{row?.Status}: {row?.Detail ?? policy?.Detail ?? "Usage limit detected"}");
            await db.SaveChangesAsync(ct);
            events.AgentChanged(agent.RunnerId, id);
        }
    }

    public void Dispose()
    {
        foreach (var gate in _gates.Values) gate.Dispose();
    }
}
