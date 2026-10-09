using Fonoo.Windows.Accounts;

namespace Fonoo.Windows.Telephony;

// Kept in memory only. Expiry is independent of a slow or stalled HTTP request.
public sealed class TeamPresenceState(string tenant, string user, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private PresenceSnapshot? snapshot;
    private double? newestObserved;
    private long receivedAt;
    public PresenceSnapshot? Current { get { Expire(); return snapshot; } }
    public bool TryApply(PresenceSnapshot next)
    {
        next.Validate(tenant, user);
        if (!next.CollectorAvailable) { Clear(); return true; }
        if (newestObserved is { } last && next.ObservedAt < last) return false;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds() / 1000d;
        if (next.ObservedAt > now + 5) { Clear(); return true; }
        newestObserved = next.ObservedAt;
        receivedAt = time.GetTimestamp();
        snapshot = next.ExpiresAt <= now ? null : next;
        return true;
    }
    public bool Expire()
    {
        if (snapshot is null || (snapshot.ExpiresAt > time.GetUtcNow().ToUnixTimeMilliseconds() / 1000d &&
            time.GetElapsedTime(receivedAt).TotalSeconds < 15)) return false;
        Clear(); return true;
    }
    public void Clear() => snapshot = null; // Keep the watermark until the account/company context changes.
}

public sealed record PhonePresenceDisplay(string Text, string Detail, string Color)
{
    public static readonly PhonePresenceDisplay Unknown = new("Telefonstatus unbekannt", "", "#777386");
}

public static class TeamPresenceResolver
{
    public static string FavoriteId(string tenant, string user) => $"team:{tenant}:{user}";
    public static TeamMember? FavoriteMember(Favorite favorite, TeamSnapshot? team, string tenant, string user)
    {
        if (team is null || team.TenantId != tenant || team.SelfUserId != user) return null;
        if (favorite.Id.StartsWith("team:", StringComparison.Ordinal))
            return team.Members.SingleOrDefault(m => FavoriteId(tenant, m.Id) == favorite.Id && m.Callable && m.Number == favorite.Number);
        // Legacy/local favorites correlate only with an unambiguous, exact internal extension.
        var matches = team.Members.Where(m => m.Callable && m.Number == favorite.Number).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static PhonePresenceDisplay Display(string memberId, PresenceSnapshot? presence, TeamSnapshot? team,
        IEnumerable<ContactRow> contacts, IEnumerable<Favorite> favorites)
    {
        var member = presence?.Members.SingleOrDefault(m => m.UserId == memberId);
        if (member is null) return PhonePresenceDisplay.Unknown;
        var detail = "";
        if (member.State == "busy")
        {
            if (member.CallCount > 1) detail = $"{member.CallCount} Gespräche";
            else if (member.PeerNumber is { } number)
            {
                string? name = null;
                if (team?.TenantId == presence!.TenantId && team.SelfUserId == presence.SelfUserId)
                    name = team.Members.SingleOrDefault(m => m.Id == member.PeerUserId)?.Name;
                if (name is null && NumberKey(number) is { } key)
                {
                    var names = contacts.Where(c => NumberKey(c.Number) == key).Select(c => c.Name)
                        .Concat(favorites.Where(f => (!f.Id.StartsWith("team:", StringComparison.Ordinal) ||
                            FavoriteMember(f, team, presence!.TenantId, presence.SelfUserId) is not null) && NumberKey(f.Number) == key).Select(f => f.Name))
                        .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(2).ToArray();
                    if (names.Length == 1) name = names[0];
                }
                detail = "Mit " + (name ?? number);
            }
        }
        return member.State switch
        {
            "idle" => new("Frei", "", "#16813D"),
            "busy" => new("Telefoniert", detail, "#C42B1C"),
            "ringing" => new("Klingelt", "", "#A95B00"),
            "dialing" => new("Ruft an", "", "#A95B00"),
            _ => PhonePresenceDisplay.Unknown
        };
    }

    // AT dial region; short extensions remain exact. Never compare number suffixes.
    public static string? NumberKey(string raw)
    {
        var number = DialNumber.Normalize(raw);
        if (number is null || number.Any(c => !char.IsAsciiDigit(c) && c != '+')) return null;
        if (number.Length <= 6) return number;
        if (number.StartsWith("00", StringComparison.Ordinal)) return "+" + number[2..];
        if (number[0] == '0') return "+43" + number[1..];
        return number;
    }
}
