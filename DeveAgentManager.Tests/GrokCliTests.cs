using System.Diagnostics;
using System.Text.Json.Nodes;
using DeveAgentManager.Harnesses.Grok;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class GrokCliTests
{
    [Fact(Timeout = 120_000)]
    public async Task OfficialCliResumesManagerWrittenHistoryAndCompactionCheckpoint()
    {
        var binary = Environment.GetEnvironmentVariable("DAM_GROK_CLI");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(binary), "Set DAM_GROK_CLI to test the installed official Grok CLI against a local model.");
        var root = Directory.CreateTempSubdirectory("dam-grok-cli-");
        try
        {
            var format = new GrokSessionFormat();
            var workspace = Path.Combine(root.FullName, "workspace"); Directory.CreateDirectory(workspace);
            var home = Path.Combine(root.FullName, "grok"); Directory.CreateDirectory(home);
            var session = format.Read(GrokTests.History);
            var folder = Path.Combine(home, "sessions", GrokSessionFormat.SessionDirectoryName(workspace), GrokTests.Id);
            Directory.CreateDirectory(folder);
            foreach (var (name, contents) in format.WriteCompanionFiles(session, GrokTests.Id, workspace))
            {
                await File.WriteAllTextAsync(Path.Combine(folder, name), contents, TestContext.Current.CancellationToken);
            }
            await File.WriteAllTextAsync(Path.Combine(folder, "updates.jsonl"), format.Write(session, GrokTests.Id, workspace), TestContext.Current.CancellationToken);
            var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../DeveAgentManager.Tests/Integration/grok-cli.py"));
            async Task Run(string directory, string id, string mode)
            {
                using var process = Process.Start(new ProcessStartInfo("python3", [script, binary!, directory, id, mode])
                    { RedirectStandardOutput = true, RedirectStandardError = true })!;
                var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
                catch { process.Kill(entireProcessTree: true); throw; }
                Assert.True(process.ExitCode == 0, await output + await error);
                Assert.Contains("saved replay checkpoint", await output);
            }
            await Run(root.FullName, GrokTests.Id, "initial");
            var saved = format.Read(await File.ReadAllTextAsync(Path.Combine(folder, "updates.jsonl"), TestContext.Current.CancellationToken));
            var files = new Dictionary<string, string>();
            foreach (var file in format.CompanionFiles(saved))
                if (File.Exists(Path.Combine(folder, file.Path))) files[file.Path] = await File.ReadAllTextAsync(Path.Combine(folder, file.Path), TestContext.Current.CancellationToken);
            format.ApplyCompanionFiles(saved, files);
            var restored = format.Read(format.Export(saved, GrokTests.Id, workspace));
            var newRoot = Path.Combine(root.FullName, "restored"); var newWorkspace = Path.Combine(newRoot, "workspace");
            var newId = Guid.NewGuid().ToString(); Directory.CreateDirectory(newWorkspace);
            var newFolder = Path.Combine(newRoot, "grok", "sessions", GrokSessionFormat.SessionDirectoryName(newWorkspace), newId);
            foreach (var (name, content) in format.WriteCompanionFiles(restored, newId, newWorkspace))
            {
                var path = Path.Combine(newFolder, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            }
            await File.WriteAllTextAsync(Path.Combine(newFolder, "updates.jsonl"), format.Write(restored, newId, newWorkspace), TestContext.Current.CancellationToken);
            await Run(newRoot, newId, "restored");
        }
        finally { root.Delete(true); }
    }
}
