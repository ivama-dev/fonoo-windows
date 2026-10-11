using Fonoo.Windows.Telephony;

namespace Fonoo.Windows.Accounts;

public sealed partial class AccountClient
{
    private static readonly System.Text.Json.JsonSerializerOptions FavoritesJson = new(Json)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public Task<CloudFavoriteSnapshot> ReadFavoritesAsync(CancellationToken ct)
        => RequestAsync<CloudFavoriteSnapshot>("cloud/favorites", null, ct);
    public async Task<CloudFavoriteSnapshot> WriteFavoritesAsync(long revision, CloudFavoriteLibrary library, CancellationToken ct)
    {
        library.Validate();
        try { return await RequestAsync<CloudFavoriteSnapshot>("cloud/favorites", new { expected_revision = revision, library }, ct); }
        catch (AccountException ex) when (ex.StatusCode == 409) { throw new FavoriteSyncConflict(); }
    }
}
