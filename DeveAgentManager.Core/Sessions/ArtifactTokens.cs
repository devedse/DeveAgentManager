using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Ssh;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace DeveAgentManager.Core.Sessions;

/// <summary>A read capability bound to an agent, its owner and its runner. UUIDs alone never authorize a request.</summary>
public sealed class ArtifactTokens(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("DeveAgentManager.ArtifactRead.v1");
    private static string Identity(Agent agent) => $"{agent.Id:N}/{agent.OwnerId:N}/{agent.RunnerId:N}";
    public static string PathFor(Guid agent) => $"~/.deveagentmanager/artifact-access/{agent:N}.token";
    internal string Issue(Agent agent) => _protector.Protect(Identity(agent));
    internal bool Valid(string? token, Agent agent)
    {
        if (token is null || token.Length > 4096) return false;
        try { return _protector.Unprotect(token) == Identity(agent); }
        catch (CryptographicException) { return false; }
    }
    internal async Task InstallAsync(RunnerShell shell, Runner runner, Agent agent, CancellationToken ct)
    {
        (await shell.RunAsync(runner, "umask 077; mkdir -p ~/.deveagentmanager/artifact-access; chmod 700 ~/.deveagentmanager/artifact-access", cancellationToken: ct))
            .EnsureSuccess("Preparing private artifact access");
        await shell.UploadAsync(runner, PathFor(agent.Id), Issue(agent), "600", ct);
    }
}
