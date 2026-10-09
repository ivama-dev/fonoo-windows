#if FONOO_DESIGN_PREVIEW
using Fonoo.Windows.Accounts;
using Fonoo.Windows.Telephony;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private async Task ShowAudioPreviewAsync()
    {
        audioDialogOpen = true;
        var view = new Desktop.AudioSettingsView();
        view.MarkPreview();
        SetOptions(view.Inputs, [new("preview-mic", "Headset · Mikrofon"), new("preview-default", "Windows-Standardmikrofon")], "preview-mic");
        SetOptions(view.Outputs, [new("preview-speaker", "Headset · Kopfhörer"), new("preview-default", "Windows-Standardausgabe")], "preview-speaker");
        var timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        long echoUntil = 0, speakerUntil = 0;
        void Render()
        {
            var now = Environment.TickCount64;
            var echo = now < echoUntil; var speaker = now < speakerUntil;
            var level = .55 + .15 * Math.Sin(now / 230d);
            view.SetMeter(level, level + .1, snapshot.InCall ? snapshot.Muted ? Desktop.AudioMeterMode.Muted : Desktop.AudioMeterMode.Call : echo ? Desktop.AudioMeterMode.EchoTest : Desktop.AudioMeterMode.Local);
            view.SetTests(echo, (int)Math.Ceiling(Math.Max(0, echoUntil - now) / 1000d), speaker, snapshot.InCall, false, true, true, true);
        }
        view.EchoTest.Click += (_, _) => { echoUntil = echoUntil > Environment.TickCount64 ? 0 : Environment.TickCount64 + 15000; Render(); };
        view.SpeakerTest.Click += (_, _) => { speakerUntil = speakerUntil > Environment.TickCount64 ? 0 : Environment.TickCount64 + 3000; Render(); };
        view.Refresh.Click += (_, _) => view.SetNotice("Beispielgeräte aktualisiert. Die Vorschau verwendet keine echten Audiogeräte.");
        view.WindowsSettings.IsEnabled = false;
        timer.Tick += (_, _) => Render();
        try { Render(); timer.Start(); await CreateAudioDialog(view).ShowAsync(); }
        finally { timer.Stop(); audioDialogOpen = false; }
    }

    private void InitializeDesignFixtures()
    {
        // Synthetic data confined to an explicitly built Debug preview. No real
        // account, provisioner, SIP worker or tray is created by these fixtures.
        accountId = "preview-user"; activeTenant = "preview-company";
        availability = new AvailabilitySnapshot { SchemaVersion = 1, TenantId = activeTenant, UserId = accountId, SelfUserId = accountId, Revision = 1,
            ProfilesAvailable = true, DeviceProfilesVersion = 1,
            PersonalDevices = [new() { Id = "preview-pc", Name = "Windows-PC", UserId = accountId, Enabled = true },
                new() { Id = "preview-iphone", Name = "iPhone", UserId = accountId, Enabled = true },
                new() { Id = "preview-mac", Name = "MacBook", UserId = accountId, Enabled = false }],
            Settings = new() { DeviceProfiles = [DeviceProfile.Standard(), new() { Id = "preview-office", Name = "Büro", DeviceIds = ["preview-pc", "preview-mac"] },
                new() { Id = "preview-mobile", Name = "Unterwegs", DeviceIds = ["preview-iphone"] }], ActiveProfileId = "standard" }
        }.Validate(activeTenant, accountId);
        RenderProfileButton();
        PreviewCallButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        PreviewMiniButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        favoritesStore = new FavoritesStore(Path.Combine(AppContext.BaseDirectory, "qa", "favorites"), accountId, activeTenant);
        foreach (var favorite in favoritesStore.Load()) favorites.Add(favorite);
        if (!favorites.Any(f => f.Number == "101")) favorites.Add(new("preview-favorite", "Alex Beispiel", "101"));
        NoFavorites.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        history.Add(new(Guid.NewGuid().ToString(), "101", false, DateTimeOffset.UtcNow.AddMinutes(-25), 94, "completed"));
        history.Add(new(Guid.NewGuid().ToString(), "102", true, DateTimeOffset.UtcNow.AddHours(-1), 0, "missed"));
        history.Add(new(Guid.NewGuid().ToString(), "103", true, DateTimeOffset.UtcNow.AddHours(-2), 61, "completed"));
        history.Add(new(Guid.NewGuid().ToString(), "104", true, DateTimeOffset.UtcNow.AddHours(-3), 0, "declined"));
        history.Add(new(Guid.NewGuid().ToString(), "105", false, DateTimeOffset.UtcNow.AddHours(-4), 0, "failed"));
        RenderHistory();
        contacts = [new("preview-contact", "Alex Beispiel", "+43123456789", "Arbeit")];
        outlookContacts = [new("outlook:preview-contact", "Mara Beispiel", "+437200101010", "Outlook · Mobil")]; RenderContacts();
        teamSnapshot = new TeamSnapshot { TenantId = activeTenant, SelfUserId = accountId, TenantName = "Beispielfirma · Designvorschau", Members = [
            new() { Id = accountId, Name = "Du · Vorschau", Email = "du@example.invalid", Number = "100" },
            new() { Id = "preview-other", Name = "Alex Beispiel", Email = "alex@example.invalid", Number = "101", Availability = new() { State = "available", WorkMode = "office" } },
            new() { Id = "preview-mara", Name = "Mara Beispiel", Email = "mara@example.invalid", Number = "102" },
            new() { Id = "preview-ringing", Name = "Robin Beispiel", Email = "robin@example.invalid", Number = "103" },
            new() { Id = "preview-multiple", Name = "Sam Beispiel", Email = "sam@example.invalid", Number = "104" } ] };
        TeamCompany.Text = teamSnapshot.TenantName; RenderTeam();
        RenderFavoriteRows(); PreviewPresenceButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible; SetPresencePreview();
        if (Environment.GetCommandLineArgs().Contains("--preview-call-ui"))
        {
            UpdatePhone(new(false, true, false, false, "Gesprächsansicht · Designvorschau", "Alex Beispiel", Active: true));
            CallDuration.Text = "1:34";
        }
        RenderDestination();
        VerifyProtectedStorage();
    }

    private void SetPresencePreview()
    {
        phonePresence = new(activeTenant, accountId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        phonePresence.TryApply(new() { SchemaVersion = 1, TenantId = activeTenant, SelfUserId = accountId,
            CollectorAvailable = true, ObservedAt = now, ExpiresAt = now + 15, Members = [
            new() { UserId = accountId, State = "idle" },
            new() { UserId = "preview-other", State = "busy", CallCount = 1, PeerNumber = "102", PeerUserId = "preview-mara" },
            new() { UserId = "preview-mara", State = "busy", CallCount = 1, PeerNumber = "00437200101010", Direction = "outgoing" },
            new() { UserId = "preview-ringing", State = "ringing" },
            new() { UserId = "preview-multiple", State = "busy", CallCount = 2 } ] });
        presenceExpiryTimer?.Start(); RenderPresence();
    }
    private async void VerifyProtectedStorage()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "qa", "dpapi-" + Guid.NewGuid().ToString("N"));
        var store = new ProtectedSessionStore(directory);
        try
        {
            var synthetic = new DeviceSession { Token = new('a', 43), RefreshToken = new('b', 43), DeviceId = Guid.NewGuid().ToString(), UserId = "synthetic", Email = "synthetic@example.invalid", ExpiresAt = 100, DeviceExpiresAt = 200 };
            store.Save(synthetic);
            if (store.Load()?.RefreshToken != synthetic.RefreshToken || System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "session.protected"))).Contains(synthetic.RefreshToken)) throw new InvalidDataException();
            var historyCache = new CloudHistoryCache(directory, "synthetic", "synthetic-company");
            var historyData = new CloudHistorySnapshot { SchemaVersion = 1, TenantId = "synthetic-company", SelfUserId = "synthetic", Revision = 1,
                Entries = [new() { Id = Guid.NewGuid().ToString(), Number = "101", Incoming = true, StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Outcome = "missed" }] };
            historyCache.Save(historyData);
            var protectedHistory = Directory.GetFiles(directory, "cloud-history-*.protected").Single();
            if (historyCache.Load()?.Entries.Single().Id != historyData.Entries[0].Id || System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(protectedHistory)).Contains("synthetic-company")) throw new InvalidDataException();
            ShowNotice("Windows-Speicherprüfung erfolgreich: DPAPI schützt die synthetische Testsitzung.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success);
            var wav = Path.Combine(directory, "pcm-check.wav");
            using (var writer = new BinaryWriter(File.Create(wav)))
            {
                const int rate = 16000, samples = rate * 2;
                writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
                for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 5000));
            }
            try
            {
                var ok = await Desktop.MicrophoneMeter.CheckSyntheticPcmAsync(wav);
                var clientId = Guid.NewGuid().ToString();
                var contactsStore = new Microsoft365.ProtectedMicrosoftContactStore(directory, "synthetic", clientId);
                try
                {
                    contactsStore.Save(new("synthetic", clientId, "synthetic-ms", "synthetic@example.invalid", DateTimeOffset.UtcNow, [new("synthetic", "DPAPI Kontaktprüfung", "101", "Outlook")]));
                    if (contactsStore.Load()?.Contacts.Single().Name != "DPAPI Kontaktprüfung" || System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "contacts.protected"))).Contains("DPAPI Kontaktprüfung")) throw new InvalidDataException();
                    var tokenCache = await Microsoft.Identity.Client.Extensions.Msal.MsalCacheHelper.CreateAsync(new Microsoft.Identity.Client.Extensions.Msal.StorageCreationPropertiesBuilder("msal-check.protected", directory).Build());
                    tokenCache.VerifyPersistence();
                }
                finally { contactsStore.Clear(); }
                ShowNotice(ok ? "Windows-Prüfungen erfolgreich: geschützter Anrufcache, DPAPI-Sitzung, Outlook, MSAL und PCM-Pegel. Nur synthetische Daten." : "DPAPI erfolgreich. PCM: " + Desktop.MicrophoneMeter.SyntheticResult, ok ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            }
            finally { File.Delete(wav); }
        }
        catch { ShowNotice("Die Windows-Speicherprüfung (DPAPI) konnte nicht abgeschlossen werden."); }
        finally
        {
            store.Clear();
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.GetFiles(directory, "cloud-history-*.protected")) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }
}
#endif
