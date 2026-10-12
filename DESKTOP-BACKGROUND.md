# Background mode and Windows shell status

**Konto → Fenster & Infobereich** provides a saved switch for minimizing Fonu into the notification area. It is enabled by default. When disabled, minimizing keeps the normal taskbar button. Closing the main window continues to hide Fonu in the notification area; **Fonu beenden** remains the explicit exit action and asks before ending a live call.

Hiding a window leaves the same liblinphone worker/registration alive. It does not sign out, change an incoming-call profile or start a second SIP engine. The compact call window remains available for incoming/live calls. A first-use, silent shell hint per app run explains how to reopen Fonu. Left click or keyboard activation of the icon opens the main window. A tray failure restores/retains the window; open dialogs prevent hiding it. The feature does not wake an exited process or a sleeping/offline PC.

## Status and quick actions

The original Fonu icon receives a small color/symbol badge:

| State | Badge |
| --- | --- |
| Registered and ready | Green check |
| Signed out/disconnected | Gray cross |
| Do not disturb | Red minus |
| Incoming/outgoing call setup | Violet bell/ellipsis |
| Connected call | Violet activity bars |
| Held call | Amber pause |
| Muted microphone | Amber microphone/slash |

Status priority preserves incoming, held and active call feedback even if registration is interrupted or DND is set. The tooltip includes the telephone status and company/extension/profile while idle, or the peer and duration during a call. It is bounded to the shell limit without splitting Unicode surrogate pairs. Native menu labels strip control characters and escape ampersands.

The right-click menu shows current status, company/extension and profile, and links to Wählen, Favoriten, Anrufliste, Audio and Anrufprofile. Active call actions use the same guarded controller as the main/mini windows. Reconnect is available only outside calls; DND shows its checked state. **In den Hintergrund** and **Fonu beenden** are separate actions. The settings page's **Schnellmenü öffnen** button opens this same menu for discoverability.

When a taskbar button exists, `ITaskbarList3.SetOverlayIcon` applies the same status badge with accessible text. Badges change only on state transitions, without animation. The tray tooltip updates call duration at most once a second. All native icons are cached per state and released on shutdown. A shell restart re-adds the tray icon and reapplies the overlay; another one-shot retry covers taskbar recreation. There is no background timer dedicated to drawing badges.

The shell remains responsible for placing the notification-area icon in its visible or overflow section. Users can move it out of the overflow area; Fonu does not change Windows taskbar settings. Taskbar overlays depend on shell/taskbar configuration. See [Microsoft's notification-area API](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area) and [taskbar overlay API](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist3-setoverlayicon).

## Settings and validation

`%LOCALAPPDATA%\Fonoo\Windows\desktop-settings.json` contains only schema version 1 and `MinimizeToTray`. This is a local Windows-user preference, independent of Fonu account/company and call-routing profiles. Writes use an atomic temporary-file replacement. Unreadable, oversized or unknown-version files are preserved, the switch is disabled, and normal taskbar minimization remains available. The explicit Debug preview stores its setting under its own `qa/desktop` directory.

DesktopData checks cover preference persistence, invalid-file preservation, safe hiding policy, status precedence, tooltip limits and distinct rasterized glyphs. Native icon probing additionally checks eight states, repeated cached lookups and stable GDI/USER resource counts across create/dispose cycles. Native Windows UI checks cover minimizing/reopening and the menu. Sleep/network recovery and incoming call behavior on other hardware remain part of release acceptance.
