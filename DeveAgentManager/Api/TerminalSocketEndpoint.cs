using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeveAgentManager.Contracts;
using DeveAgentManager.Core.Accounts;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.Ssh;
using DeveAgentManager.Core.Users;
// A pasted file (its name, which may be empty, and bytes) to the terminal's Runner; returns its path there.
using ImageUploader = System.Func<string, byte[], System.Threading.CancellationToken, System.Threading.Tasks.Task<string>>;

namespace DeveAgentManager.Api;

/// <summary>
/// Browser terminals (/ws/terminal). One WebSocket per terminal, straight from xterm.js to an SSH terminal on a Runner:
/// binary frames carry the terminal's data both ways; JSON text frames carry control (see <see cref="TerminalSocket"/>).
/// <list type="bullet">
/// <item><c>?kind=agent&amp;id=</c>: attached to an agent's tmux session. Joins watching when other screens are attached,
/// and takes the size on its first real input (keys, not the terminal's own replies). People who can only read the agent
/// get a read-only tmux client, and what they type or paste is dropped here as well.</item>
/// <item><c>?kind=shell&amp;id=</c>: a shell on a Runner, with the user's own environment (Use access).</item>
/// <item><c>?kind=login&amp;runner=&amp;harness=[&amp;account=][&amp;name=]</c>: a harness's subscription login. The Manager builds
/// the command itself (the browser only names the harness) and saves what the login produced as the user's login.</item>
/// </list>
/// A terminal closes when the user's access ends while it is open (see <see cref="OpenConnections"/>).
/// </summary>
public static partial class TerminalSocketEndpoint
{
    private const string DoneMarker = "__DAM_LOGIN_OK__";

    public static void MapTerminalSocket(this IEndpointRouteBuilder app) =>
        app.Map(TerminalSocket.Path, async (HttpContext context, TerminalService terminals, AgentService agents, AccountService accounts, RunnerService runners,
            AccessControl access, OpenConnections connections, ILoggerFactory loggers) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest("A WebSocket is needed.");
            }

            if (SignIn.UsableUser(context) is not { } user)
            {
                return Results.Unauthorized();
            }

            var query = context.Request.Query;
            var columns = Math.Clamp(int.TryParse(query["cols"], out var c) ? c : 120, 20, 1000);
            var rows = Math.Clamp(int.TryParse(query["rows"], out var r) ? r : 40, 5, 500);
            var kind = query["kind"].ToString();
            var logger = loggers.CreateLogger("DeveAgentManager.Terminal");

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var session = new TerminalSession(socket, logger);
            // Ends the terminal when the user's access ends while it is open.
            using var revoked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var stamp = SignIn.SecurityStamp(context);
            IDisposable Watch(Func<CurrentUser, CancellationToken, Task<bool>> stillAllowed) => connections.Register(user, stamp, stillAllowed, revoked.Cancel, SignIn.SessionId(context));
            try
            {
                switch (kind)
                {
                    case "agent" when Guid.TryParse(query["id"], out var agentId):
                        var level = await access.AgentAsync(user, agentId, context.RequestAborted);
                        if (level < AgentAccess.Read)
                        {
                            await session.CloseAsync(TerminalSocket.ClosedError, "Agent not found.");
                            break;
                        }

                        var readOnly = level < AgentAccess.Write;
                        var needed = readOnly ? AgentAccess.Read : AgentAccess.Write;
                        using (Watch(async (current, ct) => await access.AgentAsync(current, agentId, ct) >= needed))
                        {
                            await session.RunAsync(await terminals.OpenAgentAsync(agentId, readOnly, columns, rows, context.RequestAborted), agentId, agents, login: null,
                                readOnly ? null : (name, bytes, ct) => terminals.UploadAsync(agentId, null, name, bytes, ct), readOnly, revoked.Token);
                        }

                        break;
                    case "shell" when Guid.TryParse(query["id"], out var runnerId):
                        if (await access.RunnerAsync(user, runnerId, context.RequestAborted) < RunnerAccess.Use)
                        {
                            await session.CloseAsync(TerminalSocket.ClosedError, "Runner not found.");
                            break;
                        }

                        using (Watch(async (current, ct) => await access.RunnerAsync(current, runnerId, ct) >= RunnerAccess.Use))
                        {
                            await session.RunAsync(await terminals.OpenShellAsync(runnerId, user.Id, columns, rows, context.RequestAborted), null, agents, login: null,
                                (name, bytes, ct) => terminals.UploadAsync(null, runnerId, name, bytes, ct), readOnly: false, revoked.Token);
                        }

                        break;
                    case "login" when Guid.TryParse(query["runner"], out var runnerId):
                        var replacing = Guid.TryParse(query["account"], out var accountId) ? accountId : (Guid?)null;
                        if (await access.RunnerAsync(user, runnerId, context.RequestAborted) < RunnerAccess.Use
                            || replacing is { } own && !await access.OwnsAccountAsync(user, own, context.RequestAborted))
                        {
                            await session.CloseAsync(TerminalSocket.ClosedError, "Runner or login not found.");
                            break;
                        }

                        var login = await LoginWatcher.CreateAsync(runnerId, query["harness"].ToString(), user.Id, replacing, query["name"].ToString(), accounts, runners, context.RequestAborted);
                        using (Watch(async (current, ct) => await access.RunnerAsync(current, runnerId, ct) >= RunnerAccess.Use))
                        {
                            // Wide, so a token is printed on one line (the harness's login command also sets it with stty).
                            await session.RunAsync(await terminals.OpenCommandAsync(runnerId, login.Command, 500, rows, context.RequestAborted), null, agents, login,
                                uploadImage: null, readOnly: false, revoked.Token);
                        }

                        break;
                    default:
                        await session.CloseAsync(TerminalSocket.ClosedError, "Unknown terminal.");
                        break;
                }

                if (revoked.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
                {
                    await session.CloseAsync(TerminalSocket.ClosedError, "Your access to this ended.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Terminal failed");
                await session.CloseAsync(TerminalSocket.ClosedError, ex.Message);
            }

            return Results.Empty;
        });

    /// <summary>One browser terminal: pumps the SSH terminal's output to the socket and the socket's input to the terminal.</summary>
    private sealed class TerminalSession(WebSocket socket, ILogger logger)
    {
        private static readonly byte[] KickedMarker = Encoding.UTF8.GetBytes(AgentService.KickedMarker);
        private static readonly byte[] DetachedMarker = "[detached (from session"u8.ToArray();

        private readonly SemaphoreSlim _sending = new(1, 1);
        private string _closeReason = TerminalSocket.ClosedExited;

        /// <param name="readOnly">Drops what the user types or pastes (the terminal is only watched).</param>
        public async Task RunAsync(TerminalConnection connection, Guid? agentId, AgentService agents, LoginWatcher? login, ImageUploader? uploadImage, bool readOnly,
            CancellationToken cancellationToken)
        {
            using (connection)
            {
                var output = Task.Factory.StartNew(() => PumpOutputAsync(connection, login, cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
                var input = PumpInputAsync(connection, agentId, agents, login is null, uploadImage, readOnly, cancellationToken);
                if (await Task.WhenAny(output, input) == output)
                {
                    // The terminal ended: say why, then close. The browser answers the close, which ends the input side.
                    // (Cancelling the socket's receive instead would abort it before the reason is sent.)
                    await CloseAsync(_closeReason, null);
                    await Task.WhenAny(input, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
                }

                // Leaving disposes the SSH terminal, which also ends the output side when the browser left first.
            }
        }

        private async Task PumpOutputAsync(TerminalConnection connection, LoginWatcher? login, CancellationToken cancellationToken)
        {
            var ready = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Blocks until the terminal prints something; an empty result means it ended.
                    var data = connection.Read();
                    if (data.Length == 0)
                    {
                        return;
                    }

                    if (data.AsSpan().IndexOf(DetachedMarker) >= 0)
                    {
                        _closeReason = TerminalSocket.ClosedDetached;
                    }

                    if (data.AsSpan().IndexOf(KickedMarker) >= 0)
                    {
                        _closeReason = TerminalSocket.ClosedKicked;
                        data = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace(AgentService.KickedMarker, ""));
                    }

                    // Ready with the first output. Agent and login terminals know their tty by then (it identifies an agent's
                    // screen among its viewers); a plain shell doesn't need one.
                    if (!ready)
                    {
                        ready = true;
                        await SendJsonAsync(new JsonObject { ["type"] = TerminalSocket.Ready, ["tty"] = connection.Tty }, cancellationToken);
                    }

                    await SendAsync(data, WebSocketMessageType.Binary, cancellationToken);
                    if (login is not null && await login.ObserveAsync(Encoding.UTF8.GetString(data), cancellationToken) is { } result)
                    {
                        await SendJsonAsync(new JsonObject { ["type"] = TerminalSocket.Login, ["ok"] = result.Ok, ["message"] = result.Message }, cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or WebSocketException)
            {
                // The browser went away, or the terminal was closed.
            }
        }

        private async Task PumpInputAsync(TerminalConnection connection, Guid? agentId, AgentService agents, bool forwardResize, ImageUploader? uploadImage, bool readOnly,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            var sizeClaimed = false;
            // An image being received: its announced bytes arrive in binary frames, which are not terminal input.
            (JsonNode? Id, string Name, MemoryStream Bytes, long Size)? image = null;
            try
            {
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(buffer, cancellationToken);
                        if (received.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        message.Write(buffer, 0, received.Count);
                        if (message.Length > 16 * 1024 * 1024)
                        {
                            throw new InvalidDataException("Terminal message too large.");
                        }
                    }
                    while (!received.EndOfMessage);

                    var bytes = message.ToArray();
                    if (received.MessageType == WebSocketMessageType.Binary && image is { } receiving)
                    {
                        if (receiving.Bytes.Length + bytes.Length > receiving.Size)
                        {
                            throw new InvalidDataException("More image bytes than announced.");
                        }

                        receiving.Bytes.Write(bytes);
                        if (receiving.Bytes.Length == receiving.Size)
                        {
                            image = null;
                            // Stored while the terminal goes on; the answer names the image it is for.
                            _ = UploadImageAsync(uploadImage!, receiving.Id, receiving.Name, receiving.Bytes, cancellationToken);
                        }
                    }
                    else if (received.MessageType == WebSocketMessageType.Binary && readOnly)
                    {
                        // Watching only: keys never reach the terminal (the read-only tmux client would ignore them too).
                    }
                    else if (received.MessageType == WebSocketMessageType.Binary)
                    {
                        connection.Write(bytes);
                        // A screen that joined watching sets the size once somebody types on it (not on the terminal's replies).
                        if (agentId is { } id && !sizeClaimed && connection.Tty is { } tty && !TerminalInput.IsTerminalReply(Encoding.UTF8.GetString(bytes)))
                        {
                            sizeClaimed = true;
                            _ = ClaimSizeAsync(agents, id, tty);
                        }
                    }
                    else if (JsonNode.Parse(bytes) is not JsonObject control)
                    {
                        // Not a control message: ignored.
                    }
                    else if ((string?)control["type"] == TerminalSocket.Resize && forwardResize)
                    {
                        connection.Resize((int?)control["cols"] ?? 0, (int?)control["rows"] ?? 0);
                    }
                    else if ((string?)control["type"] == TerminalSocket.Image)
                    {
                        // The browser checks the size first, so a wrong one isn't from the Portal: stop rather than type the
                        // image's bytes into the terminal.
                        var size = (long?)control["size"] ?? 0;
                        if (uploadImage is null || image is not null || size <= 0 || size > TerminalService.MaxUploadBytes)
                        {
                            throw new InvalidDataException("Unexpected image.");
                        }

                        image = (control["id"]?.DeepClone(), (string?)control["name"] ?? "", new MemoryStream((int)size), size);
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or InvalidDataException)
            {
                // The browser went away or sent something that isn't a terminal message.
            }
        }

        private async Task UploadImageAsync(ImageUploader uploadImage, JsonNode? id, string name, MemoryStream bytes, CancellationToken cancellationToken)
        {
            var answer = new JsonObject { ["type"] = TerminalSocket.Image, ["id"] = id };
            try
            {
                using (bytes)
                {
                    answer["path"] = await uploadImage(name, bytes.ToArray(), cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Image upload failed");
                answer["error"] = ex.Message;
            }

            try
            {
                await SendJsonAsync(answer, cancellationToken);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The browser went away meanwhile.
            }
        }

        private async Task ClaimSizeAsync(AgentService agents, Guid agentId, string tty)
        {
            try
            {
                await agents.ClaimSizeAsync(agentId, tty);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not give terminal {Tty} the size", tty);
            }
        }

        public async Task CloseAsync(string reason, string? message)
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            try
            {
                await SendJsonAsync(new JsonObject { ["type"] = TerminalSocket.Closed, ["reason"] = reason, ["message"] = message }, CancellationToken.None);
                // Only our side of the close: the input pump may be receiving, and it gets the browser's answer.
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // Already gone.
            }
        }

        private Task SendJsonAsync(JsonObject message, CancellationToken cancellationToken) =>
            SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, cancellationToken);

        private async Task SendAsync(byte[] data, WebSocketMessageType type, CancellationToken cancellationToken)
        {
            await _sending.WaitAsync(cancellationToken);
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(data, type, true, cancellationToken);
                }
            }
            finally
            {
                _sending.Release();
            }
        }
    }

    /// <summary>A subscription login in a terminal: watches its output for the credential and saves it as an account.</summary>
    private sealed partial class LoginWatcher
    {
        private readonly StringBuilder _output = new();
        private readonly AccountService _accounts;
        private readonly Runner _runner;
        private readonly Guid _ownerId;
        private readonly string _harnessId;
        private readonly Guid? _accountId;
        private readonly string _name;
        private readonly string? _tokenPattern;
        private readonly string? _loginFile;
        private readonly string _loginFolder;
        private bool _done;

        private LoginWatcher(AccountService accounts, Runner runner, Guid ownerId, string harnessId, Guid? accountId, string name, string? tokenPattern, string? loginFile,
            string loginFolder, string command)
        {
            _accounts = accounts;
            _runner = runner;
            _ownerId = ownerId;
            _harnessId = harnessId;
            _accountId = accountId;
            _name = name;
            _tokenPattern = tokenPattern;
            _loginFile = loginFile;
            _loginFolder = loginFolder;
            Command = command;
        }

        public string Command { get; }

        /// <param name="ownerId">Whose login it becomes.</param>
        /// <param name="accountId">One of theirs to connect again, or null for a new one.</param>
        public static async Task<LoginWatcher> CreateAsync(Guid runnerId, string harnessId, Guid ownerId, Guid? accountId, string name, AccountService accounts, RunnerService runners,
            CancellationToken cancellationToken)
        {
            var support = accounts.GetHarness(harnessId).AccountSupport!;
            var runner = await runners.GetAsync(runnerId, cancellationToken) ?? throw new KeyNotFoundException("Runner not found.");
            if (!runner.HarnessIds.Contains(harnessId))
            {
                throw new InvalidOperationException($"{accounts.GetHarness(harnessId).DisplayName} is not installed on '{runner.Name}'.");
            }

            var folder = AccountService.NewLoginFolder();
            // The marker is printed in two parts, so it only appears in the output once the login succeeded.
            var command = $"""
                export DAM_LOGIN_DIR="{folder}"; mkdir -p "$DAM_LOGIN_DIR"
                {support.LoginCommand} && printf '\n%s%s\n' {DoneMarker[..8]} {DoneMarker[8..]}
                printf '\n(The login finished. You can close this window.)\n'; sleep 900
                """;
            return new LoginWatcher(accounts, runner, ownerId, harnessId, accountId, name, support.TokenPattern, support.LoginFile, folder, command);
        }

        /// <summary>Returns the result once the login's credential was found and saved (or failed), else null.</summary>
        public async Task<(bool Ok, string Message)?> ObserveAsync(string text, CancellationToken cancellationToken)
        {
            if (_done)
            {
                return null;
            }

            _output.Append(text);
            if (_output.Length > 200_000)
            {
                _output.Remove(0, _output.Length - 100_000);
            }

            var clean = Escapes().Replace(_output.ToString(), "");
            string? secret = null;
            try
            {
                if (_tokenPattern is not null && Regex.Match(clean, _tokenPattern) is { Success: true } match && match.Index + match.Length < clean.Length)
                {
                    // Only once something follows it: the token may still be arriving.
                    secret = match.Value;
                }
                else if (_loginFile is not null && clean.Contains(DoneMarker, StringComparison.Ordinal))
                {
                    secret = await _accounts.ReadLoginFileAsync(_runner, _loginFolder, _loginFile, cancellationToken);
                }

                if (secret is null)
                {
                    return null;
                }

                _done = true;
                if (_accountId is { } existing)
                {
                    await _accounts.ReplaceSecretAsync(existing, AccountKind.Subscription, secret, cancellationToken);
                    return (true, "Connected again. Agents use the new login from their next start.");
                }

                var account = await _accounts.AddAsync(_ownerId, _harnessId, _name, AccountKind.Subscription, secret, cancellationToken);
                return (true, $"Account '{account.Name}' connected.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _done = true;
                return (false, $"Could not save the login: {ex.Message}");
            }
        }

        // Escape sequences (colors, cursor moves, OSC) come between the characters of what the login prints.
        [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b[()][A-Za-z0-9]|\x1b[=>]")]
        private static partial Regex Escapes();
    }
}
