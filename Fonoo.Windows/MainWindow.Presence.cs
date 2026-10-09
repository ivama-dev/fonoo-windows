using Fonoo.Windows.Accounts;
using Fonoo.Windows.Desktop;
using Fonoo.Windows.Telephony;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? presenceTimer;
    private DispatcherQueueTimer? presenceExpiryTimer;
    private CancellationTokenSource? presenceLifetime;
    private TeamPresenceState? phonePresence;
    private bool presencePolling;
    private bool presenceForeground;
    private int presenceSession;

    private void InitializePresence()
    {
        presenceTimer = DispatcherQueue.CreateTimer(); presenceTimer.Interval = TimeSpan.FromSeconds(3);
        presenceTimer.Tick += async (_, _) => await RefreshPresenceAsync();
        presenceExpiryTimer = DispatcherQueue.CreateTimer(); presenceExpiryTimer.Interval = TimeSpan.FromSeconds(1);
        presenceExpiryTimer.Tick += (_, _) => { if (phonePresence?.Expire() == true) RenderPresence(); };
        Activated += (_, args) =>
        {
            presenceForeground = args.WindowActivationState != WindowActivationState.Deactivated;
            RefreshPresenceLifecycle();
        };
    }

    private void StartPresenceScope()
    {
        StopPresence();
        phonePresence = new(activeTenant, accountId); presenceSession = account.SessionGeneration;
        RenderPresence(); RefreshPresenceLifecycle();
    }

    private void StopPresence()
    {
        presenceLifetime?.Cancel(); presenceLifetime?.Dispose(); presenceLifetime = null;
        presenceTimer?.Stop(); presenceExpiryTimer?.Stop(); phonePresence = null;
        RenderPresence();
    }

    private void RefreshPresenceLifecycle()
    {
        if (previewMode) return;
        CheckWindowsContactAccess();
        var active = !closed && !closingWithEngine && presenceForeground && AppWindow.IsVisible &&
            AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized } &&
            account.SignedIn && activeTenant.Length > 0 && accountId.Length > 0;
        if (!active)
        {
            presenceLifetime?.Cancel(); presenceLifetime?.Dispose(); presenceLifetime = null;
            presenceTimer?.Stop(); presenceExpiryTimer?.Stop(); phonePresence?.Clear(); RenderPresence();
            return;
        }
        if (phonePresence is null || presenceSession != account.SessionGeneration)
        {
            phonePresence = new(activeTenant, accountId); presenceSession = account.SessionGeneration;
            RenderPresence();
        }
        if (presenceLifetime is not null) return;
        presenceLifetime = new(); presenceTimer?.Start(); presenceExpiryTimer?.Start();
        _ = RefreshPresenceAsync();
    }

    private async Task RefreshPresenceAsync()
    {
        if (previewMode || presencePolling || presenceLifetime is null || phonePresence is null || !account.SignedIn) return;
        var lifetime = presenceLifetime; var state = phonePresence; var ct = lifetime.Token;
        var tenant = activeTenant; var user = accountId; var session = account.SessionGeneration;
        bool Current() => !closed && !closingWithEngine && !ct.IsCancellationRequested &&
            ReferenceEquals(lifetime, presenceLifetime) && ReferenceEquals(state, phonePresence) &&
            tenant == activeTenant && user == accountId && session == account.SessionGeneration;
        presencePolling = true;
        try
        {
            // Favorites also need the directory to resolve an exact internal identity.
            // This initial read uses the same authenticated client, without touching SIP.
            if (teamSnapshot is null && operation is null)
            {
                try
                {
                    var directory = await account.GetTeamAsync(tenant, user, ct);
                    if (!Current()) return;
                    teamSnapshot = directory; TeamCompany.Text = directory.TenantName; RenderTeam();
                }
                catch (AccountException ex) when (!ex.SessionExpired) { /* Presence may be available before the directory. */ }
            }
            var next = await account.GetPresenceAsync(tenant, user, ct);
            if (Current() && state.TryApply(next)) RenderPresence();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (AccountException ex) when (ex.SessionExpired)
        {
            if (!closed && ReferenceEquals(lifetime, presenceLifetime) && tenant == activeTenant && user == accountId && !account.SignedIn)
                ResetSession();
            else if (Current()) { state.Clear(); RenderPresence(); }
        }
        catch { if (Current()) { state.Clear(); RenderPresence(); } }
        finally { presencePolling = false; }
    }

    private void RenderFavoriteRows()
    {
        FavoritesGrid.ItemsSource = favorites.Select(f => new FavoriteListItem(f)).ToArray();
        RenderPresence();
    }

    private void RenderPresence()
    {
        if (TeamList is null || FavoritesGrid is null) return;
        var current = phonePresence?.Current;
        var allowedOutlook = previewMode || (microsoftOwner == accountId && microsoftCompany == activeTenant && microsoftPolicy is not null);
        var directory = contacts.Concat(allowedOutlook ? outlookContacts : []).ToArray();
        if (TeamList.ItemsSource is TeamListItem[] rows)
            foreach (var row in rows) row.Phone.Update(teamSnapshot?.TenantId == activeTenant && teamSnapshot.SelfUserId == accountId
                ? TeamPresenceResolver.Display(row.Member.Id, current, teamSnapshot, directory, favorites) : PhonePresenceDisplay.Unknown);
        if (FavoritesGrid.ItemsSource is FavoriteListItem[] saved)
            foreach (var row in saved)
            {
                var member = TeamPresenceResolver.FavoriteMember(row.Favorite, teamSnapshot, activeTenant, accountId);
                // A scoped internal favorite still shows Unknown while its directory is loading.
                var scoped = row.Favorite.Id.StartsWith("team:" + activeTenant + ":", StringComparison.Ordinal);
                row.Phone.Update(member is not null ? TeamPresenceResolver.Display(member.Id, current, teamSnapshot, directory, favorites)
                    : scoped ? PhonePresenceDisplay.Unknown : null);
            }
        TeamPresenceStatus.Text = current is null ? "Telefonstatus unbekannt · keine aktuelle Rückmeldung"
            : "Telefonstatus live · Anrufe über Fonoo";
        if (TeamList.SelectedItem is TeamListItem selected)
            TeamDetailPresence.Text = selected.Phone.Text + (selected.Phone.Detail.Length == 0 ? "" : " · " + selected.Phone.Detail);
    }

    private void PreviewPresenceClicked(object sender, RoutedEventArgs e)
    {
#if FONOO_DESIGN_PREVIEW
        if (previewMode) SetPresencePreview();
#endif
    }
}
