using System.Text.Json;
using DeveAgentManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

public sealed partial class AgentService
{
    private static IReadOnlyList<AgentMoveStepDto> MoveStepPlan() => [
        new("check", "Check runners and destination", Detail: "Checks access, installed harness, paths, background processes and destination conflicts."),
        new("stop", "Stop the source agent", Detail: "Stops approved background processes and the source harness, then checks that they have exited."),
        new("history", "Verify session history and attachments", Detail: "Fully verifies native history and preserves screenshots, shared documents and their collection metadata."),
        new("pack", "Find files and prepare the archive", Detail: "Discovers referenced uploads, packs workspace and native files, then verifies the source and archive checksum."),
        new("download", "Download from the source", Detail: "Streams the private archive through the Manager without loading it into memory."),
        new("upload", "Upload to the destination", Detail: "Streams the archive to the destination. Filenames follow the reported archive byte position."),
        new("restore", "Restore and verify destination files", Detail: "Checks the archive checksum, restores into private staging folders and verifies every file."),
        new("install", "Install files and commit the destination", Detail: "Installs without overwriting existing paths, rechecks the source, then switches the stored agent location."),
        new("start", "Start the destination agent", Detail: "Resumes an agent that was running and confirms its terminal is alive. Skipped for stopped agents."),
        new("cleanup", "Remove source files", Detail: "Removes the exclusive source workspace and runtime only after the destination is committed. Shared cache files can remain."),
        new("target-cleanup", "Clean destination after a failure", Detail: "Runs only after a failure. Removes move-owned files; changed files are retained for inspection."),
        new("recovery", "Restore the agent on the source", Detail: "Runs only after a failure. Confirms destination liveness before resuming the source; an uncertain launch is never replayed.")
    ];

    // Hydrate display data only. Never replace the caller's durable safety flags or lifecycle phase.
    private async Task<AgentMoveState> ReadMoveStepsAsync(AgentMoveState move, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = await db.Agents.Where(a => a.Id == move.AgentId).Select(a => a.MoveJson).SingleAsync(ct);
        var stored = json is null ? null : JsonSerializer.Deserialize<AgentMoveState>(json);
        return stored?.Id == move.Id ? move with { Steps = stored.Steps ?? move.Steps ?? MoveStepPlan(), Progress = stored.Progress }
            : move with { Steps = move.Steps ?? MoveStepPlan() };
    }

    private async Task<AgentMoveState> StartMoveStepAsync(AgentMoveState move, string id, CancellationToken ct)
    {
        move = await ReadMoveStepsAsync(move, ct);
        var now = DateTime.UtcNow;
        var steps = move.Steps!.Select(step =>
        {
            if (step.Id == id) return step with { Status = AgentMoveStepStatus.Running, StartedAt = step.StartedAt ?? now, FinishedAt = null };
            if (step.Status != AgentMoveStepStatus.Running) return step;
            LogMoveStepCompleted(move.Id, step, now);
            return step with { Status = AgentMoveStepStatus.Completed, FinishedAt = now };
        }).ToArray();
        var current = steps.Single(step => step.Id == id);
        move = move with { Steps = steps, Progress = null };
        await SaveMoveAsync(move, current.Title + "…", ct);
        logger.LogInformation("Agent move {MoveId} started step {Step}: {Title}", move.Id, id, current.Title);
        return move;
    }

    private async Task<AgentMoveState> FailMoveStepAsync(AgentMoveState move, string error, CancellationToken ct)
    {
        move = await ReadMoveStepsAsync(move, ct);
        var now = DateTime.UtcNow;
        var steps = move.Steps!.Select(step => step.Status == AgentMoveStepStatus.Running
            ? step with { Status = AgentMoveStepStatus.Failed, FinishedAt = now, Error = error } : step).ToList();
        if (!steps.Any(step => step.Status == AgentMoveStepStatus.Failed))
            steps.Add(new("interrupted", "Previous move interrupted", AgentMoveStepStatus.Failed, FinishedAt: now,
                Detail: "Step timings were not recorded by the previous Manager version.", Error: error));
        move = move with { Steps = steps, Progress = null };
        await SaveMoveAsync(move, error, ct);
        return move;
    }

    private async Task<AgentMoveState> FinishMoveStepsAsync(AgentMoveState move, CancellationToken ct)
    {
        move = await ReadMoveStepsAsync(move, ct);
        var now = DateTime.UtcNow;
        return move with { Progress = null, Steps = move.Steps!.Select(step => step.Status switch
        {
            AgentMoveStepStatus.Running => Complete(step),
            AgentMoveStepStatus.Pending => step with { Status = AgentMoveStepStatus.Skipped },
            _ => step
        }).ToArray() };
        AgentMoveStepDto Complete(AgentMoveStepDto step)
        {
            LogMoveStepCompleted(move.Id, step, now);
            return step with { Status = AgentMoveStepStatus.Completed, FinishedAt = now };
        }
    }

    private void LogMoveStepCompleted(string moveId, AgentMoveStepDto step, DateTime finished) =>
        logger.LogInformation("Agent move {MoveId} completed step {Step} in {Seconds:F2}s", moveId, step.Id,
            step.StartedAt is { } started ? Math.Max(0, (finished - started).TotalSeconds) : 0);
}
