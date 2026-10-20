using System.Text;
using DeveAgentManager.Contracts;

namespace DeveAgentManager.Core.Sessions;

/// <summary>Small inline text or a streamed preview descriptor. File size never prevents viewing text or HTML.</summary>
public static class SessionFilePreviewReader
{
    public const int InlineBytes = 512 * 1024;
    public static bool IsHtml(string name) => Path.GetExtension(name).ToLowerInvariant() is ".html" or ".htm";
    public static bool IsPdf(string name) => Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public static string Charset(ReadOnlySpan<byte> data) => data.StartsWith(new byte[] { 0xff, 0xfe }) ? "utf-16"
        : data.StartsWith(new byte[] { 0xfe, 0xff }) ? "utf-16BE" : "utf-8";

    public static async Task<SessionFilePreview> ReadAsync(string path, string name, CancellationToken ct)
    {
        if (IsPdf(name)) return new(null, false, null);
        var markdown = Path.GetExtension(name).ToLowerInvariant() is ".md" or ".markdown" or ".mdown" or ".mkd";
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // Large content is streamed directly to the browser, not buffered/serialized in the Manager.
        var bytes = new byte[(int)Math.Min(stream.Length, InlineBytes)];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        try
        {
            var data = bytes.AsSpan(0, count);
            var encoding = Charset(data) switch { "utf-16" => (Encoding)new UnicodeEncoding(false, true, true),
                "utf-16BE" => new UnicodeEncoding(true, true, true), _ => new UTF8Encoding(false, true) };
            var skip = data.StartsWith(new byte[] { 0xff, 0xfe }) || data.StartsWith(new byte[] { 0xfe, 0xff }) ? 2
                : data.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            var chars = new char[encoding.GetMaxCharCount(count)];
            // A sample ending in a partial Unicode character is valid when more bytes follow.
            encoding.GetDecoder().Convert(data[skip..], chars, flush: stream.Position == stream.Length,
                out _, out var used, out _);
            var text = new string(chars, 0, used);
            if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                return new(null, markdown, "This file contains binary data. Download it to open it.");
            return new(stream.Position == stream.Length ? text : null, markdown, null);
        }
        catch (DecoderFallbackException)
        {
            return new(null, markdown, "This file is binary or uses an unsupported text encoding. Download it to open it.");
        }
    }
}
