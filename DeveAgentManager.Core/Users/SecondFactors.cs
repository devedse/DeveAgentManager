using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DeveAgentManager.Core.Data;
using DeveAgentManager.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace DeveAgentManager.Core.Users;

/// <summary>RFC 6238 SHA1 TOTP with atomic replay prevention and hashed, single-use recovery codes.</summary>
public sealed class SecondFactors(IDbContextFactory<ManagerDbContext> factory, SecretProtector protector, TimeProvider clock)
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public async Task<bool> EnabledAsync(Guid user, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserSecondFactors.AnyAsync(f => f.UserId == user && f.Enabled, ct);
    }
    public async Task<string> BeginAsync(Guid user, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.UserSecondFactors.FindAsync([user], ct);
        if (existing?.Enabled == true) throw new InvalidOperationException("Disable your existing authenticator before enrolling another.");
        var secret = Encode(RandomNumberGenerator.GetBytes(20));
        if (existing is null) { existing = new UserSecondFactor { UserId = user }; db.UserSecondFactors.Add(existing); }
        existing.Secret = protector.Protect(secret)!;
        existing.LastCounter = -1;
        await db.SaveChangesAsync(ct);
        return secret;
    }
    public async Task<string[]> ConfirmAsync(Guid user, string code, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var factor = await db.UserSecondFactors.AsNoTracking().SingleOrDefaultAsync(f => f.UserId == user, ct);
        var counter = factor is null ? null : Match(protector.Unprotect(factor.Secret), code);
        if (factor is null || factor.Enabled || counter is null) throw new ArgumentException("The authenticator code is incorrect.");
        var codes = Enumerable.Range(0, 10).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(10))).ToArray();
        var hashes = string.Join('\n', codes.Select(Hash));
        if (await db.UserSecondFactors.Where(f => f.UserId == user && !f.Enabled && f.Secret == factor.Secret).ExecuteUpdateAsync(u => u
            .SetProperty(f => f.Enabled, true).SetProperty(f => f.LastCounter, counter.Value).SetProperty(f => f.RecoveryHashes, hashes), ct) != 1)
            throw new InvalidOperationException("Enrollment changed; start again.");
        return codes;
    }
    public async Task<bool> VerifyAsync(Guid user, string code, CancellationToken ct = default)
    {
        if (code.Length > 100) return false;
        await using var db = await factory.CreateDbContextAsync(ct);
        var factor = await db.UserSecondFactors.AsNoTracking().SingleOrDefaultAsync(f => f.UserId == user && f.Enabled, ct);
        if (factor is null) return false;
        var counter = Match(protector.Unprotect(factor.Secret), code);
        if (counter is { } current && current > factor.LastCounter)
            return await db.UserSecondFactors.Where(f => f.UserId == user && f.Enabled && f.Secret == factor.Secret && f.LastCounter < current)
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.LastCounter, current), ct) == 1;
        var hash = Hash(code.Trim().ToUpperInvariant());
        var hashes = factor.RecoveryHashes.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (!hashes.Contains(hash, StringComparer.Ordinal)) return false;
        var remaining = string.Join('\n', hashes.Where(h => h != hash));
        return await db.UserSecondFactors.Where(f => f.UserId == user && f.Enabled && f.RecoveryHashes == factor.RecoveryHashes)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.RecoveryHashes, remaining), ct) == 1;
    }
    public async Task DisableAsync(Guid user, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.UserSecondFactors.Where(f => f.UserId == user).ExecuteDeleteAsync(ct);
    }
    public async Task<string> EnforcementAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.Settings.FindAsync(["totp-enforcement"], ct))?.Value ?? "optional";
    }
    public async Task<bool> RequiredAsync(CurrentUser user, CancellationToken ct = default) => await EnforcementAsync(ct) switch
    {
        "everyone" => true, "roles" => user.Roles != UserRoles.None, _ => false,
    };
    public async Task<bool> CanUseAsync(CurrentUser user, CancellationToken ct = default) =>
        !await RequiredAsync(user, ct) || await EnabledAsync(user.Id, ct);

    public async Task EnforceAsync(string mode, CancellationToken ct = default)
    {
        if (mode is not ("optional" or "everyone" or "roles")) throw new ArgumentException("Choose optional, roles or everyone.");
        await using var db = await factory.CreateDbContextAsync(ct);
        // Enforcement only activates once every affected account has enrolled; never surprise-lock someone out.
        if (mode != "optional" && await db.Users.AnyAsync(u => !u.Disabled && (mode == "everyone" || u.Roles != UserRoles.None)
            && !db.UserSecondFactors.Any(f => f.UserId == u.Id && f.Enabled), ct))
            throw new InvalidOperationException("Every affected enabled user must enroll before enforcement can be enabled.");
        var setting = await db.Settings.FindAsync(["totp-enforcement"], ct);
        if (setting is null) { setting = new SettingEntry { Key = "totp-enforcement" }; db.Settings.Add(setting); }
        setting.Value = mode; setting.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }
    private long? Match(string? secret, string code)
    {
        if (secret is null || code.Length != 6 || code.Any(c => c is < '0' or > '9')) return null;
        var counter = clock.GetUtcNow().ToUnixTimeSeconds() / 30;
        for (var offset = -1; offset <= 1; offset++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, counter + offset)), Encoding.ASCII.GetBytes(code))) return counter + offset;
        return null;
    }
    public static string Code(string secret, long counter)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, counter);
        var digest = HMACSHA1.HashData(Decode(secret), bytes);
        var offset = digest[^1] & 15;
        var value = BinaryPrimitives.ReadUInt32BigEndian(digest.AsSpan(offset, 4)) & 0x7fffffff;
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    private static string Encode(byte[] bytes)
    {
        var result = new StringBuilder(); var buffer = 0; var bits = 0;
        foreach (var b in bytes) { buffer = (buffer << 8) | b; bits += 8; while (bits >= 5) { bits -= 5; result.Append(Alphabet[(buffer >> bits) & 31]); } }
        if (bits > 0) result.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }
    private static byte[] Decode(string value)
    {
        var bytes = new List<byte>(); var buffer = 0; var bits = 0;
        foreach (var c in value) { buffer = (buffer << 5) | Alphabet.IndexOf(c); bits += 5; if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); } }
        return bytes.ToArray();
    }
}
