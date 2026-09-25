using Renci.SshNet;
using SshNet.Keygen;
using SshNet.Keygen.Extensions;

namespace DeveAgentManager.Core.Ssh;

/// <summary>
/// The Manager's own SSH key pair (Ed25519). It is created on first start in /data/ssh and injected into every Runner.
/// </summary>
public sealed class ManagerSshKey
{
    public const string Comment = "deveagentmanager";

    private ManagerSshKey(PrivateKeyFile keyFile, string publicKey)
    {
        KeyFile = keyFile;
        PublicKey = publicKey;
    }

    /// <summary>OpenSSH format, ready for authorized_keys.</summary>
    public string PublicKey { get; }

    public PrivateKeyFile KeyFile { get; }

    public static ManagerSshKey LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var privatePath = Path.Combine(directory, "id_ed25519");

        if (!File.Exists(privatePath))
        {
            SshKey.Builder(SshKeyType.ED25519).WithComment(Comment).Generate(privatePath, FileMode.CreateNew);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        var keyFile = new PrivateKeyFile(privatePath);
        var publicKey = keyFile.ToOpenSshPublicFormat().Trim();
        File.WriteAllText(privatePath + ".pub", publicKey + "\n");
        return new ManagerSshKey(keyFile, publicKey);
    }
}
