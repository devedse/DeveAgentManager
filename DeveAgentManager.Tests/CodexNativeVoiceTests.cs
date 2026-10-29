using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DeveAgentManager.Harnesses.Codex;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class CodexNativeVoiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Resource(string file)
    {
        using var stream = typeof(CodexHarness).Assembly.GetManifestResourceStream("DeveAgentManager.Harnesses.Codex." + file)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact(Timeout = 30000)]
    public async Task NativeHelperWaitsForBrowserTransportAndAppliesControlsAndMeasuredLevels()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "dam-native-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var address = Path.Combine(root, "audio.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(address)); listener.Listen(1);
        var helper = Path.Combine(root, "helper.py");
        await File.WriteAllTextAsync(helper, Resource("CodexNativeVoiceHelper.py"), Ct);
        const string parent = "import os,subprocess,sys; sys.exit(subprocess.call([sys.executable,sys.argv[1]], env={'HOME':os.environ['HOME'],'PATH':os.environ['PATH']}))";
        var start = new ProcessStartInfo("python3", ["-c", parent, helper]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["DAM_NATIVE_VOICE_SOCKET"] = address;
        using var process = Process.Start(start)!;
        try
        {
            async Task SendAsync(object value)
            {
                var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
                var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
                await process.StandardInput.BaseStream.WriteAsync(header, Ct);
                await process.StandardInput.BaseStream.WriteAsync(bytes, Ct);
                await process.StandardInput.BaseStream.FlushAsync(Ct);
            }
            async Task<JsonNode> ReadAsync()
            {
                var header = new byte[4]; await process.StandardOutput.BaseStream.ReadExactlyAsync(header, Ct);
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                Assert.InRange(length, 1, 128 * 1024);
                var bytes = new byte[length]; await process.StandardOutput.BaseStream.ReadExactlyAsync(bytes, Ct);
                return JsonNode.Parse(bytes)!;
            }
            await SendAsync(new { type = "hello", protocol = 1, buildCommit = "test-only" });
            Assert.Equal("ready", (string?)(await ReadAsync())["type"]);
            await SendAsync(new { type = "initializeRuntime" });
            using var accepted = await listener.AcceptAsync(Ct);
            using var stream = new NetworkStream(accepted, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            async Task<JsonNode> RequestAsync() => JsonNode.Parse((await reader.ReadLineAsync(Ct))!)!;
            Task ReplyAsync(string json) => writer.WriteLineAsync(json.AsMemory(), Ct);
            Assert.Equal("runtimeReady", (string?)(await ReadAsync())["type"]);
            await SendAsync(new { type = "startTransport" });
            Assert.Equal("offer", (string?)(await RequestAsync())["type"]);
            await ReplyAsync("""{"type":"offer","sdp":"test-only browser offer"}""");
            Assert.Equal("test-only browser offer", (string?)(await ReadAsync())["sdp"]);
            await SendAsync(new { type = "applyAnswer", sdp = "test-only answer" });
            Assert.Equal("answer", (string?)(await RequestAsync())["type"]);
            var awaitingTransport = ReadAsync();
            await Task.Delay(30, Ct);
            Assert.False(awaitingTransport.IsCompleted, "Native transport must not report ready before the browser connects.");
            await ReplyAsync("""{"type":"transportReady"}""");
            Assert.Equal("transportReady", (string?)(await awaitingTransport)["type"]);
            await SendAsync(new { type = "openDevices" });
            Assert.Equal("devices", (string?)(await RequestAsync())["type"]);
            await ReplyAsync("""{"type":"devicesOpened"}""");
            Assert.Equal("devicesOpened", (string?)(await ReadAsync())["type"]);
            await SendAsync(new { type = "setAudioControls", controls = new { microphoneMuted = true, speakerSuppressed = true } });
            var controls = await RequestAsync();
            Assert.True((bool)controls["microphoneMuted"]!); Assert.True((bool)controls["speakerSuppressed"]!);
            await ReplyAsync("""{"type":"controlsApplied"}""");
            Assert.Equal("audioControlsApplied", (string?)(await ReadAsync())["type"]);
            await SendAsync(new { type = "inspectAudio" });
            Assert.Equal("levels", (string?)(await RequestAsync())["type"]);
            await ReplyAsync("""{"type":"levels","microphonePeak":1200,"speakerPeak":3400}""");
            var levels = (await ReadAsync())["state"]!;
            Assert.Equal(1200, (int)levels["microphonePeak"]!); Assert.Equal(3400, (int)levels["speakerPeak"]!);
            await SendAsync(new { type = "close" });
            Assert.Equal("close", (string?)(await RequestAsync())["type"]);
            await ReplyAsync("""{"type":"closed"}""");
            Assert.Equal("closed", (string?)(await ReadAsync())["type"]);
            await process.WaitForExitAsync(Ct);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task PrivatePackageKeepsInstalledFilesAndActiveCachesWhileRetiringUnusedVersions()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "dam-native-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "package.py");
        await File.WriteAllTextAsync(package, Resource("CodexNativeVoicePackage.py"), Ct);
        const string test = """
            import fcntl,os,pathlib,shutil,subprocess,sys,tempfile
            from pathlib import Path
            root=Path(sys.argv[2]); home=root/'home'; home.mkdir()
            original=root/'installed'; (original/'bin').mkdir(parents=True)
            helper_path=original/'codex-resources/voice/bin/codex-voice-host'; helper_path.parent.mkdir(parents=True)
            helper_path.write_text('original installed helper')
            (original/'codex-package.json').write_text('{}')
            shutil.copyfile('/bin/true',original/'bin/codex'); (original/'bin/codex').chmod(0o700)
            os.environ['HOME']=str(home); os.environ['PATH']=str(original/'bin')+':'+os.environ['PATH']
            exec(Path(sys.argv[1]).read_text())
            source='#!/usr/bin/python3\n# test-only adapter\n'
            first,lease=native_terminal(source)
            first_root=Path(first).parent.parent
            assert first!='codex' and helper_path.read_text()=='original installed helper'
            assert (first_root/'codex-resources/voice/bin/codex-voice-host').read_text().endswith('# test-only adapter\n')
            assert Path(first).stat().st_ino!=(original/'bin/codex').stat().st_ino
            unknown=home/'.deveagentmanager/native-codex/unrelated'; unknown.mkdir(); (unknown/'keep').write_text('keep')
            second,second_lease=native_terminal(source+'# another adapter\n')
            second_root=Path(second).parent.parent
            assert first_root.exists(), 'an active terminal package was removed'
            lease.close()
            third,third_lease=native_terminal(source+'# third adapter\n')
            assert not first_root.exists() and second_root.exists() and (unknown/'keep').exists()
            assert helper_path.read_text()=='original installed helper'
            second_lease.close(); third_lease.close()
            print('PASS: installed package and active cache leases preserved')
            """;
        using var process = Process.Start(new ProcessStartInfo("python3", ["-c", test, package, root]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(Ct);
            var error = await process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);
            Assert.True(process.ExitCode == 0, output + error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            Directory.Delete(root, true);
        }
    }
}
