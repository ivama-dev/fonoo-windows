using Fonoo.Windows.Accounts;
using Fonoo.Windows.Telephony;
using Microsoft.UI.Dispatching;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private CloudFavoriteCache? cloudFavorites;
    private CloudFavoriteSync? favoriteSync;
    private CloudFavoriteLibrary? presentedFavorites;
    private CancellationTokenSource? favoriteLifetime;
    private DispatcherQueueTimer? favoriteTimer;

    private void InitializeFavoriteSync()
    {
        favoriteTimer = DispatcherQueue.CreateTimer(); favoriteTimer.Interval = TimeSpan.FromSeconds(5);
        favoriteTimer.Tick += async (_, _) => await SyncFavoritesAsync();
        Activated += async (_, e) => { if (e.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated) await SyncFavoritesAsync(); };
    }
    private void StartFavoriteSync(FavoriteLibrary legacy, string source)
    {
        StopFavoriteSync();
        var owner = accountId;
        var cache = new CloudFavoriteCache(UserDataDirectory, owner); cache.Load();
        FavoriteCloudBridge.ImportOnce(cache, legacy, source);
        cloudFavorites = cache; favoriteLifetime = new();
        favoriteSync = new CloudFavoriteSync(cache, account.ReadFavoritesAsync, account.WriteFavoritesAsync);
        favoriteSync.Updated += status =>
        {
            if (closed || owner != accountId || !ReferenceEquals(cache, cloudFavorites)) return;
            FavoriteSyncStatus.Text = status;
            if (!modalOpen && draggedFavoriteGroup is null && draggedFavorite is null) ApplyCloudFavorites();
        };
        ApplyCloudFavorites(); FavoriteSyncStatus.Text = "Favoriten werden automatisch synchronisiert …";
        favoriteTimer?.Start(); _ = SyncFavoritesAsync();
    }
    private void StopFavoriteSync()
    {
        favoriteLifetime?.Cancel(); favoriteLifetime?.Dispose(); favoriteLifetime = null;
        favoriteTimer?.Stop(); cloudFavorites = null; favoriteSync = null; presentedFavorites = null;
        if (FavoriteSyncStatus is not null) FavoriteSyncStatus.Text = "Favoriten gehören zu deinem fonoo-Konto.";
    }
    private void ApplyCloudFavorites()
    {
        if (cloudFavorites is null) return;
        var library = cloudFavorites.State.Library;
        if (library.Same(presentedFavorites)) return;
        var local = FavoriteCloudBridge.ToLocal(library);
        favorites.Clear(); foreach (var favorite in local.Favorites) favorites.Add(favorite);
        favoriteGroups.Clear(); favoriteGroups.AddRange(local.Groups); presentedFavorites = library;
        RenderFavoriteRows(); UpdateContactFavorites(); RefreshPeerDisplay();
    }
    private async Task SyncFavoritesAsync()
    {
        if (previewMode || closed || closingWithEngine || !account.SignedIn || favoriteSync is null || favoriteLifetime is null ||
            modalOpen || draggedFavoriteGroup is not null || draggedFavorite is not null) return;
        var worker = favoriteSync; var lifetime = favoriteLifetime;
        try { await worker.RefreshAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (AccountException ex) when (ex.SessionExpired)
        { if (!closed && ReferenceEquals(worker, favoriteSync)) ResetSession(); }
        catch
        {
            if (!closed && ReferenceEquals(worker, favoriteSync))
                FavoriteSyncStatus.Text = "Lokal gespeichert · automatischer Abgleich wird wiederholt";
        }
    }
}
