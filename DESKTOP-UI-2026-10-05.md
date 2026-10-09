# Desktop-Oberfläche

Die Oberfläche orientiert sich am Fonoo-Desktop-Design und verwendet native WinUI-Kontrollen.

Die Windows-App übernimmt die linke Navigation mit 220 Pixeln Breite, den zentrierten Wählbereich, rechteckige Zifferntasten, den breiten Anrufbutton, Favoriten als Liste und die getrennte Gesprächsansicht. Das Desktop-Violett ist #6E4AD9. WinUI-Systemkontrollen bleiben erhalten. Das Fenster startet mit 980 × 760 Pixeln, Inhalte bleiben scrollbar. Anmeldung per Passwort nutzt dieselbe Formularbreite wie die E-Mail-/Code-Anmeldung.

Seit 06.10.2026 passt sich auch der untere Seitenleistenbereich an den offenen/geschlossenen Navigationszustand an: In der schmalen Leiste erscheinen nur Symbole für Telefonieverbindung, Firma/Nebenstelle, „Nicht stören“ und gegebenenfalls erneutes Verbinden. Die Texte sind vollständig ausgeblendet, statt in der schmalen Spalte umzubrechen. Hinweise beim Darüberfahren und zugängliche Namen bleiben aktuell. Ausgeklappt stehen die Beschriftungen neben den Symbolen; lange Texte werden gekürzt angezeigt. Die vorhandenen Telefonieaktionen bleiben unverändert.

## Gespräch und Fenster

Ein neues Gespräch öffnet die Gesprächsansicht. Währenddessen kann das Tastenfeld DTMF senden; der Rückkehrbutton führt zum Gespräch. Nach Gesprächsende wird die zuvor gewählte Ansicht wiederhergestellt, sofern die Gesprächsansicht noch geöffnet ist. Abmelden und Firmenwechsel sind während des Gesprächs gesperrt.

Halten/Fortsetzen, direkte Weiterleitung und Rückfrage nutzen bestätigte SDK-Zustände. Die Rückfrage beginnt erst nach bestätigtem Halten des ursprünglichen Gesprächs. „Gespräche verbinden“ vermittelt beide Gesprächspartner; „Zurück zum Gespräch“ beendet die Rückfrage und setzt das ursprüngliche Gespräch fort. Eine abgelehnte Weiterleitung stellt dessen Bedienung wieder her. Beide Gesprächsfenster nutzen denselben Controller, Zeitstempel und dieselben gemessenen Aktivitätswerte.

Beim Minimieren oder Verbergen des Hauptfensters erscheint während eines Gesprächs das kompakte Fenster. Es bietet Annehmen/Ablehnen, Stumm, Halten, Tastenfeld, Weiterleitung, Rückkehr/Vermittlung und Auflegen. Schließen verbirgt nur das Mini-Fenster. Das native Symbol im Infobereich hält Fonoo auch bei geschlossenem Hauptfenster erreichbar und bietet Gesprächsaktionen, „Nicht stören“ und explizites Beenden. Bei fehlendem Infobereich bleibt das Hauptfenster sichtbar. Beim Beenden mit laufendem Gespräch erscheint eine Bestätigung.

Seit 06.10.2026 nutzt die Mini-Ansicht regulär 320 × 176 logische Pixel Inhalt, mit zusätzlicher Höhe nur für Annehmen und Rückfrage-Aktionen. Sie zeigt den gleichen segmentierten Pegelbalken wie die Audioeinstellungen. Vier Symboltasten bieten Stumm, Halten, das Menü für Tastenfeld/Weiterleitung und Auflegen; Tooltips und zugängliche Namen beschreiben den jeweiligen Zustand. Rückfrage-Aktionen bleiben bei Bedarf direkt sichtbar. DPI-Anpassung und Begrenzung auf den Arbeitsbereich halten das Fenster erreichbar. Details und Prüfstand stehen in [AUDIO-INTEGRATION.md](AUDIO-INTEGRATION.md).

## Anrufliste, Kontakte und Team

Die Anrufliste übernimmt die zentrale Fonoo-Historie und enthält ausgehende/eingehende, verpasste, abgelehnte und fehlgeschlagene Gespräche mit Dauer. Oben wechseln die nativen Filter „Alle“ und „Verpasst“ zwischen allen Einträgen und ausschließlich verpassten eingehenden Anrufen; die Rufnummernsuche wirkt zusätzlich auf den gewählten Filter. „Alle“ ist beim Start ausgewählt. Suche, Rückruf und Löschen sind umgesetzt. Der geschützte lokale Zwischenspeicher ist getrennt nach Konto und Firma, auf 500 Einträge begrenzt und verwirft Einträge nach 90 Tagen. Details stehen in [CALL-HISTORY.md](CALL-HISTORY.md).

Windows-Kontakte werden nach bewusster Auswahl schreibgeschützt geladen; alternativ können vCard-Dateien geöffnet werden. Nur validierte Rufnummern sind wählbar. Die Anzeige unterstützt Suche und mehrere Rufnummern je Kontakt. Die App schreibt keine Systemkontakte.

Zusätzlich ist die Microsoft-Anmeldung mit lesendem Outlook-Kontaktzugriff eingebaut. Die gemeinsame Liste zeigt Windows-, vCard- und Outlook-Kontakte; Outlook nutzt einen geschützten Zwischenspeicher pro Fonoo-Konto. Eindeutige Kontakte werden in der Gesprächsansicht nach Namen angezeigt. Entra-Konfiguration, Berechtigungen und aktuelle Prüfgrenzen stehen in `MICROSOFT-CONTACTS.md`.

Das Team nutzt die macOS-API: Suche, Details, Nebenstelle anrufen, Favorit anlegen und eigenen Anzeigenamen ändern. Eigene Geräte erscheinen nur beim eigenen Benutzer. „Meine Verfügbarkeit“ bietet Präsenz, Ablauf/automatischen Zeitplan, Arbeitsmodus und dessen Geräteauswahl sowie Rufteam-Pause/Fortsetzen. Revisionen verhindern unbemerktes Überschreiben gleichzeitiger Änderungen. Ein lokales „Nicht stören“ bleibt zusätzlich zur Firmenverfügbarkeit wirksam. Fehlende Backend-Endpunkte werden als Fehler angezeigt; es gibt keine vorgetäuschten Teamdaten im regulären Build.

## Anmeldung und Hintergrundbetrieb

Optionale Geräteanmeldung ist unter dem Windows-Benutzer mit DPAPI geschützt. Der zuletzt gewählte Firmenzugang wird wiederhergestellt und neu provisioniert. Abmelden entfernt die lokale Sitzung und versucht die serverseitige Geräteabmeldung. Kennwort und SIP-Geheimnisse bleiben im Arbeitsspeicher. Der reguläre Build verwendet eine einzelne App-Instanz.

Die App bleibt nur erreichbar, solange der Prozess läuft. Netzwechsel und Rückkehr aus dem Ruhezustand stoßen eine erneute Registrierung an; während eines Gesprächs wird sie zurückgestellt. Es gibt keinen Windows-Dienst, Autostart oder Push-Dienst zum Aufwecken eines beendeten Prozesses.

## Prüfung und Grenzen

Debug/x64-Build, AccountFlow.Tests, PhoneData.Tests, DesktopData.Tests und native SRTP-Loopback-Tests für Gesprächsaktionen bestanden. DPAPI und lokale PCM-Pegelmessung wurden im nativen Windows-Benutzerkontext mit isolierten synthetischen Daten geprüft. Anmeldung, Wählpad, Anrufliste, Kontakte/Team und Mini-Fenster wurden visuell geprüft.

Die gesondert gebaute Designvorschau ist ausdrücklich als solche beschriftet und verbindet sich nicht mit einem Konto oder SIP. Ihr isolierter Speicher-/PCM-Test entfernt seine synthetischen Dateien anschließend. Der reguläre Build zeigt diese Vorschauaktionen nicht.

Live-Team-/Verfügbarkeits-API, echter Fonoo-Anruf mit Audio, Hardwarewechsel, Netzverlust/Ruhezustand, unterschiedliche DPI und Barrierefreiheit benötigen noch Abnahme. Systemkontakte-Zugriff muss insbesondere im signierten MSIX geprüft werden. Dies ist Funktionsangleichung, noch keine bestätigte vollständige Mac-Parität oder Store-Freigabe.
