using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Agents;

/// <summary>
/// The icons of the projects agents work on (<see cref="RepoIcons"/>). An agent's folder is searched when the agent is
/// new, then once a day (a project can get an icon later), unless one was picked in the Portal. Searches run one at a
/// time, in the background.
/// </summary>
public sealed class AgentIconService(
    IDbContextFactory<ManagerDbContext> dbFactory,
    RunnerShell shell,
    ManagerEvents events,
    ILogger<AgentIconService> logger) : BackgroundService
{
    private static readonly TimeSpan SearchEvery = TimeSpan.FromDays(1);
    private static readonly TimeSpan Round = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>The icon and its content type, or null when the agent has none.</summary>
    public async Task<(byte[] Data, string ContentType)?> GetAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentIcons.AsNoTracking().FirstOrDefaultAsync(i => i.AgentId == agentId, cancellationToken) is { Data: { } data, ContentType: { } type }
            ? (data, type)
            : null;
    }

    /// <summary>Every image in the agent's folder, best first, and the one in use.</summary>
    public async Task<(IReadOnlyList<ScoredRepoIcon> Images, string? Current, bool ChosenByUser)> ListAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var (agent, runner) = await LoadAsync(agentId, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var icon = await db.AgentIcons.AsNoTracking().FirstOrDefaultAsync(i => i.AgentId == agentId, cancellationToken);
        return (RepoIcons.Rank(await ScanAsync(runner, agent, cancellationToken), ProjectName(agent)), icon?.Path, icon?.ChosenByUser ?? false);
    }

    /// <summary>An image from the agent's folder (for the picker), or null.</summary>
    public async Task<(byte[] Data, string ContentType)?> ReadAsync(Guid agentId, string path, CancellationToken cancellationToken)
    {
        if (!RepoIcons.IsSafePath(path))
        {
            throw new ArgumentException("Not an image in the agent's folder.");
        }

        var (agent, runner) = await LoadAsync(agentId, cancellationToken);
        return await shell.DownloadBytesAsync(runner, $"{agent.WorkingDirectory.TrimEnd('/')}/{path}", RepoIcons.MaxBytes, cancellationToken) is { } data
            ? (data, RepoIcons.ContentTypes[Path.GetExtension(path)])
            : null;
    }

    /// <summary>Uses this image (a path in the folder), or none at all (null: the harness's logo). The choice is kept.</summary>
    public async Task ChooseAsync(Guid agentId, string? path, CancellationToken cancellationToken)
    {
        if (path is null)
        {
            await StoreAsync(agentId, null, null, chosenByUser: true, cancellationToken);
            return;
        }

        var image = await ReadAsync(agentId, path, cancellationToken) ?? throw new KeyNotFoundException($"'{path}' is not in the agent's folder (any more).");
        await StoreAsync(agentId, path, image, chosenByUser: true, cancellationToken);
    }

    /// <summary>Forgets a choice made in the Portal and searches the folder again, now.</summary>
    public async Task DetectAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var (agent, runner) = await LoadAsync(agentId, cancellationToken);
            await SearchAsync(agent, runner, cancellationToken);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A new agent is searched right away, the others at the next round.
        events.Changed += OnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SearchDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Looking for agent icons failed");
                }

                await _wake.WaitAsync(Round, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            events.Changed -= OnChanged;
        }
    }

    private void OnChanged(ManagerEvent managerEvent)
    {
        if (managerEvent.Kind == ManagerEventKind.AgentChanged && _wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    /// <summary>Agents on ready Runners that were never searched, or not for a day, and have no icon picked in the Portal.</summary>
    private async Task SearchDueAsync(CancellationToken cancellationToken)
    {
        List<(Agent Agent, Runner Runner)> due;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var since = DateTime.UtcNow - SearchEvery;
            due = (await db.Agents.AsNoTracking().Include(a => a.Runner)
                    .Where(a => a.Runner!.State == RunnerState.Ready && a.State != AgentState.Starting)
                    .Where(a => !db.AgentIcons.Any(i => i.AgentId == a.Id && (i.ChosenByUser || i.CheckedAt > since)))
                    .ToListAsync(cancellationToken))
                .Select(a => (a, a.Runner!)).ToList();
        }

        foreach (var (agent, runner) in due)
        {
            await _oneAtATime.WaitAsync(cancellationToken);
            try
            {
                await SearchAsync(agent, runner, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Tried again at the next round tomorrow; the agent keeps its icon meanwhile.
                logger.LogInformation(ex, "Could not look for an icon for agent {Agent}", agent.Name);
                await MarkCheckedAsync(agent.Id, cancellationToken);
            }
            finally
            {
                _oneAtATime.Release();
            }
        }
    }

    private async Task SearchAsync(Agent agent, Runner runner, CancellationToken cancellationToken)
    {
        var scan = await ScanAsync(runner, agent, cancellationToken);
        var pick = RepoIcons.Pick(scan, ProjectName(agent));
        var image = pick is null ? null : await ReadAsync(agent.Id, pick.Path, cancellationToken);
        await StoreAsync(agent.Id, image is null ? null : pick!.Path, image, chosenByUser: false, cancellationToken);
    }

    private async Task<RepoIconScan> ScanAsync(Runner runner, Agent agent, CancellationToken cancellationToken)
    {
        var result = await shell.RunAsync(runner, RepoIcons.ScanScript(agent.WorkingDirectory), TimeSpan.FromMinutes(1), cancellationToken);
        return RepoIcons.ParseScan(result.EnsureSuccess("Looking for the project's icon").Output);
    }

    private async Task StoreAsync(Guid agentId, string? path, (byte[] Data, string ContentType)? image, bool chosenByUser, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
        {
            return;
        }

        var icon = await db.AgentIcons.FirstOrDefaultAsync(i => i.AgentId == agentId, cancellationToken);
        if (icon is null)
        {
            icon = new AgentIcon { AgentId = agentId };
            db.AgentIcons.Add(icon);
        }

        var changed = icon.Path != path || !(icon.Data ?? []).AsSpan().SequenceEqual(image?.Data ?? []);
        icon.Path = path;
        icon.ContentType = image?.ContentType;
        icon.Data = image?.Data;
        icon.ChosenByUser = chosenByUser;
        icon.CheckedAt = DateTime.UtcNow;
        if (changed)
        {
            agent.IconVersion = image is null ? null : DateTime.UtcNow.Ticks;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (changed)
        {
            events.AgentChanged(agent.RunnerId, agent.Id);
        }
    }

    private async Task MarkCheckedAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.AgentIcons.FirstOrDefaultAsync(i => i.AgentId == agentId, cancellationToken) is { } icon)
        {
            icon.CheckedAt = DateTime.UtcNow;
        }
        else if (await db.Agents.AnyAsync(a => a.Id == agentId, cancellationToken))
        {
            db.AgentIcons.Add(new AgentIcon { AgentId = agentId, CheckedAt = DateTime.UtcNow });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(Agent Agent, Runner Runner)> LoadAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.AsNoTracking().Include(a => a.Runner).FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new KeyNotFoundException("Agent not found.");
        if (agent.Runner is not { State: RunnerState.Ready } runner)
        {
            throw new InvalidOperationException("The agent's Runner is not ready.");
        }

        return (agent, runner);
    }

    /// <summary>The repository's name (from its URL), or the folder's.</summary>
    private static string ProjectName(Agent agent) =>
        agent.RepositoryUrl is { Length: > 0 } url ? url.TrimEnd('/').Split('/', ':').Last() : Path.GetFileName(agent.WorkingDirectory.TrimEnd('/'));
}
