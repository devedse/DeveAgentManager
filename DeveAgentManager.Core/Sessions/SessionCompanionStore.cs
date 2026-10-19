using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;

namespace DeveAgentManager.Core.Sessions;

/// <summary>Content-addressed native metadata/checkpoints, separate from the incrementally indexed event log.</summary>
internal sealed class SessionCompanionStore(string root)
{
    private sealed record SavedFile(string Stamp, string Hash, Guid? StreamId = null, long Bytes = 0, bool Verified = true);
    private sealed class OpaqueFormat : ISessionFormat
    {
        public string HarnessId => "native-companion";
        public bool SupportsIncrementalRead => true;
        public bool IndexNativeRecords => false;
        public string LocateCommand(string id) => throw new NotSupportedException();
        public string GetPath(string id, string cwd, DateTime at) => throw new NotSupportedException();
        public DeveSession Read(string content) => new();
        public string Write(DeveSession session, string id, string cwd) => throw new NotSupportedException();
    }
    private static readonly OpaqueFormat StreamFormat = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, NativeHistoryStore> streamStores = new();
    private string Folder(Guid id) => Path.Combine(root, "companions", id.ToString("N"));
    private string Manifest(Guid id, string? generation = null) => Path.Combine(Folder(id), generation is null ? "manifest.json" : Guid.ParseExact(generation, "N").ToString("N") + ".manifest.json");
    private NativeHistoryStore Streams(Guid id) => streamStores.GetOrAdd(id, key => new(Path.Combine(Folder(key), "streams"), keepGenerations: true));
    private async Task<Dictionary<string, SavedFile>> ManifestAsync(Guid id, CancellationToken ct, string? generation = null) => File.Exists(Manifest(id, generation))
        ? JsonSerializer.Deserialize<Dictionary<string, SavedFile>>(await File.ReadAllTextAsync(Manifest(id, generation), ct)) ?? [] : [];

    internal static string ValidatePath(string path)
    {
        if (path.Length == 0 || path.Length > 200 || path.Split('/').Any(p => p.Length == 0 || p is "." or "..")
            || path.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '/')))
            throw new FormatException("Invalid native companion path.");
        return path;
    }

    public async Task<string?> SyncAsync(RunnerShell shell, Runner runner, Guid id, ISessionFormat format, string source,
        DeveSession metadata, CancellationToken ct, NativeHistorySyncPolicy? policy = null)
    {
        var wanted = format.CompanionFiles(metadata);
        if (wanted.Count == 0) return null;
        foreach (var file in wanted) ValidatePath(file.Path);
        var saved = await ManifestAsync(id, ct);
        var basePath = source[..source.LastIndexOf('/')];
        async Task<Dictionary<string, (long Size, string Stamp)>> Stat(IEnumerable<SessionCompanionFile> requested)
        {
            var script = new StringBuilder();
            foreach (var file in requested)
                script.Append("if [ -f ").Append(ShellQuote.Quote(basePath + "/" + file.Path))
                    .Append(" ]; then printf '%s\\t%s\\n' ").Append(ShellQuote.Quote(file.Path))
                    .Append(" \"$(stat -L -c '%s\t%d:%i:%y:%z' ").Append(ShellQuote.Quote(basePath + "/" + file.Path)).Append(")\"; fi\n");
            if (script.Length == 0) return [];
            var result = await shell.RunAsync(runner, script.ToString(), TimeSpan.FromSeconds(30), ct);
            result.EnsureSuccess("Locating native session checkpoints");
            return result.Output.Split('\n').Select(l => l.TrimEnd('\r').Split('\t', 3)).Where(p => p.Length == 3)
                .ToDictionary(p => p[0], p => (long.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), p[2]));
        }
        var check = wanted.Where(f => !f.Immutable || !saved.TryGetValue(f.Path, out var oldFile) || !File.Exists(Path.Combine(Folder(id), oldFile.Hash))).ToList();
        var before = await Stat(check);
        var downloaded = new Dictionary<string, (string Content, string Stamp)>();
        var next = saved.Where(p => wanted.Any(f => f.Path == p.Key)).ToDictionary(p => p.Key, p => p.Value);
        foreach (var file in check)
        {
            if (!before.TryGetValue(file.Path, out var stat))
            {
                if (file.Required) throw new IOException("A required native replay checkpoint is missing.");
                continue;
            }
            var stamp = stat.Stamp;
            if (file.Incremental)
            {
                var streamId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(file.Path)).AsSpan(0, 16));
                var remote = basePath + "/" + file.Path;
                var streams = Streams(id);
                await streams.SyncAsync(streamId, StreamFormat, remote, stat.Size, stamp,
                    async (offset, hash, output, token) =>
                    {
                        var result = await shell.DownloadHistoryRangeAsync(runner, remote, offset, stat.Size, hash, output, token);
                        return new(result is not null, result);
                    }, ct, policy, (offset, samples, output, token) => shell.DownloadHistoryAppendAsync(runner, remote, offset, stat.Size, stamp, samples, output, token));
                var state = (await streams.SummaryAsync(streamId, ct))!;
                next[file.Path] = new(stamp, state.File, streamId, state.Bytes, state.Sha256.Length > 0);
                continue;
            }
            if (saved.TryGetValue(file.Path, out var old) && old.Stamp == stamp && File.Exists(Path.Combine(Folder(id), old.Hash))) continue;
            var content = await shell.DownloadAsync(runner, basePath + "/" + file.Path, ct)
                ?? throw new IOException("A native replay checkpoint could not be downloaded.");
            downloaded[file.Path] = (content, stamp);
        }
        var after = await Stat(check.Where(f => downloaded.ContainsKey(f.Path)));
        if (downloaded.Any(f => after.GetValueOrDefault(f.Key).Stamp != f.Value.Stamp))
            throw new IOException("Native session metadata changed during synchronization; the saved checkpoint was kept.");
        Directory.CreateDirectory(Folder(id));
        foreach (var file in wanted)
        {
            if (downloaded.TryGetValue(file.Path, out var fresh))
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fresh.Content)));
                var destination = Path.Combine(Folder(id), hash);
                await File.WriteAllTextAsync(destination + ".tmp", fresh.Content, new UTF8Encoding(false), ct);
                File.Move(destination + ".tmp", destination, true);
                next[file.Path] = new(fresh.Stamp, hash);
            }
            else if (!next.ContainsKey(file.Path) && file.Required) throw new IOException("A required native replay checkpoint has not been saved.");
        }
        var json = JsonSerializer.Serialize(next);
        var generation = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(json)).AsSpan(0, 16)).ToString("N");
        if (!File.Exists(Manifest(id, generation))) await File.WriteAllTextAsync(Manifest(id, generation), json, ct);
        await File.WriteAllTextAsync(Manifest(id) + ".tmp", json, ct);
        File.Move(Manifest(id) + ".tmp", Manifest(id), true);
        return generation;
    }

    public async Task ApplyAsync(Guid id, ISessionFormat format, DeveSession session, CancellationToken ct, string? generation = null)
    {
        if (generation is not null && !File.Exists(Manifest(id, generation))) throw new IOException("The native companion checkpoint is missing.");
        var saved = await ManifestAsync(id, ct, generation);
        var content = new Dictionary<string, string>();
        foreach (var (name, file) in saved)
        {
            ValidatePath(name);
            if (file.StreamId is { } streamId)
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Hash), "N", out _) || Path.GetExtension(file.Hash) != ".jsonl")
                    throw new IOException("Invalid native stream generation.");
                var path = Path.Combine(Folder(id), "streams", "native", streamId.ToString("N"), file.Hash);
                await using var input = File.OpenRead(path);
                var streamBytes = new byte[checked((int)file.Bytes)];
                await input.ReadExactlyAsync(streamBytes, ct);
                content[name] = Encoding.UTF8.GetString(streamBytes);
                continue;
            }
            if (file.Hash.Length != 64 || file.Hash.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("Invalid native checkpoint hash.");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Folder(id), file.Hash), ct);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Hash) throw new IOException("The saved native checkpoint is corrupt.");
            content[name] = Encoding.UTF8.GetString(bytes);
        }
        format.ApplyCompanionFiles(session, content);
    }

    public async Task<bool> HasPendingVerificationAsync(Guid id, string? generation, CancellationToken ct) =>
        generation is not null && (await ManifestAsync(id, ct, generation)).Values.Any(f => !f.Verified);

    // Called only after the authoritative log commits. Native readers load their companions while holding the same
    // log gate, so no reader can still be opening an obsolete manifest or stream generation at this point.
    public async Task PruneAsync(Guid id, string? generation, CancellationToken ct)
    {
        if (!Directory.Exists(Folder(id))) return;
        if (generation is null) { Delete(id); return; }
        var files = await ManifestAsync(id, ct, generation);
        var keep = files.Values.Select(f => f.StreamId is { } streamId
            ? Path.Combine(Folder(id), "streams", "native", streamId.ToString("N"), f.Hash)
            : Path.Combine(Folder(id), f.Hash)).ToHashSet(StringComparer.Ordinal);
        keep.Add(Manifest(id)); keep.Add(Manifest(id, generation));
        foreach (var path in Directory.EnumerateFiles(Folder(id), "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (!keep.Contains(path) && (name.EndsWith(".manifest.json", StringComparison.Ordinal)
                || name.EndsWith(".jsonl", StringComparison.Ordinal) || name.Length == 64 && name.All(char.IsAsciiHexDigit))) File.Delete(path);
        }
    }

    public long Size(Guid id) => Directory.Exists(Folder(id)) ? Directory.EnumerateFiles(Folder(id), "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) : 0;
    public void Delete(Guid id) { if (Directory.Exists(Folder(id))) Directory.Delete(Folder(id), true); }
}
