using Renci.SshNet;
using Renci.SshNet.Common;

namespace DeveAgentManager.Core.Ssh;

public sealed record SshTarget(Guid RunnerId, string Host, int Port, string Username, string? Password, string? KnownHostKey);

public sealed class HostKeyMismatchException(string host, string expected, string actual)
    : Exception($"The SSH host key of {host} changed (expected {expected}, got {actual}). If the machine was reinstalled, use 'Forget host key' on the Runner page.")
{
    public string Actual { get; } = actual;
}

/// <summary>Opens SSH connections to Runners with the Manager's key (and the password as fallback).</summary>
public sealed class SshConnector(ManagerSshKey key)
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Connects and verifies the host key. When the Runner has no known host key yet, the key is trusted and passed to
    /// <paramref name="onHostKeyLearned"/> so it can be stored.
    /// </summary>
    public async Task<SshClient> ConnectAsync(SshTarget target, Func<string, Task>? onHostKeyLearned, CancellationToken cancellationToken)
    {
        var client = new SshClient(CreateConnectionInfo(target));
        var seenFingerprint = await ConnectVerifiedAsync(client, target, cancellationToken);
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);

        try
        {
            if (target.KnownHostKey is null && onHostKeyLearned is not null)
                await onHostKeyLearned(seenFingerprint);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    public async Task<SftpClient> ConnectSftpAsync(SshTarget target, CancellationToken cancellationToken)
    {
        var client = new SftpClient(CreateConnectionInfo(target));
        await ConnectVerifiedAsync(client, target, cancellationToken);
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        return client;
    }

    private static async Task<string> ConnectVerifiedAsync(BaseClient client, SshTarget target, CancellationToken cancellationToken)
    {
        string? seenFingerprint = null;
        client.HostKeyReceived += (_, e) =>
        {
            seenFingerprint = "SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = target.KnownHostKey is null || string.Equals(target.KnownHostKey, seenFingerprint, StringComparison.Ordinal);
        };

        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (SshConnectionException) when (seenFingerprint is not null && target.KnownHostKey is not null && seenFingerprint != target.KnownHostKey)
        {
            client.Dispose();
            throw new HostKeyMismatchException(target.Host, target.KnownHostKey, seenFingerprint);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return seenFingerprint ?? "";
    }

    private ConnectionInfo CreateConnectionInfo(SshTarget target)
    {
        var methods = new List<AuthenticationMethod>
        {
            new PrivateKeyAuthenticationMethod(target.Username, key.KeyFile),
        };
        if (!string.IsNullOrEmpty(target.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(target.Username, target.Password));
            methods.Add(CreateKeyboardInteractive(target.Username, target.Password));
        }

        return new ConnectionInfo(target.Host, target.Port, target.Username, methods.ToArray())
        {
            Timeout = ConnectTimeout,
        };
    }

    private static KeyboardInteractiveAuthenticationMethod CreateKeyboardInteractive(string username, string password)
    {
        var method = new KeyboardInteractiveAuthenticationMethod(username);
        method.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    prompt.Response = password;
                }
            }
        };
        return method;
    }
}
