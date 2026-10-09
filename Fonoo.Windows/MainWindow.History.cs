using Fonoo.Windows.Accounts;
using Fonoo.Windows.Telephony;
using Microsoft.UI.Dispatching;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private CloudHistoryCache? cloudHistoryCache;
    private long? historyRevision;
    private bool historySyncing;
    private CancellationTokenSource? historyLifetime;
    private DispatcherQueueTimer? historyTimer;

    private void StartHistorySync(string tenant)
    {
        StopHistorySync();
        cloudHistoryCache = new(UserDataDirectory, accountId, tenant);
        historyLifetime = new();
        try
        {
            if (cloudHistoryCache.Load() is { } cached)
            {
                historyRevision = cached.Revision;
                history.Clear(); history.AddRange(cached.Validate(tenant, accountId));
                HistorySyncStatus.Text = "Fonoo-Konto · gespeicherter Stand, wird aktualisiert";
                RenderHistory();
            }
        }
        catch { HistorySyncStatus.Text = "Fonoo-Konto · gespeicherter Stand konnte nicht gelesen werden"; }
        historyTimer ??= DispatcherQueue.CreateTimer();
        historyTimer.Interval = TimeSpan.FromSeconds(15);
        if (!historyTimer.IsRunning) historyTimer.Start();
        _ = SyncHistoryAsync();
    }
    private void StopHistorySync()
    {
        historyLifetime?.Cancel(); historyLifetime?.Dispose(); historyLifetime = null;
        historyTimer?.Stop(); cloudHistoryCache = null; historyRevision = null;
        if (HistorySyncStatus is not null) HistorySyncStatus.Text = "Auf diesem PC gespeichert · getrennt nach Konto und Firma";
    }
    private async Task SyncHistoryAsync()
    {
        if (historySyncing || historyLifetime is null || cloudHistoryCache is null || closed || !account.SignedIn) return;
        var lifetime = historyLifetime; var cache = cloudHistoryCache; var tenant = activeTenant; var user = accountId;
        historySyncing = true;
        try
        {
            var response = await account.ReadHistoryAsync(tenant, historyRevision, lifetime.Token);
            if (closed || !ReferenceEquals(lifetime, historyLifetime) || tenant != activeTenant || user != accountId) return;
            if (!ApplyHistoryResponse(response, cache, tenant, user)) return;
            HistorySyncStatus.Text = response.CollectorAvailable
                ? "Mit deinem Fonoo-Konto synchronisiert · auf allen Geräten"
                : "Fonoo-Konto · Telefonieserver-Abgleich ausstehend";
            // Leave the current scroll position and row containers intact when unchanged.
            if (!response.NotModified) RenderHistory();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (AccountException ex) when (ex.SessionExpired)
        {
            if (ReferenceEquals(lifetime, historyLifetime) && !closed) ResetSession();
        }
        catch
        {
            if (ReferenceEquals(lifetime, historyLifetime) && !closed)
                HistorySyncStatus.Text = historyRevision is null
                    ? "Kontosynchronisierung noch nicht erreichbar · bisherige Liste auf diesem PC"
                    : "Fonoo-Konto · gespeicherter Stand, Verbindung wird wiederholt";
        }
        finally
        {
            // Expiry also applies while offline; the timer does not upload local call results.
            if (ReferenceEquals(lifetime, historyLifetime) && !closed)
            {
                try
                {
                    if (cache.PruneExpired() is { } pruned)
                    {
                        history.Clear(); history.AddRange(pruned.Validate(tenant, user)); RenderHistory();
                    }
                }
                catch { HistorySyncStatus.Text = "Fonoo-Konto · gespeicherter Stand konnte nicht aktualisiert werden"; }
            }
            historySyncing = false;
        }
    }

    private bool ApplyHistoryResponse(CloudHistorySnapshot response, CloudHistoryCache cache, string tenant, string user)
    {
        response.Validate(tenant, user);
        // A slow read or deletion reply must never roll back a newer confirmed list.
        if (historyRevision is { } revision && response.Revision < revision) return false;
        if (response.NotModified)
        {
            if (response.Revision != historyRevision) throw new AccountException("Die Anrufliste konnte nicht abgeglichen werden.");
            return true;
        }
        var saved = cache.Save(response);
        history.Clear(); history.AddRange(saved.Validate(tenant, user)); historyRevision = response.Revision;
        return true;
    }

    private async void RefreshHistoryClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await SyncHistoryAsync();
    private async Task RefreshHistoryAfterCallAsync()
    {
        var lifetime = historyLifetime;
        if (lifetime is null) return;
        try { await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token); if (ReferenceEquals(lifetime, historyLifetime)) await SyncHistoryAsync(); }
        catch (OperationCanceledException) { }
    }
}
