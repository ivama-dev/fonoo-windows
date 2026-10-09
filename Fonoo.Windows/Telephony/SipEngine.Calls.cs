using Linphone;

namespace Fonoo.Windows.Telephony;

public sealed record CallActivity(double Level, DateTimeOffset? ConnectedAt, string CallId = "");

public sealed partial class SipEngine
{
    private sealed class Session(Call native, string number, bool received)
    {
        public Call Native { get; } = native;
        public string Number { get; } = number;
        public bool Received { get; } = received;
        public string Id { get; } = Guid.NewGuid().ToString();
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? Connected;
        public bool Declined;
    }
    private readonly Dictionary<Call, Session> sessions = [];
    private Call? consultation;
    private string? consultationTarget;
    private string? launchingConsultation;
    private bool resumeWhenHeld;
    private bool endingAll;
    private bool holdPending;
    private bool transferPending;
    private long actionDeadline;
    private bool doNotDisturb;
    private string callStatus = "";
    public event Action<CallHistoryEntry>? CallEnded;
    private Call? ControlledCall => consultation ?? call;
    private bool Active => ControlledCall?.State == CallState.StreamsRunning;
    private bool Held => call?.State == CallState.Paused;
    private bool CanTransfer => call is not null && sessions.ContainsKey(call) && sessions[call].Connected is not null &&
        consultation is null && consultationTarget is null && !holdPending && !transferPending && !endingAll &&
        call.State is CallState.StreamsRunning or CallState.Paused;

    private void HandleCallState(Call native, CallState state)
    {
        if (state is CallState.IncomingReceived or CallState.OutgoingInit)
        {
            StopAudioTest();
            if (!sessions.ContainsKey(native)) sessions.Add(native, new(native, native.RemoteAddress?.Username ?? "Unbekannt", state == CallState.IncomingReceived));
            if (state == CallState.IncomingReceived)
            {
                if (call is not null && ReferenceEquals(native.ReplacedCall, call) && consultation is null)
                {
                    // The SDK matched SIP Replaces against the existing dialog.
                    // This is continuation of that call, not a second ringing call.
                    call = native; incoming = false; peer = sessions[native].Number;
                    var parameters = core!.CreateCallParams(native); parameters.AudioEnabled = true; parameters.VideoEnabled = false; parameters.MediaEncryption = MediaEncryption.SRTP;
                    native.AcceptWithParams(parameters); Publish("Gespräch wird übernommen …"); return;
                }
                if (doNotDisturb || call is not null) { sessions[native].Declined = true; native.Decline(doNotDisturb ? Reason.DoNotDisturb : Reason.Busy); return; }
                call = native; incoming = true; muted = false; core!.MicEnabled = true;
                peer = sessions[native].Number; callStatus = "Eingehender Anruf";
            }
            else if (launchingConsultation is not null) { consultation = native; callStatus = "Rückfrage wird aufgebaut …"; }
            else if (call is not null && ReferenceEquals(native.TransfererCall, call)) { call = native; peer = sessions[native].Number; incoming = false; callStatus = "Weiterleitung wird aufgebaut …"; }
            else if (call is null) { call = native; peer = sessions[native].Number; incoming = false; callStatus = "Verbindung wird aufgebaut …"; }
        }
        if (state is CallState.End or CallState.Error)
        {
            if (sessions.Remove(native, out var finished)) Archive(finished);
            if (ReferenceEquals(native, consultation))
            {
                consultation = null;
                if (!endingAll && !transferPending) ResumeOriginal();
            }
            else if (ReferenceEquals(native, call))
            {
                if (!endingAll && native.TransfererCall is { } previous && sessions.ContainsKey(previous) && previous.State is not (CallState.End or CallState.Error or CallState.Released))
                {
                    call = previous; incoming = false; peer = sessions[previous].Number;
                    Publish("Weiterleitung fehlgeschlagen · ursprüngliches Gespräch bleibt verfügbar"); return;
                }
                endingAll = true;
                consultationTarget = launchingConsultation = null; resumeWhenHeld = false;
                call = null;
                var other = consultation; consultation = null;
                try { other?.Terminate(); } catch { }
                holdPending = transferPending = incoming = muted = false; actionDeadline = 0;
                core!.MicEnabled = true;
                endingAll = false;
            }
            else return;
            Publish(call is null ? (state == CallState.Error ? "Anruf fehlgeschlagen" : "Anruf beendet") : "Gespräch wird fortgesetzt …");
            return;
        }
        if (state == CallState.Released || (!ReferenceEquals(native, call) && !ReferenceEquals(native, consultation))) return;
        var controlled = ReferenceEquals(native, ControlledCall);
        switch (state)
        {
            case CallState.OutgoingProgress: if (controlled) callStatus = consultation is null ? "Verbindung wird aufgebaut …" : "Rückfrage wird aufgebaut …"; break;
            case CallState.OutgoingRinging: if (controlled) callStatus = "Es klingelt …"; break;
            case CallState.Connected:
                if (sessions.TryGetValue(native, out var connected)) connected.Connected ??= DateTimeOffset.UtcNow;
                if (controlled) { incoming = false; callStatus = "Audio wird aufgebaut …"; }
                break;
            case CallState.StreamsRunning:
                if (sessions.TryGetValue(native, out var running)) running.Connected ??= DateTimeOffset.UtcNow;
                if (ReferenceEquals(native, call)) { holdPending = false; if (!transferPending) actionDeadline = 0; }
                if (controlled) { incoming = false; callStatus = consultation is null ? "Gespräch läuft · SRTP" : "Rückfrage · ursprüngliches Gespräch gehalten"; }
                break;
            case CallState.Pausing: if (ReferenceEquals(native, call)) holdPending = true; if (controlled) callStatus = "Gespräch wird gehalten …"; break;
            case CallState.Resuming: if (ReferenceEquals(native, call)) holdPending = true; if (controlled) callStatus = "Gespräch wird fortgesetzt …"; break;
            case CallState.Paused:
                if (ReferenceEquals(native, call))
                {
                    holdPending = false; if (!transferPending) actionDeadline = 0;
                    if (resumeWhenHeld) { resumeWhenHeld = false; ResumeOriginal(); }
                    else if (consultationTarget is { } target) LaunchConsultation(target);
                }
                if (controlled && consultation is null) callStatus = "Gespräch gehalten";
                break;
            case CallState.PausedByRemote:
                if (ReferenceEquals(native, call)) { holdPending = false; if (!transferPending) actionDeadline = 0; }
                if (controlled) callStatus = "Gegenstelle hält das Gespräch"; break;
        }
        Publish(callStatus);
    }
    private void HandleTransfer(Call native, CallState state)
    {
        if (!ReferenceEquals(native, call) || !transferPending) return;
        if (state is CallState.Connected or CallState.StreamsRunning) { transferPending = false; actionDeadline = 0; EndAll(); }
        else if (state is CallState.Error or CallState.End)
        { transferPending = false; actionDeadline = 0; Publish("Weiterleitung fehlgeschlagen · Gespräch bleibt verfügbar"); }
    }
    private Call Invite(string number)
    {
        var address = Factory.Instance.CreateAddress($"sip:{domain}"); address.Username = number;
        var parameters = core!.CreateCallParams(null);
        parameters.AudioEnabled = true; parameters.VideoEnabled = false; parameters.MediaEncryption = MediaEncryption.SRTP;
        return core.InviteAddressWithParams(address, parameters) ?? throw new InvalidOperationException();
    }
    private void EndAll()
    {
        endingAll = true; consultationTarget = null; launchingConsultation = null; resumeWhenHeld = false; actionDeadline = 0;
        var original = call; var declineOriginal = incoming;
        foreach (var session in sessions.Values.ToArray())
        {
            if (session.Native.State is CallState.End or CallState.Error or CallState.Released) continue;
            if (declineOriginal && ReferenceEquals(session.Native, original)) { session.Declined = true; session.Native.Decline(Reason.Declined); }
            else session.Native.Terminate();
        }
    }
    private void Archive(Session finished) => CallEnded?.Invoke(new(finished.Id, finished.Number, finished.Received, finished.Started,
        finished.Connected is { } at ? (int)Math.Max(0, (DateTimeOffset.UtcNow - at).TotalSeconds) : 0,
        finished.Connected is not null ? "completed" : finished.Declined ? "declined" : finished.Received ? "missed" : "failed"));
    private void ResumeOriginal()
    {
        if (call?.State != CallState.Paused || endingAll) return;
        try { holdPending = true; core!.MicEnabled = !muted; call.Resume(); callStatus = "Gespräch wird fortgesetzt …"; }
        catch { holdPending = false; Publish("Fortsetzen fehlgeschlagen · bitte erneut versuchen"); }
    }
    private void LaunchConsultation(string target)
    {
        if (call?.State != CallState.Paused || consultation is not null || endingAll) return;
        consultationTarget = null; launchingConsultation = target;
        try { consultation = Invite(target); }
        catch { consultation = null; ResumeOriginal(); callStatus = "Rückfrage fehlgeschlagen · Gespräch wird fortgesetzt"; }
        finally { launchingConsultation = null; }
    }
    public Task HoldAsync(bool hold) => Enqueue(() =>
    {
        if (call is null || consultation is not null || consultationTarget is not null || holdPending || transferPending || endingAll ||
            (hold && call.State != CallState.StreamsRunning) || (!hold && !Held)) throw new InvalidOperationException();
        holdPending = true; actionDeadline = Environment.TickCount64 + 10000;
        try { if (hold) call.Pause(); else call.Resume(); }
        catch { holdPending = false; actionDeadline = 0; throw; }
    });
    public Task TransferAsync(string number, bool consult) => Enqueue(() =>
    {
        var targetNumber = DialNumber.Normalize(number);
        if (!CanTransfer || targetNumber is null) throw new InvalidOperationException();
        if (consult)
        {
            consultationTarget = targetNumber; actionDeadline = Environment.TickCount64 + 10000;
            try { if (Held) LaunchConsultation(targetNumber); else { holdPending = true; call!.Pause(); } }
            catch { consultationTarget = null; holdPending = false; actionDeadline = 0; throw; }
        }
        else
        {
            transferPending = true; actionDeadline = Environment.TickCount64 + 25000;
            try { var target = Factory.Instance.CreateAddress($"sip:{domain}"); target.Username = targetNumber; call!.TransferTo(target); }
            catch { transferPending = false; actionDeadline = 0; throw; }
            Publish("Weiterleitung wird bestätigt …");
        }
    });
    public Task CompleteTransferAsync() => Enqueue(() =>
    {
        if (!Held || consultation?.State != CallState.StreamsRunning || transferPending || endingAll) throw new InvalidOperationException();
#if FONOO_CALL_DIAGNOSTICS
        if (TraceEnabled) Console.WriteLine($"{TraceLabel}: consultation contact port {consultation.RemoteContactAddress?.Port}; target port {consultation.RemoteAddress?.Port}");
#endif
        transferPending = true; actionDeadline = Environment.TickCount64 + 25000;
        try { call!.TransferToAnother(consultation); }
        catch { transferPending = false; actionDeadline = 0; throw; }
        Publish("Weiterleitung wird bestätigt …");
    });
    public Task ReturnToOriginalAsync() => Enqueue(() =>
    {
        if (transferPending || endingAll) throw new InvalidOperationException();
        resumeWhenHeld = consultationTarget is not null && !Held;
        consultationTarget = null; actionDeadline = 0;
        if (consultation is { } other) other.Terminate(); else ResumeOriginal();
        Publish("Zurück zum Gespräch …");
    });
    public Task SetDoNotDisturbAsync(bool value) => Enqueue(() =>
    {
        doNotDisturb = value;
        core!.Ring = value ? null! : Path.Combine(AppContext.BaseDirectory, "share", "sounds", "linphone", "rings", "oldphone-mono.wav");
        if (call is null) Publish(value ? "Nicht stören" : registered ? "Bereit zum Telefonieren" : "Nicht verbunden");
    });
    public Task RefreshRegistrationAsync() => Enqueue(() => core!.RefreshRegisters());
    public async Task<CallActivity> GetActivityAsync()
    {
        CallActivity result = new(0, null);
        await Enqueue(() =>
        {
            var controlled = ControlledCall;
            if (controlled is null) return;
            var level = 0.0;
            if (controlled.State == CallState.StreamsRunning)
            {
                var db = Math.Max(muted ? -120 : controlled.RecordVolume, controlled.PlayVolume);
                if (double.IsFinite(db)) level = Math.Clamp((db + 60) / 60.0, 0, 1);
            }
            var session = sessions.GetValueOrDefault(controlled);
            result = new(level, session?.Connected, session?.Id ?? "");
        });
        return result;
    }
    private void CheckActionDeadline()
    {
        if (actionDeadline == 0 || Environment.TickCount64 < actionDeadline) return;
        actionDeadline = 0;
        Publish(transferPending ? "Die Anlage hat die Weiterleitung noch nicht bestätigt. Du kannst auflegen." : "Halten wird noch nicht bestätigt. Bitte warten oder die Rückfrage abbrechen.");
    }
}
