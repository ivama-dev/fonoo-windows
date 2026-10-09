using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fonoo.Windows.Accounts;

public sealed class DeviceSession
{
    public string Token { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public long ExpiresAt { get; set; }
    public long DeviceExpiresAt { get; set; }
    public string? PendingRequestId { get; set; }
    public bool AccessOnly { get; set; }
    public DeviceSession Validate(string device, string? user = null)
    {
        if (DeviceId != device || !Guid.TryParse(DeviceId, out _) || (user is not null && UserId != user) || string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(Email) ||
            Token.Length is < 32 or > 128 ||
            (AccessOnly ? RefreshToken.Length != 0 || PendingRequestId is not null : RefreshToken.Length is < 32 or > 128 || ExpiresAt <= 0 || ExpiresAt > DeviceExpiresAt) ||
            (PendingRequestId is not null && !Guid.TryParse(PendingRequestId, out _)))
            throw new AccountException("Die Geräteanmeldung konnte nicht sicher übernommen werden.");
        return this;
    }
    public override string ToString() => "Fonoo device session (protected)";
}
public interface IDeviceSessionStore
{
    DeviceSession? Load();
    void Save(DeviceSession session);
    void Clear();
}

// DPAPI binds the encrypted refresh credential to this Windows user and computer.
// Neither the account password nor provisioned SIP credentials are persisted.
public sealed class ProtectedSessionStore(string directory) : IDeviceSessionStore
{
    private readonly string path = Path.Combine(directory, "session.protected");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public DeviceSession? Load()
    {
        if (!File.Exists(path)) return null;
        var plain = Transform(File.ReadAllBytes(path), false);
        try { return JsonSerializer.Deserialize<DeviceSession>(plain, Json) ?? throw new InvalidDataException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Save(DeviceSession session)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(session, Json);
        byte[] cipher;
        try { cipher = Transform(plain, true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, cipher); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Clear() { if (File.Exists(path)) File.Delete(path); }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static byte[] Transform(byte[] input, bool encrypt)
    {
        if (input.Length is < 1 or > 65536) throw new InvalidDataException();
        var pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
        Blob result = default;
        try
        {
            var data = new Blob { Length = input.Length, Data = pinned.AddrOfPinnedObject() };
            var ok = encrypt ? CryptProtectData(ref data, "Fonoo", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result)
                : CryptUnprotectData(ref data, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result);
            if (!ok) throw new CryptographicException("Windows konnte die Geräteanmeldung nicht schützen.");
            var bytes = new byte[result.Length]; Marshal.Copy(result.Data, bytes, 0, bytes.Length); return bytes;
        }
        finally { if (result.Data != IntPtr.Zero) LocalFree(result.Data); pinned.Free(); }
    }
}

public sealed partial class AccountClient
{
    private IDeviceSessionStore? sessionStore;
    private string sessionDeviceId = "";
    private DeviceSession? remembered;
    private int loginGeneration;
    private readonly SemaphoreSlim renewal = new(1, 1);
    public bool Remembered => remembered is not null;
    public bool CanRenew => remembered is { AccessOnly: false };
    public AccountClient(IDeviceSessionStore store, string deviceId) : this() { sessionStore = store; sessionDeviceId = deviceId; }
    public AccountClient(HttpMessageHandler handler, IDeviceSessionStore store, string deviceId) : this(handler) { sessionStore = store; sessionDeviceId = deviceId; }
    public bool Restore()
    {
        var saved = sessionStore?.Load();
        if (saved is null) return false;
        saved.Validate(sessionDeviceId);
        if (!saved.AccessOnly && saved.DeviceExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) { SignOut(); return false; }
        remembered = saved; SetToken(saved.Token); return true;
    }
    public void RememberAccessSession(AccountSummary summary)
    {
        if (remembered is not null || sessionStore is null || !SignedIn) return;
        var saved = new DeviceSession { Token = token, DeviceId = sessionDeviceId, UserId = summary.Id, Email = summary.Email, AccessOnly = true };
        saved.Validate(sessionDeviceId); sessionStore.Save(saved); remembered = saved;
    }
    public async Task VerifyDeviceAsync(string challenge, string code, CancellationToken ct)
    {
        if (sessionStore is null) throw new InvalidOperationException();
        var generation = loginGeneration;
        DeviceSession result;
        try { result = await RequestAsync<DeviceSession>("account/device/verify", new { challenge, code, device_id = sessionDeviceId, device_name = Environment.MachineName[..Math.Min(Environment.MachineName.Length, 80)] }, ct, false); }
        catch (AccountException ex) when (ex.StatusCode == 404)
        {
            // An older service must still permit ordinary OTP login. A missing route
            // did not consume the challenge; never retry an invalid/expired code here.
            await VerifyAsync(challenge, code, ct); return;
        }
        if (generation != loginGeneration) throw new OperationCanceledException();
        result.Validate(sessionDeviceId);
        sessionStore.Save(result); remembered = result; SetToken(result.Token);
    }
    private async Task RenewAsync(CancellationToken ct, string oldToken)
    {
        var generation = loginGeneration;
        await renewal.WaitAsync(ct);
        try
        {
            if (generation != loginGeneration) throw new OperationCanceledException();
            if (token != oldToken) return; // Another request already renewed this session.
            var saved = remembered ?? throw new AccountException("Bitte melde dich erneut an.", true);
            saved.PendingRequestId ??= Guid.NewGuid().ToString();
            sessionStore!.Save(saved); // Persist before HTTP; retries reuse the same rotation request.
            var next = await SendRequestAsync<DeviceSession>("account/device/refresh", new { device_id = sessionDeviceId, refresh_token = saved.RefreshToken, request_id = saved.PendingRequestId }, ct, false);
            if (generation != loginGeneration) throw new OperationCanceledException();
            next.Validate(sessionDeviceId, saved.UserId);
            next.PendingRequestId = null;
            sessionStore.Save(next); remembered = next; token = next.Token;
        }
        catch (AccountException ex) when (ex.SessionExpired && generation == loginGeneration) { SignOut(); throw; }
        finally { renewal.Release(); }
    }
    public async Task SetPasswordAsync(string password, CancellationToken ct) => _ = await RequestAsync<AccountSummary>("account/password/set", new { password }, ct);
    public async Task RevokeDeviceAsync(CancellationToken ct)
    {
        var savedToken = token;
        var body = new { device_id = remembered?.DeviceId ?? sessionDeviceId };
        SignOut(); // Local sign-out is immediate, even if revocation cannot reach the server.
        if (savedToken.Length == 0) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, "account/logout"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", savedToken);
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json));
        request.Content.Headers.ContentType = new("application/json");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new AccountException("Du bist lokal abgemeldet. Die Geräteabmeldung im Kontodienst konnte nicht bestätigt werden.");
    }
}
