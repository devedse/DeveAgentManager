using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

public sealed record AgentJobPublication(Guid Id, Guid AgentId, Guid SessionId, string Caption, string Machine,
    string OutputParent, string Outcome, int? ExitCode, DateTimeOffset Created, DateTimeOffset? Finished,
    JsonElement? Event, string? Error);

/// <summary>Controller outbox delivery; never relaunches work or submits over a busy prompt/draft.</summary>
public sealed class AgentJobCompletions(IDbContextFactory<ManagerDbContext> factory, RunnerShell shell,
    ILimitTerminal terminal, SessionService sessions, ILogger<AgentJobCompletions> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var runners = await db.Runners.AsNoTracking().Where(r => r.State == RunnerState.Ready).ToArrayAsync(ct);
                await Task.WhenAll(runners.Select(async r =>
                {
                    try { await CollectAsync(r, ct); }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogDebug(ex, "Could not deliver job completions from {Runner}", r.Id); }
                }));
                // Only the controller scans its durable outbox. It spends no agent turns or tokens.
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Job completion collection failed"); await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        }
    }

    internal async Task CollectAsync(Runner runner, CancellationToken ct)
    {
        var result = await shell.RunAsync(runner,
            "if [ -x ~/.deveagentmanager/dam-tools.py ] && [ -n \"$(ls -A ~/.deveagentmanager/jobs 2>/dev/null)\" ]; then python3 ~/.deveagentmanager/dam-tools.py --pending-jobs; else printf '[]'; fi",
            TimeSpan.FromSeconds(15), ct);
        if (!result.Succeeded || result.Output.Length > 100_000) return;
        var jobs = JsonSerializer.Deserialize<AgentJobPublication[]>(result.Output, ApiJson.Options) ?? [];
        foreach (var job in jobs.Take(20))
        {
            if (job.Id == Guid.Empty || job.AgentId == Guid.Empty || job.SessionId == Guid.Empty) continue;
            var notification = Describe(job); // reject malformed/cross-job terminal evidence before delivery
            await using var db = await factory.CreateDbContextAsync(ct);
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == job.AgentId && a.RunnerId == runner.Id, ct);
            var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == job.SessionId, ct);
            if (!Belongs(agent, session, runner.Id))
            {
                await Ack(runner, job.Id, "STALE", ct);
                continue;
            }
            if (agent!.State != AgentState.Running) continue;
            var screen = await terminal.ReadAsync(agent, ct);
            if (!screen.InputEmpty || AgentScreenDetector.Detect(agent.HarnessId, screen.Screen, DateTime.UtcNow).Status != AgentActivityStatus.Ready) continue;
            using var lease = await sessions.LockAsync(ct);
            // Recheck the selected conversation before crossing the terminal boundary.
            db.ChangeTracker.Clear();
            agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == job.AgentId && a.RunnerId == runner.Id, ct);
            session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == job.SessionId, ct);
            if (!Belongs(agent, session, runner.Id) || agent!.State != AgentState.Running) continue;
            var key = job.AgentId + ":" + job.SessionId + ":" + job.Id + ":" + EventId(job);
            var sent = await shell.RunAsync(runner, LimitTerminal.BuildSubmitScript(agent.TmuxSession, screen, notification,
                deliveryKey: key), TimeSpan.FromSeconds(15), ct);
            if (!sent.Succeeded) continue; // keyed Runner journal decides whether a retry is safe
            var outcome = sent.Output.Trim();
            if (outcome == "SENT") await Ack(runner, job.Id, "SENT", ct);
            else if (outcome == "UNCONFIRMED") await Ack(runner, job.Id, "UNCERTAIN", ct);
        }
    }

    internal static bool Belongs(Agent? agent, StoredSession? session, Guid runnerId) =>
        agent is not null && session is not null && agent.RunnerId == runnerId && session.AgentId == agent.Id &&
        session.HarnessId == agent.HarnessId && session.HarnessSessionId == agent.HarnessSessionId;

    internal static string EventId(AgentJobPublication job) => job.Event is { } value ? value.GetProperty("EventId").GetString()! : job.Id + ":inspection";

    internal static string Describe(AgentJobPublication job)
    {
        static string Clean(string? value, int limit)
        {
            var clean = string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return clean[..Math.Min(clean.Length, limit)];
        }
        if (job.Event is not { } value)
        {
            if (job.Outcome != "INSPECTION_REQUIRED") throw new InvalidDataException("Missing terminal job evidence.");
            return $"Background job {job.Id} requires inspection. Transport/process exit {job.ExitCode?.ToString() ?? "unknown"}; " +
                $"no validated campaign completion. {Clean(job.Error, 600)} Evidence: ~/.deveagentmanager/jobs/{job.Id:N}. " +
                "Read the exact job evidence and report status; do not relaunch the workload.";
        }
        if (job.Outcome != "VALIDATED" || value.GetProperty("SchemaVersion").GetInt32() != 1) throw new InvalidDataException("Invalid completion schema/outcome.");
        var id = value.GetProperty("CampaignId").GetString() ?? throw new InvalidDataException("Missing campaign identity.");
        var directory = value.GetProperty("Directory").GetString() ?? throw new InvalidDataException("Missing campaign path.");
        var status = value.GetProperty("Status").GetString();
        var digest = value.GetProperty("ManifestSha256").GetString();
        if (value.GetProperty("EventId").GetString() != id + ":terminal:1" || digest?.Length != 64 || !digest.All(Uri.IsHexDigit) ||
            status is not ("COMPLETED" or "INCOMPLETE" or "RESTORATION_FAILED" or "CANCELLED") ||
            !SameParent(directory, job.OutputParent, id)) throw new InvalidDataException("Completion does not belong to this job.");
        var completed = value.GetProperty("CompletedPhases").GetInt32(); var expected = value.GetProperty("ExpectedPhases").GetInt32();
        var collected = value.GetProperty("CollectedCases"); var cases = value.GetProperty("ExpectedCases");
        var restoration = value.GetProperty("Restoration").GetString(); var finished = value.GetProperty("Finished").GetDateTimeOffset();
        if (completed < 0 || expected < 0 || completed > expected || (status == "COMPLETED" &&
            (completed != expected || collected.ValueKind == JsonValueKind.Null || cases.ValueKind == JsonValueKind.Null ||
             collected.GetInt32() != cases.GetInt32() || restoration != "RESTORED"))) throw new InvalidDataException("Invalid completion counts/restoration.");
        return $"Background verification {Clean(job.Caption, 200)} completed: {status}; restoration {Clean(restoration, 80)}; " +
            $"{completed}/{expected} phases, {collected}/{cases} outer cases, elapsed {(finished - job.Created).TotalSeconds:F1}s. " +
            $"Validated event {id}:terminal:1. Read SUMMARY.md, results.json, status.json, run.log and raw evidence in {Clean(directory, 1000)}, then report findings. " +
            "Traced/MEASURED results alone are not a performance acceptance verdict.";
    }

    private static bool SameParent(string path, string parent, string id)
    {
        // Windows completion paths are validated on their originating machine, then checked lexically here.
        var separator = parent.Contains('\\') ? '\\' : '/';
        var comparison = separator == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(parent.TrimEnd(separator) + separator + id, comparison) && !id.Contains('/') && !id.Contains('\\') && id is not "." and not "..";
    }

    private Task<CommandResult> Ack(Runner runner, Guid id, string state, CancellationToken ct) =>
        shell.RunAsync(runner, $"python3 ~/.deveagentmanager/dam-tools.py --ack-job {id:N} {state}", TimeSpan.FromSeconds(10), ct);
}
