using Fonoo.Windows.Telephony;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fonoo.Windows.Microsoft365;

public sealed class GraphContactsReader : IDisposable
{
    private const int MaximumRows = 10000, MaximumPages = 500, MaximumFolders = 1000;
    private readonly HttpClient http;
    private static readonly Regex ContactPath = new(@"^/v1\.0/me/(contacts|contactFolders(?:/[^/]+/childFolders)*|contactFolders(?:/[^/]+/childFolders)*/[^/]+/(contacts|childFolders))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public GraphContactsReader(HttpMessageHandler? handler = null) => http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public static Uri ValidatedUri(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "graph.microsoft.com" || uri.Port != 443 ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || address.Length > 16000 || !ContactPath.IsMatch(uri.AbsolutePath))
            throw new MicrosoftConnectionException("Microsoft hat eine ungültige Kontaktantwort geliefert. Bitte erneut aktualisieren.");
        return uri;
    }
    public async Task<ContactRow[]> ReadAsync(Func<bool, CancellationToken, Task<string>> acquireToken, CancellationToken ct)
    {
        var rows = new Dictionary<(string, string), ContactRow>();
        var visitedPages = new HashSet<string>(StringComparer.Ordinal);
        var folders = new Queue<(string Path, int Depth)>(); folders.Enqueue(("https://graph.microsoft.com/v1.0/me", 0));
        var folderCount = 0;
        await ContactsAsync("https://graph.microsoft.com/v1.0/me/contacts");
        while (folders.TryDequeue(out var parent))
        {
            ct.ThrowIfCancellationRequested();
            var list = parent.Depth == 0 ? parent.Path + "/contactFolders" : parent.Path + "/childFolders";
            await PagesAsync(list + "?$select=id&$top=100", async item =>
            {
                var id = String(item, "id");
                if (id.Length is < 1 or > 2048 || ++folderCount > MaximumFolders || parent.Depth >= 16) throw new MicrosoftConnectionException("Das Kontaktverzeichnis ist zu groß. Bitte reduziere die Kontaktordner.");
                var path = list + "/" + Uri.EscapeDataString(id);
                await ContactsAsync(path + "/contacts"); folders.Enqueue((path, parent.Depth + 1));
            });
        }
        return rows.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

        async Task ContactsAsync(string path) => await PagesAsync(path + "?$select=id,displayName,givenName,surname,businessPhones,homePhones,mobilePhone&$top=250", item =>
        {
            var id = String(item, "id");
            if (id.Length == 0) throw new MicrosoftConnectionException("Ein Outlook-Kontakt konnte nicht sicher gelesen werden.");
            var name = String(item, "displayName").Trim();
            if (name.Length == 0) name = (String(item, "givenName") + " " + String(item, "surname")).Trim();
            Add(String(item, "mobilePhone"), "Outlook · Mobil");
            foreach (var phone in Phones(item, "businessPhones")) Add(phone, "Outlook · Arbeit");
            foreach (var phone in Phones(item, "homePhones")) Add(phone, "Outlook · Privat");
            return Task.CompletedTask;
            void Add(string raw, string kind)
            {
                if (raw.Length > 128 || ContactRow.NormalizeNumber(raw) is not { } number) return;
                rows.TryAdd((id, number), new("outlook:" + id + ":" + number, name.Length == 0 ? number : name[..Math.Min(160, name.Length)], number, kind));
                if (rows.Count > MaximumRows) throw new MicrosoftConnectionException("Outlook enthält mehr als 10.000 wählbare Rufnummern. Bitte reduziere dein Kontaktverzeichnis.");
            }
        });
        async Task PagesAsync(string address, Func<JsonElement, Task> item)
        {
            string? next = address;
            while (next is not null)
            {
                var uri = ValidatedUri(next);
                if (!visitedPages.Add(uri.AbsoluteUri) || visitedPages.Count > MaximumPages) throw new MicrosoftConnectionException("Die Kontaktantwort konnte nicht vollständig gelesen werden.");
                using var page = await PageAsync(uri, acquireToken, ct).ConfigureAwait(false);
                if (!page.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array) throw new MicrosoftConnectionException("Die Kontaktantwort ist unvollständig.");
                foreach (var value in values.EnumerateArray()) { ct.ThrowIfCancellationRequested(); await item(value).ConfigureAwait(false); }
                next = page.RootElement.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind != JsonValueKind.Null ? link.GetString() : null;
            }
        }
    }
    private async Task<JsonDocument> PageAsync(Uri uri, Func<bool, CancellationToken, Task<string>> acquireToken, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await acquireToken(attempt > 0, ct).ConfigureAwait(false));
            request.Headers.Accept.Add(new("application/json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new MicrosoftConnectionException("Microsoft hat den Kontaktzugriff nicht freigegeben. Bitte erneut anmelden oder die Freigabe deiner Firma prüfen.", true);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                throw new MicrosoftConnectionException("Microsoft begrenzt gerade die Kontaktabfrage. Bitte später erneut aktualisieren.");
            if (!response.IsSuccessStatusCode) throw new MicrosoftConnectionException("Outlook-Kontakte konnten nicht geladen werden. Bitte erneut aktualisieren.");
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var stream = await response.Content.ReadAsStreamAsync(readTimeout.Token).ConfigureAwait(false);
            using var body = new MemoryStream(); var buffer = new byte[16384]; int count;
            while ((count = await stream.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false)) > 0)
            { if (body.Length + count > 4_000_000) throw new MicrosoftConnectionException("Die Kontaktantwort ist zu groß."); await body.WriteAsync(buffer.AsMemory(0, count), readTimeout.Token).ConfigureAwait(false); }
            body.Position = 0;
            try { return await JsonDocument.ParseAsync(body, cancellationToken: readTimeout.Token).ConfigureAwait(false); }
            catch (JsonException) { throw new MicrosoftConnectionException("Microsoft hat keine lesbare Kontaktantwort geliefert."); }
        }
        throw new MicrosoftConnectionException("Bitte verbinde dein Microsoft-Konto erneut.", true);
    }
    private static string String(JsonElement value, string property) => value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    private static IEnumerable<string> Phones(JsonElement value, string property) => value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.Array
        ? field.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "").ToArray() : [];
    public void Dispose() => http.Dispose();
}
