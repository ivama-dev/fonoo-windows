# Anrufprofile

Der Button neben **Audio** zeigt das bestätigte aktive Anrufprofil. Sein Menü wechselt das Profil oder öffnet **Profile verwalten**. Die Verwaltung ist zusätzlich im Team erreichbar. Dort lassen sich bis zu 20 Profile einschließlich Standard anlegen, benennen, bearbeiten und nach Bestätigung löschen.

- **Standard** enthält alle eigenen Geräte, auch später hinzugefügte. Der API-Wert `device_ids: null` erhält diese dynamische Bedeutung. Standard kann nicht geändert oder gelöscht werden. Deaktivierte und abgemeldete Geräte bleiben serverseitig ausgeschlossen.
- Eigene Profile enthalten eine ausdrückliche Auswahl eigener Geräte. Fremde Mitarbeitergeräte werden weder angeboten noch als gültige Antwort akzeptiert. Deaktivierte eigene Geräte sind gekennzeichnet.
- Eine leere Auswahl ist möglich und wird ausdrücklich erklärt: Eingehende Anrufe klingeln auf keinem Gerät dieses Profils. Neue Geräte müssen in eigenen Profilen bewusst ergänzt werden.
- Beim Löschen des aktiven Profils wird im selben Schreibvorgang auf Standard gewechselt. Das Anlegen eines Profils ändert die aktive Auswahl nicht.

## Gemeinsamer Serverstand

Windows verwendet denselben Vertrag wie die Mac-App: `POST cloud/availability/read` liefert `settings.device_profiles`, `settings.active_profile_id`, die eigenen Geräte sowie optional `device_profiles_version: 1` und `profiles_available`. Die Antworten werden auf Schema, Benutzer, Firma, eindeutige Profile und Geräteauswahl geprüft. Ältere Antworten ohne Profilfelder werden als Standard interpretiert; eine fehlende Bestätigung der Telefonieserver-Unterstützung bleibt sichtbar.

`POST cloud/availability/user` schreibt ausschließlich die geänderten Profilfelder mit `tenant_id` und `expected_revision`. Ein Wechsel schreibt nur `active_profile_id`. Bearbeiten und Löschen schreiben die vollständige Profilliste und die aktive ID gemeinsam. Status, Zeitpläne und frühere Arbeitsmodi werden dabei nicht überschrieben. Konflikte verlangen ein bewusstes Neuladen; Windows wiederholt sie nicht mit einer neuen Revision. Nach dem Schreiben liest Windows den bestätigten Stand erneut.

Die bestehende Verfügbarkeitsabfrage übernimmt Änderungen anderer Fonoo-Apps alle 15 Sekunden. Vor dem Öffnen des Profilmenüs und der Verwaltung wird zusätzlich aktualisiert. Abmeldung, Firmenwechsel und Beenden brechen Anfragen des alten Kontextes ab; ältere Revisionen überschreiben keinen neueren Stand. Profilanfragen blockieren weder den SIP-Worker noch die Gesprächssteuerung.

Die PBX entscheidet anhand des aktiven Profils, welche Geräte für **neue eingehende Anrufe** klingeln. Profilwechsel legen laufende Gespräche nicht auf und erfordern keine erneute SIP-Registrierung. Profile sind unabhängig von der Freigabe der erweiterten Status-/Zeitplan-/Rufteam-Steuerung. Sie werden serverseitig pro Benutzer und Firma gespeichert; Windows erstellt dafür keine zusätzliche dauerhafte lokale Profildatei.

## Prüfung

`DeviceProfiles.Tests` prüft Standard/null, Fremdgeräte, doppelte/fehlende Profile, Bearbeiten/Anlegen/Löschen, leere Auswahlen, Kapazität, bestätigte Schreibantworten, Revisionskonflikte und Antworten nach Abmeldung. Die explizite Debug-Designvorschau verwendet drei synthetische Geräte und Profile; Änderungen dort bleiben im Speicher und erzeugen keine Konto- oder SIP-Anfragen. Ein echter geräteübergreifender Klingeltest erfordert angemeldete Geräte und einen eingehenden Testanruf.

Referenzvertrag: [Mac-Implementierung](https://github.com/ivama-dev/fonoo-macos/blob/c86f05a3bf1b932f6481280abf8c848ffccd7a25/Shared/TeamMember.swift).
