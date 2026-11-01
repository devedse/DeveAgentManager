using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Configuration;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Events;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Sessions;

/// <summary>
/// An agent's durable artifact collection. The index references existing attachment folders: upgrading never copies
/// large files. Source session ids remain provenance, not lifetime. Session-only shares retain their original scope.
/// </summary>
public sealed class AgentArtifacts(IDbContextFactory<ManagerDbContext> factory, ManagerOptions options,
    SessionFileStore files, SessionImageStore images, AccessControl access, ManagerEvents events)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Folder => Path.Combine(options.SessionsDirectory, "artifacts");
    private string Index(Guid agent) => Path.Combine(Folder, agent.ToString("N") + ".json");

    private async Task<HashSet<Guid>> ReadIndexAsync(Guid scope, CancellationToken ct)
    {
        var path = Index(scope);
        if (!File.Exists(path)) return [];
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<HashSet<Guid>>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("Invalid artifact collection index.");
    }

    private async Task WriteIndexAsync(Guid scope, HashSet<Guid> ids, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var temporary = Index(scope) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(ids.Order()), ct);
            File.Move(temporary, Index(scope), overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    // Called under _gate. A deleted agent's remaining archived sessions still resolve to the same collection.
    private async Task<Guid?> ScopeAsync(ManagerDbContext db, Guid session, CancellationToken ct)
    {
        var row = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == session, ct);
        if ((row?.WorkspaceAgentId ?? row?.AgentId) is { } scope) return scope;
        if (Directory.Exists(Folder))
            foreach (var path in Directory.EnumerateFiles(Folder, "*.json"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)
                    && (await ReadIndexAsync(id, ct)).Contains(session)) return id;
        return null;
    }

    private async Task<List<Guid>> BindAsync(ManagerDbContext db, Guid scope, CancellationToken ct)
    {
        var saved = await ReadIndexAsync(scope, ct);
        var current = await db.Sessions.AsNoTracking().Where(s => s.WorkspaceAgentId == scope || s.AgentId == scope)
            .Select(s => s.Id).ToListAsync(ct);
        if (current.Any(id => !saved.Contains(id)))
        {
            saved.UnionWith(current);
            // Commit this before a source session is removed. Repeated imports and restarts are idempotent.
            await WriteIndexAsync(scope, saved, ct);
        }
        return saved.Order().ToList();
    }

    private Task<List<Guid>> SourcesAsync(Guid session, CurrentUser? actor, CancellationToken ct) => SourcesAsync(session, actor, null, ct);

    /// <param name="elsewhere">Filled with the sources that another workspace of the project shared, and its name.</param>
    private async Task<List<Guid>> SourcesAsync(Guid session, CurrentUser? actor, Dictionary<Guid, string>? elsewhere, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var scope = await ScopeAsync(db, session, ct);
            if (scope is null) return [session];
            var sources = await BindAsync(db, scope.Value, ct);
            if (actor is not null && await db.Agents.AnyAsync(a => a.Id == scope, ct)
                && await access.AgentAsync(actor, scope.Value, ct) < AgentAccess.Read) return [session];
            // After agent deletion, a session share still only grants that individual session's attachments.
            if (actor is not null && !await db.Agents.AnyAsync(a => a.Id == scope, ct)
                && !actor.Has(UserRoles.ConversationManager)
                && await db.Sessions.AnyAsync(s => s.Id == session && s.OwnerId != actor.Id, ct)) return [session];
            if (await db.Agents.AnyAsync(a => a.Id == scope, ct))
                return await ProjectSourcesAsync(db, scope.Value, actor, ct, elsewhere);
            return sources;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<Guid>> ProjectSourcesAsync(ManagerDbContext db, Guid agentId, CurrentUser? actor, CancellationToken ct,
        Dictionary<Guid, string>? elsewhere = null)
    {
        var project = await db.Agents.Where(a => a.Id == agentId).Select(a => a.ProjectId).SingleOrDefaultAsync(ct);
        var agents = await db.Agents.Where(a => a.Id == agentId || project != null && a.ProjectId == project)
            .Select(a => new { a.Id, Workspace = a.IsClone ? a.Name : "Main" }).ToListAsync(ct);
        var sources = new HashSet<Guid>();
        var own = new HashSet<Guid>();
        foreach (var agent in agents)
        {
            if (actor is not null && await access.AgentAsync(actor, agent.Id, ct) < AgentAccess.Read) continue;
            var bound = await BindAsync(db, agent.Id, ct);
            sources.UnionWith(bound);
            if (agent.Id == agentId) own.UnionWith(bound);
            else if (elsewhere is not null) foreach (var source in bound) elsewhere.TryAdd(source, agent.Workspace);
        }
        if (project is not null && (actor is null || await access.AgentAsync(actor, agentId, ct) >= AgentAccess.Read))
            foreach (var (source, workspace) in await RemovedWorkspaceSourcesAsync(db, project.Value, agents.Select(a => a.Id).ToHashSet(), ct))
            {
                sources.Add(source);
                elsewhere?.TryAdd(source, workspace);
            }
        // A conversation that ran here too is this workspace's.
        foreach (var source in own) elsewhere?.Remove(source);
        return sources.Order().ToList();
    }

    /// <summary>
    /// What removed workspaces of a project shared: their conversations stay with the project, with no workspace, and
    /// their collections also hold conversations deleted before. Access follows the project's, like their conversations'.
    /// </summary>
    private async Task<Dictionary<Guid, string>> RemovedWorkspaceSourcesAsync(ManagerDbContext db, Guid project, HashSet<Guid> current, CancellationToken ct)
    {
        var result = (await db.Sessions.AsNoTracking().Where(s => s.ProjectId == project && s.WorkspaceAgentId == null && s.AgentId == null)
                .Select(s => new { s.Id, s.WorkingDirectory }).ToListAsync(ct))
            .ToDictionary(s => s.Id, s => s.WorkingDirectory is { } folder ? $"{folder.TrimEnd('/').Split('/')[^1]} (removed)" : "a removed workspace");
        if (result.Count == 0 || !Directory.Exists(Folder)) return result;
        foreach (var path in Directory.EnumerateFiles(Folder, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) || current.Contains(id)) continue;
            var index = await ReadIndexAsync(id, ct);
            var known = index.FirstOrDefault(result.ContainsKey);
            if (known == Guid.Empty) continue;
            foreach (var source in index) result.TryAdd(source, result[known]);
        }
        return result;
    }

    public async Task<List<SessionFileDto>> ListFilesAsync(Guid session, CurrentUser? actor, CancellationToken ct)
    {
        var elsewhere = new Dictionary<Guid, string>();
        // Own sources first: a copy that this workspace also has (a copied conversation) counts as its own.
        var files = await FilesAsync((await SourcesAsync(session, actor, elsewhere, ct)).OrderBy(elsewhere.ContainsKey), ct);
        return files.Select(f => f with { SessionId = session, OtherWorkspace = elsewhere.GetValueOrDefault(f.SessionId) }).ToList();
    }

    public async Task<List<SessionImageDto>> ListImagesAsync(Guid session, CurrentUser? actor, CancellationToken ct)
    {
        var elsewhere = new Dictionary<Guid, string>();
        var images = await ImagesAsync((await SourcesAsync(session, actor, elsewhere, ct)).OrderBy(elsewhere.ContainsKey), ct);
        return images.Select(i => i with { SessionId = session, OtherWorkspace = elsewhere.GetValueOrDefault(i.SessionId) }).ToList();
    }

    private async Task<List<SessionFileDto>> FilesAsync(IEnumerable<Guid> sources, CancellationToken ct)
    {
        var result = new List<SessionFileDto>();
        foreach (var source in sources) result.AddRange(await files.ListAsync(source, ct));
        return result.OrderByDescending(f => f.CreatedAt).ThenBy(f => f.Id).DistinctBy(f => f.Id).ToList();
    }

    private async Task<List<SessionImageDto>> ImagesAsync(IEnumerable<Guid> sources, CancellationToken ct)
    {
        var result = new List<SessionImageDto>();
        foreach (var source in sources) result.AddRange(await images.ListAsync(source, ct));
        return result.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id).DistinctBy(i => i.Id).ToList();
    }

    public async Task<(SessionFileDto File, string Path)?> FileAsync(Guid session, Guid id, CurrentUser? actor, CancellationToken ct)
    {
        foreach (var source in await SourcesAsync(session, actor, ct))
            if (await files.GetAsync(source, id, ct) is { } file) return (file with { SessionId = session }, files.DataPath(source, id));
        return null;
    }

    public async Task<(SessionImageDto Image, byte[] Data)?> ImageAsync(Guid session, Guid id, CurrentUser? actor, CancellationToken ct)
    {
        foreach (var source in await SourcesAsync(session, actor, ct))
            if (await images.ReadAsync(source, id, ct) is { } image) return (image.Image with { SessionId = session }, image.Data);
        return null;
    }

    public async Task<bool> DeleteFileAsync(Guid session, Guid id, CurrentUser? actor, CancellationToken ct)
    {
        var removed = false;
        foreach (var source in await SourcesAsync(session, actor, ct)) removed |= await files.DeleteAsync(source, id, ct);
        if (removed) await NotifyAsync(session, ct);
        return removed;
    }

    public async Task NotifyAsync(Guid session, CancellationToken ct)
    {
        var sources = await SourcesAsync(session, null, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var current = await db.Sessions.AsNoTracking().Where(s => sources.Contains(s.Id))
            .Select(s => new { s.Id, s.WorkspaceAgentId }).ToListAsync(ct);
        foreach (var row in current) events.SessionChanged(row.Id, row.WorkspaceAgentId);
    }

    /// <summary>Bind before deleting history; only the last archive of a deleted agent removes the collection.</summary>
    public async Task RemoveSessionAsync(Guid session, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var scope = await ScopeAsync(db, session, ct);
            if (scope is null) { files.DeleteSession(session); images.DeleteSession(session); return; }
            var sources = await BindAsync(db, scope.Value, ct);
            if (await db.Agents.AnyAsync(a => a.Id == scope, ct)
                || await db.Sessions.AnyAsync(s => s.Id != session && sources.Contains(s.Id), ct)) return;
            foreach (var source in sources) { files.DeleteSession(source); images.DeleteSession(source); }
            File.Delete(Index(scope.Value));
        }
        finally { _gate.Release(); }
    }

    internal async Task<(List<SessionFileDto> Files, List<SessionImageDto> Images)> ForAgentAsync(CurrentUser actor, Guid agent, CancellationToken ct)
    {
        if (await access.AgentAsync(actor, agent, ct) < AgentAccess.Read) throw new KeyNotFoundException("Agent not found or not shared with you.");
        List<Guid> sources;
        await _gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); sources = await ProjectSourcesAsync(db, agent, actor, ct); }
        finally { _gate.Release(); }
        return (await FilesAsync(sources, ct), await ImagesAsync(sources, ct));
    }

    internal async Task PreserveAsync(Guid agent, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); await BindAsync(db, agent, ct); }
        finally { _gate.Release(); }
    }

    internal async Task<bool> HasCollectionAsync(Guid session, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); return await ScopeAsync(db, session, ct) is not null; }
        finally { _gate.Release(); }
    }

    internal async Task CopyAttachmentsAsync(Guid source, Guid target, CurrentUser actor, CancellationToken ct)
    {
        foreach (var folder in await SourcesAsync(source, actor, ct))
        {
            await files.CopyAsync(folder, target, ct);
            await images.CopyAsync(folder, target, ct);
        }
    }

    internal async Task<bool> OwnsSourceAsync(Guid runner, Guid agent, Guid session, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Agents.AnyAsync(a => a.Id == agent && a.RunnerId == runner, ct)) return false;
        if (await db.Sessions.AnyAsync(s => s.Id == session && (s.WorkspaceAgentId == agent || s.AgentId == agent), ct)) return true;
        // A publication queued just before history deletion still belongs to the living agent.
        return (await ReadIndexAsync(agent, ct)).Contains(session);
    }
}
