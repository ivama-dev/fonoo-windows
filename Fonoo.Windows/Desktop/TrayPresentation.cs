using Fonoo.Windows.Telephony;

namespace Fonoo.Windows.Desktop;

public enum TrayStatusKind { Offline, Ready, DoNotDisturb, Ringing, Calling, Call, Held, Muted }
public sealed record TrayPresentation(TrayStatusKind Kind, string Status, string Tooltip)
{
    public static TrayPresentation From(SipSnapshot state, string connectionStatus, string duration = "", string company = "", string extension = "", string profile = "")
    {
        var kind = state.Incoming ? TrayStatusKind.Ringing : state.InCall
            ? state.Held || state.RemoteHeld ? TrayStatusKind.Held : state.Active && state.Muted ? TrayStatusKind.Muted : state.Active ? TrayStatusKind.Call : TrayStatusKind.Calling
            : !state.Registered ? TrayStatusKind.Offline : state.DoNotDisturb ? TrayStatusKind.DoNotDisturb : TrayStatusKind.Ready;
        var status = kind switch {
            TrayStatusKind.Ready => "Verbunden · für Anrufe bereit",
            TrayStatusKind.DoNotDisturb => "Nicht stören · keine eingehenden Anrufe",
            TrayStatusKind.Ringing => "Eingehender Anruf",
            TrayStatusKind.Call => "Gespräch läuft",
            TrayStatusKind.Held => state.RemoteHeld ? "Gespräch von der Gegenseite gehalten" : "Gespräch gehalten",
            TrayStatusKind.Muted => "Gespräch läuft · Mikrofon stumm",
            TrayStatusKind.Calling => state.Status,
            _ => string.IsNullOrWhiteSpace(connectionStatus) ? "Nicht verbunden" : connectionStatus
        };
        var parts = new List<string> { "Fonu · " + status };
        if (state.InCall) parts.Add(string.Join(" · ", new[] { state.Peer, duration }.Where(s => !string.IsNullOrWhiteSpace(s))));
        else
        {
            if (!string.IsNullOrWhiteSpace(company)) parts.Add(company);
            if (!string.IsNullOrWhiteSpace(extension)) parts.Add(extension);
            if (!string.IsNullOrWhiteSpace(profile)) parts.Add("Anrufprofil: " + profile);
        }
        return new(kind, status, Limit(string.Join("\n", parts.Select(Clean)), 127));
    }
    public static string Clean(string text) => string.Concat(text.Where(c => !char.IsControl(c)));
    public static string Limit(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        var length = maximum;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
    public static bool ShouldHideOnMinimize(bool enabled, bool trayAvailable, bool visible, bool minimized, bool dialogOpen, bool quitting) =>
        enabled && trayAvailable && visible && minimized && !dialogOpen && !quitting;
}
