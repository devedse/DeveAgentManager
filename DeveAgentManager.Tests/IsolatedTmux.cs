using System.Diagnostics;
using DeveAgentManager.Core.Ssh;

namespace DeveAgentManager.Tests;

/// <summary>Every fixture tmux invocation names its socket, including commands emitted by application scripts.</summary>
internal sealed class IsolatedTmux
{
    internal string SocketPath { get; }
    internal string Bin { get; }

    internal IsolatedTmux(string root)
    {
        SocketPath = Path.Combine(root, "tmux.sock");
        Bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(Bin);
        var wrapper = Path.Combine(Bin, "tmux");
        File.WriteAllText(wrapper, "#!/bin/sh\nunset TMUX TMUX_PANE\nexec /usr/bin/tmux -S " + ShellQuote.Quote(SocketPath) + " -f /dev/null \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal void Configure(ProcessStartInfo start)
    {
        start.Environment.Remove("TMUX");
        start.Environment.Remove("TMUX_PANE");
        start.Environment["PATH"] = Bin + ":" + Environment.GetEnvironmentVariable("PATH");
    }

    internal async Task StopAsync()
    {
        // Never infer the cleanup target from inherited environment, HOME, or the system's default socket.
        if (!File.Exists(SocketPath)) return;
        var start = new ProcessStartInfo("/usr/bin/tmux", ["-S", SocketPath, "kill-server"])
            { RedirectStandardOutput = true, RedirectStandardError = true };
        Configure(start);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync(CancellationToken.None);
    }
}
