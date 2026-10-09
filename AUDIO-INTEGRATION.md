# Windows-Audio

Über **Audio** werden Mikrofon und Lautsprecher aus der tatsächlichen Linphone/WASAPI-Geräteliste gewählt. Die Auswahl gilt für neue und laufende Gespräche sowie eine Rückfrage; die Ausgabe wird auch zum Klingeln verwendet. Geräte-IDs werden lokal unter `%LOCALAPPDATA%/Fonoo/Windows/audio.json` gespeichert. Fehlt ein bevorzugtes Gerät, wird ein verfügbares Ersatzgerät genutzt und der Dialog weist darauf hin. Die bevorzugte Auswahl bleibt für die Wiederverbindung gespeichert. SDK-Geräteereignisse und der Aktualisieren-Button erneuern die Zuordnung.

Ohne Anmeldung läuft nur während des Dialogs eine lokale Engine ohne SIP-Transporte und Konto. Mit Anmeldung nutzt der Dialog die vorhandene Engine. Alle SDK-Geräte-/Gesprächszugriffe erfolgen auf deren Worker. Bei geöffnetem, aktivem Dialog wird alle 100 ms abgefragt, ohne überlappende Abfragen. Gerätewechsel und Teststarts sperren die zugehörigen Bedienelemente bis zum Abschluss. Eine Aktualisierung während einer laufenden Abfrage bleibt für die nächste Abfrage vorgemerkt.

## Messung und Hörtest

Während eines Gesprächs stammt der Mikrofonpegel aus `Call.RecordVolume`; Stumm setzt ihn auf null. Die Aktivität in der Gesprächsansicht und im Mini-Fenster nutzt den größeren Wert von bestätigtem Aufnahme- und Wiedergabepegel. Die fünf Balken zeigen aufeinanderfolgende Messwerte; es werden keine zufälligen Werte erzeugt. Beide Ansichten teilen dieselben Werte und Gesprächszeitstempel. Gehaltene Gespräche zeigen keine Aktivität. Die Anzeige fragt nur während eines Gesprächs ab: 40 ms bei sichtbarer Gesprächsansicht, ansonsten 1 s. Sie ist relativ, kein kalibriertes Messgerät.

Im aktiven Audio-Dialog misst ein Windows AudioGraph das gewählte Mikrofon lokal über einen FrameOutputNode. Float32-PCM wird im gültigen Buffer-Lebenszyklus gelesen und als RMS- und Sample-Spitzenpegel ausgewertet. Geräte-ID hat Vorrang; ein Namensabgleich ist nur bei eindeutiger Zuordnung erlaubt. Die normale Messung hat keinen Lautsprecher-, Datei- oder Netzwerkausgang. Das Meter stoppt bei Fokusverlust, Gerätewechsel, Dialogschließen, Anruf und Beenden. Eine Generationsprüfung verwirft verspätet gestartete Messgraphen nach einem solchen Stopp. Ohne aktuelle PCM-Frames erscheint kein behaupteter aktiver Pegel.

**Mich selbst hören** verbindet nur auf ausdrücklichen Knopfdruck das Mikrofon mit einem Ausgabeknoten desselben lokalen Windows-AudioGraph. Der ausgewählte Ausgabe-Endpunkt wird explizit zugeordnet; die Pegelmessung bleibt sichtbar. Eine unabhängige einmalige Zeitbegrenzung schließt den Graph nach 15 Sekunden, auch wenn die UI-Abfrage verzögert ist. Gerätewechsel, Dialogschließen, Fokusverlust, Anruf und Beenden stoppen ihn ebenfalls. Der frühere SDK-Echo-Test konnte am vorhandenen Headset nicht gestartet werden und wird von der Oberfläche nicht mehr verwendet.

**Testton abspielen** spielt die eigene drei Sekunden lange WAV-Datei aus `Assets/AudioTest.wav` über das gewählte Ausgabegerät ab. Dieser Test benötigt kein Mikrofon. Dateiende, Gerätefehler, Dialogschließen, Fokusverlust und Anruf beenden die Wiedergabe. Es wird keine Lautstärke des Systems verändert.

Ein eingehender Anruf schließt den Audio-Dialog, damit Annehmen erreichbar ist. Beide lokalen Tests speichern und übertragen kein Mikrofon-Audio; Gesprächsaudio nutzt die konfigurierte SRTP-Verbindung. Windows-Audioeinstellungen lassen sich über eine beschriftete Schaltfläche öffnen, ohne Rechte oder Systemlautstärke automatisch zu ändern.

## Pegelanzeige und Mini-Fenster

Audio-Dialog und Mini-Fenster verwenden denselben 24-segmentigen `AudioLevelBar` mit zeitabhängigem Abfall und kurz gehaltenem Spitzenindikator. Violett zeigt normale Aktivität, hohe Werte werden zusätzlich farblich unterschieden; Text und Hilfen erläutern die Zustände. Theme-Ressourcen unterstützen den Windows-Kontrastmodus. Die Skala ist relativ (-60 bis 0 dB), kein kalibriertes Lautstärke- oder Qualitätsmessgerät. Im Dialog stammt der Spitzenindikator außerhalb eines Gesprächs aus den PCM-Samples; während eines Gesprächs und im Mini-Fenster hält er den höchsten zuletzt gemeldeten SDK-Pegel.

Die Mini-Ansicht hat regulär 320 × 176 logische Pixel Inhalt statt bisher 350 × 480 Pixel Fenstergröße. Sie berücksichtigt DPI, zeigt Name, Status, Dauer und Gesprächspegel und bietet Symboltasten für Stumm, Halten, weitere Aktionen und Auflegen mit eindeutigen Tooltips und Zugänglichkeitsnamen. Tastenfeld und Weiterleitung liegen im Menü. Annehmen und die Rückfrage-Aktionen bleiben direkt sichtbar, wenn sie benötigt werden. Der Gesprächspegel berücksichtigt Aufnahme und Wiedergabe; ein stummes Mikrofon verhindert deshalb nicht die Anzeige des Gesprächspartners. Gehaltene Gespräche zeigen keine Aktivität.

Das Fenster bleibt oben, öffnet sich zunächst am Rand des Arbeitsbereichs und kann verschoben werden. Eine Größenerweiterung für eingehende Anrufe/Rückfragen und DPI-Wechsel hält es im Arbeitsbereich. Das Schließen der Mini-Ansicht beendet kein Gespräch.

## Prüfung am 5. Oktober 2026

- Debug/x64-Build erfolgreich.
- Produktionsengine dreimal gestartet, Geräte erneuert, Routen geprüft, ungültige Auswahl abgelehnt und sauber beendet; dabei keine Aufnahme und kein Anruf gestartet.
- Drei Eingabe- und drei Ausgabegeräte erkannt; Dialog mit tatsächlichen Headset-/Mikrofoneinträgen geprüft.
- Native PCM-Messung aus synthetischer WAV-Datei erfolgreich, ohne physisches Mikrofon. Der Test prüft den gemessenen Spitzenwert, da ein reiner Dateigraph vor dem Prüfzeitpunkt das Dateiende erreichen kann.
- Lokale SRTP-Gespräche mit synthetischem Audio für Halten, DTMF, Rückfrage, Transfer und Fehlerfälle erfolgreich.
- Hörbare Qualität, echte Gespräche, Abziehen/Anstecken, Bluetooth, Echo-Unterdrückung und verweigerter Mikrofonzugriff benötigen Geräteabnahme.

```powershell
dotnet run --project diagnostics/LinphoneProbe --no-restore -p:LinphoneSdkRoot="C:\path\to\extracted-sdk" -- --audio
dotnet run --project diagnostics/LinphoneProbe --no-restore -p:LinphoneSdkRoot="C:\path\to\extracted-sdk" -- --calls
```

SDK-Referenz: [liblinphone 5.5](https://download.linphone.org/releases/docs/liblinphone/5.5/c/group__group__misc.html).

## Erweiterung und Prüfung am 6. Oktober 2026

- Debug/x64 und das Release/x64-Store-Paket bauen erfolgreich; die Paketprüfung enthält jetzt auch den eigenen Testton. Kein Upload oder Store-Release.
- Die PCM-Prüfung verifiziert Stille, nicht endliche Samples, den -60-dB-Boden, RMS/Sample-Peak einer bekannten Sinuswelle, kurze Spitzen, Begrenzung und unabhängige Folgeframes.
- Der reguläre Windows-Dialog wurde mit den tatsächlichen Plantronics-Headset-Endpunkten geöffnet. Lokale Messung, der beendete Testton und der Windows-Hörtest mit sichtbarer Restzeit und automatischem Ende wurden in der UI geprüft. Das bestätigt die Verarbeitung und Zustandswechsel, nicht eine neue subjektive Hörqualitätsabnahme.
- Die kompakte Mini-Ansicht wurde mit ausdrücklich synthetischen Vorschauwerten visuell geprüft: laufender Pegel, Stumm-Symbol und zugängliche Beschriftung, Halten mit ruhendem Pegel, Fortsetzen, Aktionsmenü, Rückfrage und Rückkehr zum ursprünglichen Gespräch. Die Vorschauwerte und simulierten Aktionen sind ausschließlich in `FONOO_DESIGN_PREVIEW`-Builds enthalten.
- Hotplug, Bluetooth, verweigerte Berechtigungen und längere echte Gespräche bleiben Bestandteil der Geräteabnahme.

```powershell
dotnet run --project tests/AudioData.Tests
```
