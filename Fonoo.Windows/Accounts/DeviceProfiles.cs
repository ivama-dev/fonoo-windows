using System.Text;
using System.Text.Json.Serialization;

namespace Fonoo.Windows.Accounts;

public sealed class DeviceProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string[]? DeviceIds { get; set; }
    [JsonIgnore] public bool IsStandard => Id == "standard";
    public static DeviceProfile Standard() => new() { Id = "standard", Name = "Standard", DeviceIds = null };
}

public sealed partial class AvailabilitySettings
{
    public DeviceProfile[]? DeviceProfiles { get; set; }
    public string? ActiveProfileId { get; set; }
    [JsonIgnore] public DeviceProfile[] Profiles => DeviceProfiles ?? [DeviceProfile.Standard()];
    [JsonIgnore] public DeviceProfile ActiveProfile => Profiles.FirstOrDefault(p => p.Id == (ActiveProfileId ?? "standard")) ?? DeviceProfile.Standard();
}

public sealed partial class AvailabilitySnapshot
{
    public bool? ProfilesAvailable { get; set; }
    public int? DeviceProfilesVersion { get; set; }
    [JsonIgnore] public PersonalDevice[] OwnDevices => PersonalDevices.Where(d => d.UserId == UserId).ToArray();
    public void ValidateProfiles()
    {
        var profiles = Settings.Profiles;
        var own = OwnDevices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        if (DeviceProfilesVersion is not (null or 1) || profiles.Length is < 1 or > 20 ||
            PersonalDevices.Length > 10000 || own.Count != OwnDevices.Length ||
            profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 128 || p.Id.Any(char.IsControl)) ||
            profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Length ||
            !profiles.Any(p => p.IsStandard && p.Name == "Standard" && p.DeviceIds is null) ||
            !profiles.Any(p => p.Id == (Settings.ActiveProfileId ?? "standard")) ||
            profiles.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Name.EnumerateRunes().Count() > 50 || p.Name.Any(char.IsControl) ||
                (p.IsStandard ? p.Name != "Standard" || p.DeviceIds is not null : p.DeviceIds is null ||
                 p.DeviceIds.Length > own.Count || p.DeviceIds.Distinct(StringComparer.Ordinal).Count() != p.DeviceIds.Length || p.DeviceIds.Any(id => !own.Contains(id)))))
            throw new AccountException("Die Anrufprofile konnten nicht sicher geladen werden. Bitte aktualisieren.");
    }
}

public sealed class DeviceProfileChanges
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DeviceProfile[]? DeviceProfiles { get; init; }
    public required string ActiveProfileId { get; init; }

    public static DeviceProfileChanges Select(AvailabilitySnapshot current, string id)
    {
        current.Validate(current.TenantId, current.UserId);
        if (!current.Settings.Profiles.Any(p => p.Id == id)) throw new AccountException("Dieses Profil ist nicht mehr verfügbar. Bitte aktualisieren.");
        return new() { ActiveProfileId = id };
    }
    public static DeviceProfileChanges Upsert(AvailabilitySnapshot current, DeviceProfile profile, bool isNew)
    {
        current.Validate(current.TenantId, current.UserId);
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Trim().EnumerateRunes().Count() > 50 || profile.Name.Any(char.IsControl))
            throw new AccountException("Bitte gib einen Profilnamen mit 1 bis 50 Zeichen ein.");
        if (profile.IsStandard || isNew == current.Settings.Profiles.Any(p => p.Id == profile.Id))
            throw new AccountException("Dieses Profil kann so nicht geändert werden. Bitte aktualisieren.");
        var next = new DeviceProfile { Id = profile.Id, Name = profile.Name.Trim(), DeviceIds = profile.DeviceIds?.ToArray() };
        var changes = new DeviceProfileChanges { ActiveProfileId = current.Settings.ActiveProfile.Id,
            DeviceProfiles = current.Settings.Profiles.Where(p => p.Id != profile.Id).Append(next).ToArray() };
        changes.Apply(current).Validate(current.TenantId, current.UserId);
        return changes;
    }
    public static DeviceProfileChanges Delete(AvailabilitySnapshot current, string id)
    {
        _ = Select(current, id);
        if (id == "standard") throw new AccountException("Das Profil Standard bleibt immer erhalten.");
        return new() { ActiveProfileId = current.Settings.ActiveProfile.Id == id ? "standard" : current.Settings.ActiveProfile.Id,
            DeviceProfiles = current.Settings.Profiles.Where(p => p.Id != id).ToArray() };
    }
    public AvailabilitySnapshot Apply(AvailabilitySnapshot current) => new()
    {
        SchemaVersion = current.SchemaVersion, TenantId = current.TenantId, UserId = current.UserId, SelfUserId = current.SelfUserId,
        Revision = current.Revision, ProfilesAvailable = current.ProfilesAvailable, DeviceProfilesVersion = current.DeviceProfilesVersion,
        PersonalDevices = current.PersonalDevices, CallTeams = current.CallTeams, Schedules = current.Schedules, Company = current.Company, Effective = current.Effective,
        Settings = new() { Presence = current.Settings.Presence, WorkMode = current.Settings.WorkMode, ModeDevices = current.Settings.ModeDevices,
            ScheduleId = current.Settings.ScheduleId, ActiveProfileId = ActiveProfileId, DeviceProfiles = DeviceProfiles ?? current.Settings.Profiles }
    };
}

public sealed partial class AccountClient
{
    public async Task<AvailabilitySnapshot> SaveDeviceProfilesAsync(AvailabilitySnapshot current, DeviceProfileChanges changes, CancellationToken ct)
    {
        current.Validate(current.TenantId, current.UserId);
        var expected = changes.Apply(current).Validate(current.TenantId, current.UserId);
        var response = await RequestAsync<RevisionResponse>("cloud/availability/user",
            new { tenant_id = current.TenantId, expected_revision = current.Revision, changes }, ct);
        if (response.Revision <= current.Revision) throw new AccountException("Die Profiländerung wurde nicht bestätigt. Bitte aktualisieren.");
        var next = await GetAvailabilityAsync(current.TenantId, current.UserId, ct);
        if (next.Revision < response.Revision || (next.Revision == response.Revision &&
            (next.Settings.ActiveProfile.Id != expected.Settings.ActiveProfile.Id || changes.DeviceProfiles is not null &&
             !ProfilesEqual(next.Settings.Profiles, expected.Settings.Profiles))))
            throw new AccountException("Die Profiländerung konnte noch nicht abgeglichen werden. Bitte aktualisieren.");
        return next;
    }
    private static bool ProfilesEqual(DeviceProfile[] left, DeviceProfile[] right) => left.Length == right.Length && left.All(p =>
        right.Any(q => q.Id == p.Id && q.Name == p.Name && (p.DeviceIds is null ? q.DeviceIds is null : q.DeviceIds is not null &&
            p.DeviceIds.ToHashSet(StringComparer.Ordinal).SetEquals(q.DeviceIds))));
}
