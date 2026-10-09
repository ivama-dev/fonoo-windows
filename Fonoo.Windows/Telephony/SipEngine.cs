using Fonoo.Windows.Accounts;
using Linphone;
using System.Collections.Concurrent;

namespace Fonoo.Windows.Telephony;

public sealed record SipSnapshot(bool Registered, bool InCall, bool Incoming, bool Muted, string Status, string Peer,
    bool Active = false, bool Held = false, bool RemoteHeld = false, bool HoldPending = false,
    bool Consulting = false, bool ConsultationActive = false, bool TransferPending = false, string OriginalPeer = "", bool DoNotDisturb = false, string CallId = "")
{
    public bool CanTransfer => InCall && (Active || Held) && !RemoteHeld && !HoldPending && !Consulting && !TransferPending;
}

// Core, calls, accounts and all SDK callbacks belong exclusively to this worker thread.
public sealed partial class SipEngine
{
    private static readonly object initializeGate = new();
    private readonly BlockingCollection<(Action Action, TaskCompletionSource Completion)> commands = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool stopping;
    private Core? core;
    private Call? call;
    private bool registered;
    private bool incoming;
    private bool muted;
    private string domain = "";
    private string peer = "";
    public event Action<SipSnapshot>? Changed;
    public Task Ready => ready.Task;

    public SipEngine(SipConfiguration? configuration)
    {
        var worker = new Thread(() => Run(configuration)) { Name = "Fonoo.SIP", IsBackground = true };
        worker.Start();
    }

    private void Publish(string status)
    {
        callStatus = status;
        Changed?.Invoke(new(registered, call is not null, incoming, muted, status,
            consultation is not null ? sessions.GetValueOrDefault(consultation)?.Number ?? peer : peer,
            Active, Held, ControlledCall?.State == CallState.PausedByRemote, holdPending,
            consultation is not null || consultationTarget is not null, consultation?.State == CallState.StreamsRunning,
            transferPending, peer, doNotDisturb, ControlledCall is { } controlled ? sessions.GetValueOrDefault(controlled)?.Id ?? "" : ""));
    }

    private void Run(SipConfiguration? configuration)
    {
        try
        {
            // Factory/resource and SRTP initialization are process-wide native state.
            lock (initializeGate) Initialize(configuration);
            ready.TrySetResult();
            while (!stopping)
            {
                if (commands.TryTake(out var command, 20))
                {
                    try { command.Action(); command.Completion.TrySetResult(); }
                    catch { command.Completion.TrySetException(new InvalidOperationException("Die Telefonie-Aktion konnte nicht ausgeführt werden.")); }
                }
                core!.Iterate();
                CheckActionDeadline();
                if (audioDevicesChanged)
                {
                    audioDevicesChanged = false;
                    StopAudioTest();
                    try { ApplyAudioPreferences(); } catch { audioNotice = "Audiogeräte konnten nicht automatisch gewechselt werden."; }
                }
                if (audioTestUntil != 0 && Environment.TickCount64 >= audioTestUntil) StopAudioTest();
            }
        }
        catch
        {
            ready.TrySetException(new InvalidOperationException("Die Telefonie-Engine konnte nicht gestartet werden. Bitte prüfe die Installation."));
            registered = false;
            call = consultation = null; incoming = muted = false;
            Publish("Telefonie-Engine nicht verfügbar");
        }
        finally
        {
            stopping = true;
            while (commands.TryTake(out var pending)) pending.Completion.TrySetCanceled();
            try
            {
                if (core is not null)
                {
                    StopAudioTest();
                    foreach (var session in sessions.Values.ToArray()) Archive(session);
                    sessions.Clear();
                    core.Listener = null;
                    core.TerminateAllCalls();
                    core.ClearAccounts();
                    core.ClearAllAuthInfo();
                    core.Stop();
                    core = null;
                }
            }
            catch { /* Shutdown must still complete if native cleanup fails. */ }
            call = null;
            stopped.TrySetResult();
        }
    }

    private void Initialize(SipConfiguration? c)
    {
        var resources = Path.Combine(AppContext.BaseDirectory, "share");
        var roots = Path.Combine(resources, "linphone", "rootca.pem");
        if (!File.Exists(Path.Combine(resources, "belr", "grammars", "vcard_grammar.belr")) ||
            !File.Exists(roots) || !File.ReadAllText(roots).Contains("-----BEGIN CERTIFICATE-----"))
            throw new InvalidOperationException("SDK resources missing");
        var factory = Factory.Instance;
        factory.EnableLogCollection(LogCollectionState.Disabled);
        LoggingService.Instance.LogLevel = LogLevel.Fatal;
        factory.TopResourcesDir = resources;
        factory.MspluginsDir = Path.Combine(AppContext.BaseDirectory, "mediastreamer", "plugins");
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fonoo", "Windows", "engine");
        Directory.CreateDirectory(data);
        factory.DataDir = factory.ConfigDir = factory.CacheDir = data;
        var config = factory.CreateConfigFromString("[sip]\nstore_auth_info=0\nsip_port=0\nsip_tcp_port=0\nsip_tls_port=-1\n[storage]\nuri=null\ncall_logs_db_uri=null\n[video]\nenabled=0\ncapture=0\ndisplay=0\n");
        core = factory.CreateCoreWithConfig(config, IntPtr.Zero);
        core.AutoIterateEnabled = false;
        core.VideoCaptureEnabled = core.VideoDisplayEnabled = core.VideoPreviewEnabled = false;
        core.MaxCalls = 2; // Original call plus an explicitly requested consultation only.
        core.AudioAdaptiveJittcompEnabled = true;
        core.AudioJittcomp = 60;
        config.SetInt("rtp", "jitter_buffer_max_size", 200);
        core.AdaptiveRateControlEnabled = true;
        core.AgcEnabled = false;
        core.EchoLimiterEnabled = false;
        foreach (var payload in core.AudioPayloadTypes.Where(p => p.MimeType.Equals("opus", StringComparison.OrdinalIgnoreCase)))
            payload.RecvFmtp = "useinbandfec=1;stereo=0;sprop-stereo=0;usedtx=0;maxaveragebitrate=40000";
        core.Ring = Path.Combine(AppContext.BaseDirectory, "share", "sounds", "linphone", "rings", "oldphone-mono.wav");
        core.IncTimeout = 45;
        core.SetUserAgent("Fonoo-Windows", "0.1");
        core.RootCa = roots;
        core.VerifyServerCertificates(true);
        core.VerifyServerCn(true);
        core.MediaEncryption = MediaEncryption.SRTP;
        core.MediaEncryptionMandatory = true;
        core.UseRfc2833ForDtmf = true;
        core.UseInfoForDtmf = false;
        core.Listener.OnAccountRegistrationStateChanged = (_, _, state, _) =>
        {
            registered = state == RegistrationState.Ok;
            if (call is null) Publish(state switch
            {
                RegistrationState.Ok => doNotDisturb ? "Nicht stören" : "Bereit zum Telefonieren",
                RegistrationState.Progress or RegistrationState.Refreshing => "Telefonie wird verbunden …",
                RegistrationState.Failed => "SIP-Anmeldung fehlgeschlagen · bitte erneut verbinden",
                _ => "Telefonie nicht verbunden"
            });
        };
        core.Listener.OnAudioDevicesListUpdated = _ => audioDevicesChanged = true;
        // Native callbacks must never unwind through the C ABI.
        core.Listener.OnCallStateChanged = (_, native, state, _) =>
        {
            #if FONOO_CALL_DIAGNOSTICS
            if (TraceEnabled) Console.WriteLine($"{TraceLabel}: {state}; replaces={native.ReplacedCall is not null}; transferer={native.TransfererCall is not null}");
            if (TraceEnabled && state == CallState.Error) Console.WriteLine($"{TraceLabel}: failure {native.ErrorInfo?.ProtocolCode}");
#endif
            try { HandleCallState(native, state); }
            catch { Publish("Gesprächsstatus konnte nicht vollständig aktualisiert werden. Du kannst auflegen."); }
        };
        core.Listener.OnTransferStateChanged = (_, native, state) =>
        {
            #if FONOO_CALL_DIAGNOSTICS
            if (TraceEnabled) Console.WriteLine($"{TraceLabel}: transfer {state}");
#endif
            try { HandleTransfer(native, state); } catch { Publish("Weiterleitung konnte nicht abgeschlossen werden."); }
        };
        if (c is null)
        {
            var transports = factory.CreateTransports();
            transports.UdpPort = transports.TcpPort = transports.TlsPort = 0;
            core.Transports = transports;
            core.NetworkReachable = false;
        }
        core.EchoCancellationEnabled = true;
        core.Start();
        LoadAudioPreferences();
        ApplyAudioPreferences();
        if (c is null) return;
        if (!core.MediaEncryptionSupported(MediaEncryption.SRTP)) throw new InvalidOperationException("SRTP unavailable");
        domain = c.Domain;
        var nat = core.CreateNatPolicy();
        nat.IceEnabled = c.IceEnabled;
        nat.StunEnabled = c.IceEnabled && !string.IsNullOrWhiteSpace(c.StunServer);
        nat.TurnEnabled = c.TurnEnabled;
        if (c.TurnEnabled)
        {
            nat.StunServer = HostPort(c.TurnServer, c.TurnPort);
            nat.StunServerUsername = c.TurnUsername;
            nat.UdpTurnTransportEnabled = c.TurnTransport == "UDP";
            nat.TcpTurnTransportEnabled = c.TurnTransport == "TCP";
            nat.TlsTurnTransportEnabled = c.TurnTransport == "TLS";
            core.AddAuthInfo(factory.CreateAuthInfo(c.TurnUsername, null, c.TurnPassword, null, null, c.TurnServer));
        }
        else if (nat.StunEnabled) nat.StunServer = HostPort(c.StunServer, c.StunPort);
        core.ForcedIceRelayEnabled = c.ForceTurn;
        var identity = factory.CreateAddress($"sip:{domain}");
        identity.Username = c.Username;
        var server = factory.CreateAddress($"sip:{HostPort(c.Server, c.Port)}");
        server.Transport = TransportType.Tls;
        var parameters = core.CreateAccountParams();
        parameters.IdentityAddress = identity;
        parameters.ServerAddress = server;
        parameters.RoutesAddresses = new[] { server };
        parameters.NatPolicy = nat;
        parameters.RegisterEnabled = true;
        parameters.Expires = 600;
        parameters.PushNotificationAllowed = parameters.RemotePushNotificationAllowed = false;
        core.AddAuthInfo(factory.CreateAuthInfo(c.Username, c.AuthenticationName, c.Password, null, null, c.Domain));
        var account = core.CreateAccount(parameters);
        core.AddAccount(account);
        core.DefaultAccount = account;
        Publish("Telefonie wird verbunden …");
    }

    private static string HostPort(string host, int port) => $"{(host.Contains(':') ? $"[{host}]" : host)}:{port}";
    private Task Enqueue(Action action)
    {
        if (stopping) return Task.FromException(new InvalidOperationException("Telefonie wird beendet."));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Add((action, completion));
        // Covers shutdown racing a producer after the worker drained its queue.
        if (stopping) completion.TrySetCanceled();
        return completion.Task;
    }
    public Task DialAsync(string number) => Enqueue(() =>
    {
        var target = DialNumber.Normalize(number);
        if (!registered || call is not null || target is null) throw new InvalidOperationException();
        StopAudioTest();
        peer = target;
        endingAll = false;
        core!.MicEnabled = true; muted = false;
        call = Invite(target);
    });
    public Task HangUpAsync() => Enqueue(EndAll);
    public Task AnswerAsync() => Enqueue(() =>
    {
        if (call is null || !incoming) return;
        var parameters = core!.CreateCallParams(call);
        parameters.AudioEnabled = true; parameters.VideoEnabled = false; parameters.MediaEncryption = MediaEncryption.SRTP;
        core.MicEnabled = true;
        call.AcceptWithParams(parameters);
    });
    public Task MuteAsync(bool value) => Enqueue(() =>
    {
        if (call is null) return;
        core!.MicEnabled = !value; muted = value; Publish(callStatus);
    });
    public Task DtmfAsync(char digit) => Enqueue(() =>
    {
        if (ControlledCall?.State == CallState.StreamsRunning && "0123456789*#".Contains(digit)) ControlledCall.SendDtmf((sbyte)digit);
    });
    public Task StopAsync() { stopping = true; return stopped.Task; }
}

