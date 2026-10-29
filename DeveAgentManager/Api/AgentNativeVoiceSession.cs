using System.Net.WebSockets;
using System.Text.Json;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Ssh;

namespace DeveAgentManager.Api;

/// <summary>Native audio signaling only. The terminal owns the call and delegation; browser WebRTC carries audio.</summary>
internal static class AgentNativeVoiceSession
{
    public static async Task RunAsync(WebSocket browser, WebSocket codex, AgentVoiceTarget target, AgentService agents, RunnerShell shell, string sdp, CancellationToken ct)
    {
        if (target.NativeConnectCommand is null) throw new InvalidOperationException("This harness does not support terminal voice audio.");
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(ct);
        opening.CancelAfter(TimeSpan.FromSeconds(20));
        await AgentVoiceSocketEndpoint.SendAsync(codex, new { id = 1, method = "initialize", @params = new
        {
            clientInfo = new { name = "deveagentmanager_native_voice", version = "1.0.0" }, capabilities = new { experimentalApi = true },
        } }, opening.Token);
        await ResponseAsync(codex, 1, opening.Token);
        await AgentVoiceSocketEndpoint.SendAsync(codex, new { method = "initialized" }, opening.Token);
        // Attach a listener to the already running thread without reading or replaying its history.
        await AgentVoiceSocketEndpoint.SendAsync(codex, new { id = 2, method = "thread/resume", @params = new { threadId = target.SessionId, excludeTurns = true } }, opening.Token);
        await ResponseAsync(codex, 2, opening.Token);
        await using var audio = await RunnerSocketConnection.OpenAsync(shell, target.Runner, target.NativeConnectCommand, opening.Token);
        using (var ready = await AgentVoiceSocketEndpoint.ReceiveAsync(audio.Socket, 8192, opening.Token))
        {
            if (ready?.RootElement.TryGetProperty("type", out var type) != true || type.GetString() != "ready")
            {
                if (ready is not null && ready.RootElement.TryGetProperty("message", out var message) && message.GetString() is { } hint)
                    throw new InvalidOperationException(hint);
                throw new InvalidOperationException("The Runner did not open terminal audio.");
            }
        }
        if (!await agents.VoiceTargetIsCurrentAsync(target, opening.Token)) throw new InvalidOperationException("The agent changed. Connect voice again.");
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sends = new SemaphoreSlim(1, 1);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task SendBrowserAsync(object value, CancellationToken token)
        {
            await sends.WaitAsync(token);
            try { await AgentVoiceSocketEndpoint.SendAsync(browser, value, token); }
            finally { sends.Release(); }
        }
        async Task BackendAsync()
        {
            while (true)
            {
                using var message = await AgentVoiceSocketEndpoint.ReceiveAsync(codex, 1024 * 1024, pumps.Token);
                if (message is null) return;
                var root = message.RootElement;
                if (!root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && number == 3)
                { stopped.TrySetResult(); return; }
                if (!root.TryGetProperty("method", out var method) || !root.TryGetProperty("params", out var parameters)
                    || !parameters.TryGetProperty("threadId", out var thread) || thread.GetString() != target.SessionId) continue;
                switch (method.GetString())
                {
                    case "thread/realtime/error":
                        throw new InvalidOperationException("Codex could not start terminal voice. Check the native terminal message and your Codex login.");
                    case "thread/realtime/closed": return;
                }
            }
        }
        async Task BrowserAsync()
        {
            while (true)
            {
                using var message = await AgentVoiceSocketEndpoint.ReceiveAsync(browser, 4096, pumps.Token);
                if (message is null) return;
                var root = message.RootElement;
                if (!root.TryGetProperty("type", out var type)) throw new InvalidOperationException("Unexpected terminal voice control.");
                object value;
                switch (type.GetString())
                {
                    case "stop": return;
                    case "connected": value = new { type = "connected" }; break;
                    case "controlsApplied": value = new { type = "controlsApplied" }; break;
                    case "devicesOpened" when root.TryGetProperty("available", out var available) && available.ValueKind is JsonValueKind.True or JsonValueKind.False:
                        value = new { type = "devicesOpened", available = available.GetBoolean() }; break;
                    case "levels" when root.TryGetProperty("microphonePeak", out var mic) && mic.TryGetInt32(out var microphonePeak) && microphonePeak is >= 0 and <= 65535
                        && root.TryGetProperty("speakerPeak", out var speaker) && speaker.TryGetInt32(out var speakerPeak) && speakerPeak is >= 0 and <= 65535:
                        value = new { type = "levels", microphonePeak, speakerPeak }; break;
                    default: throw new InvalidOperationException("Unexpected terminal voice control.");
                }
                await AgentVoiceSocketEndpoint.SendAsync(audio.Socket, value, pumps.Token);
            }
        }
        async Task AudioAsync()
        {
            while (true)
            {
                using var message = await AgentVoiceSocketEndpoint.ReceiveAsync(audio.Socket, 70 * 1024, pumps.Token);
                if (message is null) return;
                var root = message.RootElement;
                if (!root.TryGetProperty("type", out var type)) throw new InvalidOperationException("Unexpected native audio control.");
                object value;
                switch (type.GetString())
                {
                    case "closed": return;
                    case "active":
                        connected.TrySetResult();
                        value = new { type = "active" }; break;
                    case "answer" when root.TryGetProperty("sdp", out var answer) && answer.GetString() is { Length: > 0 and <= 65536 } description:
                        value = new { type = "answer", sdp = description }; break;
                    case "devices": value = new { type = "devices" }; break;
                    case "controls" when root.TryGetProperty("microphoneMuted", out var mic) && mic.ValueKind is JsonValueKind.True or JsonValueKind.False
                        && root.TryGetProperty("speakerSuppressed", out var speaker) && speaker.ValueKind is JsonValueKind.True or JsonValueKind.False:
                        value = new { type = "controls", microphoneMuted = mic.GetBoolean(), speakerSuppressed = speaker.GetBoolean() }; break;
                    case "error":
                        throw new InvalidOperationException(root.TryGetProperty("message", out var error) && error.GetString() is { Length: <= 512 } hint ? hint : "Native audio could not connect.");
                    default: throw new InvalidOperationException("Unexpected native audio control.");
                }
                await SendBrowserAsync(value, pumps.Token);
            }
        }
        async Task WatchAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(pumps.Token))
                if (!await agents.VoiceTargetIsCurrentAsync(target, pumps.Token)) return;
        }
        async Task DeadlineAsync()
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(35), pumps.Token);
            await Task.Delay(Timeout.Infinite, pumps.Token);
        }
        var backend = BackendAsync();
        Task? input = null, output = null, watch = null, deadline = null;
        var triggered = false;
        try
        {
            await AgentVoiceSocketEndpoint.SendAsync(audio.Socket, new { type = "start", sdp }, opening.Token);
            triggered = true;
            input = BrowserAsync(); output = AudioAsync(); watch = WatchAsync(); deadline = DeadlineAsync();
            await await Task.WhenAny(backend, input, output, watch, deadline);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            using var ending = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendBrowserAsync(new { type = "error", message = ex is InvalidOperationException ? ex.Message : "Terminal voice disconnected. You can start it again." }, ending.Token);
        }
        finally
        {
            if (triggered && codex.State == WebSocketState.Open)
            {
                try
                {
                    using var ending = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await AgentVoiceSocketEndpoint.SendAsync(codex, new { id = 3, method = "thread/realtime/stop", @params = new { threadId = target.SessionId } }, ending.Token);
                    if (!backend.IsCompleted) await stopped.Task.WaitAsync(ending.Token);
                }
                catch { }
            }
            pumps.Cancel(); audio.Socket.Abort(); codex.Abort();
            foreach (var task in new[] { backend, input, output, watch, deadline })
                if (task is not null) try { await task; } catch { }
        }
    }

    private static async Task ResponseAsync(WebSocket socket, int expected, CancellationToken ct)
    {
        while (true)
        {
            using var message = await AgentVoiceSocketEndpoint.ReceiveAsync(socket, 1024 * 1024, ct)
                ?? throw new IOException("Codex closed its control connection.");
            if (message.RootElement.TryGetProperty("method", out _) || !message.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number != expected) continue;
            if (message.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex could not attach terminal voice. Update Codex on the Runner.");
            return;
        }
    }
}
