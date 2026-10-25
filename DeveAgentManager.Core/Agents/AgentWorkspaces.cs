using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

/// <summary>A reason from the Runner the person can act on, e.g. a taken folder or a repository without a remote.</summary>
public sealed class WorkspaceRefusedException(string message) : InvalidOperationException(message);

/// <summary>A folder kept when its workspace was removed. <paramref name="Token"/> is the ownership marker in its .git folder.</summary>
internal sealed record KeptFolder(string Path, string Name, string? Branch, DateTime RemovedAt, string Token);

/// <summary>
/// Workspaces: independent clones next to Main, in &lt;main&gt;.workspaces/&lt;name&gt;, each with its own agent. Clones
/// behave like any checkout, so agents pull and push normally. See docs/project-workspaces.md.
/// </summary>
public sealed partial class AgentService
{
    internal static readonly SemaphoreSlim ProjectGate = new(1, 1);

    /// <summary>Durable creation intent, recorded before the Runner changes anything.</summary>
    internal sealed record WorkspacePlan(string Main, string Source, string Container, string Path, string Name, WorkspaceStart Start,
        bool Pull, string Repo, string? Branch, string? NewBranch, string? Remote, string? Upstream, string Commit, string Token,
        Guid? Resume = null);

    private static readonly string WorkspaceScript = ReadScript("workspace.py");
    private static readonly JsonSerializerOptions WorkspaceJson = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Where a project's workspaces go: /work/app's are in /work/app.workspaces.</summary>
    public static string WorkspaceContainer(string main) => main.TrimEnd('/') + ".workspaces";

    internal static string WorkspaceCommand(string phase, object data, int? timeoutSeconds = null) =>
        (timeoutSeconds is { } seconds ? $"timeout {seconds} " : "") + "python3 - " + ShellQuote.Quote(phase) + " "
        + ShellQuote.Quote(JsonSerializer.Serialize(data, WorkspaceJson)) + " <<'DAM_WORKSPACE'\n" + WorkspaceScript + "\nDAM_WORKSPACE";

    /// <summary>Runs a workspace phase. Its refusals (exit code 3) are messages for the person, not failures.</summary>
    private async Task<JsonElement> WorkspacePhaseAsync(Runner runner, string phase, object data, TimeSpan timeout, CancellationToken ct, int? timeoutSeconds = null)
    {
        var result = await shell.RunAsync(runner, WorkspaceCommand(phase, data, timeoutSeconds), timeout, ct);
        if (result.ExitCode == 3) throw new WorkspaceRefusedException(result.Error.Trim());
        result.EnsureSuccess("Workspace " + phase);
        using var document = JsonDocument.Parse(result.Output.Trim().Split('\n')[^1]);
        return document.RootElement.Clone();
    }

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static List<KeptFolder> KeptFolders(Project project) =>
        project.KeptFoldersJson is { } json ? JsonSerializer.Deserialize<List<KeptFolder>>(json) ?? [] : [];

    private async Task<(Runner Runner, Agent Agent, Project? Project)> LoadWorkspaceAsync(ManagerDbContext db, Guid agentId, CancellationToken ct)
    {
        var agent = await db.Agents.Include(a => a.Runner).Include(a => a.Project).SingleOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new KeyNotFoundException("Agent not found.");
        return (RequireReady(agent.Runner!), agent, agent.Project);
    }

    /// <summary>How much a start point would copy, the free space next to Main and whether copies are copy-on-write.</summary>
    public async Task<WorkspaceEstimateDto> EstimateWorkspaceAsync(Guid sourceId, WorkspaceStart start, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (runner, source, project) = await LoadWorkspaceAsync(db, sourceId, ct);
        var main = project?.RootDirectory ?? source.WorkingDirectory;
        // The Runner-side timeout ends a measurement that outlives a closed dialog.
        var e = await WorkspacePhaseAsync(runner, "inspect", new { Main = main, Source = source.WorkingDirectory, Container = WorkspaceContainer(main), Start = start.ToString() },
            TimeSpan.FromMinutes(2), ct, timeoutSeconds: 110);
        return new(e.GetProperty("Bytes").GetInt64(), e.GetProperty("Files").GetInt32(), e.GetProperty("FreeBytes").GetInt64(),
            e.GetProperty("CopyOnWrite").GetBoolean(), Text(e, "Branch"), e.GetProperty("CanPull").GetBoolean());
    }

    public async Task<Agent> CreateWorkspaceAsync(CurrentUser actor, Guid sourceId, CreateWorkspaceRequest request, CancellationToken ct = default)
    {
        await ProjectGate.WaitAsync(ct);
        try
        {
            await AgentMoveCoordination.Gate.WaitAsync(ct);
            try { return await CreateWorkspaceLockedAsync(actor, sourceId, request, ct); }
            finally { AgentMoveCoordination.Gate.Release(); }
        }
        finally { ProjectGate.Release(); }
    }

    private async Task<Agent> CreateWorkspaceLockedAsync(CurrentUser actor, Guid sourceId, CreateWorkspaceRequest request, CancellationToken ct)
    {
        // Reuse lifecycle coordination: source deletion, moves and conversation selection cannot race creation.
        using var lease = await sessions.LockAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.Agents.Include(a => a.Runner).Include(a => a.Project).Include(a => a.Shares)
            .SingleOrDefaultAsync(a => a.Id == sourceId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        if (await access.AgentAsync(actor, sourceId, ct) < AgentAccess.Owner)
            throw new UnauthorizedAccessException("Only the project owner can create a workspace.");
        if (await access.RunnerAsync(actor, source.RunnerId, ct) < RunnerAccess.Use)
            throw new UnauthorizedAccessException("Creating a workspace needs Use access to its Runner.");
        RequireNotMoving(source);
        var runner = RequireReady(source.Runner!);
        if (AgentMoveCoordination.OperatingRunners.Contains(runner.Id))
            throw new InvalidOperationException("Wait for Runner maintenance before creating a workspace.");
        var project = source.Project ?? new Project { Id = source.Id, RunnerId = source.RunnerId, OwnerId = source.OwnerId,
            Name = source.Name, RootDirectory = source.WorkingDirectory, CreatedAt = source.CreatedAt };
        if (source.Project is null) source.Project = project;
        if (project.RunnerId != runner.Id || project.OwnerId != source.OwnerId)
            throw new InvalidOperationException("The project and workspace ownership do not match.");
        if (await db.Agents.AnyAsync(a => a.ProjectId == project.Id && a.MoveJson != null, ct))
            throw new InvalidOperationException("Finish all moves and cleanup in this project first.");

        StoredSession? resume = null;
        if (request.ResumeSessionId is { } resumeId)
        {
            resume = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == resumeId, ct) ?? throw new ArgumentException("Conversation not found.");
            if (!await InProjectAsync(db, resume, project.Id, ct)) throw new InvalidOperationException("Choose a conversation from this project.");
            if (resume.AgentId is not null) throw new InvalidOperationException("This conversation is active in another workspace. Open that workspace instead.");
        }
        var harness = GetHarness(request.HarnessId ?? resume?.HarnessId ?? source.HarnessId);
        if (!runner.HarnessIds.Contains(harness.Id)) throw new ArgumentException("Install the selected harness on the Runner first.");
        if (request.AccountId is { } login) await RequireOwnLoginAsync(actor, harness, login, ct);

        var kept = KeptFolders(project);
        var reuse = request.KeptFolder is { } keptPath
            ? kept.SingleOrDefault(k => k.Path == keptPath) ?? throw new ArgumentException("That kept folder is no longer listed.")
            : null;
        var names = await WorkspaceNamesAsync(db, runner.Id, reuse?.Name ?? request.Name, ct);
        var id = Guid.NewGuid();
        var token = id.ToString("N");
        var main = project.RootDirectory;
        WorkspacePlan? plan = null;
        string path, name;
        string? branch;
        if (reuse is null)
        {
            var pull = request.Pull && request.Start == WorkspaceStart.MainBranch;
            var newBranch = string.IsNullOrWhiteSpace(request.NewBranch) ? null : request.NewBranch.Trim();
            // Validates the repository and resolves the exact start commit before anything is recorded or changed.
            var checkedPlan = await WorkspacePhaseAsync(runner, "preflight", new { Main = main, Source = source.WorkingDirectory, Container = WorkspaceContainer(main),
                Start = request.Start.ToString(), Pull = pull, NewBranch = newBranch, Names = names, Token = token }, TimeSpan.FromMinutes(1), ct);
            plan = new(main, source.WorkingDirectory, WorkspaceContainer(main), checkedPlan.GetProperty("Path").GetString()!, checkedPlan.GetProperty("Name").GetString()!,
                request.Start, pull, checkedPlan.GetProperty("Repo").GetString()!, Text(checkedPlan, "Branch"), newBranch, Text(checkedPlan, "Remote"),
                Text(checkedPlan, "Upstream"), checkedPlan.GetProperty("Commit").GetString()!, token, resume?.Id);
            (path, name, branch) = (plan.Path, plan.Name, plan.NewBranch ?? plan.Branch);
        }
        else (path, name, branch) = (reuse.Path, names[0], reuse.Branch);
        if (await db.Agents.AnyAsync(a => a.RunnerId == runner.Id && a.WorkingDirectory == path, ct))
            throw new InvalidOperationException("Another agent already uses this folder.");

        var now = DateTime.UtcNow;
        var agent = new Agent { Id = id, RunnerId = runner.Id, OwnerId = source.OwnerId, Name = name, Project = project,
            IsClone = true, WorkingDirectory = path, RepositoryUrl = source.RepositoryUrl, RepositoryBranch = branch,
            WorkspaceCreationJson = plan is null ? null : JsonSerializer.Serialize(plan, WorkspaceJson), HarnessId = harness.Id, HarnessSessionId = harness.NewSessionId(id),
            TmuxSession = $"dam-{id:N}", AccountId = request.AccountId, State = AgentState.Starting,
            StateMessage = reuse is null ? "Creating workspace…" : "Preparing the kept folder…", CreatedAt = now, UpdatedAt = now };
        // Sharing remains project-wide; Sharing updates every workspace together.
        agent.Shares = source.Shares.Select(s => new AgentShare { AgentId = id, UserId = s.UserId, Access = s.Access,
            GrantedAt = s.GrantedAt, GrantedById = s.GrantedById }).ToList();
        await accounts.ResolveAsync(agent, ct);
        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        events.AgentChanged(runner.Id, id);
        var prepared = false;
        try
        {
            var result = reuse is null
                ? await WorkspacePhaseAsync(runner, "create", plan!, TimeSpan.FromMinutes(30), ct)
                : await WorkspacePhaseAsync(runner, "adopt", new { reuse.Path, reuse.Token, NewToken = token }, TimeSpan.FromMinutes(1), ct);
            prepared = true;
            agent.RepositoryBranch = Text(result, "Branch") ?? agent.RepositoryBranch;
            if (reuse is not null) project.KeptFoldersJson = JsonSerializer.Serialize(kept.Where(k => k.Path != reuse.Path).ToList());
            await sessions.AttachToAgentLockedAsync(runner, agent, resume?.Id, ct);
            agent.WorkspaceCreationJson = null;
            agent.State = AgentState.Stopped;
            agent.StateMessage = null;
            await db.SaveChangesAsync(CancellationToken.None);
            if (request.StartAgent)
            {
                await LaunchAsync(runner, agent, false, ct, loginActor: actor);
                agent.State = AgentState.Running;
            }
        }
        catch (Exception ex) when (!prepared && ex is WorkspaceRefusedException or RunnerCommandException)
        {
            // The script ran and cleaned up after itself: nothing exists on the Runner, so forget the reservation.
            if (ex is RunnerCommandException) logger.LogWarning(ex, "Creating workspace {Workspace} failed on the Runner", path);
            db.Agents.Remove(agent);
            await db.SaveChangesAsync(CancellationToken.None);
            events.Publish(new ManagerEvent(ManagerEventKind.AgentRemoved, runner.Id, id));
            if (ex is WorkspaceRefusedException) throw;
            throw new InvalidOperationException("Creating the workspace failed on the Runner, and nothing was kept. The Manager log has the details.");
        }
        catch (Exception ex)
        {
            agent.State = AgentState.Error;
            agent.StateMessage = prepared
                ? $"Starting the workspace failed: {ex.Message}. Use Start to try again."
                : $"Creating the workspace may not have finished: {ex.Message}. Start checks the folder before using it; Remove cleans it up.";
            throw;
        }
        finally
        {
            if (db.Entry(agent).State != EntityState.Detached)
            {
                agent.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                events.AgentChanged(runner.Id, id);
            }
        }
        return agent;
    }

    /// <summary>Free agent names on the Runner for a workspace: the requested name, then name-2, name-3…</summary>
    private static async Task<string[]> WorkspaceNamesAsync(ManagerDbContext db, Guid runnerId, string? requested, CancellationToken ct)
    {
        var taken = (await db.Agents.Where(a => a.RunnerId == runnerId).Select(a => a.Name).ToListAsync(ct)).ToHashSet();
        // "Fix Toolbar" becomes fix-toolbar.
        var name = string.Join('-', (requested ?? "").Trim().ToLowerInvariant().Split([' ', '_', '/', '.'], StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 0 && !AgentName().IsMatch(name)) throw new ArgumentException("Use lowercase letters, digits and dashes for the workspace name.");
        var stem = name.Length == 0 ? "workspace" : name[..Math.Min(name.Length, 36)].TrimEnd('-');
        IEnumerable<string> candidates = Enumerable.Range(2, 98).Select(n => $"{stem}-{n}");
        if (name.Length > 0) candidates = candidates.Prepend(name);
        var free = candidates.Where(n => !taken.Contains(n)).Take(20).ToArray();
        return free.Length > 0 ? free : throw new ArgumentException("Choose another workspace name.");
    }

    /// <summary>A conversation belongs to a project directly, or through the workspace it last ran in.</summary>
    private static async Task<bool> InProjectAsync(ManagerDbContext db, StoredSession session, Guid projectId, CancellationToken ct) =>
        session.ProjectId == projectId || session.WorkspaceAgentId is { } workspace && await db.Agents.AnyAsync(a => a.Id == workspace && a.ProjectId == projectId, ct);

    /// <summary>Checks a clone before an agent uses it, and completes a creation whose response was lost. Never repeats creation.</summary>
    private async Task VerifyWorkspaceAsync(Runner runner, Agent agent, CancellationToken ct)
    {
        if (!agent.IsClone) return;
        var plan = agent.WorkspaceCreationJson is { } json ? JsonSerializer.Deserialize<WorkspacePlan>(json, WorkspaceJson) : null;
        var verified = await WorkspacePhaseAsync(runner, "verify", new { Path = agent.WorkingDirectory, Token = agent.Id.ToString("N") }, TimeSpan.FromMinutes(1), ct);
        agent.RepositoryBranch = Text(verified, "Branch") ?? agent.RepositoryBranch;
        if (plan is not null)
        {
            if (await sessions.GetForAgentAsync(agent.Id, ct) is null) await sessions.AttachToAgentLockedAsync(runner, agent, plan.Resume, ct);
            agent.WorkspaceCreationJson = null;
        }
    }

    /// <summary>Uncommitted files and unpushed commits, for removing a workspace or updating it before a new conversation.</summary>
    public async Task<WorkspaceChangesDto> GetWorkspaceChangesAsync(Guid agentId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (runner, agent, _) = await LoadWorkspaceAsync(db, agentId, ct);
        var e = await WorkspacePhaseAsync(runner, "changes", new { Path = agent.WorkingDirectory, Token = agent.Id.ToString("N") }, TimeSpan.FromMinutes(1), ct);
        var conversations = await db.Sessions.CountAsync(s => s.WorkspaceAgentId == agentId || s.AgentId == agentId, ct);
        return new(agent.WorkingDirectory, e.GetProperty("Exists").GetBoolean(), e.GetProperty("Uncommitted").GetInt32(),
            e.GetProperty("Sample").EnumerateArray().Select(s => s.GetString()!).ToList(), e.GetProperty("Unpushed").GetInt32(),
            Text(e, "Upstream"), Text(e, "Branch"), conversations);
    }

    /// <summary>
    /// Stops a workspace's agent and removes it. Its conversations stay with the project. The folder is deleted, or kept
    /// (and listed on the project) with <paramref name="keepFiles"/>. Only a folder with this workspace's marker is deleted.
    /// </summary>
    public async Task RemoveWorkspaceAsync(Guid agentId, bool keepFiles, CancellationToken ct)
    {
        await ProjectGate.WaitAsync(ct);
        try
        {
            using var lease = await sessions.LockAsync(ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var (runner, agent, project) = await LoadWorkspaceAsync(db, agentId, ct);
            if (!agent.IsClone || project is null)
                throw new InvalidOperationException("Main is the project's own folder and is never removed. Delete the agent once the other workspaces are gone.");
            if (agent.MoveJson is not null) throw new InvalidOperationException("Finish the project move first.");
            var token = agent.Id.ToString("N");
            await shell.RunAsync(runner, $"tmux kill-session -t {ShellQuote.Quote("=" + agent.TmuxSession)} 2>/dev/null || true", cancellationToken: ct);
            try { await sessions.SyncRunnerLockedAsync(runner, ct, agent.Id); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Final session sync failed for {Agent}", agentId); }
            await sessions.PreserveArtifactsAsync(agent.Id, ct);
            if (!keepFiles)
                await WorkspacePhaseAsync(runner, "remove", new { Path = agent.WorkingDirectory, Container = WorkspaceContainer(project.RootDirectory), Token = token }, TimeSpan.FromMinutes(30), ct);
            else if ((await WorkspacePhaseAsync(runner, "changes", new { Path = agent.WorkingDirectory, Token = token }, TimeSpan.FromMinutes(1), ct)).GetProperty("Exists").GetBoolean())
                project.KeptFoldersJson = JsonSerializer.Serialize(KeptFolders(project).Append(new KeptFolder(agent.WorkingDirectory, agent.Name, agent.RepositoryBranch, DateTime.UtcNow, token)).ToList());
            // Its conversations keep the project; the workspace reference clears with the agent.
            await db.Sessions.Where(s => (s.WorkspaceAgentId == agent.Id || s.AgentId == agent.Id) && s.ProjectId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProjectId, project.Id), ct);
            db.Agents.Remove(agent);
            await db.SaveChangesAsync(ct);
            events.Publish(new ManagerEvent(ManagerEventKind.AgentRemoved, agent.RunnerId, agent.Id));
        }
        finally { ProjectGate.Release(); }
    }

    public async Task<IReadOnlyList<KeptFolderDto>> ListKeptFoldersAsync(Guid agentId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Agents.Where(a => a.Id == agentId).Select(a => a.Project).SingleOrDefaultAsync(ct);
        return project is null ? [] : KeptFolders(project).Select(k => new KeptFolderDto(k.Path, k.Name, k.Branch, k.RemovedAt)).ToList();
    }

    /// <summary>Deletes a kept folder, after checking it is still the one its workspace left behind.</summary>
    public async Task DeleteKeptFolderAsync(Guid agentId, string path, CancellationToken ct)
    {
        await ProjectGate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var (runner, agent, project) = await LoadWorkspaceAsync(db, agentId, ct);
            var kept = project is null ? [] : KeptFolders(project);
            var folder = kept.SingleOrDefault(k => k.Path == path) ?? throw new ArgumentException("That kept folder is no longer listed.");
            await WorkspacePhaseAsync(runner, "remove", new { folder.Path, Container = WorkspaceContainer(project!.RootDirectory), folder.Token }, TimeSpan.FromMinutes(30), ct);
            project.KeptFoldersJson = JsonSerializer.Serialize(kept.Where(k => k.Path != path).ToList());
            await db.SaveChangesAsync(ct);
            events.AgentChanged(runner.Id, agent.Id);
        }
        finally { ProjectGate.Release(); }
    }

    /// <summary>The branches the project's workspaces have checked out now; an agent may have switched since it started.</summary>
    public async Task<IReadOnlyDictionary<Guid, string?>> RefreshBranchesAsync(Guid agentId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (runner, agent, _) = await LoadWorkspaceAsync(db, agentId, ct);
        var members = agent.ProjectId is { } projectId
            ? await db.Agents.Where(a => a.ProjectId == projectId && a.RunnerId == runner.Id && a.MoveJson == null && a.WorkspaceCreationJson == null).ToListAsync(ct)
            : [agent];
        var found = await WorkspacePhaseAsync(runner, "branches", new { Paths = members.Select(m => m.WorkingDirectory).Distinct().ToArray() }, TimeSpan.FromSeconds(30), ct);
        var changed = new List<Agent>();
        foreach (var member in members)
        {
            if (Text(found, member.WorkingDirectory) is not { } branch || branch == member.RepositoryBranch) continue;
            member.RepositoryBranch = branch;
            changed.Add(member);
        }
        if (changed.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var member in changed) events.AgentChanged(runner.Id, member.Id);
        }
        return members.ToDictionary(m => m.Id, m => m.RepositoryBranch);
    }

    /// <summary>Pulls a workspace's branch before a new conversation, when it has nothing uncommitted or unpushed.</summary>
    private Task UpdateWorkspaceAsync(Runner runner, Agent agent, CancellationToken ct) =>
        WorkspacePhaseAsync(runner, "pull", new { Path = agent.WorkingDirectory }, TimeSpan.FromMinutes(3), ct);
}
