using System.Collections.Concurrent;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

/// <summary>
/// Durable one-time schedules (any harness) and opt-in recurring usage-limit continuation (Codex and Claude, whose
/// limit reset times are recognized). A limit belongs to the harness, conversation and login it was seen with
/// (<see cref="UsageLimitState.Session"/>): after a switch it no longer applies, unless you scheduled it yourself.
/// </summary>
public sealed class AgentContinuations(IDbContextFactory<ManagerDbContext> factory, ILimitTerminal terminal,
    ManagerEvents events, TimeProvider clock, ILogger<AgentContinuations> logger, AgentDiagnostics? diagnostics = null,
    UsageLimitReader? limitReader = null) : IDisposable
{
    private readonly UsageLimitReader _reader = limitReader ?? UsageLimitReader.Default;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private SemaphoreSlim Gate(Guid id) => _gates.GetOrAdd(id, _ => new(1, 1));
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Session(Agent a) => UsageLimitState.Session(a);

    /// <summary>Harnesses whose usage-limit reset times are recognized, so automatic continuation can work.</summary>
    public static bool DetectsLimits(string harnessId) => harnessId is "codex" or "claude";

    /// <summary>
    /// A limit from another harness, conversation or login: it says nothing about the agent now. Your own schedule is
    /// kept (it explains that it waits, until you cancel it), and so is a send in progress.
    /// </summary>
    private static bool IsStale(AgentLimit row, Agent agent) =>
        row.Status is not ("Cleared" or "Scheduled" or "Sending") && row.SessionId != Session(agent);

    private static void Clear(AgentLimit row)
    {
        row.Status = "Cleared";
        row.ContinueAt = null;
        row.Detail = null;
        row.NativeAutoContinue = false;
    }

    public async Task<AutoContinueDto> GetAutomaticAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var policy = await db.AgentAutoContinues.AsNoTracking().FirstOrDefaultAsync(p => p.AgentId == id, ct);
        var available = await db.Agents.Where(a => a.Id == id).Select(a => a.HarnessId).FirstOrDefaultAsync(ct) is { } harness && DetectsLimits(harness);
        return policy is null ? new(Available: available) : new(policy.Enabled, policy.Remaining, policy.Sent, policy.Detail, available);
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
            if (enabled && !DetectsLimits(agent.HarnessId))
                throw new ArgumentException("Automatic continuation needs a recognized usage-limit reset time, which only Codex and Claude provide. Use a one-time schedule.");
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
            if (enabled) await ApplyToCurrentLimitAsync(id, ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Just switched on: a usage limit already on screen is scheduled now (or explained, when its reset time is unknown),
    /// rather than at the next background check, so the dialog that switched it on shows what will happen.
    /// </summary>
    private async Task ApplyToCurrentLimitAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await RefreshLockedAsync(id, false, ct);
            // A limit seen for the first time is only recorded; arming happens on the next check, which is now.
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await db.AgentLimits.AnyAsync(l => l.AgentId == id && l.Status == "Detected" && !l.Manual, ct))
                await RefreshLockedAsync(id, false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The terminal can't be read right now: the background check does it later.
            logger.LogDebug(ex, "Could not check agent {Agent} for a usage limit after enabling automatic continuation", id);
        }
    }

    public async Task<AgentLimitDto?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AgentLimits.AsNoTracking().FirstOrDefaultAsync(l => l.AgentId == id, ct);
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (agent is null) return null;
        if (row is null || row.Status == "Cleared" || IsStale(row, agent))
            // A failed first observation has no durable row yet. Surface its warning without
            // inventing a detected limit, and ignore failures interpreting obsolete stored rows.
            return _reader.ScreenProblem(id) is { } problem
                ? new("Usage-limit detection unavailable.", null, Now, null, null, "Detected", null, false, true, DetectionProblem: problem)
                : null;
        var read = _reader.Stored(id, row);
        // A reset is an advertised retry time, not evidence that the stopped turn resumed.
        // Keep the limit and delivery result visible until a live observation clears them.
        return Dto(row, read, _reader.Problem(id));
    }

    private static AgentLimitDto Dto(AgentLimit l, UsageLimitRead read, string? problem)
    {
        // Re-read the original message at its original observation time. ResetAt is the user's
        // selected reset and may have been overridden; it must never masquerade as detection.
        var detected = l.Manual ? null : read.Limit?.ResetAt;
        return new(l.Manual ? "Manual continuation — no usage limit is required." : l.Message, l.TimeZoneId, l.DetectedAt, l.ResetAt, l.ContinueAt, l.Status, l.Detail, l.NativeAutoContinue, l.Manual, detected, read.Problem ?? problem);
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
            var limit = await db.AgentLimits.FindAsync([id], ct);
            if (limit is not null && IsStale(limit, agent)) Clear(limit);
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
        if (row is not null && IsStale(row, agent)) Clear(row);
        LimitScreen? screen = null;
        ParsedUsageLimit? parsed = null;
        // Every harness: a one-time schedule waits for an empty, ready prompt. Pi's quota errors have no reset time.
        if (agent.State == AgentState.Running && !agent.LoginBlocked && !agent.LoginStopPending && agent.Runner?.State == RunnerState.Ready)
        {
            screen = observed ?? await terminal.ReadAsync(agent, ct);
            var read = _reader.Screen(agent, screen, Now);
            if (!read.Available) { events.AgentChanged(agent.RunnerId, id); return; }
            parsed = read.Limit;
        }

        var stored = row is { Manual: false } ? _reader.Stored(id, row) : new UsageLimitRead(null);
        if (!stored.Available && row is not { Automatic: false, Status: "Scheduled" }) { events.AgentChanged(agent.RunnerId, id); return; }
        var detectedReset = stored.Limit?.ResetAt;
        var activity = screen is null ? AgentActivityStatus.Unknown
            : AgentScreenDetector.Detect(agent.HarnessId, screen.Screen, Now).Status;

        // Automatic schedules follow the current rejection. A retry or account-side quota
        // change can replace a weekly reset with a session reset before the old due date.
        // Explicit one-time schedules and ambiguous/in-progress sends remain untouched.
        if (row is { Automatic: true, Status: "Scheduled" } && row.SessionId == Session(agent)
            && parsed is not null && (row.Message != parsed.Message || row.PaneIdentity != screen!.Identity
                || row.TimeZoneId != parsed.TimeZoneId || parsed.NativeAutoContinue))
        {
            Clear(row);
            // Scheduled is an unsent reservation. Release it so changed wording with
            // the same reset can be scheduled again; Sent/Uncertain never take this path.
            if (policy is not null) policy.LastLimitKey = null;
            diagnostics?.Record(id, "Continuation", "The current usage-limit message changed; replacing the automatic schedule.");
        }
        else if (row is { Automatic: true, Status: "Scheduled" } && row.SessionId == Session(agent)
            && parsed is null && screen is not null && (activity == AgentActivityStatus.Working
                || activity == AgentActivityStatus.Ready && agent.LastAssistantMessageAt > row.DetectedAt))
        {
            Clear(row);
            if (policy is { Enabled: true })
            {
                policy.LastLimitKey = null;
                policy.Detail = "The agent resumed. Waiting for the next usage limit.";
            }
        }

        // Repair stored messages after parser improvements without moving their observation
        // date or overwriting a manually selected reset / existing schedule.
        if (row is { Manual: false } && parsed is not null && row.SessionId == Session(agent)
            && row.PaneIdentity == screen?.Identity)
        {
            var original = stored.Limit;
            if (original?.Message == parsed.Message)
            {
                row.Message = original.Message;
                // Repair pre-fix expiry rows without re-anchoring a clock-only reset to today.
                if (row.Status == "Expired") { row.Status = "Detected"; row.ContinueAt = null; row.Detail = original.Detail; }
                if (row.Status == "Detected" && row.ResetAt is null)
                {
                    row.ResetAt = original.ResetAt;
                    row.Detail = original.Detail;
                }
            }
        }

        // Re-arm only for a detected reset, never by repeatedly reacting to an old screen.
        // The reset key is persisted before sending and survives process/Manager restarts.
        if (policy is { Enabled: true } && parsed is not null && !parsed.NativeAutoContinue
            && row is not null && row.Status is not ("Scheduled" or "Sending" or "Uncertain" or "Sent" or "Cleared")
            && row.Message == parsed.Message && row.SessionId == Session(agent) && !row.Manual
            && detectedReset is { } reset)
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
                logger.LogInformation("Automatic continuation for {Agent} scheduled at {ContinueAt}; detected reset {ResetAt}, timezone {TimeZone}",
                    id, row.ContinueAt, reset, row.TimeZoneId);
                // The claim below must find Scheduled in the database even on an already-expired reset.
                await db.SaveChangesAsync(ct);
                events.AgentChanged(agent.RunnerId, id);
            }
        }

        // An explicitly scheduled attempt is independent of later screen text or usage-limit detection: the user
        // cancels it, or it is done once the agent got going again without it. If it is due but cannot safely submit,
        // leave it armed and explain why.
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
                // A current rejection takes priority over cached work/reply timestamps.
                if (parsed is null && ContinuedWithout(agent, row, activity))
                {
                    row.Status = "Cleared";
                    row.ContinueAt = null;
                    row.Detail = null;
                    if (row.Automatic && policy is { Enabled: true }) policy.Detail = "The agent continued by itself. Waiting for the next usage limit.";
                    diagnostics?.Record(id, "Continuation", "Not sent: the agent was already going again, so the one-time continue is done.");
                    logger.LogInformation("Agent {Agent} continued without its scheduled continue; the schedule is done", id);
                    await db.SaveChangesAsync(ct);
                    events.AgentChanged(agent.RunnerId, id);
                    return;
                }

                row.Detail = Session(agent) != row.SessionId ? "Waiting: the agent's harness or session changed. Cancel this schedule to replace it."
                    : screen is null ? "Waiting for the agent and Runner to be available."
                    : !screen.InputEmpty ? "Waiting for an empty prompt. Your draft will not be submitted."
                    : parsed?.NativeAutoContinue == true ? "Waiting while Claude handles its own automatic continuation."
                    : activity is not (AgentActivityStatus.Ready or AgentActivityStatus.UsageLimited)
                        ? "Waiting for the agent to be idle and ready for input." : null;
                if (row.Detail is null)
                {
                    // Claim before the network side effect. Ambiguous delivery must never cause a duplicate send.
                    var claimed = await db.AgentLimits.Where(l => l.AgentId == id && l.Status == "Scheduled")
                        .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "Sending"), ct);
                    if (claimed == 0) return;
                    try
                    {
                        using var loginLease = await Accounts.AccountCoordination.LockAsync(ct);
                        var allowed = await db.Agents.AsNoTracking().AnyAsync(a => a.Id == id && !a.LoginBlocked && !a.LoginStopPending
                            && a.State == AgentState.Running && a.HarnessSessionId == agent.HarnessSessionId && a.HarnessId == agent.HarnessId, ct);
                        var delivery = allowed ? await terminal.SendContinueAsync(agent, screen!, ct) : ContinueDelivery.NotTyped;
                        var sent = delivery == ContinueDelivery.Sent;
                        row.Status = sent ? "Sent" : "Scheduled";
                        if (sent && row.Automatic && policy is not null)
                        {
                            policy.Sent++;
                            if (policy.Remaining is { } count) policy.Remaining = Math.Max(0, count - 1);
                            if (policy.Remaining == 0) policy.Enabled = false;
                            policy.Detail = policy.Enabled ? "Continue submitted. Waiting for the harness to resume." : "The chosen number of continuations has been sent.";
                        }
                        // Force Scheduled to be written back after the database-only claim above.
                        db.Entry(row).Property(l => l.Status).IsModified = true;
                        row.Detail = sent ? (row.Automatic ? policy?.Detail : "Submitted continue once. If the harness reaches its limit again, review the new reset before scheduling another attempt.")
                            : delivery == ContinueDelivery.NotTyped ? "Waiting: the screen changed just before sending, so nothing was typed. Trying again on the next check."
                            : "Waiting: the prompt changed while typing continue, so Enter was not sent. Check the prompt; the text may still be there.";
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
        else if (row?.Status == "Sent" && parsed is null && screen is not null &&
            activity is AgentActivityStatus.Ready or AgentActivityStatus.Working)
        {
            // A successful continuation is no longer an active limit once normal activity resumes.
            // Require positive live evidence: a missing connection or unrecognized screen is not recovery.
            row.Status = "Cleared";
            row.ContinueAt = null;
            row.Detail = null;
            if (policy is { Enabled: true }) policy.Detail = "The agent resumed. Waiting for the next usage limit.";
        }
        else if (row?.Status is "Sent" or "Uncertain" && (parsed is null || parsed.Message == row.Message))
        {
            // Retain delivery uncertainty, or a sent result until the limit clears and normal activity is observed.
        }
        else if (parsed is null)
        {
            if (row is null || row.Status == "Cleared" || row.Manual
                || activity is not (AgentActivityStatus.Ready or AgentActivityStatus.Working))
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
                diagnostics?.Record(id, "Usage limit", $"Detected usage limit. Timezone: {row.TimeZoneId ?? "unknown"}. Reset UTC: {row.ResetAt?.ToString("O") ?? "unknown"}. Automatic continuation: {policy?.Enabled == true}.");
                logger.LogInformation("Usage limit detected for {Agent}; reset {ResetAt}, timezone {TimeZone}, automatic continuation {Automatic}",
                    id, row.ResetAt, row.TimeZoneId, policy?.Enabled == true);
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
            else if (parsed is not null && row?.Status == "Detected" && row.ResetAt is null)
                policy.Detail = "The reset time is uncertain. Switch to a one-time schedule to choose it manually.";
            else if (parsed is not null && row?.Status == "Detected" && policy.LastLimitKey == Session(agent) + "|" + detectedReset?.ToString("O"))
                policy.Detail = row.Detail = "A continuation was already attempted for this reset. Check the terminal before retrying.";
            else if (parsed is not null && row?.Status == "Detected") policy.Detail = "Usage limit detected. Preparing automatic continuation.";
            else if (row is { Automatic: true, Status: "Scheduled", Detail: not null }) policy.Detail = row.Detail;
        }
        if (db.ChangeTracker.HasChanges())
        {
            diagnostics?.Record(id, "Continuation", $"{row?.Status}: {row?.Detail ?? policy?.Detail ?? "Usage limit detected"}");
            await db.SaveChangesAsync(ct);
            events.AgentChanged(agent.RunnerId, id);
        }
    }

    /// <summary>
    /// Whether a due one-time continue is no longer needed: the agent is working, or replied after the moment the schedule
    /// waited for (the reset; for a chosen duration, a minute before it is due). The harness continued by itself (Claude
    /// does at the reset) or someone prompted it. Sending later would prompt a finished turn, or the next limit.
    /// </summary>
    private static bool ContinuedWithout(Agent agent, AgentLimit row, AgentActivityStatus activity) =>
        agent.Working || activity == AgentActivityStatus.Working
        || agent.LastAssistantMessageAt > (row.ResetAt ?? row.ContinueAt!.Value.AddMinutes(-1));

    public void Dispose()
    {
        foreach (var gate in _gates.Values) gate.Dispose();
    }
}
