using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using Microsoft.Extensions.Logging;

namespace DeveAgentManager.Core.VSCode;

/// <summary>
/// Manages code-server instances on Runners. Starts code-server on-demand via SSH and tracks running instances.
/// </summary>
public sealed class VSCodeService(SshConnector connector, RunnerShell shell, ILogger<VSCodeService> logger)
{
    private const int CodeServerPort = 8765; // Fixed port on Runner for code-server

    /// <summary>
    /// Ensures code-server is running on the Runner at the specified path. Returns the SSH connection
    /// that can be used to create a tunnel to the code-server port.
    /// </summary>
    public async Task<VSCodeSession> StartAsync(Runner runner, string path, CancellationToken cancellationToken)
    {
        var target = shell.CreateTarget(runner);
        var client = await connector.ConnectAsync(target, onHostKeyLearned: null, cancellationToken);

        // Check if code-server is installed
        var checkInstalled = client.RunCommand("which code-server");
        if (checkInstalled.ExitStatus != 0)
        {
            throw new InvalidOperationException("code-server is not installed on this Runner. Enable it in Settings → Setup Profile.");
        }

        // Kill any existing code-server on this port (cleanup from previous sessions)
        client.RunCommand($"pkill -f 'code-server.*--bind-addr.*:{CodeServerPort}'");
        await Task.Delay(500, cancellationToken); // Brief pause for process cleanup

        // Start code-server in the background
        var startCommand = $"nohup code-server --auth none --bind-addr 127.0.0.1:{CodeServerPort} '{path}' > /dev/null 2>&1 & echo $!";
        var startResult = client.RunCommand(startCommand);

        if (startResult.ExitStatus != 0 || string.IsNullOrWhiteSpace(startResult.Result))
        {
            throw new InvalidOperationException($"Failed to start code-server: {startResult.Error}");
        }

        var pid = startResult.Result.Trim();
        logger.LogInformation("Started code-server on {Runner} at {Path} (PID: {Pid})", runner.Name, path, pid);

        // Wait for code-server to be ready (max 10 seconds)
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500, cancellationToken);
            var check = client.RunCommand($"curl -s http://127.0.0.1:{CodeServerPort} > /dev/null && echo ok || echo no");
            if (check.Result?.Contains("ok") == true)
            {
                logger.LogInformation("code-server ready on {Runner}", runner.Name);
                return new VSCodeSession(client, CodeServerPort, pid);
            }
        }

        throw new TimeoutException("code-server did not start within 10 seconds");
    }

    /// <summary>Stops a code-server instance.</summary>
    public void Stop(VSCodeSession session)
    {
        try
        {
            session.SshClient.RunCommand($"kill {session.ProcessId}");
            logger.LogInformation("Stopped code-server PID {Pid}", session.ProcessId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop code-server PID {Pid}", session.ProcessId);
        }
    }
}

/// <summary>A running code-server instance on a Runner.</summary>
public sealed record VSCodeSession(Renci.SshNet.SshClient SshClient, int RemotePort, string ProcessId);
