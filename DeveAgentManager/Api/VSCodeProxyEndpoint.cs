using System.Net;
using DeveAgentManager.Core.Agents;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Runners;
using DeveAgentManager.Core.VSCode;

namespace DeveAgentManager.Api;

/// <summary>
/// Proxies VSCode (code-server) traffic from the browser to a Runner via SSH tunnel.
/// Routes: /vscode/runners/{id}?path=/folder or /vscode/agents/{id}
/// </summary>
public static class VSCodeProxyEndpoint
{
    public static void MapVSCodeProxy(this IEndpointRouteBuilder app)
    {
        var vscode = app.MapGroup("/vscode").RequireAuthorization();

        // Runner VSCode: /vscode/runners/{runnerId}?path=/path/to/folder
        vscode.Map("/runners/{runnerId:guid}/{**catchAll}", ProxyRunnerVSCodeAsync);

        // Agent VSCode: /vscode/agents/{agentId} (auto-detects agent's folder)
        vscode.Map("/agents/{agentId:guid}/{**catchAll}", ProxyAgentVSCodeAsync);
    }

    private static async Task ProxyRunnerVSCodeAsync(
        HttpContext context,
        Guid runnerId,
        string? catchAll,
        RunnerService runners,
        VSCodeService vscode,
        CancellationToken cancellationToken)
    {
        var runner = await runners.GetAsync(runnerId, cancellationToken);
        if (runner is null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Runner not found", cancellationToken);
            return;
        }

        var path = context.Request.Query["path"].FirstOrDefault() ?? runner.HomeDirectory;
        await ProxyToVSCodeAsync(context, runner, path, catchAll ?? "", vscode, cancellationToken);
    }

    private static async Task ProxyAgentVSCodeAsync(
        HttpContext context,
        Guid agentId,
        string? catchAll,
        AgentService agents,
        RunnerService runners,
        VSCodeService vscode,
        CancellationToken cancellationToken)
    {
        var agent = await agents.GetAsync(agentId, cancellationToken);
        if (agent is null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Agent not found", cancellationToken);
            return;
        }

        var runner = await runners.GetAsync(agent.RunnerId, cancellationToken);
        if (runner is null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Runner not found", cancellationToken);
            return;
        }

        await ProxyToVSCodeAsync(context, runner, agent.WorkingDirectory, catchAll ?? "", vscode, cancellationToken);
    }

    private static async Task ProxyToVSCodeAsync(
        HttpContext context,
        Runner runner,
        string path,
        string catchAll,
        VSCodeService vscode,
        CancellationToken cancellationToken)
    {
        try
        {
            // Start code-server on the Runner
            var session = await vscode.StartAsync(runner, path, cancellationToken);

            // Create SSH tunnel (port forwarding)
            var localPort = FreeTcpPort();
            var forwardedPort = new Renci.SshNet.ForwardedPortLocal("127.0.0.1", (uint)localPort, "127.0.0.1", (uint)session.RemotePort);
            session.SshClient.AddForwardedPort(forwardedPort);
            forwardedPort.Start();

            try
            {
                // Proxy the request through the tunnel
                var targetUrl = $"http://127.0.0.1:{localPort}/{catchAll}{context.Request.QueryString}";

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

                if (context.WebSockets.IsWebSocketRequest)
                {
                    // WebSocket proxy (for terminal in code-server)
                    await ProxyWebSocketAsync(context, targetUrl, cancellationToken);
                }
                else
                {
                    // HTTP proxy
                    var requestMessage = new HttpRequestMessage
                    {
                        Method = new HttpMethod(context.Request.Method),
                        RequestUri = new Uri(targetUrl)
                    };

                    // Copy headers
                    foreach (var header in context.Request.Headers.Where(h => !h.Key.StartsWith(":")))
                    {
                        requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    }

                    // Copy body for POST/PUT
                    if (context.Request.ContentLength > 0)
                    {
                        requestMessage.Content = new StreamContent(context.Request.Body);
                    }

                    var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    // Copy response
                    context.Response.StatusCode = (int)response.StatusCode;
                    foreach (var header in response.Headers.Concat(response.Content.Headers))
                    {
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }

                    await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
                }
            }
            finally
            {
                forwardedPort.Stop();
                forwardedPort.Dispose();
            }
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"VSCode proxy error: {ex.Message}", cancellationToken);
        }
    }

    private static async Task ProxyWebSocketAsync(HttpContext context, string targetUrl, CancellationToken cancellationToken)
    {
        // WebSocket proxying is complex - for now, return not implemented
        // code-server's terminal will work without WebSocket proxy for basic editing
        context.Response.StatusCode = 501;
        await context.Response.WriteAsync("WebSocket proxy not yet implemented", cancellationToken);
    }

    private static int FreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
