using Fonoo.Windows.Accounts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fonoo.Windows.Telephony;

/// A separate cache keeps pre-sync device history intact and isolates each account.
public sealed class CloudHistoryCache
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private readonly string directory, user, tenant, path, legacyPath;
    private readonly TimeProvider clock;
    private readonly Func<byte[], bool, byte[]> protect;
    private CloudHistorySnapshot? cached;

    public CloudHistoryCache(string directory, string user, string tenant, TimeProvider? clock = null,
        Func<byte[], bool, byte[]>? protection = null)
    {
        this.directory = directory; this.user = user; this.tenant = tenant;
        this.clock = clock ?? TimeProvider.System; protect = protection ?? HistoryDataProtection.Transform;
        var prefix = Path.Combine(directory, "cloud-history-v1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + "\n" + tenant))));
        path = prefix + ".protected"; legacyPath = prefix + ".json";
    }

    public CloudHistorySnapshot? Load()
    {
        var source = File.Exists(path) ? path : legacyPath;
        if (!File.Exists(source)) return null;
        if (new FileInfo(source).Length is < 1 or > 1_000_000) throw new InvalidDataException();
        var plain = source == path ? protect(File.ReadAllBytes(source), false) : File.ReadAllBytes(source);
        CloudHistorySnapshot value;
        try { value = JsonSerializer.Deserialize<CloudHistorySnapshot>(plain) ?? throw new InvalidDataException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var restricted = Restrict(value);
        // A valid old cache is removed only after its protected replacement succeeds.
        if (source == legacyPath || restricted.Entries.Length != value.Entries.Length) return Save(restricted);
        return cached = restricted;
    }
    public CloudHistorySnapshot Save(CloudHistorySnapshot value)
    {
        var restricted = Restrict(value);
        var plain = JsonSerializer.SerializeToUtf8Bytes(restricted);
        byte[] cipher;
        try { cipher = protect(plain, true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, cipher); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (File.Exists(legacyPath)) File.Delete(legacyPath);
        return cached = restricted;
    }

    public CloudHistorySnapshot? PruneExpired()
    {
        if (cached is null) return null;
        var restricted = Restrict(cached);
        return restricted.Entries.Length == cached.Entries.Length ? null : Save(restricted);
    }

    private CloudHistorySnapshot Restrict(CloudHistorySnapshot value)
    {
        value.Validate(tenant, user);
        if (value.NotModified) throw new InvalidDataException();
        var cutoff = (clock.GetUtcNow() - Retention).ToUnixTimeSeconds();
        return new()
        {
            SchemaVersion = value.SchemaVersion, TenantId = value.TenantId, SelfUserId = value.SelfUserId,
            Revision = value.Revision, CollectorAvailable = value.CollectorAvailable, LastCollectedAt = value.LastCollectedAt,
            Entries = value.Entries.Where(e => e.StartedAt >= cutoff).OrderByDescending(e => e.StartedAt).ThenBy(e => e.Id).ToArray()
        };
    }
}
