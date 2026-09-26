using System.Text;
using Renci.SshNet;

namespace DeveAgentManager.Core.Ssh;

/// <summary>
/// An interactive terminal (PTY) on a Runner, used by the browser terminal. Owns its own SSH connection.
/// </summary>
public sealed class TerminalConnection : IDisposable
{
    public const string TerminalType = "xterm-256color";

    private readonly SshClient _client;
    private readonly ShellStream _shell;
    private readonly StartupOutputFilter? _filter;

    private TerminalConnection(SshClient client, ShellStream shell, StartupOutputFilter? filter)
    {
        _client = client;
        _shell = shell;
        _filter = filter;
    }

    /// <summary>
    /// Opens a login shell. When <paramref name="command"/> is set, the shell is replaced by that command
    /// (e.g. <c>tmux attach</c>) and everything the shell prints before it (prompt, echo) is hidden.
    /// </summary>
    public static TerminalConnection Open(SshClient client, string? command, int columns, int rows)
    {
        var shell = client.CreateShellStream(TerminalType, (uint)columns, (uint)rows, 0, 0, 64 * 1024);
        StartupOutputFilter? filter = null;
        if (command is not null)
        {
            filter = new StartupOutputFilter();
            // The tty goes out before the marker (hidden with the rest): it identifies this terminal among the clients
            // attached to a tmux session.
            shell.Write($"stty -echo 2>/dev/null; printf '{StartupOutputFilter.EscapedTtyMarker}%s\\n' \"$(tty)\"; printf '{StartupOutputFilter.EscapedMarker}\\n'; exec {command}\n");
            shell.Flush();
        }

        return new TerminalConnection(client, shell, filter);
    }

    /// <summary>The terminal's tty on the Runner (e.g. /dev/pts/3), known once the startup output has been read.</summary>
    public string? Tty => _filter?.Tty;

    /// <summary>Blocks until output is available. Returns an empty array when the terminal closed.</summary>
    public byte[] Read()
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read;
            try
            {
                read = _shell.Read(buffer, 0, buffer.Length);
            }
            catch (ObjectDisposedException)
            {
                return [];
            }

            if (read <= 0)
            {
                return _filter?.Flush() ?? [];
            }

            var data = buffer.AsSpan(0, read);
            var forward = _filter is null ? data.ToArray() : _filter.Process(data);
            if (forward.Length > 0)
            {
                return forward;
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        _shell.Write(data.ToArray(), 0, data.Length);
        _shell.Flush();
    }

    public void Resize(int columns, int rows)
    {
        if (columns > 0 && rows > 0)
        {
            _shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        }
    }

    public void Dispose()
    {
        try
        {
            _shell.Dispose();
            _client.Dispose();
        }
        catch
        {
            // Closing a dead connection can throw; nothing to do about it.
        }
    }
}

/// <summary>
/// Hides the shell's own output (MOTD, prompt, echoed command) until the marker printed right before
/// <c>exec</c> shows up. The marker is written with octal escapes so the echoed command never contains it.
/// </summary>
public sealed class StartupOutputFilter
{
    public const string Marker = "__DAM_READY__";

    public const string TtyMarker = "__DAM_TTY__";

    public static readonly string EscapedMarker = Escape(Marker);

    public static readonly string EscapedTtyMarker = Escape(TtyMarker);

    /// <summary>The tty printed before the marker, when there was one.</summary>
    public string? Tty { get; private set; }

    private static string Escape(string marker) => string.Concat(marker.Select(c => c == '_' ? "\\137" : c.ToString()));

    private const int MaxBuffered = 256 * 1024;

    private readonly MemoryStream _buffer = new();
    private readonly DateTime _started = DateTime.UtcNow;
    private bool _passThrough;

    public byte[] Process(ReadOnlySpan<byte> data)
    {
        if (_passThrough)
        {
            return data.ToArray();
        }

        _buffer.Write(data);
        var text = Encoding.UTF8.GetString(_buffer.GetBuffer(), 0, (int)_buffer.Length);
        var index = text.IndexOf(Marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            var ttyIndex = text.LastIndexOf(TtyMarker, index, StringComparison.Ordinal);
            if (ttyIndex >= 0)
            {
                var value = text[(ttyIndex + TtyMarker.Length)..index].Split('\n', 2)[0].Trim();
                Tty = value.StartsWith("/dev/", StringComparison.Ordinal) ? value : null;
            }

            var end = index + Marker.Length;
            while (end < text.Length && text[end] is '\r' or '\n')
            {
                end++;
            }

            _passThrough = true;
            var rest = Encoding.UTF8.GetBytes(text[end..]);
            _buffer.SetLength(0);
            return rest;
        }

        if (_buffer.Length > MaxBuffered || DateTime.UtcNow - _started > TimeSpan.FromSeconds(10))
        {
            return Flush();
        }

        return [];
    }

    /// <summary>Gives up on the marker and returns everything buffered so far.</summary>
    public byte[] Flush()
    {
        _passThrough = true;
        var all = _buffer.ToArray();
        _buffer.SetLength(0);
        return all;
    }
}
