using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeveAgentManager.Api;
using DeveAgentManager.Core.Sessions;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Harnesses.Codex;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class CodexVoiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void NativeVoiceTranscriptsAreVisibleIncrementalAndLossless()
    {
        const string header = """{"timestamp":"2026-10-28T10:00:00Z","type":"session_meta","payload":{"id":"0199aaaa-bbbb-7ccc-8ddd-eeeeeeeeeeee","cwd":"/work/example"}}""";
        const string user = """{"timestamp":"2026-10-28T10:00:01Z","ordinal":1,"type":"realtime_item","payload":{"id":"voice-input","realtime_session_id":"voice-session","type":"transcript_segment","role":"user","text":"Check the tests."}}""";
        const string assistant = """{"timestamp":"2026-10-28T10:00:02Z","ordinal":2,"type":"realtime_item","payload":{"id":"voice-output","realtime_session_id":"voice-session","type":"transcript_segment","role":"assistant","text":"The tests passed."}}""";
        var format = new CodexSessionFormat();
        var cursor = new SessionReadCursor();
        format.AppendLines(cursor, new[] { header, user }.Select(s => JsonNode.Parse(s)!.AsObject()));
        format.AppendLines(cursor, new[] { JsonNode.Parse(assistant)!.AsObject() });
        Assert.Equal("Check the tests.", cursor.Session.Title);
        var messages = cursor.Session.Entries.Where(e => e.Kind == DeveEntryKind.Message).ToArray();
        Assert.Equal(new DeveRole?[] { DeveRole.User, DeveRole.Assistant }, messages.Select(e => e.Role));
        Assert.Equal(new[] { "Check the tests.", "The tests passed." }, messages.Select(e => e.PlainText()));
        var written = format.Write(cursor.Session, "0199aaaa-bbbb-7ccc-8ddd-eeeeeeeeeeee", "/work/example");
        var lines = written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(user), JsonNode.Parse(lines[1])));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(assistant), JsonNode.Parse(lines[2])));
        Assert.Equal(format.Read(header + "\n" + user + "\n" + assistant).Entries.Select(e => e.PlainText()), cursor.Session.Entries.Select(e => e.PlainText()));
    }

    [Fact(Timeout = 60000)]
    public async Task PrivateWebSocketOverRealSshTransfersBothWaysAndBoundsBrowserMessages()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.SkipWhen(!File.Exists("/usr/sbin/sshd"), "Requires the disposable SSH fixture.");
        await using var fixture = await RunnerShellIntegrationTests.Fixture.StartAsync();
        var path = fixture.Root + "/voice.sock";
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(1);
        const string relay = """
            import os, select, socket, sys
            sock=socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            sock.connect(sys.argv[1])
            while True:
                ready,_,_=select.select([sock,sys.stdin.buffer],[],[])
                if sock in ready:
                    data=sock.recv(65536)
                    if not data: break
                    sys.stdout.buffer.write(data); sys.stdout.buffer.flush()
                if sys.stdin.buffer in ready:
                    data=os.read(0,65536)
                    if not data: break
                    sock.sendall(data)
            """;
        using var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var opening = RunnerSocketConnection.OpenAsync(fixture.Shell, fixture.Runner,
            "exec python3 -c " + ShellQuote.Quote(relay) + " " + ShellQuote.Quote(path), connectionDeadline.Token);
        using var accepted = await listener.AcceptAsync(Ct);
        await using var stream = new NetworkStream(accepted, ownsSocket: false);
        var request = new StringBuilder();
        var one = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            Assert.Equal(1, await stream.ReadAsync(one, Ct)); request.Append((char)one[0]);
            Assert.True(request.Length < 8192);
        }
        var key = request.ToString().Split("\r\n").Single(s => s.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
        var answer = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + answer + "\r\n\r\n"), Ct);
        using var server = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
        await using var connection = await opening;
        // The opening deadline must not become a deadline for an already connected call.
        connectionDeadline.Cancel();
        await AgentVoiceSocketEndpoint.SendAsync(connection.Socket, new { type = "start", sdp = "test-only offer café" }, Ct);
        using (var message = await AgentVoiceSocketEndpoint.ReceiveAsync(server, 1024, Ct))
            Assert.Equal("test-only offer café", message!.RootElement.GetProperty("sdp").GetString());
        await AgentVoiceSocketEndpoint.SendAsync(server, new { type = "answer", sdp = "test-only answer" }, Ct);
        using (var message = await AgentVoiceSocketEndpoint.ReceiveAsync(connection.Socket, 1024, Ct))
            Assert.Equal("test-only answer", message!.RootElement.GetProperty("sdp").GetString());

        // A fragmented over-limit message is rejected before it can become a control request.
        await server.SendAsync(Encoding.UTF8.GetBytes(new string('x', 900)).AsMemory(), WebSocketMessageType.Text, false, Ct);
        await server.SendAsync(Encoding.UTF8.GetBytes(new string('x', 900)).AsMemory(), WebSocketMessageType.Text, true, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgentVoiceSocketEndpoint.ReceiveAsync(connection.Socket, 1024, Ct));
    }
}
