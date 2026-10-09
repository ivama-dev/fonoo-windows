using Fonoo.Windows.Telephony;

namespace Fonoo.Windows.Accounts;

public sealed class CloudHistoryEntry
{
    public string Id { get; set; } = "";
    public string Number { get; set; } = "";
    public bool Incoming { get; set; }
    public long StartedAt { get; set; }
    public int DurationSeconds { get; set; }
    public string Outcome { get; set; } = "";
    public CallHistoryEntry ToLocal()
    {
        if (!Guid.TryParse(Id, out _) || Number.Length is < 1 or > 64 ||
            (Number != "anonymous" && !System.Text.RegularExpressions.Regex.IsMatch(Number, @"^\+?[0-9*#]{1,64}$")) ||
            StartedAt <= 0 || StartedAt > DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds() ||
            DurationSeconds is < 0 or > 604800 || Outcome is not ("completed" or "missed" or "failed") ||
            (Outcome == "missed" && !Incoming) || (Outcome != "completed" && DurationSeconds != 0))
            throw new AccountException("Die gemeinsame Anrufliste enthält einen ungültigen Eintrag.");
        return new(Id, Number, Incoming, DateTimeOffset.FromUnixTimeSeconds(StartedAt), DurationSeconds, Outcome);
    }
}

public sealed class CloudHistorySnapshot
{
    public int SchemaVersion { get; set; }
    public string TenantId { get; set; } = "";
    public string SelfUserId { get; set; } = "";
    public long Revision { get; set; }
    public bool NotModified { get; set; }
    public bool CollectorAvailable { get; set; }
    public long LastCollectedAt { get; set; }
    public CloudHistoryEntry[] Entries { get; set; } = [];
    public CallHistoryEntry[] Validate(string tenant, string user)
    {
        if (SchemaVersion != 1 || TenantId != tenant || SelfUserId != user || Revision < 0 || Entries is null ||
            Entries.Length > 500 || (NotModified && Entries.Length != 0) || Entries.Select(e => e?.Id).Distinct().Count() != Entries.Length)
            throw new AccountException("Die gemeinsame Anrufliste konnte nicht sicher übernommen werden.");
        return Entries.Select(e => e?.ToLocal() ?? throw new AccountException("Ungültige Anrufliste.")).OrderByDescending(e => e.StartedAt).ThenBy(e => e.Id).ToArray();
    }
}

public sealed partial class AccountClient
{
    public Task<CloudHistorySnapshot> ReadHistoryAsync(string tenant, long? revision, CancellationToken ct)
        => RequestAsync<CloudHistorySnapshot>("cloud/call-history/read", revision is { } value ? new { tenant_id = tenant, revision = value } : (object)new { tenant_id = tenant }, ct);
    public Task<CloudHistorySnapshot> ClearHistoryAsync(string tenant, CancellationToken ct)
        => RequestAsync<CloudHistorySnapshot>("cloud/call-history/delete", new { tenant_id = tenant, clear_all = true }, ct);
    public Task<CloudHistorySnapshot> DeleteHistoryAsync(string tenant, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var canonical)) throw new AccountException("Dieser Anruf kann nicht gelöscht werden.");
        return RequestAsync<CloudHistorySnapshot>("cloud/call-history/delete", new { tenant_id = tenant, ids = new[] { canonical.ToString() } }, ct);
    }
}
