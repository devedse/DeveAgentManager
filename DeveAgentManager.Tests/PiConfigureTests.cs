using DeveAgentManager.Core.Setup;
using DeveAgentManager.Harnesses.Pi;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Runs pi's configure script with bash and node against a stub <c>pi</c> in a temporary home folder.</summary>
public sealed class PiConfigureTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("dam-pi-home-").FullName;
    private readonly string _bin = Directory.CreateTempSubdirectory("dam-pi-bin-").FullName;
    private readonly PiHarness _pi = new();

    public PiConfigureTests()
    {
        // Records every call; "npm:broken" fails to install.
        var stub = Path.Combine(_bin, "pi");
        File.WriteAllText(stub, "#!/bin/sh\necho \"$*\" >> \"$HOME/pi.log\"\n[ \"$2\" != npm:broken ]\n");
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string SettingsFile => Path.Combine(_home, ".pi", "agent", "settings.json");

    [Fact]
    public void InstallsPackages_RemovesDroppedOnes_MergesSettings()
    {
        if (!HasTools())
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, """{ "theme": "dark", "packages": ["npm:mine"], "compaction": { "enabled": true, "reserveTokens": 1 } }""");

        var exit = Run(new HarnessConfig
        {
            Packages = ["npm:a", " npm:b ", "", "npm:a"],
            DefaultModel = "claude-sonnet-4-5",
            DefaultThinkingLevel = "high",
            Settings = """{ "compaction": { "reserveTokens": 2 }, "packages": ["ignored"], "hideThinkingBlock": true }""",
        });
        Assert.Equal(0, exit);
        Assert.Equal(["install npm:a", "install npm:b"], Log());

        var settings = JsonNode.Parse(File.ReadAllText(SettingsFile))!;
        Assert.Equal("dark", (string?)settings["theme"]);
        Assert.Equal("npm:mine", (string?)settings["packages"]![0]);
        Assert.Single(settings["packages"]!.AsArray());
        Assert.True((bool)settings["compaction"]!["enabled"]!);
        Assert.Equal(2, (int)settings["compaction"]!["reserveTokens"]!);
        Assert.True((bool)settings["hideThinkingBlock"]!);
        Assert.Equal("claude-sonnet-4-5", (string?)settings["defaultModel"]);
        Assert.Equal("high", (string?)settings["defaultThinkingLevel"]);
        Assert.Null(settings["defaultProvider"]);

        // Dropping npm:a removes it; packages the Manager didn't install (npm:mine) are left alone.
        File.Delete(Path.Combine(_home, "pi.log"));
        Assert.Equal(0, Run(new HarnessConfig { Packages = ["npm:b"] }));
        Assert.Equal(["remove npm:a", "install npm:b"], Log());

        // An empty list removes the rest.
        File.Delete(Path.Combine(_home, "pi.log"));
        Assert.Equal(0, Run(new HarnessConfig()));
        Assert.Equal(["remove npm:b"], Log());
    }

    [Fact]
    public void FailsWhenAPackageCantBeInstalled_AfterTryingTheOthers()
    {
        if (!HasTools())
        {
            return;
        }

        Assert.NotEqual(0, Run(new HarnessConfig { Packages = ["npm:broken", "npm:fine"] }));
        Assert.Equal(["install npm:broken", "install npm:fine"], Log());
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("{ not json")]
    public void RefusesSettingsThatAreNotAJsonObject(string settings) =>
        Assert.Throws<ArgumentException>(() => _pi.BuildConfigureScript(new HarnessConfig { Settings = settings }));

    [Fact]
    public void RefusesPackagesWithSpaces() =>
        Assert.Throws<ArgumentException>(() => _pi.BuildConfigureScript(new HarnessConfig { Packages = ["npm:a npm:b"] }));

    private int Run(HarnessConfig config)
    {
        var start = new ProcessStartInfo("bash", ["-c", _pi.BuildConfigureScript(config)]) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["HOME"] = _home;
        start.Environment["PATH"] = _bin + ":" + Environment.GetEnvironmentVariable("PATH");
        start.Environment.Remove("PI_CODING_AGENT_DIR");
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private string[] Log()
    {
        var file = Path.Combine(_home, "pi.log");
        return File.Exists(file) ? File.ReadAllLines(file) : [];
    }

    private static bool HasTools() => !OperatingSystem.IsWindows() && Which("bash") && Which("node");

    private static bool Which(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sh", ["-c", $"command -v {tool}"]) { RedirectStandardOutput = true })!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Directory.Delete(_home, true);
        Directory.Delete(_bin, true);
    }
}
