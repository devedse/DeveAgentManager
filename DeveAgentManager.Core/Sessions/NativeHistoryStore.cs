using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeveAgentManager.Core.Abstractions;

namespace DeveAgentManager.Core.Sessions;

internal sealed record NativeHistoryTransfer(bool PrefixMatches, string? SnapshotSha256 = null);

/// <summary>Verified native history on disk; ordinary syncs parse only appended records into compact metadata.</summary>
internal sealed class NativeHistoryStore(string root)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    internal sealed class State
    {
        public string File { get; set; } = "";
        public string Harness { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string Stamp { get; set; } = "";
        public long Bytes { get; set; }
        public string Sha256 { get; set; } = "";
        public bool EndsWithNewline { get; set; }
        public string CursorFile { get; set; } = "";
        public long IndexBytes { get; set; }
        public string ReplyPreview { get; set; } = "";
        public DateTime? LastAssistantMessageAt { get; set; }
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
        return state.Bytes + state.IndexBytes + input.Length;
    }

    private async Task<State?> ReadState(Guid id, CancellationToken ct)
    {
        if (!Exists(id)) return null;
        await using var stream = new FileStream(Manifest(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, true);
        return await JsonSerializer.DeserializeAsync<State>(stream, DeveSession.JsonOptions, ct);
    }

    // Checkpoints are replaced atomically. Lists can read the previous summary during an in-flight SSH transfer.
    public Task<State?> SummaryAsync(Guid id, CancellationToken ct) => ReadState(id, ct);

    public async Task<bool> IsCurrentAsync(Guid id, string harness, string sourcePath, long size, string stamp, CancellationToken ct)
    {
        var state = await SummaryAsync(id, ct);
        return state is not null && state.Harness == harness && state.SourcePath == sourcePath && state.Bytes == size
            && state.Stamp == stamp && stamp.Length > 0 && File.Exists(Path.Combine(Folder(id), state.File))
            && new FileInfo(Path.Combine(Folder(id), state.File)).Length >= size;
    }

    public async Task<DeveSession?> LoadAsync(Guid id, IReadOnlyDictionary<string, ISessionFormat> formats, CancellationToken ct)
    {
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
            return formats[state.Harness].Read(reader);
        }
        finally { gate.Release(); }
    }

    // transfer validates the old prefix's SHA-256, then writes exactly size-offset bytes. False requests a full rebuild.
    public async Task<(DeveSession Metadata, bool Changed)> SyncAsync(Guid id, ISessionFormat format, string sourcePath,
        long size, string stamp, Func<long, string?, Stream, CancellationToken, Task<NativeHistoryTransfer>> transfer, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        string? incoming = null;
        string? pendingRaw = null;
        string? pendingIndex = null;
        try
        {
            var folder = Folder(id);
            Directory.CreateDirectory(folder);
            var old = await ReadState(id, ct);
            foreach (var stale in Directory.EnumerateFiles(folder, "*.jsonl").Where(p => Path.GetFileName(p) != old?.File)) File.Delete(stale);
            foreach (var stale in Directory.EnumerateFiles(folder, "*.index.json").Where(p => Path.GetFileName(p) != old?.CursorFile)) File.Delete(stale);
            var reusable = old is not null && old.Harness == format.HarnessId && old.SourcePath == sourcePath
                && old.Bytes <= size && File.Exists(Path.Combine(folder, old.File))
                && new FileInfo(Path.Combine(folder, old.File)).Length >= old.Bytes;
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
            if (reusable && old!.Bytes == size && old.Stamp == stamp && stamp.Length > 0)
                return (old.Cursor.Session, false);
            var offset = reusable ? old!.Bytes : 0;
            incoming = Path.Combine(folder, "incoming.tmp");
            NativeHistoryTransfer received;
            await using (var output = new FileStream(incoming, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, true))
            {
                received = await transfer(offset, reusable ? old!.Sha256 : null, output, ct);
                if (!received.PrefixMatches)
                {
                    offset = 0; reusable = false;
                    output.SetLength(0); output.Position = 0;
                    received = await transfer(0, null, output, ct);
                    if (!received.PrefixMatches) throw new IOException("Could not capture a stable native history.");
                }
                if (output.Length != size - offset) throw new IOException("Native history changed during transfer; synchronization will retry.");
            }
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
            using (var input = File.OpenRead(path))
            {
                input.Position = parseOffset;
                using var reader = new StreamReader(input, Encoding.UTF8);
                foreach (var line in JsonLines.Parse(reader))
                {
                    ct.ThrowIfCancellationRequested();
                    var start = state.Cursor.Session.Entries.Count;
                    format.AppendLines(state.Cursor, [line]);
                    state.Cursor.Compact(start);
                }
            }
            await using (var input = File.OpenRead(path))
            {
                state.Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
                if (received.SnapshotSha256 is not null && !string.Equals(received.SnapshotSha256, state.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Native history changed during transfer; synchronization will retry.");
                state.EndsWithNewline = size == 0 || (input.Seek(-1, SeekOrigin.End) >= 0 && input.ReadByte() == '\n');
            }
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
            await using (var output = new FileStream(Manifest(id) + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                await JsonSerializer.SerializeAsync(output, state, DeveSession.JsonOptions, ct);
            File.Move(Manifest(id) + ".tmp", Manifest(id), true);
            pendingRaw = null; pendingIndex = null;
            foreach (var stale in Directory.EnumerateFiles(folder, "*.jsonl").Where(p => p != path)) File.Delete(stale);
            foreach (var stale in Directory.EnumerateFiles(folder, "*.index.json").Where(p => Path.GetFileName(p) != state.CursorFile)) File.Delete(stale);
            return (state.Cursor.Session, true);
        }
        finally
        {
            try
            {
                if (incoming is not null) File.Delete(incoming);
                if (pendingRaw is not null) File.Delete(pendingRaw);
                if (pendingIndex is not null) File.Delete(pendingIndex);
            }
            finally { gate.Release(); }
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { if (Directory.Exists(Folder(id))) Directory.Delete(Folder(id), true); }
        finally { gate.Release(); }
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
