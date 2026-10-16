using System.Text;
using DeveAgentManager.Contracts;

namespace DeveAgentManager.Core.Sessions;

/// <summary>Bounded, inert text preview; original downloads remain available for every file.</summary>
public static class SessionFilePreviewReader
{
    public const int MaxBytes = 512 * 1024;

    public static async Task<SessionFilePreview> ReadAsync(string path, string name, CancellationToken ct)
    {
        var markdown = Path.GetExtension(name).ToLowerInvariant() is ".md" or ".markdown" or ".mdown" or ".mkd";
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxBytes) return new(null, markdown, "This file is too large to preview (512 KiB maximum). Download it to read it.");
        // Read at most the limit plus one, even if the file grows after the length check.
        var bytes = new byte[MaxBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxBytes) return new(null, markdown, "This file is too large to preview (512 KiB maximum). Download it to read it.");
        try
        {
            var data = bytes.AsSpan(0, count);
            var text = data.StartsWith(new byte[] { 0xff, 0xfe }) ? new UnicodeEncoding(false, true, true).GetString(data[2..])
                : data.StartsWith(new byte[] { 0xfe, 0xff }) ? new UnicodeEncoding(true, true, true).GetString(data[2..])
                : new UTF8Encoding(false, true).GetString(data.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? data[3..] : data);
            if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                return new(null, markdown, "This file contains binary data. Download it to open it.");
            return new(text, markdown, null);
        }
        catch (DecoderFallbackException)
        {
            return new(null, markdown, "This file is binary or uses an unsupported text encoding. Download it to open it.");
        }
    }
}
