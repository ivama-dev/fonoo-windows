using System.Text.Json.Serialization;

namespace Fonoo.Windows.Accounts;

public sealed class PresenceSnapshot
{
    public int SchemaVersion { get; set; }
    public string TenantId { get; set; } = "";
    public string SelfUserId { get; set; } = "";
    public bool CollectorAvailable { get; set; }
    public double? ObservedAt { get; set; }
    public double? ExpiresAt { get; set; }
    public CallPresence[] Members { get; set; } = [];

    public PresenceSnapshot Validate(string tenant, string user)
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(user) ||
            TenantId != tenant || SelfUserId != user || Members is null || Members.Length > 10000 ||
            Members.Any(m => m is null || string.IsNullOrWhiteSpace(m.UserId) || m.UserId.Length > 256 ||
                m.State is not ("idle" or "busy" or "ringing" or "dialing" or "unknown") || m.CallCount is < 0 or > 1000 ||
                m.Direction is not (null or "incoming" or "outgoing") ||
                (m.PeerNumber is not null && (m.PeerNumber.Length is < 1 or > 64 ||
                    !m.PeerNumber.Any(char.IsAsciiDigit) ||
                    m.PeerNumber.Any(c => !char.IsAsciiDigit(c) && c != '+') || m.PeerNumber.Count(c => c == '+') > 1 ||
                    (m.PeerNumber.Contains('+') && m.PeerNumber[0] != '+'))) ||
                (m.State == "busy" && m.CallCount == 0) || (m.State == "idle" && m.CallCount != 0)) ||
            Members.Select(m => m.UserId).Distinct(StringComparer.Ordinal).Count() != Members.Length ||
            (ObservedAt is { } observed && (!double.IsFinite(observed) || observed < 0)) ||
            (ExpiresAt is { } expiry && (!double.IsFinite(expiry) || expiry < 0)) ||
            (CollectorAvailable && (ObservedAt is null || ExpiresAt is null || ExpiresAt <= ObservedAt || ExpiresAt - ObservedAt > 15.001)))
            throw new AccountException("Der Telefonstatus konnte nicht sicher geladen werden.");
        var ids = Members.Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);
        if (Members.Any(m => m.PeerUserId is not null && !ids.Contains(m.PeerUserId)))
            throw new AccountException("Der Gesprächspartner gehört nicht zur aktuellen Firma.");
        return this;
    }
}

public sealed class CallPresence
{
    public string UserId { get; set; } = "";
    public string State { get; set; } = "unknown";
    public int CallCount { get; set; }
    public string? PeerNumber { get; set; }
    public string? PeerUserId { get; set; }
    public string? Direction { get; set; }
}

public sealed partial class AccountClient
{
    [JsonIgnore] public int SessionGeneration => loginGeneration;
    public async Task<PresenceSnapshot> GetPresenceAsync(string tenant, string user, CancellationToken ct) =>
        (await RequestAsync<PresenceSnapshot>("cloud/team/presence", new { tenant_id = tenant }, ct)).Validate(tenant, user);
}
