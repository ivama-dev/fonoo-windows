# Microsoft-Anmeldung und Outlook-Kontakte

Die Windows-App verbindet das Microsoft-Konto über MSAL.NET 4.90.1 und den Windows Web Account Manager (WAM). Wenn WAM nicht verfügbar ist, nutzt MSAL den Systembrowser mit Authorization Code und PKCE über `http://localhost`. Die Anmeldung wird nur durch einen Klick auf „Outlook verbinden“, „Konto wechseln“ oder „Erneut anmelden“ geöffnet. Eine stille Aktualisierung zeigt keinen Anmeldedialog. Ein eingehender Anruf bricht eine laufende Anmeldung ab; während eines Gesprächs kann keine neue interaktive Microsoft-Anmeldung begonnen werden.

Diese Verbindung dient dem Kontaktzugriff. Sie ersetzt die Fonoo-Kontoanmeldung nicht: Ein Microsoft-Token wird nicht als Fonoo-Sitzung akzeptiert und Konten werden nicht allein anhand gleicher E-Mail-Adressen verknüpft. Eine spätere Firmenanmeldung bei Fonoo über OIDC oder SAML benötigt eine eigene, serverseitig validierte Konto-/Firmenzuordnung.

## Entra-Registrierung

Eigene Registrierung für **Fonoo Desktop – Outlook-Kontakte**, getrennt vom vertraulichen Mailversand-Client:

1. Kontotyp: Konten beliebiger Entra-Mandanten und persönliche Microsoft-Konten.
2. Plattform: Öffentlicher Client/nativ (mobil und Desktop).
3. Desktop-Umleitungen: `http://localhost` für den Systembrowser sowie `ms-appx-web://microsoft.aad.brokerplugin/<Client-ID>` für WAM.
4. Microsoft Graph: ausschließlich delegiertes `Contacts.Read`. Eine eventuell automatisch angelegte Graph-Berechtigung `User.Read` wird für diese Implementierung nicht benötigt.
5. Kein Client-Secret, keine Anwendungsberechtigung für alle Postfächer und keine Kennwortabfrage in Fonoo. Ein Microsoft-Administrator kann die delegierten Rechte für seinen Mandanten freigeben. Ohne diese Freigabe hängt eine persönliche Zustimmung von den Firmenrichtlinien ab.

Die öffentliche Client-ID gehört in `Fonoo.Windows/microsoft365.json` und wird mit der App ausgeliefert. Solange sie fehlt, bleibt die Verbindung ausdrücklich deaktiviert. `Allow public client flows` muss für unseren Authorization-Code-/Broker-Ablauf nicht pauschal aktiviert werden; es werden weder ROPC noch Device-Code-Login implementiert.

Die offizielle öffentliche Desktop-Client-ID steht in der Windows-Konfiguration. Sie ist kein Geheimnis und gibt keinen Zugriff auf Kontakte. Ohne gültige serverseitige Firmenzuordnung bleibt die persönliche Verbindung gesperrt. Eigene Dienstbereitstellungen benötigen eine passende öffentliche Client-Registrierung und Backend-Konfiguration; Serverzertifikate und private Schlüssel gehören nicht in dieses Repository.

## Ablauf für Firmenkunden

Eine zentrale, von Fonoo verwaltete Registrierung bedient alle Kundenmandanten. Kunden legen keine eigenen App-Registrierungen an; durch die Freigabe entsteht die Unternehmensanwendung im jeweiligen Microsoft-Mandanten.

1. Ein Fonoo-Firmenadministrator öffnet im Kundenbereich „Microsoft 365 für die Firma verbinden“.
2. Die Freigabe erfolgt bei Microsoft mit einem Konto, das dort die erforderliche Administratorrolle besitzt. Die Fonoo-Administratorrolle allein reicht dafür nicht. Angefordert wird gezielt delegiertes `Contacts.Read` für die Desktop-Client-ID.
3. Anschließend klickt jeder Mitarbeiter in Fonoo auf „Outlook verbinden“ und meldet sich mit seinem eigenen Microsoft-Konto an. Für dieselben bereits vom Administrator freigegebenen Rechte ist normalerweise keine weitere Zustimmung erforderlich. MFA, Kontozuweisungen und Zugriffsrichtlinien gelten weiterhin.
4. Fonoo liest nur die Postfachkontakte des jeweils verbundenen Benutzers. Die Firmenfreigabe ermöglicht keinen unbeaufsichtigten Zugriff auf alle Postfächer und schließt kein Firmenadressbuch ein.

Windows prüft die serverseitige Firmenzuordnung vor Verbindung und manueller Aktualisierung und verwendet ausschließlich den zugehörigen Mandanten. Die von MSAL bestätigte Token-Mandanten-ID wird vor jedem Graph-Abruf geprüft, auch nach einer Token-Erneuerung. Beim nächsten geprüften Status entfernt eine getrennte/ersetzte Firmenzuordnung die betroffene lokale Verbindung. Der vertrauliche OIDC-/Administratorablauf im Kundenportal ist Teil des getrennten Fonoo-Backends. Eine Freigabe gilt für die angeforderte Client-ID; getrennte Registrierungen anderer Plattformen werden dadurch nicht automatisch freigegeben.

„Verbindung trennen“ in der Desktop-App entfernt die lokale Verbindung und die App-Token; es widerruft keine mandantenweite Administratorfreigabe. Diesen Widerruf verwaltet der Microsoft-Administrator. Eine separate Verbindung persönlicher Outlook.com-Konten ist im neuen Firmenablauf nicht implementiert; die Registrierung behält diesen Kontotyp für eine spätere Erweiterung bei.

## Kontaktanzeige und Speicherung

Microsoft Graph v1.0 lädt persönliche Kontakte aus dem Standardordner und weiteren Kontaktordnern einschließlich Unterordnern und Folgeseiten. Abgefragt werden nur ID, Name und Mobil-/Privat-/Geschäftsrufnummern. Reine E-Mail-Einträge und ungültige SIP-/Wählziele werden nicht zum Anrufen übernommen. Das Firmenadressbuch/GAL, fremde freigegebene Postfächer und lokale PST-Dateien sind nicht Bestandteil dieser Verbindung.

Die Liste fasst Outlook, Windows-Kontakte und vCards zusammen. Suche bleibt lokal. Der Stern neben einer Rufnummer übernimmt Name und Nummer direkt in die lokalen Fonoo-Favoriten. Ein gefüllter Stern kennzeichnet bereits gespeicherte Nummern; derselbe Wählwert wird bei der Kontaktübernahme nicht doppelt angelegt. Bei mehreren Nummern eines Kontakts wird die jeweilige Rufnummer einzeln gespeichert. Das schreibt keine Daten nach Microsoft und die Favoriten bleiben nach einem Neustart verfügbar, getrennt nach Fonoo-Konto und Firma. Namens- oder Nummernänderungen in Outlook ändern diese lokale Kopie nicht automatisch. Bei einem eindeutig zugeordneten Namen zeigt die Gesprächsansicht den Namen und die ursprüngliche Rufnummer; das Mini-Fenster verwendet denselben Anzeigenamen. Es gibt keine heuristische Zuordnung anhand der letzten Ziffern.

Die Auswahl des Microsoft-Kontos und der Kontaktzwischenspeicher sind mit DPAPI unter dem aktuellen Windows-Benutzer geschützt. MSAL verwendet seinen offiziellen verschlüsselten Cache; es gibt keinen Klartext-Fallback. Dateien liegen in `%LOCALAPPDATA%/Fonoo/Windows/Microsoft365/<Hash aus Fonoo-Konto, Firma, Microsoft-Mandant, Verbindungsrevision und Client-ID>/`. Konten und Firmen sind getrennt. Ein Firmen-/Zuordnungswechsel trennt die vorige aktive Verbindung, bevor ein neuer Cache verwendet wird. Abmelden von Fonoo oder „Verbindung trennen“ entfernt die lokale Verbindung, Kontakte und die Token dieser App; das Windows-Konto und die tatsächlichen Outlook-Kontakte werden nicht gelöscht.

Bei Netzfehlern bleibt der letzte vollständige Kontaktstand erhalten. Authentifizierungs-/Freigabefehler erfordern einen bewussten erneuten Anmeldeklick und entfernen die betroffenen zwischengespeicherten Kontakte aus der Anzeige. Kontowechsel und verspätete Antworten können nach dem Trennen keine Daten erneut übernehmen. Es wird beim Start still aktualisiert; danach gibt es eine manuelle Aktualisierung statt eines ständig laufenden Cloud-Pollings.

## Prüfung

```powershell
dotnet run --project tests/MicrosoftContacts.Tests --no-restore
```

Die deterministische Testsuite prüft minimale Scopes, Folgeseiten/Unterordner, Nummernprüfung, Beschränkung sämtlicher authentifizierter URLs auf Graph-Kontaktressourcen, einmalige Erneuerung bei 401, verweigerte Freigabe, Drosselung, Offline-Daten, Kontotrennung und verspätete Antworten nach Abmeldung. Sie verwendet synthetische Identitäten/HTTP-Antworten. In einer eingeschränkten Kommando-Sandbox überspringt `--skip-dpapi` ausdrücklich nur den DPAPI-Dateitest; der reguläre Windows-Benutzerkontext ist dafür erforderlich.

Die native Designvorschau prüft zusätzlich DPAPI-Kontaktspeicherung und `MsalCacheHelper.VerifyPersistence` mit isolierten synthetischen Daten und anschließender Entfernung. Vorschauaktionen stellen keine Microsoft-/Fonoo-Verbindung her. Ein echter Microsoft-Anmelde-/Kontaktabruf, MFA/Conditional Access, Administratorfreigaben und signiertes MSIX müssen separat mit dem freigegebenen Testkonto geprüft werden.

Am 06.10.2026 bestanden Debug/x64-Build, alle vier Daten-/Kontotestsuiten und die native Speicherprüfung. Die neue Kontaktansicht wurde unter Windows visuell geprüft. Der abschließende Online-Restore prüfte App und Microsoft-Kontakttests einschließlich transitiver Pakete gegen den offiziellen NuGet-Sicherheitsfeed und meldete keine bekannten Paketlücken. Die vorherige NU1900-Meldung ist behoben; die temporäre lokale Build-Brücke gehört nicht zur Anwendung.

Die Kontakt-Favoritenaktion wurde zusätzlich in der nativen Windows-Vorschau mit einem synthetischen Outlook-Eintrag geprüft: Name und Rufnummer erscheinen in Favoriten, der Stern wird gefüllt und die Hinzufügen-Aktion deaktiviert, ohne einen Anruf auszulösen. Nach regulärem Schließen und Neustart blieb die Markierung erhalten. Die Vorschau speichert ausschließlich unter ihrem eigenen Build-Verzeichnis; die reguläre App verwendet weiterhin den bestehenden konto-/firmengebundenen Favoritenspeicher. Der reguläre Debug/x64-Build und die vorhandene Telefon-/Favoritendatensuite bestanden erneut.

## Microsoft-Referenzen

- [WAM und MSAL.NET](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam)
- [Desktop-Token-Cache](https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization)
- [Kontakte lesen](https://learn.microsoft.com/en-us/graph/api/user-list-contacts?view=graph-rest-1.0)
- [SAML und OAuth für Graph](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-token-exchange-saml-oauth)
- [Mehrere Kundenmandanten und Administratorzustimmung](https://learn.microsoft.com/en-us/entra/identity-platform/howto-convert-app-to-be-multi-tenant)
- [Administratorfreigabe und sichere Rückleitung](https://learn.microsoft.com/en-us/entra/identity-platform/v2-admin-consent)
- [Benutzer- und Administratorzustimmung](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/user-admin-consent-overview)
