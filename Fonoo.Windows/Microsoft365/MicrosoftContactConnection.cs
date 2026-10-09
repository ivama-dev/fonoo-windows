using Fonoo.Windows.Telephony;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fonoo.Windows.Microsoft365;

public sealed record MicrosoftContactSnapshot(string FonooAccountId, string ClientId, string MicrosoftAccountId, string Username, DateTimeOffset? SyncedAt, ContactRow[] Contacts, string TenantId = "");
public interface IMicrosoftContactStore
{
    MicrosoftContactSnapshot? Load();
    void Save(MicrosoftContactSnapshot snapshot);
    void Clear();
}

public sealed class ProtectedMicrosoftContactStore(string directory, string fonooAccountId, string clientId) : IMicrosoftContactStore
{
    private readonly string path = Path.Combine(directory, "contacts.protected");
    public static string ScopedDirectory(string root, string owner, string clientId)
    {
        if (string.IsNullOrWhiteSpace(owner) || !Guid.TryParse(clientId, out var id) || id == Guid.Empty) throw new ArgumentException("Invalid account scope.");
        return Path.Combine(root, "Microsoft365", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner + "\n" + id.ToString()))).ToLowerInvariant());
    }
    private MicrosoftContactSnapshot Validate(MicrosoftContactSnapshot value)
    {
        if (value.FonooAccountId != fonooAccountId || value.ClientId != clientId || string.IsNullOrWhiteSpace(value.MicrosoftAccountId) || value.MicrosoftAccountId.Length > 512 ||
            value.Username is null || value.Username.Length > 512 || value.Contacts is null || value.Contacts.Length > 10000 ||
            value.Contacts.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 4096 || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 160 || c.Kind is null || c.Kind.Length > 80 || !DialNumber.IsValid(c.Number)))
            throw new InvalidDataException("Invalid Microsoft contact cache.");
        return value;
    }
    public MicrosoftContactSnapshot? Load()
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8_000_000) throw new InvalidDataException();
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return Validate(JsonSerializer.Deserialize<MicrosoftContactSnapshot>(plain) ?? throw new InvalidDataException()); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Save(MicrosoftContactSnapshot value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(Validate(value)); byte[] cipher;
        try { if (plain.Length > 7_000_000) throw new InvalidDataException(); cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, cipher); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Clear() { if (File.Exists(path)) File.Delete(path); }
}

public sealed class MicrosoftContactConnection(IMicrosoftIdentity identity, GraphContactsReader reader, IMicrosoftContactStore store, string owner, string clientId, string expectedTenant = "") : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private volatile MicrosoftContactSnapshot? snapshot;
    private int generation;
    public MicrosoftContactSnapshot? Snapshot => snapshot;
    public bool SignInRequired { get; private set; }
    public async Task RestoreAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var loaded = await Task.Run(store.Load, linked.Token).ConfigureAwait(false);
            if (loaded is not null && expectedTenant.Length > 0 && !string.Equals(loaded.TenantId, expectedTenant, StringComparison.OrdinalIgnoreCase)) { store.Clear(); loaded = null; }
            lock (stateGate) { linked.Token.ThrowIfCancellationRequested(); snapshot = loaded; }
        }
        finally { gate.Release(); }
    }
    public async Task ConnectAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await gate.WaitAsync(linked.Token);
        var started = generation;
        try
        {
            var session = await identity.ConnectAsync(linked.Token);
            ValidateTenant(session);
            var contacts = await ReadAsync(session, linked.Token).ConfigureAwait(false);
            var next = new MicrosoftContactSnapshot(owner, clientId, session.AccountId, session.Username, DateTimeOffset.UtcNow, contacts, session.TenantId);
            Commit(next, started, linked.Token);
        }
        finally { gate.Release(); }
    }
    public async Task RefreshAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        var started = generation;
        try
        {
            var current = snapshot ?? throw new MicrosoftConnectionException("Bitte verbinde zuerst dein Microsoft-Konto.", true);
            var session = await identity.AcquireSilentAsync(current.MicrosoftAccountId, false, linked.Token).ConfigureAwait(false);
            ValidateTenant(session);
            if (session.AccountId != current.MicrosoftAccountId) throw new MicrosoftConnectionException("Das Microsoft-Konto hat sich geändert. Bitte erneut verbinden.", true);
            var rows = await ReadAsync(session, linked.Token).ConfigureAwait(false);
            var next = current with { Contacts = rows, SyncedAt = DateTimeOffset.UtcNow };
            Commit(next, started, linked.Token);
        }
        catch (MicrosoftConnectionException ex) when (ex.SignInRequired)
        {
            lock (stateGate)
            {
                if (started == generation && !lifetime.IsCancellationRequested)
                {
                    SignInRequired = true;
                    if (snapshot is { } old) { var cleared = old with { Contacts = [], SyncedAt = null }; store.Save(cleared); snapshot = cleared; }
                }
            }
            throw;
        }
        finally { gate.Release(); }
    }
    private Task<ContactRow[]> ReadAsync(MicrosoftSession session, CancellationToken ct) => reader.ReadAsync(async (force, token) =>
    {
        if (!force) return session.AccessToken;
        var refreshed = await identity.AcquireSilentAsync(session.AccountId, true, token).ConfigureAwait(false);
        ValidateTenant(refreshed);
        if (refreshed.AccountId != session.AccountId) throw new MicrosoftConnectionException("Microsoft hat eine andere Anmeldung bestätigt. Bitte erneut verbinden.", true);
        session = refreshed; return refreshed.AccessToken;
    }, ct);
    private void ValidateTenant(MicrosoftSession session)
    {
        if (expectedTenant.Length > 0 && !string.Equals(session.TenantId, expectedTenant, StringComparison.OrdinalIgnoreCase))
            throw new MicrosoftConnectionException("Dieses Microsoft-Konto gehört nicht zur verbundenen Firma. Bitte verwende dein Firmenkonto.", true);
    }
    private void Commit(MicrosoftContactSnapshot next, int started, CancellationToken ct)
    {
        lock (stateGate)
        {
            ct.ThrowIfCancellationRequested(); if (started != generation) throw new OperationCanceledException();
            store.Save(next); snapshot = next; SignInRequired = false;
        }
    }
    public async Task DisconnectAsync()
    {
        Exception? storageError = null; string accountId;
        lock (stateGate)
        {
            generation++; accountId = snapshot?.MicrosoftAccountId ?? ""; snapshot = null; SignInRequired = false;
            try { store.Clear(); } catch (Exception ex) { storageError = ex; }
        }
        lifetime.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        try { await identity.RemoveAsync(accountId).ConfigureAwait(false); }
        finally { gate.Release(); }
        if (storageError is not null) throw new MicrosoftConnectionException("Die gespeicherten Microsoft-Kontakte konnten nicht vollständig entfernt werden.");
    }
    public void Dispose() { lock (stateGate) { generation++; } lifetime.Cancel(); identity.Dispose(); reader.Dispose(); }
}
