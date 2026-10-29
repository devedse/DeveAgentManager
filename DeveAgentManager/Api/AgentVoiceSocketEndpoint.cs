using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;

namespace DeveAgentManager.Api;

/// <summary>Only voice signaling crosses this socket. Codex credentials and its general RPC interface stay on the Runner.</summary>
public static class AgentVoiceSocketEndpoint
{
    private static readonly ConcurrentDictionary<Guid, byte> Owners = new();

    public static void MapAgentVoiceSocket(this IEndpointRouteBuilder app) => app.Map("/ws/agents/{agentId:guid}/voice",
        async (HttpContext context, Guid agentId, AgentService agents, RunnerShell shell, AccessControl access,
            OpenConnections connections, IHostApplicationLifetime lifetime, ILoggerFactory loggers) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) return Results.BadRequest("A WebSocket is needed.");
            if (SignIn.UsableUser(context) is not { } user) return Results.Unauthorized();
            if (await access.AgentAsync(user, agentId, context.RequestAborted) < AgentAccess.Write
                || !await access.CanPromptAgentAsync(agentId, context.RequestAborted)) return Results.NotFound();

            using var browser = await context.WebSockets.AcceptWebSocketAsync();
            if (!Owners.TryAdd(agentId, 0))
            {
                await SendAsync(browser, new { type = "error", message = "Voice is already connected in another tab. End it there first." }, CancellationToken.None);
                await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Voice already connected", CancellationToken.None);
                return Results.Empty;
            }
            using var revoked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
            using var registration = connections.Register(user, SignIn.SecurityStamp(context), async (current, ct) =>
                await access.AgentAsync(current, agentId, ct) >= AgentAccess.Write && await access.CanPromptAgentAsync(agentId, ct),
                revoked.Cancel, SignIn.SessionId(context));
            var logger = loggers.CreateLogger("DeveAgentManager.Voice");
            try
            {
                using var opening = CancellationTokenSource.CreateLinkedTokenSource(revoked.Token);
                opening.CancelAfter(TimeSpan.FromSeconds(20));
                using var offer = await ReceiveAsync(browser, 70 * 1024, opening.Token);
                if (offer is null || !offer.RootElement.TryGetProperty("type", out var type) || type.GetString() != "start")
                    throw new InvalidOperationException("The browser did not provide a valid voice connection.");
                var native = offer.RootElement.TryGetProperty("mode", out var mode) && mode.GetString() == "terminal";
                var sdp = offer.RootElement.TryGetProperty("sdp", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null;
                if (sdp is not { Length: > 0 and <= 65536 })
                    throw new InvalidOperationException("The browser did not provide a valid voice connection.");
                var target = await agents.GetVoiceTargetAsync(agentId, opening.Token);
                RunnerSocketConnection remote;
                try { remote = await RunnerSocketConnection.OpenAsync(shell, target.Runner, target.ConnectCommand, opening.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { throw new InvalidOperationException("Update Codex on the Runner, then restart this agent once to enable browser voice."); }
                await using (remote)
                {
                    if (!await agents.VoiceTargetIsCurrentAsync(target, opening.Token))
                        throw new InvalidOperationException("The agent changed. Connect voice again from its current conversation.");
                    if (native) await AgentNativeVoiceSession.RunAsync(browser, remote.Socket, target, agents, shell, sdp!, revoked.Token);
                    else await RunAsync(browser, remote.Socket, target, sdp!, agents, revoked.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // SDP contains ICE credentials, and provider errors can contain URLs. Never log or echo raw protocol data.
                logger.LogDebug("Voice ended for {Agent}: {FailureType}", agentId, ex.GetType().Name);
                try
                {
                    using var ending = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await SendAsync(browser, new { type = "error", message = ex is InvalidOperationException ? ex.Message : "Voice disconnected. You can start it again." }, ending.Token);
                }
                catch { }
            }
            finally
            {
                Owners.TryRemove(agentId, out _);
                try
                {
                    using var ending = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Voice ended", ending.Token);
                }
                catch { browser.Abort(); }
            }
            return Results.Empty;
        });

    private static async Task RunAsync(WebSocket browser, WebSocket codex, AgentVoiceTarget target, string sdp, AgentService agents, CancellationToken ct)
    {
        using var initializing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        initializing.CancelAfter(TimeSpan.FromSeconds(10));
        await SendAsync(codex, new { id = 1, method = "initialize", @params = new
        {
            clientInfo = new { name = "deveagentmanager_voice", version = "1.0.0" }, capabilities = new { experimentalApi = true },
        } }, initializing.Token);
        while (true)
        {
            using var response = await ReceiveAsync(codex, 1024 * 1024, initializing.Token)
                ?? throw new IOException("Codex closed voice signaling.");
            if (!response.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number != 1) continue;
            if (response.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("This Codex version could not initialize browser voice. Update Codex on the Runner.");
            break;
        }
        await SendAsync(codex, new { method = "initialized" }, initializing.Token);
        using var pumps = new CancellationTokenSource();
        using var sends = new SemaphoreSlim(1, 1);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startRejected = false;
        var started = false;
        async Task BackendAsync()
        {
            while (true)
            {
                using var message = await ReceiveAsync(codex, 1024 * 1024, pumps.Token);
                if (message is null) return;
                var root = message.RootElement;
                if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number))
                {
                    if (number == 2 && root.TryGetProperty("error", out _))
                    {
                        startRejected = true;
                        throw new InvalidOperationException("Codex could not start voice. Check that this login supports voice and that another voice call is not active.");
                    }
                    if (number == 3) { stopped.TrySetResult(); return; }
                }
                if (!root.TryGetProperty("method", out var method) || !root.TryGetProperty("params", out var parameters)
                    || !parameters.TryGetProperty("threadId", out var thread) || thread.GetString() != target.SessionId) continue;
                switch (method.GetString())
                {
                    case "thread/realtime/sdp":
                        if (!parameters.TryGetProperty("sdp", out var remote) || remote.GetString() is not { Length: > 0 and <= 65536 } value)
                            throw new InvalidOperationException("Codex returned an invalid voice connection.");
                        if (answer.Task.IsCompleted) break;
                        await sends.WaitAsync(pumps.Token);
                        try { await SendAsync(browser, new { type = "answer", sdp = value }, pumps.Token); }
                        finally { sends.Release(); }
                        answer.TrySetResult();
                        break;
                    case "thread/realtime/error":
                        throw new InvalidOperationException("Codex voice failed. Check the Runner connection and your Codex login, then start voice again.");
                    case "thread/realtime/closed":
                        return;
                }
            }
        }
        async Task BrowserAsync()
        {
            while (true)
            {
                using var message = await ReceiveAsync(browser, 1024, pumps.Token);
                if (message is null) return;
                if (message.RootElement.TryGetProperty("type", out var type) && type.GetString() == "stop") return;
                throw new InvalidOperationException("Unexpected voice control message.");
            }
        }
        async Task WatchAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(pumps.Token))
                if (!await agents.VoiceTargetIsCurrentAsync(target, pumps.Token)) return;
        }
        async Task StartDeadlineAsync()
        {
            await answer.Task.WaitAsync(TimeSpan.FromSeconds(30), pumps.Token);
            await Task.Delay(Timeout.Infinite, pumps.Token);
        }
        Task? backend = null, input = null, watch = null, deadline = null;
        try
        {
            await SendAsync(codex, new { id = 2, method = "thread/realtime/start", @params = new
            {
                threadId = target.SessionId, outputModality = "audio", version = "v3", clientManagedHandoffs = false,
                transport = new { type = "webrtc", sdp },
            } }, initializing.Token);
            started = true;
            backend = BackendAsync(); input = BrowserAsync(); watch = WatchAsync(); deadline = StartDeadlineAsync();
            var cancelled = Task.Delay(Timeout.Infinite, ct);
            await await Task.WhenAny(backend, input, watch, deadline, cancelled);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            using var ending = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await sends.WaitAsync(ending.Token);
            try { await SendAsync(browser, new { type = "error", message = ex is InvalidOperationException ? ex.Message : "Voice disconnected. You can start it again." }, ending.Token); }
            finally { sends.Release(); }
        }
        finally
        {
            if (started && !startRejected && codex.State == WebSocketState.Open)
            {
                try
                {
                    using var ending = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await SendAsync(codex, new { id = 3, method = "thread/realtime/stop", @params = new { threadId = target.SessionId } }, ending.Token);
                    await stopped.Task.WaitAsync(ending.Token);
                }
                catch { }
            }
            pumps.Cancel(); codex.Abort();
            foreach (var pump in new[] { backend, input, watch, deadline })
                if (pump is not null) try { await pump; } catch { }
        }
    }

    internal static async Task<JsonDocument?> ReceiveAsync(WebSocket socket, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[Math.Min(limit, 8192)];
        while (true)
        {
            var part = await socket.ReceiveAsync(bytes.AsMemory(), ct);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (part.MessageType != WebSocketMessageType.Text || buffer.Length + part.Count > limit)
                throw new InvalidOperationException("Invalid voice control message.");
            buffer.Write(bytes, 0, part.Count);
            if (part.EndOfMessage) return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        }
    }

    internal static Task SendAsync(WebSocket socket, object value, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(), WebSocketMessageType.Text, true, ct).AsTask();
}
