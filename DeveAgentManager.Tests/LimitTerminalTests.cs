using System.Diagnostics;
using System.Text.Json;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Ssh;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class LimitTerminalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinueSeparatesTextFromEnterAndRechecksTheDraft(bool edited)
    {
        if (OperatingSystem.IsWindows()) return;
        var temp = Directory.CreateTempSubdirectory("dam-submit-").FullName;
        var fake = Path.Combine(temp, "tmux");
        await File.WriteAllTextAsync(fake, $$"""
            #!/usr/bin/python3
            import json,pathlib,sys,time
            root=pathlib.Path(__file__).parent
            args=sys.argv[1:]
            path=root/'state.json'
            state=json.loads(path.read_text()) if path.exists() else {'text':''}
            if args[0]=='display-message':
                print('%1|{{Environment.ProcessId}}|0|1|'+str(2+len(state['text'])))
            elif args[0]=='capture-pane':
                print("■ You've hit your usage limit. Try again at 9:37 PM.\n› "+state['text'])
            elif args[0]=='send-keys':
                if '-l' in args:
                    state['text']='continue' + (' edited' if {{(edited ? "True" : "False")}} else '')
                    state['typed']=time.monotonic()
                if 'Enter' in args: state['entered']=time.monotonic()
                path.write_text(json.dumps(state))
            """, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var ready = JsonSerializer.Deserialize<LimitScreen>(await Run(LimitTerminal.BuildReadScript("limit")))!;
            Assert.True(ready.InputEmpty);
            Assert.Equal(edited ? "CHANGED" : "SENT", (await Run(LimitTerminal.BuildContinueScript("limit", ready))).Trim());
            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp, "state.json"), TestContext.Current.CancellationToken));
            if (edited) Assert.False(state.RootElement.TryGetProperty("entered", out _));
            else Assert.True(state.RootElement.GetProperty("entered").GetDouble() - state.RootElement.GetProperty("typed").GetDouble() >= 0.3);
        }
        finally { Directory.Delete(temp, true); }

        async Task<string> Run(string script)
        {
            var start = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["PATH"] = temp + ":" + Environment.GetEnvironmentVariable("PATH");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
    }

    [Fact]
    public async Task RealTmuxProbeFindsTimezone_AndGuardPreservesDraftBeforeSendingContinue()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/tmux")) return;
        var temp = Directory.CreateTempSubdirectory("dam-limit-terminal-").FullName;
        var socket = "dam-limit-test-" + Guid.NewGuid().ToString("N");
        var received = Path.Combine(temp, "received.txt");
        var harness = Path.Combine(temp, "harness.py");
        await File.WriteAllTextAsync(Path.Combine(temp, "tmux"), $"#!/bin/sh\nexec /usr/bin/tmux -L {socket} \"$@\"\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(Path.Combine(temp, "tmux"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(harness, """
            import pathlib,sys,time
            print("■ You've hit your usage limit. Try again at 9:37 PM.")
            print('› ',end='',flush=True)
            while True:
                value=sys.stdin.readline()
                if not value: break
                with open(sys.argv[1],'a') as output: output.write(value)
                print('› ',end='',flush=True)
            """, TestContext.Current.CancellationToken);
        try
        {
            await Run("tmux new-session -d -s limit -x 120 -y 30 " + ShellQuote.Quote($"exec env TZ=Europe/Amsterdam python3 {ShellQuote.Quote(harness)} {ShellQuote.Quote(received)}"));
            var initial = await WaitForEmpty(true);
            Assert.Equal("Europe/Amsterdam", initial.TimeZone);
            Assert.NotNull(UsageLimitParser.Parse("codex", initial.Screen, initial.TimeZone, new DateTime(2026, 9, 29, 18, 0, 0, DateTimeKind.Utc)));
            await Run("tmux send-keys -t '=limit:' -l 'my unfinished draft'");
            await WaitForEmpty(false);
            Assert.Equal("CHANGED", (await Run(LimitTerminal.BuildContinueScript("limit", initial))).Trim());
            Assert.False(File.Exists(received));
            await Run("tmux send-keys -t '=limit:' C-u");
            var ready = await WaitForEmpty(true);
            Assert.Equal("SENT", (await Run(LimitTerminal.BuildContinueScript("limit", ready))).Trim());
            for (var i = 0; i < 30 && !File.Exists(received); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.Equal("continue\n", await File.ReadAllTextAsync(received, TestContext.Current.CancellationToken));
            Assert.Equal("CHANGED", (await Run(LimitTerminal.BuildContinueScript("limit", ready))).Trim());
        }
        finally
        {
            await Run("tmux kill-server || true");
            Directory.Delete(temp, true);
        }

        async Task<LimitScreen> WaitForEmpty(bool empty)
        {
            LimitScreen? last = null;
            for (var i = 0; i < 30; i++)
            {
                var state = JsonSerializer.Deserialize<LimitScreen>(await Run(LimitTerminal.BuildReadScript("limit")))!;
                last = state;
                if (state.InputEmpty == empty && state.Screen.Contains("usage limit")) return state;
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            throw new InvalidOperationException("The dummy harness did not reach the expected prompt state: " + JsonSerializer.Serialize(last) + await Run("tmux display-message -p -t '=limit:' '#{cursor_x}|#{cursor_y}'"));
        }

        async Task<string> Run(string script)
        {
            var start = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["PATH"] = temp + ":" + Environment.GetEnvironmentVariable("PATH");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
    }
}
