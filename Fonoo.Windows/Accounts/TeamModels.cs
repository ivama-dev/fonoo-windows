using System.Globalization;
using System.Text.Json.Serialization;

namespace Fonoo.Windows.Accounts;

public sealed class TeamMember
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    [JsonPropertyName("extension")] public string? Number { get; set; }
    public int PersonalDeviceCount { get; set; }
    public PersonalDevice[] PersonalDevices { get; set; } = [];
    public PersonalAvailability? Availability { get; set; }
    [JsonIgnore] public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => p[0])).ToUpperInvariant();
    [JsonIgnore] public string Detail => $"{Number ?? "Keine Nebenstelle"} · {Email}";
    [JsonIgnore] public string AvailabilityLabel => Availability?.Label ?? "";
    [JsonIgnore] public bool Callable => Number is { Length: >= 2 and <= 6 } && Number[0] != '0' && Number.All(char.IsAsciiDigit);
    public bool Matches(string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
        new[] { Name, Email, Number ?? "" }.Any(field => CultureInfo.CurrentCulture.CompareInfo.IndexOf(field, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0));
}
public sealed class PersonalDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? UserId { get; set; }
    public string? TechnicalStatus { get; set; }
    public bool? Enabled { get; set; }
}
public sealed class PersonalAvailability
{
    public string State { get; set; } = "";
    public string Description { get; set; } = "";
    public string WorkMode { get; set; } = "";
    public double? ValidUntil { get; set; }
    public string? NextAvailableAt { get; set; }
    public static readonly Dictionary<string, string> States = new() { ["available"] = "Verfügbar", ["busy"] = "Beschäftigt", ["away"] = "Abwesend", ["off_duty"] = "Nicht im Dienst", ["vacation"] = "Urlaub", ["do_not_disturb"] = "Nicht stören" };
    public static readonly Dictionary<string, string> Modes = new() { ["office"] = "Büro", ["home_office"] = "Homeoffice", ["mobile"] = "Mobil", ["custom"] = "Benutzerdefiniert" };
    [JsonIgnore] public string Label => $"{States.GetValueOrDefault(State, State)} · {Modes.GetValueOrDefault(WorkMode, WorkMode)}";
}
public sealed class TeamSnapshot
{
    public int SchemaVersion { get; set; }
    public string TenantId { get; set; } = "";
    public string TenantName { get; set; } = "";
    public int Revision { get; set; }
    public string SelfUserId { get; set; } = "";
    public TeamMember[] Members { get; set; } = [];
    public TeamSnapshot Validate(string tenant, string user)
    {
        if (SchemaVersion != 1 || TenantId != tenant || SelfUserId != user || Revision <= 0 || string.IsNullOrWhiteSpace(TenantName) ||
            Members is null || Members.Length > 10000 || Members.Select(m => m?.Id).Distinct().Count() != Members.Length ||
            Members.Any(m => m is null || string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.Email) ||
                m.PersonalDevices is null || m.PersonalDeviceCount < m.PersonalDevices.Length || (m.Id != user && m.PersonalDevices.Length > 0) || (m.Number is not null && !m.Callable)))
            throw new AccountException("Das Team konnte nicht sicher geladen werden. Bitte erneut aktualisieren.");
        return this;
    }
}
public sealed partial class AvailabilitySnapshot
{
    public int SchemaVersion { get; set; }
    public string TenantId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string SelfUserId { get; set; } = "";
    public int Revision { get; set; }
    public AvailabilitySettings Settings { get; set; } = new();
    public EffectiveAvailability Effective { get; set; } = new();
    public CallTeam[] CallTeams { get; set; } = [];
    public PersonalDevice[] PersonalDevices { get; set; } = [];
    public AvailabilitySchedule[] Schedules { get; set; } = [];
    public AvailabilityCompany Company { get; set; } = new();
    public AvailabilitySnapshot Validate(string tenant, string user)
    {
        if (SchemaVersion != 1 || TenantId != tenant || UserId != user || SelfUserId != user || Revision <= 0 ||
            Settings is null || Effective is null || Company is null || CallTeams is null || PersonalDevices is null || Schedules is null ||
            Settings.ModeDevices is null || Settings.ModeDevices.Any(p => p.Value is null) ||
            CallTeams.Any(t => t is null || string.IsNullOrEmpty(t.Id) || t.Members is null || t.Members.Any(m => m is null || m.Revision < 0)) ||
            PersonalDevices.Any(d => d is null || string.IsNullOrEmpty(d.Id) || string.IsNullOrEmpty(d.Name)) || Schedules.Any(s => s is null || string.IsNullOrEmpty(s.Id)))
            throw new AccountException("Die Verfügbarkeit konnte nicht sicher geladen werden.");
        ValidateProfiles();
        return this;
    }
}
public sealed partial class AvailabilitySettings
{
    public PersonalAvailability? Presence { get; set; }
    public string WorkMode { get; set; } = "office";
    public Dictionary<string, string[]> ModeDevices { get; set; } = [];
    public string? ScheduleId { get; set; }
}
public sealed class EffectiveAvailability
{
    public bool Eligible { get; set; }
    public string ReasonText { get; set; } = "";
    public string? NextAvailableAt { get; set; }
    public PersonalDevice[]? EffectiveDevices { get; set; }
    public PersonalAvailability? Presence { get; set; }
}
public sealed class CallTeam
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonPropertyName("extension")] public string Number { get; set; } = "";
    public bool AllowSelfPause { get; set; }
    public CallTeamMember[] Members { get; set; } = [];
}
public sealed class CallTeamMember
{
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public int Revision { get; set; }
    public double? TemporaryPauseUntil { get; set; }
    public EffectiveAvailability Effective { get; set; } = new();
}
public sealed class AvailabilitySchedule { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
public sealed class AvailabilityCompany { public bool RoutingEnabled { get; set; } }

public sealed partial class AccountClient
{
    public async Task<TeamSnapshot> GetTeamAsync(string tenant, string user, CancellationToken ct) =>
        (await RequestAsync<TeamSnapshot>("cloud/team/read", new { tenant_id = tenant }, ct)).Validate(tenant, user);
    public async Task<TeamSnapshot> RenameSelfAsync(TeamSnapshot current, string user, string name, CancellationToken ct) =>
        (await RequestAsync<TeamSnapshot>("cloud/team/name", new { tenant_id = current.TenantId, expected_revision = current.Revision, name }, ct)).Validate(current.TenantId, user);
    public async Task<AvailabilitySnapshot> GetAvailabilityAsync(string tenant, string user, CancellationToken ct) =>
        (await RequestAsync<AvailabilitySnapshot>("cloud/availability/read", new { tenant_id = tenant }, ct)).Validate(tenant, user);
    private sealed class RevisionResponse { public int Revision { get; set; } }
    public async Task SaveAvailabilityAsync(AvailabilitySnapshot current, object changes, CancellationToken ct) =>
        _ = await RequestAsync<RevisionResponse>("cloud/availability/user", new { tenant_id = current.TenantId, expected_revision = current.Revision, changes }, ct);
    public async Task PauseTeamAsync(AvailabilitySnapshot current, CallTeam team, long? until, CancellationToken ct)
    {
        var member = team.Members.SingleOrDefault(m => m.TargetType == "user" && m.TargetId == current.SelfUserId);
        if (!team.AllowSelfPause || member is null) throw new AccountException("Dieses Rufteam kann nicht pausiert werden.");
        _ = await RequestAsync<RevisionResponse>("cloud/availability/pause", new { tenant_id = current.TenantId, team_id = team.Id, expected_revision = member.Revision, until }, ct);
    }
}
