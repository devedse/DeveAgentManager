using System.Security.Cryptography;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Configuration;

namespace DeveAgentManager.Core.Sessions;

/// <summary>
/// Files agents share for download (<c>dam-tools share-file</c>), kept on disk with their conversation. A file is first
/// received into a private folder, checked against the size and SHA-256 the Runner recorded, and only then moved in.
/// </summary>
public sealed class SessionFileStore(ManagerOptions options)
{
    public const long MaxBytes = 1L << 30;
    private static readonly TimeSpan StaleIncoming = TimeSpan.FromDays(1);

    private string Root => Path.Combine(options.SessionsDirectory, "files");
    private string IncomingFolder => Path.Combine(Root, ".incoming");
    private string Folder(Guid sessionId) => Path.Combine(Root, sessionId.ToString("N"));
    private string Metadata(Guid sessionId, Guid fileId) => Path.Combine(Folder(sessionId), fileId.ToString("N") + ".json");
    public string DataPath(Guid sessionId, Guid fileId) => Path.Combine(Folder(sessionId), fileId.ToString("N") + ".file");

    /// <summary>A received file that matched its size and checksum. Disposing it deletes it unless it was committed.</summary>
    public sealed class IncomingFile(string path, long bytes, string sha256) : IDisposable
    {
        internal string? FilePath { get; set; } = path;
        public long Bytes { get; } = bytes;
        public string Sha256 { get; } = sha256;
        public void Dispose() { if (FilePath is not null) File.Delete(FilePath); }
    }

    /// <summary>
    /// Receives a file through <paramref name="write"/>, which writes it to the stream it gets. Throws
    /// <see cref="InvalidDataException"/> when the content isn't what was shared (other size or checksum).
    /// </summary>
    public async Task<IncomingFile> ReceiveAsync(long bytes, string sha256, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        if (bytes is <= 0 or > MaxBytes) throw new InvalidDataException("Shared files must be between 1 byte and 1 GiB.");
        if (!IsSha256(sha256)) throw new InvalidDataException("The file has no valid SHA-256 checksum.");
        Directory.CreateDirectory(IncomingFolder);
        RemoveStaleIncoming();
        var path = Path.Combine(IncomingFolder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            string actual;
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            await using (var checking = new CheckingStream(file, bytes))
            {
                await write(checking, ct);
                await file.FlushAsync(ct);
                if (checking.Written != bytes) throw new InvalidDataException("The file is smaller than it was when it was shared.");
                actual = checking.Sha256();
            }

            if (actual != sha256) throw new InvalidDataException("The file changed after it was shared.");
            return new IncomingFile(path, bytes, sha256);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    /// <summary>Moves a received file into a conversation. A file with this id that is already there is kept as it is.</summary>
    public async Task<SessionFileDto> CommitAsync(Guid sessionId, Guid id, IncomingFile incoming, string fileName, string caption, CancellationToken ct, DateTime? createdAt = null)
    {
        if (await GetAsync(sessionId, id, ct) is { } existing) return existing;
        var dto = new SessionFileDto(id, sessionId, SafeName(fileName), Clean(caption, 500), incoming.Bytes, incoming.Sha256, createdAt ?? DateTime.UtcNow);
        Directory.CreateDirectory(Folder(sessionId));
        File.Move(incoming.FilePath ?? throw new InvalidOperationException("This file was already committed."), DataPath(sessionId, id), overwrite: true);
        incoming.FilePath = null;
        await AtomicWriteAsync(Metadata(sessionId, id), JsonSerializer.SerializeToUtf8Bytes(dto, ApiJson.Options), ct);
        return dto;
    }

    public async Task<SessionFileDto?> GetAsync(Guid sessionId, Guid id, CancellationToken ct)
    {
        try
        {
            var path = Metadata(sessionId, id);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            var dto = JsonSerializer.Deserialize<SessionFileDto>(await File.ReadAllTextAsync(path, ct), ApiJson.Options);
            var data = new FileInfo(DataPath(sessionId, id));
            return dto is not null && dto.SessionId == sessionId && dto.Id == id && data.Exists && data.Length == dto.Bytes ? dto : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    /// <summary>Newest first.</summary>
    public async Task<List<SessionFileDto>> ListAsync(Guid sessionId, CancellationToken ct)
    {
        var result = new List<SessionFileDto>();
        var folder = Folder(sessionId);
        if (Directory.Exists(folder))
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) && await GetAsync(sessionId, id, ct) is { } dto) result.Add(dto);
        return result.OrderByDescending(f => f.CreatedAt).ThenBy(f => f.Id).ToList();
    }

    /// <summary>Removes a file; false when it wasn't there.</summary>
    public async Task<bool> DeleteAsync(Guid sessionId, Guid id, CancellationToken ct)
    {
        if (await GetAsync(sessionId, id, ct) is null) return false;
        // Metadata first: a half-deleted file is then simply not listed.
        File.Delete(Metadata(sessionId, id));
        File.Delete(DataPath(sessionId, id));
        return true;
    }

    /// <summary>Copies a conversation's files (keeping their ids and times) to another conversation, e.g. a continued copy.</summary>
    public async Task CopyAsync(Guid fromSessionId, Guid toSessionId, CancellationToken ct)
    {
        foreach (var file in await ListAsync(fromSessionId, ct))
        {
            if (await GetAsync(toSessionId, file.Id, ct) is not null) continue;
            using var incoming = await ReceiveAsync(file.Bytes, file.Sha256, async (target, token) =>
            {
                await using var source = File.OpenRead(DataPath(fromSessionId, file.Id));
                await source.CopyToAsync(target, token);
            }, ct);
            await CommitAsync(toSessionId, file.Id, incoming, file.FileName, file.Caption, ct, file.CreatedAt);
        }
    }

    public void DeleteSession(Guid sessionId)
    {
        var folder = Folder(sessionId);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    /// <summary>What browsers get the download as. Always an attachment; only types that don't run in the page.</summary>
    public static string DownloadType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        // Lets Android offer to install the package once it has downloaded.
        ".apk" => "application/vnd.android.package-archive",
        ".zip" => "application/zip",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };

    internal static bool IsSha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string SafeName(string fileName)
    {
        var name = Clean(Path.GetFileName(fileName.Replace('\\', '/')), 128);
        return name.Trim('.', ' ').Length == 0 ? "file" : name;
    }

    private static string Clean(string text, int max)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        text = string.Concat(text.Where(c => !char.IsControl(c)));
        return text.Length <= max ? text : text[..max];
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, bytes, ct); File.Move(temp, path, overwrite: true); }
        finally { File.Delete(temp); }
    }

    /// <summary>Left behind when the Manager stopped during a transfer.</summary>
    private void RemoveStaleIncoming()
    {
        foreach (var path in Directory.EnumerateFiles(IncomingFolder, "*.tmp"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > StaleIncoming) File.Delete(path);
            }
            catch (IOException) { }
        }
    }

    /// <summary>Writes through to the file while counting and hashing; refuses anything past the expected size.</summary>
    private sealed class CheckingStream(Stream inner, long expected) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Written { get; private set; }
        public string Sha256() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        private void Count(ReadOnlySpan<byte> data)
        {
            Written += data.Length;
            if (Written > expected) throw new InvalidDataException("The file is larger than it was when it was shared.");
            _hash.AppendData(data);
        }

        public override void Write(ReadOnlySpan<byte> buffer) { Count(buffer); inner.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Count(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}
