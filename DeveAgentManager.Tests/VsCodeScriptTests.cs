using System.Diagnostics;
using DeveAgentManager.Core.Editor;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>
/// Runs the script that starts VS Code on a Runner with bash, in a temporary home with stub curl and code-server
/// commands (so a real code-server on this machine is never touched).
/// </summary>
public sealed class VsCodeScriptTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("dam-vscode-home-").FullName;
    private readonly string _bin = Directory.CreateTempSubdirectory("dam-vscode-bin-").FullName;

    private string State => Path.Combine(_home, ".deveagentmanager", "code-server");

    /// <summary>
    /// curl answers healthz once code-server "started" (the stub leaves a file); code-server records its arguments; ss
    /// shows the listener with its process (as for the Runner Account's own programs) unless another user owns it.
    /// </summary>
    private void Stubs(bool installed, bool portOfAnotherUser = false)
    {
        Stub("curl", $"#!/bin/sh\n[ -f \"{_home}/started\" ]\n");
        Stub("ss", portOfAnotherUser
            ? $"#!/bin/sh\necho 'LISTEN 0 511 127.0.0.1:{VsCodeTool.Port} 0.0.0.0:*'\n"
            : $"#!/bin/sh\necho 'LISTEN 0 511 127.0.0.1:{VsCodeTool.Port} 0.0.0.0:* users:((\"node\",pid=4242,fd=22))'\n");
        if (installed)
        {
            Stub("code-server", $"#!/bin/sh\necho \"$@\" > \"{_home}/args\"\ntouch \"{_home}/started\"\nsleep 30\n");
        }
    }

    private void Stub(string name, string content)
    {
        var path = Path.Combine(_bin, name);
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void Ensure_StartsCodeServer_WithItsOwnConfigAndPassword()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Stubs(installed: true);
        var (exit, output) = Run(VsCodeTool.EnsureRunningScript);
        Assert.True(exit == 0, output);

        var config = File.ReadAllText(Path.Combine(State, "config.yaml"));
        Assert.Contains($"bind-addr: 127.0.0.1:{VsCodeTool.Port}", config);
        Assert.Contains("auth: password", config);
        var password = config.Split('\n').Single(l => l.StartsWith("password: ", StringComparison.Ordinal))["password: ".Length..];
        Assert.True(password.Length >= 30);
        Assert.Equal(VsCodeTool.PasswordMarker + password, output.Trim());
        Assert.Equal($"--config {State}/config.yaml", File.ReadAllText(Path.Combine(_home, "args")).Trim());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(State, "config.yaml")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(State));

        // Running again (already running) gives the same password; after a stop, the restart keeps it too.
        Assert.Equal(VsCodeTool.PasswordMarker + password, Run(VsCodeTool.EnsureRunningScript).Output.Trim());
        File.Delete(Path.Combine(_home, "started"));
        Assert.Equal(VsCodeTool.PasswordMarker + password, Run(VsCodeTool.EnsureRunningScript).Output.Trim());
        Run(VsCodeTool.StopScript);
    }

    [Fact]
    public void Ensure_WithoutCodeServer_SaysHowToInstallIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Stubs(installed: false);
        var (exit, output) = Run(VsCodeTool.EnsureRunningScript);
        Assert.Equal(3, exit);
        Assert.Contains("Install it on the Runner page", output);
    }

    [Fact]
    public void Ensure_DoesNotGiveThePasswordToAnotherUsersProgramOnThePort()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Stubs(installed: true, portOfAnotherUser: true);
        File.WriteAllText(Path.Combine(_home, "started"), "");
        var (exit, output) = Run(VsCodeTool.EnsureRunningScript);
        Assert.Equal(5, exit);
        Assert.DoesNotContain(VsCodeTool.PasswordMarker, output);
    }

    private (int ExitCode, string Output) Run(string script)
    {
        // Only the stubs and the basics: no real code-server from this machine's PATH.
        var start = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["HOME"] = _home;
        start.Environment["PATH"] = _bin + ":/usr/bin:/bin";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, process.ExitCode == 0 ? output : output + error);
    }

    public void Dispose()
    {
        // The stub code-server sleeps in the background; stop it before removing its home.
        var pid = Path.Combine(State, "pid");
        if (File.Exists(pid) && int.TryParse(File.ReadAllText(pid).Trim(), out var id))
        {
            try
            {
                Process.GetProcessById(id).Kill();
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }

        Directory.Delete(_home, true);
        Directory.Delete(_bin, true);
    }
}
