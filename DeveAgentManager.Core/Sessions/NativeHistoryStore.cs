using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeveAgentManager.Core.Abstractions;
using DeveAgentManager.Contracts;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.Sessions;

internal sealed record NativeHistoryTransfer(bool PrefixMatches, string? SnapshotSha256 = null, bool FullyVerified = true);
internal sealed record NativeHistorySample(long Offset, byte[] Bytes);
internal sealed record NativeHistorySyncPolicy(HistorySyncMode Mode, bool Force = false, bool Idle = false);

/// <summary>Native history with sampled appends and full-audit checkpoints; indexes contain compact metadata only.</summary>
internal sealed class NativeHistoryStore(string root, TimeProvider? timeProvider = null, ILogger? logger = null, bool keepGenerations = false)
{
    /// <summary>A transfer located its source before the history saved now: saving it would lose the newer records.</summary>
    internal sealed class StaleLocationException() : IOException("A newer history was saved meanwhile.");
    private readonly string process = Guid.NewGuid().ToString("N");
    // One gate per history: reading or syncing a large conversation never holds up the others.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    private SemaphoreSlim Gate(Guid id) => gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> verifiedThisProcess = new();
    internal static readonly TimeSpan AuditInterval = TimeSpan.FromMinutes(30);
    internal sealed class State
    {
        public int UsageSchema { get; set; }
        public string? UsageFile { get; set; }
        public long UsageBytes { get; set; }
        public string File { get; set; } = "";
        public string Harness { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string Stamp { get; set; } = "";
        public long Bytes { get; set; }
        public string Sha256 { get; set; } = "";
        // Empty Sha256 means this checkpoint includes sampled appends awaiting a full audit.
        public DateTimeOffset? VerifiedAt { get; set; }
        public bool EndsWithNewline { get; set; }
        public string CursorFile { get; set; } = "";
        public long IndexBytes { get; set; }
        public string ReplyPreview { get; set; } = "";
        public string? CompanionManifest { get; set; }
        public DateTime? LastAssistantMessageAt { get; set; }
        /// <summary>Which process saved this history, and when it located the source (<see cref="SyncAsync"/>'s location).</summary>
        public string? LocatedBy { get; set; }
        public long Location { get; set; }
        [JsonIgnore] public SessionReadCursor Cursor { get; set; } = new();
    }
    private string Folder(Guid id) => Path.Combine(root, "native", id.ToString("N"));
    private string Manifest(Guid id) => Path.Combine(Folder(id), "state.json");
    public bool Exists(Guid id) => File.Exists(Manifest(id));
    public long Size(Guid id)
    {
        if (!Exists(id)) return 0;
        using var input = new FileStream(Manifest(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var state = JsonSerializer.Deserialize<State>(input, DeveSession.JsonOptions) ?? throw new IOException("Empty native history checkpoint.");
        return state.Bytes + state.IndexBytes + state.UsageBytes + input.Length;
    }

    private async Task<State?> ReadState(Guid id, CancellationToken ct)
    {
        if (!Exists(id)) return null;
        await using var stream = new FileStream(Manifest(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, true);
        return await JsonSerializer.DeserializeAsync<State>(stream, DeveSession.JsonOptions, ct);
    }

    // Checkpoints are replaced atomically. Lists can read the previous summary during an in-flight SSH transfer.
    public Task<State?> SummaryAsync(Guid id, CancellationToken ct) => ReadState(id, ct);

    public async Task<IReadOnlyList<DeveAgentManager.Contracts.Pets.SessionUsageTurn>> UsageAsync(Guid id, State summary, CancellationToken ct)
    {
        if (summary.UsageFile is null) return [];
        await using var input = new FileStream(Path.Combine(Folder(id), summary.UsageFile), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true);
        return await JsonSerializer.DeserializeAsync<List<DeveAgentManager.Contracts.Pets.SessionUsageTurn>>(input, DeveSession.JsonOptions, ct) ?? [];
    }

    private bool AuditDue(Guid id, State state, NativeHistorySyncPolicy? policy) => policy?.Force == true
        || !verifiedThisProcess.TryGetValue(id, out var generation) || generation != state.File
        || state.UsageSchema != 1 || state.UsageFile is null || state.VerifiedAt is not { } at || clock.GetUtcNow() - at >= AuditInterval
        || state.Sha256.Length == 0 && (policy?.Idle == true || policy?.Mode is HistorySyncMode.Full or HistorySyncMode.Shadow);

    internal static string? SourceIdentity(string stamp)
    {
        var fields = stamp.Split(':', 3);
        return fields.Length == 3 && ulong.TryParse(fields[0], out _) && ulong.TryParse(fields[1], out _)
            ? fields[0] + ":" + fields[1] : null;
    }

    public async Task<bool> IsCurrentAsync(Guid id, string harness, string sourcePath, long size, string stamp, CancellationToken ct,
        NativeHistorySyncPolicy? policy = null)
    {
        var state = await SummaryAsync(id, ct);
        return state is not null && state.Harness == harness && state.SourcePath == sourcePath && state.Bytes == size
            && !AuditDue(id, state, policy) && state.Stamp == stamp && stamp.Length > 0 && File.Exists(Path.Combine(Folder(id), state.File))
            && new FileInfo(Path.Combine(Folder(id), state.File)).Length >= size;
    }

    public async Task<DeveSession?> LoadAsync(Guid id, IReadOnlyDictionary<string, ISessionFormat> formats, CancellationToken ct,
        Func<DeveSession, string?, CancellationToken, Task>? loadCompanions = null)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            var state = await ReadState(id, ct);
            if (state is null) return null;
            ct.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(Path.Combine(Folder(id), state.File));
            if (stream.Length < state.Bytes) throw new IOException("The saved native history is incomplete.");
            // Uncommitted suffixes after a failed sync are never exposed to readers.
            using var bounded = new PrefixStream(stream, state.Bytes);
            using var reader = new StreamReader(bounded, Encoding.UTF8);
            var session = formats[state.Harness].Read(reader);
            if (loadCompanions is not null) await loadCompanions(session, state.CompanionManifest, ct);
            return session;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The committed native history, opened while syncs of it wait and read after. A sync only appends past the
    /// committed size or publishes a new generation file, so these bytes stay as they are until the snapshot is disposed.
    /// </summary>
    public async Task<Snapshot?> OpenAsync(Guid id, CancellationToken ct)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            var state = await ReadState(id, ct);
            if (state is null) return null;
            var file = new FileStream(Path.Combine(Folder(id), state.File), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            if (file.Length < state.Bytes)
            {
                await file.DisposeAsync();
                throw new IOException("The saved native history is incomplete.");
            }
            return new Snapshot(state.Harness, state.Bytes, state.CompanionManifest, file);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Saves a snapshot's records as another conversation's history (a copy for another owner), record by record: the copy
    /// gets its own generation, index and checkpoint, as a sync would make them.
    /// </summary>
    public async Task CopyAsync(Snapshot snapshot, Guid to, ISessionFormat format, CancellationToken ct) =>
        await SyncAsync(to, format, "copy", snapshot.Bytes, "", async (_, _, output, token) =>
        {
            await snapshot.Read().CopyToAsync(output, 1 << 20, token);
            return new NativeHistoryTransfer(true);
        }, ct);

    /// <summary>A history's committed native bytes (<see cref="OpenAsync"/>), read again from the start for each pass.</summary>
    internal sealed class Snapshot(string harness, long bytes, string? companionManifest, FileStream file) : IDisposable
    {
        public string Harness => harness;
        public long Bytes => bytes;
        public string? CompanionManifest => companionManifest;

        /// <summary>The committed bytes from the start. Starting another read ends the previous one.</summary>
        public Stream Read()
        {
            file.Position = 0;
            return new PrefixStream(file, bytes);
        }

        public void Dispose() => file.Dispose();
    }

    // transfer validates the old prefix's SHA-256, then writes exactly size-offset bytes. False requests a full rebuild.
    public async Task<(DeveSession Metadata, bool Changed)> SyncAsync(Guid id, ISessionFormat format, string sourcePath,
        long size, string stamp, Func<long, string?, Stream, CancellationToken, Task<NativeHistoryTransfer>> transfer, CancellationToken ct,
        NativeHistorySyncPolicy? policy = null,
        Func<long, IReadOnlyList<NativeHistorySample>, Stream, CancellationToken, Task<bool>>? append = null,
        Func<DeveSession, CancellationToken, Task<string?>>? beforeCommit = null, long? location = null)
    {
        using var queueTiming = Diagnostics.CaptureOperations.Start("history.gate", session: id, bytes: size);
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        queueTiming?.Stop();
        using var syncTiming = Diagnostics.CaptureOperations.Start("history.sync", session: id, bytes: size);
        string? incoming = null;
        string? pendingRaw = null;
        string? pendingIndex = null;
        string? pendingUsage = null;
        // The audit's hash of the committed prefix, continued over the appended bytes so the prefix is read only once.
        IncrementalHash? prefixHash = null;
        long prefixHashed = -1;
        try
        {
            var folder = Folder(id);
            Directory.CreateDirectory(folder);
            var old = await ReadState(id, ct);
            // Syncs locate a source, then transfer it. One that located it earlier than the history saved now (by another
            // sync of this process) would replace newer records with an older, shorter copy.
            if (location is { } located && old is { LocatedBy: { } by } && by == process && old.Location > located)
                throw new StaleLocationException();
            if (!keepGenerations)
                foreach (var stale in Directory.EnumerateFiles(folder, "*.jsonl").Where(p => Path.GetFileName(p) != old?.File)) File.Delete(stale);
            foreach (var stale in Directory.EnumerateFiles(folder, "*.index.json").Where(p => Path.GetFileName(p) != old?.CursorFile)) File.Delete(stale);
            foreach (var stale in Directory.EnumerateFiles(folder, "*.usage.json").Where(p => Path.GetFileName(p) != old?.UsageFile)) File.Delete(stale);
            var reusable = old is not null && old.Harness == format.HarnessId && old.SourcePath == sourcePath
                && old.Bytes <= size && File.Exists(Path.Combine(folder, old.File))
                && new FileInfo(Path.Combine(folder, old.File)).Length >= old.Bytes;
            // A whole copy instead of the new part is a large operation worth knowing about, with why.
            if (old is not null && !reusable)
                logger?.LogInformation("Copying the whole history of session {Session} again ({Size:0.0} MB): {Reason}", id, size / 1048576.0, RebuildReason(old, format, sourcePath, size, folder));
            else if (old is null && size >= LoggedCopyBytes)
                logger?.LogInformation("Copying the history of session {Session} for the first time ({Size:0.0} MB)", id, size / 1048576.0);
            if (reusable)
            {
                try
                {
                    await using var index = File.OpenRead(Path.Combine(folder, old!.CursorFile));
                    old.Cursor = await JsonSerializer.DeserializeAsync<SessionReadCursor>(index, DeveSession.JsonOptions, ct)
                        ?? throw new IOException("The saved history index is missing.");
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    old!.Cursor = new();
                    old.EndsWithNewline = false; // Rebuild the disposable index from the verified raw copy.
                }
            }
            if (reusable && (old!.UsageSchema != 1 || old.UsageFile is null)) old.EndsWithNewline = false;
            if (reusable && old!.Bytes == size && old.Stamp == stamp && stamp.Length > 0 && !AuditDue(id, old, policy))
            {
                if (beforeCommit is not null && format.HasCompanionFiles)
                {
                    var generation = await beforeCommit(old.Cursor.Session, ct);
                    if (generation != old.CompanionManifest)
                    {
                        old.CompanionManifest = generation;
                        await using (var output = new FileStream(Manifest(id) + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                            await JsonSerializer.SerializeAsync(output, old, DeveSession.JsonOptions, ct);
                        File.Move(Manifest(id) + ".tmp", Manifest(id), true);
                        return (old.Cursor.Session, true);
                    }
                }
                return (old.Cursor.Session, false);
            }
            var offset = reusable ? old!.Bytes : 0;
            incoming = Path.Combine(folder, "incoming.tmp");
            NativeHistoryTransfer received;
            await using (var output = new FileStream(incoming, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, true))
            {
                using var timing = Diagnostics.CaptureOperations.Start("history.transfer", session: id, bytes: size - offset);
                var canSample = reusable && size > offset && policy is { Mode: not HistorySyncMode.Full }
                    && append is not null && !policy.Idle && !AuditDue(id, old!, policy)
                    && SourceIdentity(stamp) is { } identity && identity == SourceIdentity(old!.Stamp);
                var sampled = false;
                if (canSample)
                {
                    var samples = new List<NativeHistorySample>();
                    await using var local = File.OpenRead(Path.Combine(folder, old!.File));
                    var head = (int)Math.Min(4096, offset);
                    var tail = Math.Max(head, offset - 65536);
                    foreach (var (start, count) in new[] { (0L, head), (tail, (int)(offset - tail)) })
                    {
                        if (count == 0) continue;
                        local.Position = start;
                        var bytes = new byte[count];
                        await local.ReadExactlyAsync(bytes, ct);
                        samples.Add(new(start, bytes));
                    }
                    using var sampleTiming = Diagnostics.CaptureOperations.Start("history.sample", session: id,
                        bytes: samples.Sum(s => s.Bytes.LongLength));
                    sampled = await append!(offset, samples, output, ct);
                    sampleTiming?.SetTag("matched", sampled);
                }
                if (sampled && policy!.Mode == HistorySyncMode.Sampled)
                    received = new(true, FullyVerified: false);
                else
                {
                    output.SetLength(0); output.Position = 0;
                    string? hash = null;
                    if (reusable)
                    {
                        // Sampled appends have no whole-file fingerprint. Compute one only for the audit.
                        using var hashTiming = Diagnostics.CaptureOperations.Start("history.hash-prefix", session: id, bytes: offset);
                        prefixHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        prefixHashed = await AppendFileAsync(prefixHash, Path.Combine(folder, old!.File), 0, offset, ct);
                        hash = Convert.ToHexString(prefixHash.GetCurrentHash());
                    }
                    received = await transfer(offset, hash, output, ct);
                    if (sampled && !received.PrefixMatches)
                        logger?.LogWarning("History sample check missed a prefix edit in session {Session}; full verification rebuilt it", id);
                }
                if (!received.PrefixMatches)
                {
                    logger?.LogInformation("The history of session {Session} on the Runner no longer matches the saved copy before {Offset:0.0} MB; copying all {Size:0.0} MB again",
                        id, offset / 1048576.0, size / 1048576.0);
                    offset = 0; reusable = false;
                    output.SetLength(0); output.Position = 0;
                    received = await transfer(0, null, output, ct);
                    if (!received.PrefixMatches) throw new IOException("Could not capture a stable native history.");
                }
                if (output.Length != size - offset) throw new IOException("Native history changed during transfer; synchronization will retry.");
            }
            syncTiming?.SetTag("verification", received.FullyVerified ? "full" : "sampled");
            var state = reusable ? old! : new State { File = Guid.NewGuid().ToString("N") + ".jsonl", Cursor = new() };
            var path = Path.Combine(folder, state.File);
            if (reusable)
            {
                await using var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 65536, true);
                output.SetLength(offset); output.Position = offset;
                await using var input = File.OpenRead(incoming);
                await input.CopyToAsync(output, ct);
            }
            else { pendingRaw = path; File.Move(incoming, path, true); }

            // An unterminated last line must be reparsed when more bytes arrive, even if it used to be valid JSON.
            var parseOffset = reusable && state.EndsWithNewline ? offset : 0;
            if (parseOffset == 0) state.Cursor = new();
            state.Cursor.MetadataOnly = true;
            using (var input = File.OpenRead(path))
            {
                using var timing = Diagnostics.CaptureOperations.Start("history.parse", session: id, bytes: size - parseOffset);
                input.Position = parseOffset;
                using var reader = new StreamReader(input, Encoding.UTF8);
                foreach (var line in format.IndexNativeRecords ? JsonLines.Parse(reader) : [])
                {
                    ct.ThrowIfCancellationRequested();
                    var start = state.Cursor.Session.Entries.Count;
                    format.AppendLines(state.Cursor, [line]);
                    state.Cursor.Compact(start);
                }
            }
            if (received.FullyVerified)
            {
                // Writing kept the committed prefix (it truncated to it and appended), so the prefix hash read above still
                // describes it on disk: only the appended bytes are read back. Otherwise hash the whole assembled file.
                var continued = reusable && prefixHash is not null && prefixHashed == offset;
                using var timing = Diagnostics.CaptureOperations.Start("history.hash", session: id, bytes: continued ? size - offset : size);
                if (continued)
                {
                    await AppendFileAsync(prefixHash!, path, offset, null, ct);
                    state.Sha256 = Convert.ToHexString(prefixHash!.GetHashAndReset());
                }
                else state.Sha256 = await HashFileAsync(path, null, ct);
                if (received.SnapshotSha256 is not null && !string.Equals(received.SnapshotSha256, state.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Native history changed during transfer; synchronization will retry.");
                using var input = File.OpenRead(path);
                state.EndsWithNewline = size == 0 || (input.Seek(-1, SeekOrigin.End) >= 0 && input.ReadByte() == '\n');
                state.VerifiedAt = clock.GetUtcNow();
            }
            else
            {
                state.Sha256 = "";
                using var input = File.OpenRead(path);
                state.EndsWithNewline = size == 0 || (input.Seek(-1, SeekOrigin.End) >= 0 && input.ReadByte() == '\n');
            }
            state.UsageSchema = 1;
            state.UsageFile = Guid.NewGuid().ToString("N") + ".usage.json";
            pendingUsage = Path.Combine(folder, state.UsageFile);
            await using (var usageOutput = new FileStream(pendingUsage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await JsonSerializer.SerializeAsync(usageOutput, state.Cursor.UsageTurns, DeveSession.JsonOptions, ct);
            state.UsageBytes = new FileInfo(pendingUsage).Length;
            state.LocatedBy = location is null ? null : process; state.Location = location ?? 0;
            state.Harness = format.HarnessId; state.SourcePath = sourcePath; state.Bytes = size; state.Stamp = stamp;
            state.CursorFile = Guid.NewGuid().ToString("N") + ".index.json";
            pendingIndex = Path.Combine(folder, state.CursorFile);
            await using (var index = new FileStream(pendingIndex, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await JsonSerializer.SerializeAsync(index, state.Cursor, DeveSession.JsonOptions, ct);
            state.IndexBytes = new FileInfo(pendingIndex).Length;
            state.ReplyPreview = SessionService.ReplyPreview(state.Cursor.Session);
            var agent = new DeveAgentManager.Core.Data.Agent();
            SessionService.UpdateLastAssistantMessage(agent, state.Cursor.Session);
            state.LastAssistantMessageAt = agent.LastAssistantMessageAt;
            // A log referencing a new replay checkpoint is not committed until that checkpoint is durable locally.
            if (beforeCommit is not null) state.CompanionManifest = await beforeCommit(state.Cursor.Session, ct);
            await using (var output = new FileStream(Manifest(id) + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                await JsonSerializer.SerializeAsync(output, state, DeveSession.JsonOptions, ct);
            File.Move(Manifest(id) + ".tmp", Manifest(id), true);
            if (received.FullyVerified) verifiedThisProcess[id] = state.File;
            pendingRaw = null; pendingIndex = null; pendingUsage = null;
            if (!keepGenerations)
                foreach (var stale in Directory.EnumerateFiles(folder, "*.jsonl").Where(p => p != path)) File.Delete(stale);
            foreach (var stale in Directory.EnumerateFiles(folder, "*.index.json").Where(p => Path.GetFileName(p) != state.CursorFile)) File.Delete(stale);
            foreach (var stale in Directory.EnumerateFiles(folder, "*.usage.json").Where(p => Path.GetFileName(p) != state.UsageFile)) File.Delete(stale);
            return (state.Cursor.Session, true);
        }
        finally
        {
            try
            {
                if (incoming is not null) File.Delete(incoming);
                if (pendingRaw is not null) File.Delete(pendingRaw);
                if (pendingIndex is not null) File.Delete(pendingIndex);
                if (pendingUsage is not null) File.Delete(pendingUsage);
            }
            finally { prefixHash?.Dispose(); gate.Release(); }
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try { verifiedThisProcess.TryRemove(id, out _); if (Directory.Exists(Folder(id))) Directory.Delete(Folder(id), true); }
        finally { gate.Release(); }
    }

    private const int HashChunkBytes = 1 << 20;
    private const long LoggedCopyBytes = 16L * 1024 * 1024;

    private static string RebuildReason(State old, ISessionFormat format, string sourcePath, long size, string folder) =>
        old.Harness != format.HarnessId ? $"its harness changed from {old.Harness} to {format.HarnessId}"
        : old.SourcePath != sourcePath ? "the Runner's history file is a different one now"
        : old.Bytes > size ? $"the Runner's copy is smaller than the {old.Bytes / 1048576.0:0.0} MB saved, so it was replaced or shortened"
        : !File.Exists(Path.Combine(folder, old.File)) ? "the saved copy is missing"
        : "the saved copy is shorter than recorded";

    /// <summary>
    /// SHA-256 of a file's first <paramref name="length"/> bytes (fewer when it is shorter), or of all of it, read 1 MiB at
    /// a time. SHA256.HashDataAsync reads 4 KiB per call, and every async read of a file opened for synchronous I/O is a
    /// thread pool round trip: restart audits of a few large histories kept two cores busy for a minute.
    /// </summary>
    internal static async Task<string> HashFileAsync(string path, long? length, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await AppendFileAsync(hash, path, 0, length, ct);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Adds a file's bytes from <paramref name="start"/> (at most <paramref name="length"/>) to a hash; returns how many.</summary>
    internal static async Task<long> AppendFileAsync(IncrementalHash hash, string path, long start, long? length, CancellationToken ct)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(HashChunkBytes);
        long hashed = 0;
        try
        {
            while (length is not { } end || hashed < end)
            {
                var count = (int)Math.Min(HashChunkBytes, (length ?? long.MaxValue) - hashed);
                var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, count), start + hashed, ct);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                hashed += read;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return hashed;
    }

    private sealed class PrefixStream(Stream input, long length) : Stream
    {
        private readonly long total = length;
        private long remaining = length;
        public override int Read(byte[] buffer, int offset, int count) { var n = input.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= n; return n; }
        public override int Read(Span<byte> buffer) { var n = input.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]); remaining -= n; return n; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => total;
        public override long Position { get => total - remaining; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
