using System.Security.Cryptography;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Sessions;

/// <summary>Read-only requests from runner tools. Identity is checked against the runner and the current agent owner.</summary>
public sealed class ArtifactTransfers(IDbContextFactory<ManagerDbContext> factory, AgentArtifacts artifacts,
    SessionFileStore files, SessionImageStore images, AccessControl access, RunnerShell shell, ArtifactTokens tokens)
{
    internal sealed record Request(Guid Id, Guid AgentId, Guid SessionId, string Operation, string? SourceAgent = null,
        Guid? ArtifactId = null, string? Kind = null, int Offset = 0, int Limit = 100, bool All = false, string? Token = null);
    internal sealed record Item(Guid AgentId, string AgentName, Guid Id, string Kind, string FileName, string Caption, long Bytes, string? Sha256);
    internal sealed record Result(object Response, string? Path = null, byte[]? Data = null);

    internal async Task<Result> ResolveAsync(Guid runnerId, Request request, CancellationToken ct)
    {
        if (request.Id == Guid.Empty || request.Offset < 0 || request.Limit is < 1 or > 200
            || request.Operation is not ("list" or "fetch") || request.SourceAgent?.Length > 128)
            throw new ArgumentException("Invalid artifact request.");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await SessionImages.BelongsAsync(db, runnerId, request.AgentId, request.SessionId, ct))
            throw new KeyNotFoundException("The requesting agent session is unavailable on this runner.");
        var requester = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == request.AgentId, ct);
        if (!tokens.Valid(request.Token, requester)) throw new KeyNotFoundException("Artifact access is unavailable. Refresh the runner tools or restart this agent.");
        var owner = await db.Agents.Where(a => a.Id == request.AgentId).Select(a => a.Owner).SingleAsync(ct);
        if (owner is null || owner.Disabled) throw new KeyNotFoundException("Agent owner is unavailable.");
        var actor = CurrentUser.From(owner);
        var candidates = await AccessControl.VisibleAgents(db, actor).AsNoTracking().OrderBy(a => a.Name).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.Name }).ToListAsync(ct);
        var allowed = new List<(Guid Id, string Name)>();
        foreach (var candidate in candidates)
            if (await access.AgentAsync(actor, candidate.Id, ct) >= AgentAccess.Read) allowed.Add((candidate.Id, candidate.Name));
        var selected = request.All && request.Operation == "list" ? allowed : allowed.Where(a =>
            string.IsNullOrWhiteSpace(request.SourceAgent) ? a.Id == request.AgentId :
            Guid.TryParse(request.SourceAgent, out var id) ? a.Id == id : a.Name.Equals(request.SourceAgent, StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0 || (!request.All && selected.Count != 1))
            throw new KeyNotFoundException("Agent not found, ambiguous, or not shared with you. Use an agent id from list-artifacts --all.");
        var items = new List<(Item Item, Guid Source)>();
        foreach (var agent in selected)
        {
            var collection = await artifacts.ForAgentAsync(actor, agent.Id, ct);
            items.AddRange(collection.Files.Select(f => (new Item(agent.Id, agent.Name, f.Id, "file", f.FileName, f.Caption, f.Bytes, f.Sha256), f.SessionId)));
            items.AddRange(collection.Images.Select(i => (new Item(agent.Id, agent.Name, i.Id, "image", i.FileName, i.Caption, i.Bytes, null), i.SessionId)));
        }
        if (request.Operation == "list") return new(new
        {
            agents = allowed.Select(a => new { a.Id, a.Name }),
            artifacts = items.OrderBy(i => i.Item.AgentName).ThenBy(i => i.Item.Kind).ThenBy(i => i.Item.FileName).ThenBy(i => i.Item.Id)
                .Skip(request.Offset).Take(request.Limit).Select(i => i.Item), total = items.Count, request.Offset
        });
        var found = items.Where(i => i.Item.Id == request.ArtifactId && (request.Kind is null || request.Kind == i.Item.Kind)).ToList();
        if (found.Count != 1) throw new KeyNotFoundException("Artifact not found. Specify --kind if the id is ambiguous.");
        var item = found[0];
        // Recheck access immediately before opening bytes; a list is never a lasting grant.
        if (await access.AgentAsync(actor, item.Item.AgentId, ct) < AgentAccess.Read) throw new KeyNotFoundException("Agent access was removed.");
        if (item.Item.Kind == "file")
        {
            var file = await files.GetAsync(item.Source, item.Item.Id, ct) ?? throw new KeyNotFoundException("File was deleted.");
            return new(new { artifact = item.Item, bytes = file.Bytes, sha256 = file.Sha256 }, files.DataPath(item.Source, file.Id));
        }
        var image = await images.ReadAsync(item.Source, item.Item.Id, ct) ?? throw new KeyNotFoundException("Image was deleted.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(image.Data));
        return new(new { artifact = item.Item, bytes = (long)image.Data.Length, sha256 = hash }, Data: image.Data);
    }

    internal async Task ProcessAsync(Runner runner, Request request, CancellationToken ct)
    {
        if (request.Id == Guid.Empty) return;
        Result result;
        try { result = await ResolveAsync(runner.Id, request, ct); }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        { result = new(new { error = ex.Message }); }
        var folder = $"~/.deveagentmanager/artifact-requests/{request.Id:N}";
        (await shell.RunAsync(runner, $"python3 ~/.deveagentmanager/dam-tools.py --prepare-artifact-response {request.Id:N}", cancellationToken: ct))
            .EnsureSuccess("Preparing artifact response");
        if (result.Path is { } path)
        {
            await using var input = File.OpenRead(path);
            await shell.UploadFromAsync(runner, folder + "/content", input, ct);
        }
        else if (result.Data is { } data)
        {
            using var input = new MemoryStream(data, writable: false);
            await shell.UploadFromAsync(runner, folder + "/content", input, ct);
        }
        // The response is the completion marker. A dropped transport can only repeat a read, never a workload.
        await shell.UploadAtomicAsync(runner, folder + "/response.json", JsonSerializer.Serialize(result.Response, ApiJson.Options), ct);
    }
}
