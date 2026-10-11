using System.Diagnostics;
using System.Text.Json;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Ssh;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class LimitTerminalTests
{
    [Theory]
    [InlineData(false, "continue")]
    [InlineData(true, "continue")]
    [InlineData(false, "/model")]
    [InlineData(true, "/status")]
    [InlineData(false, "/status", true)]
    [InlineData(true, "/status", true)]
    [InlineData(false, "continue", true)]
    [InlineData(false, "continue", false, true)]
    [InlineData(true, "continue", false, true)]
    [InlineData(false, "Background verification completed successfully. Read SUMMARY.md and the detailed results, then report your findings.", false, true, true)]
    [InlineData(true, "Background verification completed successfully. Read SUMMARY.md and the detailed results, then report your findings.", false, true, true)]
    public async Task SubmissionSeparatesTextFromEnterAndJobsDoNotDependOnRenderedText(bool edited, string command, bool outputChanged = false, bool keyed = false, bool wrapped = false)
    {
        if (OperatingSystem.IsWindows()) return;
        var temp = Directory.CreateTempSubdirectory("dam-submit-").FullName;
        var fake = Path.Combine(temp, "tmux");
        await File.WriteAllTextAsync(fake, $$"""
            #!/usr/bin/python3
            import json,pathlib,sys,time,textwrap
            root=pathlib.Path(__file__).parent
            args=sys.argv[1:]
            path=root/'state.json'
            state=json.loads(path.read_text()) if path.exists() else {'text':''}
            lines=textwrap.wrap(state['text'],width=50) if {{(wrapped ? "True" : "False")}} and state['text'] else [state['text']]
            if args[0]=='display-message':
                print('%1|{{Environment.ProcessId}}|0|'+str(len(lines))+'|'+str(2+len(lines[-1])))
            elif args[0]=='capture-pane':
                if {{(keyed ? "True" : "False")}} and state['text']: sys.exit(9) # Composer unavailable after typing.
                print("■ You've hit your usage limit. Try again at 9:37 PM.\n› "+'\n  '.join(lines))
            elif args[0]=='send-keys':
                if '-l' in args:
                    state['text']=args[-1] + (' edited' if {{(edited ? "True" : "False")}} else '')
                    state['typed']=time.monotonic()
                if 'Enter' in args: state['entered']=time.monotonic()
                path.write_text(json.dumps(state))
            """, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var ready = JsonSerializer.Deserialize<LimitScreen>(await Run(LimitTerminal.BuildReadScript("limit")))!;
            Assert.True(ready.InputEmpty);
            // Working output changed since observation; slash commands should still reach the empty prompt.
            if (outputChanged) ready = ready with { Hash = "older-output" };
            var guardedScreen = command == "continue";
            // A changed screen types nothing. Jobs send Enter even if the composer cannot be read after typing.
            Assert.Equal(outputChanged && guardedScreen ? "CHANGED" : edited && !keyed ? "UNCONFIRMED" : "SENT",
                (await Run(LimitTerminal.BuildSubmitScript("limit", ready, command, guardedScreen, keyed ? "owned-job-terminal" : null))).Trim());
            if (outputChanged && guardedScreen)
            {
                Assert.False(File.Exists(Path.Combine(temp, "state.json")));
                return;
            }
            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp, "state.json"), TestContext.Current.CancellationToken));
            if (edited && !keyed) Assert.False(state.RootElement.TryGetProperty("entered", out _));
            else Assert.True(state.RootElement.GetProperty("entered").GetDouble() - state.RootElement.GetProperty("typed").GetDouble() >= 0.3);
            if (keyed)
            {
                // A retry after acknowledgement loss must not type or press Enter again.
                Assert.Equal("SENT", (await Run(LimitTerminal.BuildSubmitScript("limit", ready, command,
                    deliveryKey: "owned-job-terminal"))).Trim());
                Assert.Equal(state.RootElement.GetRawText(), await File.ReadAllTextAsync(Path.Combine(temp, "state.json"), TestContext.Current.CancellationToken));
                await File.WriteAllTextAsync(Directory.GetFiles(Path.Combine(temp, ".deveagentmanager", "job-deliveries")).Single(),
                    "SENDING", TestContext.Current.CancellationToken);
                Assert.Equal("UNCONFIRMED", (await Run(LimitTerminal.BuildSubmitScript("limit", ready, command,
                    deliveryKey: "owned-job-terminal"))).Trim());
                Assert.Equal(state.RootElement.GetRawText(), await File.ReadAllTextAsync(Path.Combine(temp, "state.json"), TestContext.Current.CancellationToken));
            }
        }
        finally { Directory.Delete(temp, true); }

        async Task<string> Run(string script)
        {
            var start = new ProcessStartInfo("bash", ["-c", script.Replace("pathlib.Path.home()", "pathlib.Path(" + JsonSerializer.Serialize(temp) + ")")]) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["PATH"] = temp + ":" + Environment.GetEnvironmentVariable("PATH");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
    }

    [Theory]
    [InlineData("", "SENT")]
    [InlineData("my draft", "CHANGED")]
    [InlineData("", "SENT", true)]
    [InlineData("my draft", "CHANGED", true)]
    public async Task Pi_ContinueGoesIntoItsEmptyPromptBox_NeverOverADraft(string draft, string expected, bool keyed = false)
    {
        // pi's prompt is an unprefixed box between two rules, with the cursor on its (blank) line and a usage footer below.
        if (OperatingSystem.IsWindows()) return;
        var temp = Directory.CreateTempSubdirectory("dam-pi-submit-").FullName;
        await File.WriteAllTextAsync(Path.Combine(temp, "state.json"), JsonSerializer.Serialize(new { text = draft }), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(temp, "tmux"), $$"""
            #!/usr/bin/python3
            import json,pathlib,sys,time
            root=pathlib.Path(__file__).parent
            args=sys.argv[1:]
            path=root/'state.json'
            state=json.loads(path.read_text())
            if args[0]=='display-message':
                print('%1|{{Environment.ProcessId}}|0|2|'+str(1+len(state['text'])))
            elif args[0]=='capture-pane':
                print(' The answer.\n' + '─'*40 + '\n ' + state['text'] + '\n' + '─'*40 + '\n~/work/x\n↑1 ↓2 R3 W4 CH0% $0.1 (sub) 1.0%/1.0M (auto)\nthink:medium')
            elif args[0]=='send-keys':
                if '-l' in args: state['text']+=args[-1]
                if 'Enter' in args: state['entered']=state['text']
                path.write_text(json.dumps(state))
            """, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(Path.Combine(temp, "tmux"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var screen = JsonSerializer.Deserialize<LimitScreen>(await Run(LimitTerminal.BuildReadScript("pi")))!;
            Assert.Equal(draft.Length == 0, screen.InputEmpty);
            Assert.Equal(expected, (await Run(LimitTerminal.BuildSubmitScript("pi", screen, "continue",
                deliveryKey: keyed ? "test-job" : null))).Trim());
            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp, "state.json"), TestContext.Current.CancellationToken));
            Assert.Equal(draft.Length == 0 ? "continue" : draft, state.RootElement.GetProperty("text").GetString());
            Assert.Equal(draft.Length == 0, state.RootElement.TryGetProperty("entered", out _));
        }
        finally { Directory.Delete(temp, true); }

        async Task<string> Run(string script)
        {
            var start = new ProcessStartInfo("bash", ["-c", script.Replace("pathlib.Path.home()", "pathlib.Path(" + JsonSerializer.Serialize(temp) + ")")]) { RedirectStandardOutput = true, RedirectStandardError = true };
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
            var start = new ProcessStartInfo("bash", ["-c", script.Replace("pathlib.Path.home()", "pathlib.Path(" + JsonSerializer.Serialize(temp) + ")")]) { RedirectStandardOutput = true, RedirectStandardError = true };
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
