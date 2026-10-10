namespace Fonoo.Windows.Telephony;

public sealed record SipSnapshot(bool Registered, bool InCall, bool Incoming, bool Muted, string Status, string Peer,
    bool Active = false, bool Held = false, bool RemoteHeld = false, bool HoldPending = false,
    bool Consulting = false, bool ConsultationActive = false, bool TransferPending = false, string OriginalPeer = "", bool DoNotDisturb = false, string CallId = "")
{
    public bool CanTransfer => InCall && (Active || Held) && !RemoteHeld && !HoldPending && !Consulting && !TransferPending;
}
