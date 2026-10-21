using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

/// <summary>
/// Agents' task lists (docs/agent-tasks.md). An agent keeps its list on its Runner with <c>dam-tools tasks</c>, so every
/// change is instant and it can always read it back. The Manager reads a list again when its revision changed, in the
/// SSH command that checks the agents every 5 seconds (<see cref="CollectScript"/>), and keeps the last copy for the
/// Portal. Clearing it from the Portal writes an empty list with a new revision on the Runner. A Runner without the list
/// (the agent moved there, or the Runner was rebuilt) gets the Manager's copy back.
/// </summary>
public sealed class AgentTasks(IDbContextFactory<ManagerDbContext> dbFactory, RunnerShell shell, ManagerEvents events, ILogger<AgentTasks> logger)
{
    public const string Marker = "__DAM_TASKS__";
    private const int MaxTasks = 50;
    private const int MaxName = 120;
    private const int MaxDescription = 1000;
    private const int MaxEncoded = 1024 * 1024;
    private const string Tools = "~/.deveagentmanager/dam-tools.py";
    private static readonly TimeSpan RestoreRetry = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, DateTime> _restoreFailed = new();

    /// <summary>Prints a marked line for each of these agents whose list on the Runner has another revision than the Manager's.</summary>
    public static string CollectScript(IEnumerable<Agent> agents)
    {
        var known = string.Join(' ', agents.Select(a => $"{a.Id:N}={a.TasksRevision}"));
        return known.Length == 0 ? "" : $"[ -f {Tools} ] && python3 {Tools} --changed-tasks {known} 2>/dev/null || true\n";
    }

    /// <summary>The lists in <paramref name="output"/>'s marked lines. A list that isn't valid is left out, never half-read.</summary>
    public static Dictionary<Guid, AgentTaskListDto> Parse(string output)
    {
        var lists = new Dictionary<Guid, AgentTaskListDto>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ');
            if (parts is not [Marker, var id, var encoded] || encoded.Length > MaxEncoded || !Guid.TryParseExact(id, "N", out var agentId)) continue;
            try
            {
                using var json = JsonDocument.Parse(Convert.FromBase64String(encoded));
                lists[agentId] = Read(json.RootElement);
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        }

        return lists;
    }

    // The agent wrote it: every field is checked and bounded, and only text and numbers come through.
    internal static AgentTaskListDto Read(JsonElement root)
    {
        var tasks = new List<AgentTaskDto>();
        foreach (var task in root.GetProperty("tasks").EnumerateArray().Take(MaxTasks))
        {
            if (task.ValueKind != JsonValueKind.Object) continue;
            var name = Text(task, "name", MaxName).Replace('\n', ' ');
            if (name.Length == 0 || !task.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number)) continue;
            tasks.Add(new AgentTaskDto(number, name, Math.Clamp(Number(task, "percent") ?? 0, 0, 100), Text(task, "description", MaxDescription),
                Time(task, "startedAt"), Time(task, "finishedAt"), NonNegative(task, "takenSeconds"), NonNegative(task, "leftSeconds"),
                Time(task, "leftAt"), Time(task, "updatedAt")));
        }

        return new AgentTaskListDto(root.GetProperty("revision").GetInt64(), Time(root, "updatedAt"), tasks);
    }

    private static string Text(JsonElement item, string name, int limit)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return "";
        var text = new StringBuilder();
        foreach (var c in value.GetString()!) text.Append(c == '\n' || !char.IsControl(c) ? c : ' ');
        var trimmed = text.ToString().Trim();
        return trimmed.Length <= limit ? trimmed : trimmed[..limit];
    }

    private static int? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? (int)Math.Round(Math.Clamp(number, int.MinValue, int.MaxValue)) : null;

    private static long? NonNegative(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : null;

    private static DateTime? Time(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? time : null;

    public async Task<AgentTaskListDto> GetAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return Stored(await db.Agents.Where(a => a.Id == agentId).Select(a => a.TasksJson).SingleOrDefaultAsync(cancellationToken));
    }

    /// <summary>Keeps the lists read from a Runner, for its own agents only, and tells their viewers.</summary>
    public async Task ApplyAsync(Guid runnerId, IReadOnlyDictionary<Guid, AgentTaskListDto> lists, CancellationToken cancellationToken)
    {
        if (lists.Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = lists.Keys.ToArray();
        var changed = new List<Guid>();
        foreach (var agent in await db.Agents.Include(a => a.Runner).Where(a => a.RunnerId == runnerId && ids.Contains(a.Id)).ToListAsync(cancellationToken))
        {
            var list = lists[agent.Id];
            // No list on the Runner, but one here: put it back rather than lose it.
            if (list.Revision == 0 && agent.TasksRevision > 0 && Stored(agent.TasksJson).Tasks.Count > 0)
                list = await RestoreAsync(agent.Runner!, agent, cancellationToken) ?? list with { Revision = agent.TasksRevision };
            if (list.Revision == agent.TasksRevision) continue;
            agent.TasksRevision = list.Revision;
            agent.TasksJson = list.Tasks.Count == 0 && list.Revision == 0 ? null : JsonSerializer.Serialize(list, ApiJson.Options);
            changed.Add(agent.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var id in changed) events.TasksChanged(id);
    }

    private static AgentTaskListDto Stored(string? json) =>
        json is null ? AgentTaskListDto.Empty : JsonSerializer.Deserialize<AgentTaskListDto>(json, ApiJson.Options) ?? AgentTaskListDto.Empty;

    // Uploads the Manager's copy beside the Runner's missing list; dam-tools installs it under the agent's lock, unless the
    // agent has started a new list meanwhile, and prints whichever list is there. A failure is retried after a while.
    private async Task<AgentTaskListDto?> RestoreAsync(Runner runner, Agent agent, CancellationToken cancellationToken)
    {
        if (_restoreFailed.TryGetValue(agent.Id, out var failed) && DateTime.UtcNow - failed < RestoreRetry) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            (await shell.RunAsync(runner, "umask 077; mkdir -p ~/.deveagentmanager/tasks", cancellationToken: timeout.Token)).EnsureSuccess("Preparing the task list folder");
            await shell.UploadAsync(runner, $"~/.deveagentmanager/tasks/{agent.Id:N}.restore", agent.TasksJson!, cancellationToken: timeout.Token);
            var result = (await shell.RunAsync(runner, $"python3 {Tools} --restore-tasks {agent.Id:N}", cancellationToken: timeout.Token))
                .EnsureSuccess("Restoring the task list");
            if (!Parse(result.Output).TryGetValue(agent.Id, out var list)) throw new InvalidOperationException("The Runner didn't confirm the restored task list.");
            _restoreFailed.TryRemove(agent.Id, out _);
            logger.LogInformation("Put the task list of agent {Agent} back on Runner {Runner}, which had none", agent.Name, runner.Name);
            return list;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _restoreFailed[agent.Id] = DateTime.UtcNow;
            logger.LogWarning(ex, "Couldn't put the task list of agent {Agent} back on Runner {Runner}; trying again in 10 minutes", agent.Name, runner.Name);
            return null;
        }
    }

    /// <summary>Clears the list on the Runner (the agent sees it on its next read) and here.</summary>
    public async Task ClearAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.AsNoTracking().Include(a => a.Runner).SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new KeyNotFoundException("Agent not found.");
        if (agent.Runner is not { State: RunnerState.Ready } runner)
            throw new InvalidOperationException("The agent's Runner is not ready, so its task list can't be cleared now.");
        var result = (await shell.RunAsync(runner, $"python3 {Tools} --clear-tasks {agent.Id:N}", TimeSpan.FromSeconds(20), cancellationToken))
            .EnsureSuccess("Clearing the task list");
        var lists = Parse(result.Output);
        if (!lists.ContainsKey(agentId)) throw new InvalidOperationException("The Runner didn't confirm the cleared task list. Update its tools and try again.");
        logger.LogInformation("Cleared the task list of agent {Agent}", agent.Name);
        await ApplyAsync(runner.Id, lists, cancellationToken);
    }
}
