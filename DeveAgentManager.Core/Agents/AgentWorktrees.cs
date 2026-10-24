using System.Text.Json;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Agents;

public sealed partial class AgentService
{
    internal static readonly SemaphoreSlim ProjectGate = new(1, 1);
    internal sealed record WorktreeIntent(string Root, string Path, string Branch, string Revision, string Token);

    public async Task<Agent> CreateWorktreeAsync(CurrentUser actor, Guid sourceId, CreateWorktreeRequest request, CancellationToken ct = default)
    {
        await ProjectGate.WaitAsync(ct);
        try
        {
            await AgentMoveCoordination.Gate.WaitAsync(ct);
            try { return await CreateWorktreeLockedAsync(actor, sourceId, request, ct); }
            finally { AgentMoveCoordination.Gate.Release(); }
        }
        finally { ProjectGate.Release(); }
    }

    private async Task<Agent> CreateWorktreeLockedAsync(CurrentUser actor, Guid sourceId, CreateWorktreeRequest request, CancellationToken ct)
    {
        // Reuse lifecycle coordination: source deletion/moves and conversation selection cannot race creation.
        using var lease = await sessions.LockAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.Agents.Include(a => a.Runner).Include(a => a.Project).Include(a => a.Shares)
            .SingleOrDefaultAsync(a => a.Id == sourceId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        if (await access.AgentAsync(actor, sourceId, ct) < AgentAccess.Owner)
            throw new UnauthorizedAccessException("Only the project owner can create a worktree.");
        if (await access.RunnerAsync(actor, source.RunnerId, ct) < RunnerAccess.Use)
            throw new UnauthorizedAccessException("Creating a worktree needs Use access to its Runner.");
        RequireNotMoving(source);
        var runner = RequireReady(source.Runner!);
        if (AgentMoveCoordination.OperatingRunners.Contains(runner.Id))
            throw new InvalidOperationException("Wait for Runner maintenance before creating a workspace.");
        var name = (request.Name ?? "").Trim().ToLowerInvariant();
        if (!AgentName().IsMatch(name)) throw new ArgumentException("Use lowercase letters, digits and dashes for the workspace name.");
        if (await db.Agents.AnyAsync(a => a.RunnerId == runner.Id && a.Name == name, ct))
            throw new ArgumentException("An agent with this name already exists on the Runner.");
        var harness = GetHarness(request.HarnessId ?? source.HarnessId);
        if (!runner.HarnessIds.Contains(harness.Id)) throw new ArgumentException("Install the selected harness on the Runner first.");
        if (request.AccountId is { } login) await RequireOwnLoginAsync(actor, harness, login, ct);
        var project = source.Project ?? new Project { Id = source.Id, RunnerId = source.RunnerId, OwnerId = source.OwnerId,
            Name = source.Name, RootDirectory = source.WorkingDirectory, CreatedAt = source.CreatedAt };
        if (source.Project is null) source.Project = project;
        if (project.RunnerId != runner.Id || project.OwnerId != source.OwnerId)
            throw new InvalidOperationException("The project and workspace ownership do not match.");
        if (await db.Agents.AnyAsync(a => a.ProjectId == project.Id && a.MoveJson != null, ct))
            throw new InvalidOperationException("Finish all moves and cleanup in this project first.");
        var id = Guid.NewGuid();
        var path = $"{project.RootDirectory.TrimEnd('/')}.dam-worktrees/{project.Id:N}/{id:N}";
        if (await db.Agents.AnyAsync(a => a.RunnerId == runner.Id && a.WorkingDirectory == path, ct))
            throw new InvalidOperationException("The workspace path is already managed.");
        var intent = new WorktreeIntent(project.RootDirectory, path, (request.Branch ?? "").Trim(), request.BaseRevision?.Trim() is { Length: > 0 } revision ? revision : "HEAD", id.ToString("N"));
        // Validate Git and resolve the exact commit before persisting intent or mutating the repository.
        var check = await shell.RunAsync(runner, WorktreeCommand(intent, "preflight", source.WorkingDirectory), TimeSpan.FromMinutes(1), ct);
        check.EnsureSuccess("Checking worktree compatibility");
        if (check.Output.Trim() is not { Length: 40 or 64 } commit || !commit.All(char.IsAsciiHexDigit))
            throw new IOException("Git did not return a verified starting commit.");
        intent = intent with { Revision = check.Output.Trim() };
        var now = DateTime.UtcNow;
        var agent = new Agent { Id = id, RunnerId = runner.Id, OwnerId = source.OwnerId, Name = name, Project = project,
            IsWorktree = true, WorkingDirectory = path, RepositoryUrl = source.RepositoryUrl, RepositoryBranch = intent.Branch,
            WorkspaceCreationJson = JsonSerializer.Serialize(intent), HarnessId = harness.Id, HarnessSessionId = harness.NewSessionId(id),
            TmuxSession = $"dam-{id:N}", AccountId = request.AccountId, State = AgentState.Starting,
            StateMessage = "Creating worktree…", CreatedAt = now, UpdatedAt = now };
        // Sharing remains project-wide; Sharing updates every workspace together.
        agent.Shares = source.Shares.Select(s => new AgentShare { AgentId = id, UserId = s.UserId, Access = s.Access,
            GrantedAt = s.GrantedAt, GrantedById = s.GrantedById }).ToList();
        await accounts.ResolveAsync(agent, ct);
        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        events.AgentChanged(runner.Id, id);
        try
        {
            (await shell.RunAsync(runner, WorktreeCommand(intent, "create"), TimeSpan.FromMinutes(3), ct)).EnsureSuccess("Creating worktree");
            await sessions.AttachToAgentLockedAsync(runner, agent, null, ct);
            agent.WorkspaceCreationJson = null;
            agent.State = AgentState.Stopped;
            agent.StateMessage = null;
            await db.SaveChangesAsync(CancellationToken.None);
            if (request.Start)
            {
                await LaunchAsync(runner, agent, false, ct, loginActor: actor);
                agent.State = AgentState.Running;
            }
        }
        catch (Exception ex)
        {
            agent.State = AgentState.Error;
            agent.StateMessage = $"Worktree preparation or launch failed: {ex.Message}. Start verifies existing resources before resuming; it never repeats worktree creation.";
            throw;
        }
        finally
        {
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            events.AgentChanged(runner.Id, id);
        }
        return agent;
    }

    private async Task VerifyWorkspaceAsync(Runner runner, Agent agent, CancellationToken ct)
    {
        if (!agent.IsWorktree) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.SingleAsync(p => p.Id == agent.ProjectId, ct);
        var intent = agent.WorkspaceCreationJson is { } json ? JsonSerializer.Deserialize<WorktreeIntent>(json)!
            : new WorktreeIntent(project.RootDirectory, agent.WorkingDirectory, agent.RepositoryBranch ?? "", "HEAD", agent.Id.ToString("N"));
        var verified = (await shell.RunAsync(runner, WorktreeCommand(intent, "verify"), TimeSpan.FromMinutes(1), ct)).EnsureSuccess("Verifying worktree ownership");
        agent.RepositoryBranch = verified.Output.Trim();
        if (agent.WorkspaceCreationJson is not null)
        {
            if (await sessions.GetForAgentAsync(agent.Id, ct) is null) await sessions.AttachToAgentLockedAsync(runner, agent, null, ct);
            agent.WorkspaceCreationJson = null;
        }
    }

    // Git owns branch/path safeguards. The marker is outside the checkout and written before Git changes anything.
    // Verification reconciles a lost response; it cannot create a folder, branch, conversation replay or process.
    internal static string WorktreeCommand(WorktreeIntent intent, string phase, string? source = null)
    {
        var script = """
            set -eu
            fail() { printf '%s\n' "$1" >&2; exit 1; }
            test ! -L "$root" || fail 'Main cannot be a symbolic link.'
            parent="$target"
            while [ "$parent" != / ]; do
                test ! -L "$parent" || fail 'Managed workspace paths cannot pass through a symbolic link.'
                parent=$(dirname "$parent")
            done
            test -d "$root/.git" && test ! -L "$root/.git" || fail 'Main must be an ordinary Git checkout; linked/external Git metadata is not supported.'
            actual=$(git -C "$root" rev-parse --show-toplevel)
            test "$(realpath "$actual")" = "$(realpath "$root")" || fail 'Main must be the repository root.'
            test ! -f "$root/.gitmodules" || fail 'Worktrees for repositories with submodules are not supported yet.'
            test "$(git -C "$root" config --bool core.sparseCheckout || true)" != true || fail 'Sparse worktrees are not supported yet.'
            test ! -f "$root/.git/shallow" || fail 'Fetch complete history before creating managed worktrees.'
            exec 9>"$root/.git/dam-worktrees.lock"
            flock -x 9
            marker="$target.dam-owner"
            if [ "$phase" = preflight ]; then
                git check-ref-format --branch "$branch" >/dev/null
                test "${branch#-}" = "$branch" || fail 'Invalid branch name.'
                ! git -C "$root" show-ref --verify --quiet "refs/heads/$branch" || fail 'Choose a new branch name.'
                test ! -e "$target" && test ! -L "$target" && test ! -e "$marker" || fail 'Workspace destination already exists.'
                # HEAD means the selected workspace's committed HEAD, not necessarily Main's branch.
                commit=$(git -C "$source" rev-parse --verify --end-of-options "$revision^{commit}")
                ! git -C "$root" cat-file -e "$commit:.gitmodules" 2>/dev/null || fail 'The starting revision has submodules; managed worktrees are unavailable.'
                ! git -C "$root" grep -q 'filter=lfs' "$commit" -- .gitattributes '**/.gitattributes' || fail 'Git LFS worktrees need a separate compatibility check.'
                printf '%s\n' "$commit"
            elif [ "$phase" = create ]; then
                test ! -e "$target" && test ! -L "$target" || fail 'Workspace destination already exists.'
                mkdir -p "$(dirname "$target")"
                (set -C; printf '%s\n' "$token" >"$marker") || fail 'Creation is already recorded; verify instead of repeating it.'
                git -C "$root" worktree add -b "$branch" -- "$target" "$revision"
            else
                test -f "$marker" && test ! -L "$marker" && test "$(cat "$marker")" = "$token" || fail 'Managed workspace ownership could not be verified.'
                test -d "$target" && test ! -L "$target" || fail 'Worktree creation did not complete; inspect the recorded operation before creating a replacement.'
                common=$(git -C "$target" rev-parse --path-format=absolute --git-common-dir)
                test "$(realpath "$common")" = "$(realpath "$root/.git")" || fail 'Workspace belongs to another repository.'
                top=$(git -C "$target" rev-parse --show-toplevel)
                test "$(realpath "$top")" = "$(realpath "$target")" || fail 'Workspace path changed.'
                git -C "$root" worktree list --porcelain | grep -Fx -- "worktree $target" >/dev/null || fail 'Worktree registration is missing.'
                git -C "$target" symbolic-ref --quiet --short HEAD || git -C "$target" rev-parse --short HEAD
            fi
            """;
        return "bash -c " + ShellQuote.Quote($"root={ShellQuote.Quote(intent.Root)}; target={ShellQuote.Quote(intent.Path)}; branch={ShellQuote.Quote(intent.Branch)}; revision={ShellQuote.Quote(intent.Revision)}; token={ShellQuote.Quote(intent.Token)}; phase={ShellQuote.Quote(phase)}; source={ShellQuote.Quote(source ?? intent.Root)};\n" + script);
    }
}
