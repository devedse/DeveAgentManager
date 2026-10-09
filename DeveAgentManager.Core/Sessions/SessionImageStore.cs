using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Configuration;

namespace DeveAgentManager.Core.Sessions;

/// <summary>Durable session attachments on disk; metadata is committed after the image.</summary>
public sealed class SessionImageStore(ManagerOptions options)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    private string Folder(Guid sessionId) => Path.Combine(options.SessionsDirectory, "images", sessionId.ToString("N"));
    private string Metadata(Guid sessionId, Guid imageId) => Path.Combine(Folder(sessionId), imageId.ToString("N") + ".json");
    private string DataPath(Guid sessionId, Guid imageId) => Path.Combine(Folder(sessionId), imageId.ToString("N") + ".image");

    internal static string ContentType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        throw new ArgumentException("Use a PNG, JPEG, GIF or WebP image.");
    }

    public async Task<SessionImageDto> SaveAsync(Guid sessionId, Guid id, byte[] data, string fileName, string caption, CancellationToken ct, DateTime? createdAt = null)
    {
        if (data.Length is 0 or > MaxBytes) throw new ArgumentException("Images must be between 1 byte and 10 MiB.");
        var type = ContentType(data);
        if (await GetAsync(sessionId, id, ct) is { } existing) return existing;
        var safeName = Path.GetFileName(fileName.Replace('\\', '/'));
        safeName = Clean(safeName, 128);
        if (safeName.Length == 0) safeName = "image";
        var dto = new SessionImageDto(id, sessionId, safeName, Clean(caption, 500), type, data.Length, createdAt ?? DateTime.UtcNow);
        Directory.CreateDirectory(Folder(sessionId));
        await AtomicWriteAsync(DataPath(sessionId, id), data, ct);
        await AtomicWriteAsync(Metadata(sessionId, id), JsonSerializer.SerializeToUtf8Bytes(dto, ApiJson.Options), ct);
        return dto;
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

    public async Task<SessionImageDto?> GetAsync(Guid sessionId, Guid id, CancellationToken ct)
    {
        try
        {
            var path = Metadata(sessionId, id);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            var dto = JsonSerializer.Deserialize<SessionImageDto>(await File.ReadAllTextAsync(path, ct), ApiJson.Options);
            return dto is not null && dto.SessionId == sessionId && dto.Id == id && File.Exists(DataPath(sessionId, id)) ? dto : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public async Task<List<SessionImageDto>> ListAsync(Guid sessionId, CancellationToken ct)
    {
        var result = new List<SessionImageDto>();
        var folder = Folder(sessionId);
        if (Directory.Exists(folder))
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) && await GetAsync(sessionId, id, ct) is { } dto) result.Add(dto);
        return result.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id).ToList();
    }

    public async Task<(SessionImageDto Image, byte[] Data)?> ReadAsync(Guid sessionId, Guid id, CancellationToken ct)
    {
        if (await GetAsync(sessionId, id, ct) is not { } dto) return null;
        try
        {
            if (new FileInfo(DataPath(sessionId, id)).Length > MaxBytes) return null;
            return (dto, await File.ReadAllBytesAsync(DataPath(sessionId, id), ct));
        }
        catch (IOException) { return null; }
    }

    /// <summary>Copies a conversation's images (keeping their ids and times) to another conversation, e.g. a continued copy.</summary>
    public async Task CopyAsync(Guid fromSessionId, Guid toSessionId, CancellationToken ct)
    {
        foreach (var image in await ListAsync(fromSessionId, ct))
        {
            if (await ReadAsync(fromSessionId, image.Id, ct) is { } found)
            {
                await SaveAsync(toSessionId, image.Id, found.Data, image.FileName, image.Caption, ct, image.CreatedAt);
            }
        }
    }

    public void DeleteSession(Guid sessionId)
    {
        var folder = Folder(sessionId);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
