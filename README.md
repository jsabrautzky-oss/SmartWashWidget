# SmartWashWidget

[![Build](https://github.com/jsabrautzky-oss/SmartWashWidget/actions/workflows/build.yml/badge.svg)](https://github.com/jsabrautzky-oss/SmartWashWidget/actions/workflows/build.yml)
![MIT-Lizenz](https://img.shields.io/badge/Lizenz-MIT-blue.svg)

Ein Desktop-Widget für **Windows 11** im Glassmorphism-/Fluent-Acrylic-Look, das **Samsung Waschmaschine und Trockner** über die **SmartThings-API** beobachtet: rotierende Trommeln mit Wasserwelle bzw. Hitzewellen, Leucht-Fortschrittsbalken, Restzeit und aktuelle Phase — live, im Tray, ohne Fenster in der Taskleiste.

Gebaut mit C#/.NET 9 (WPF), **ohne ein einziges NuGet-Paket** (nur BCL: `HttpClient`, `System.Text.Json`, GDI+ für das Tray-Symbol). Alle Grafiken und Animationen werden im Code gezeichnet.

---

## Vorschau

Waschmaschine (Wasserwelle + Perlen) und Trockner (Hitzewellen + warme Partikel), jeweils im laufenden Betrieb:

| Waschmaschine | Trockner |
|---|---|
| ![Waschmaschine im Lauf](Screenshots/animate1.gif) | ![Trockner im Lauf](Screenshots/animate2.gif) |

Das Einstellungsfenster — oben Sprache, Glas & Aussehen, unten Konto-Verbindung und Gerätezuordnung:

| Glas & Aussehen | SmartThings-Zugang |
|---|---|
| ![Einstellungen: Glas und Aussehen](Screenshots/Settings1.png) | ![Einstellungen: Zugang und Geräte](Screenshots/Settings2.png) |

Ohne Token läuft das Widget sofort im **Demo-Modus** mit animierten Schein-Läufen — die Vorschauen oben entstehen genau so.

---

## Inhalt

* [Vorschau](#vorschau)
* [Funktionen](#funktionen)
* [Schnellstart](#schnellstart)
* [SmartThings-Token einrichten](#smartthings-token-einrichten)
* [Build, Publish und Installer](#build-publish-und-installer)
* [Programm-Symbol](#programm-symbol)
* [Steuerung](#steuerung)
* [Kommandozeilen-Argumente](#kommandozeilen-argumente)
* [Konfigurationsdatei](#konfigurationsdatei)
* [So funktioniert die Glasoptik](#so-funktioniert-die-glasoptik)
* [Fehlerbehebung](#fehlerbehebung)
* [Bekannte Grenzen](#bekannte-grenzen)
* [Datenhaltung und Datenschutz](#datenhaltung-und-datenschutz)
* [Projektstruktur](#projektstruktur)
* [Lizenz](#lizenz)

---

## Funktionen

| Bereich | Details |
|---|---|
| **Echte Daten** | `GET /v1/devices` und `GET /v1/devices/{id}/status` mit Bearer-Token, fehlertolerante JSON-Auswertung (auch `samsungce.*`- und `custom.*`-Capabilitys) |
| **Automatische Geräte-Erkennung** | Waschmaschine und Trockner werden über Capabilitys gefunden und automatisch zugeordnet; bei mehreren Kandidaten hilft die Liste im Einstellungsfenster |
| **Zustände** | `Läuft`, `Pausiert`, `Fertig`, `Bereit` (Standby-Glow), `Getrennt`/`Offline` |
| **Fortschritt** | Restzeit aus `remainingTime`/`remainingTimeStr`/`completionTime`, gemeldetes `progress` der Maschine, Phasenname (Vorwäsche, Waschen, Spülen, Schleudern, Trocknen, Abkühlen) |
| **Animationen (60 FPS)** | Trommel mit Paddeln, Wasserwelle + Perlen beim Waschen, Hitzebänder + warme Partikel beim Trocknen, Leucht-Fortschrittsbalken mit Prozentangabe |
| **Tray-Symbol** | programmatisch gezeichnet, farblich nach Verbindungsstatus, Menü mit Ein-/Ausblenden, Demo-Modus, Milchglas-Blur, Einstellungen, Beenden |
| **Einstellungsfenster** | Token, Gerätezuordnung, Milchglas-Blur (Acrylic) an/aus, Glas-Tönung 0–100 % (Standard 0 %), Skalierung 70–150 %, Position sperren, Immer im Vordergrund, abgerundete Ecken, Demo-Tempo |
| **Zuverlässigkeit** | Poll-Intervall 20 s (laufend) / 45 s (direkt nach Laufende) / 180 s (Standby), Fehler-Backoff 30–300 s, Einzelinstanz-Schutz, `error.log` |

---

## Schnellstart

```bat
run.bat
```

`run.bat` benutzt den Single-File-Build in `publish\`, sonst den Release- oder Debug-Build und baut sonst selbst. Das Widget erscheint rechts oben auf dem Desktop (Position lässt sich per Ziehen an der Kopfzeile ändern).

Ohne Token startet das Widget automatisch im **Demo-Modus** mit animierten Schein-Läufen — gut, um Design und Einstellungen anzuschauen:

```bat
run.bat --demo
```

---

## SmartThings-Token einrichten

1. Auf <https://account.smartthings.com/tokens> ein **Personal Access Token** erzeugen (Scopes: *Read devices and commands* / *Read all device data*). Gültigkeit: beliebig, empfohlen „kein Ablauf“ für ein Widget.
2. Widget starten → Zahnrad (Kopfzeile) oder Tray-Menü → **Einstellungen**.
3. Token einfügen → **Speichern & prüfen**. Das Widget testet den Zugang, sucht die Geräte und ordnet Waschmaschine und Trockner automatisch zu.
4. Ab jetzt läuft das Widget im Live-Modus (Header: `Live · SmartThings`).

Der Token wird **nie im Klartext** gespeichert: er liegt mit Windows-DPAPI (CurrentUser, eigene Entropie) verschlüsselt in der eigenen `config.json` und ist nur für das eigene Windows-Benutzerkonto lesbar.

---

## Build, Publish und Installer

### Entwicklung / Kompilieren

```bat
dotnet build SmartWashWidget\SmartWashWidget.csproj -c Debug
SmartWashWidget\bin\Debug\net9.0-windows\win-x64\SmartWashWidget.exe --demo
```

### Single-File-Version (kein installiertes .NET nötig)

```bat
build_release.bat
```

Ergebnis:

* `publish\SmartWashWidget.exe` — eine einzige EXE (win-x64, self-contained; benötigt kein installiertes .NET)
* `dist\SmartWashWidget-Setup-1.0.0.exe` — Inno-Setup-Installer (wenn Inno Setup installiert ist)

Der Installer bietet:

* Ziel `%ProgramFiles%\SmartWashWidget`
* Startmenü-Verknüpfung, optional Desktop-Verknüpfung
* Optionale Aufgabe **„Beim Windows-Start automatisch starten“**
* Deinstallation mit Hinweis, dass die persönliche Konfiguration (und damit das verschlüsselte Token) erhalten bleibt

Nur kompilieren ohne Installer:

```bat
dotnet publish SmartWashWidget\SmartWashWidget.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

### Build in der Cloud (GitHub Actions)

`.github\workflows\build.yml` baut bei jedem Push auf `main`, bei Pull Requests und auf Zuruf:
Widget und Symbol-Werkzeug kompilieren, danach derselbe Single-File-Build wie `build_release.bat`.
Das Ergebnis hängt als Artefakt **SmartWashWidget-win-x64** am Lauf (kein Download-Release, der Installer bleibt lokal).

---

## Programm-Symbol

Das Programmsymbol zeigt das runde Bullauge des Widgets: Trommel mit Mitnehmern, Wasserwelle und steigenden Partikeln, Akzent Eisblau.

* Master: `Assets\SmartWashWidget.ico` — 16/24/32/48 px als 32-Bit-DIB mit AND-Maske, 64/128/256 px als PNG-Eintrag
* Genutzt von `<ApplicationIcon>` in `SmartWashWidget.csproj` (EXE, Taskleiste, Explorer) und `SetupIconFile` in `installer\setup.iss`

Alle Größen auf hellem und dunklem Grund:

![Symbol in allen Größen](Assets/preview/blatt.png)

Einzelgrößen liegen als `Assets\preview\icon-<groesse>.png` (16, 24, 32, 48, 64, 128, 256 px) bereit.

Neu erzeugen (auch nach Designänderungen in `tools\icon-gen`):

```bat
dotnet run --project tools\icon-gen -c Release
```

Der Generator zeichnet ohne Bildvorlage: alle Proportionen werden als Anteil der Iconbreite gerechnet, jedes Motiv 8-fach überabgetastet und per 8×8-Blockmittelwert mit prämultipliziertem Alpha auf die Zielgröße verdichtet. Kleine Größen bekommen mehr Kontrast, breitgezeichnete Linien und Mitnehmer sowie weniger Partikel, damit das Symbol auch mit 16 px in der Taskleiste eindeutig nach Waschmaschine aussieht.

Das Tray-Symbol wird weiterhin zur Laufzeit von `Services\TrayIconService.cs` gezeichnet, weil es je Verbindungsstatus die Farbe wechselt.

---

## Steuerung

| Wo | Aktion |
|---|---|
| Kopfzeile (ziehen) | Widget verschieben (Position wird gespeichert) |
| Kopfzeile · Symbole | Demo/Live umschalten · Einstellungen · ausblenden (in den Tray) · beenden |
| Rechter Mausklick auf das Widget | vollständiges Menü (ein-/ausblenden, Demo-Modus, Milchglas-Blur, Einstellungen, Beenden) |
| Tray-Symbol, Doppelklick | Widget einblenden |
| Tray-Symbol, Rechtsklick | Menü wie oben |

Das Widget hat **keinen Rand und keine Titelleiste**; die Kopfzeile ist Teil des eigenen Layouts und bleibt trotzdem per `DragMove()` verschiebbar.

---

## Kommandozeilen-Argumente

| Argument | Wirkung |
|---|---|
| *(kein Argument)* | aus der Konfiguration: Live-Modus mit Token, sonst Demo-Modus |
| `--demo` | Demo-Modus (animierte Schein-Läufe, keine API) |
| `--live` | Live-Modus (SmartThings-API); ohne Token fällt das Widget auf Demo zurück |
| `--settings` | Einstellungsfenster direkt nach dem Start öffnen |
| `--reset` | Konfiguration auf Werkszustand zurücksetzen (Token wird verworfen) |

---

## Konfigurationsdatei

Pfad:

```
%LOCALAPPDATA%\SmartWashWidget\config.json
```

Beispiel (Token gekürzt):

```json
{
  "configVersion": 1,
  "window": {
    "left": 2178, "top": 34,
    "scale": 1.0,
    "tintAlpha": 0.0,
    "acrylicBlur": true,
    "topmost": true,
    "positionLocked": false,
    "roundedCorners": true
  },
  "smartThings": {
    "tokenProtected": "<DPAPI-verschlüsselter Token, Base64>",
    "washerDeviceId": "28b801cc-…",
    "dryerDeviceId": "62398d14-…",
    "washerLabel": "Waschmaschine",
    "dryerLabel": "Trockner",
    "autoDiscover": true,
    "lastSyncUtc": "2026-10-31T15:21:58Z"
  },
  "demoMode": false,
  "demoTempo": 4
}
```

| Schlüssel | Bedeutung |
|---|---|
| `window.left` / `top` | gespeicherte Widget-Position (Gerätepixel) |
| `window.scale` | Gesamtskalierung, 0.70 – 1.50 |
| `window.tintAlpha` | Glas-Tönung, 0.00 (freier Durchblick, Standard) – 1.00 (stark abgedunkelt) |
| `window.acrylicBlur` | Milchglas-Blur (Acrylic) über die Accent-Policy: `true` = Desktop wird weichgezeichnet, `false` = glasklar durchsichtig |
| `window.backdrop` (alt) | aus älteren Konfigurationen (`acrylic`/`mica`/`micaalt`/`none`): alles außer `none` wird zu `acrylicBlur=true`, wird nicht mehr geschrieben |
| `window.roundedCorners` | abgerundete Ecken über DWM (`DWMWCP_ROUND`) plus GDI-Fensterregion, die den Blur auf den Eckradius beschnitten |
| `window.topmost` | Widget immer im Vordergrund |
| `window.positionLocked` | Sperre gegen Verschieben |
| `smartThings.washerDeviceId` / `dryerDeviceId` | zugewiesene Geräte |
| `smartThings.autoDiscover` | Geräte automatisch suchen, wenn keine IDs bekannt sind |
| `demoTempo` | Demo-Geschwindigkeit, 1 – 24 (1 sehr langsam, 24 sehr schnell) |

Die Datei wird atomar geschrieben (Temporärdatei + `File.Replace`), damit ein Absturz sie nicht beschädigt.

---

## So funktioniert die Glasoptik

Das Widget ist ein echtes Glas-Fenster: Alle Pixel außerhalb der gezeichneten Fläche sind wirklich
durchsichtig; den weichen Durchblick liefert Windows selbst, nicht eine halbtransparent gemalte Ebene:

1. `WindowStyle="None"`, `ResizeMode="NoResize"`, `ShowInTaskbar="False"`, **`AllowsTransparency="True"`**, `Background="Transparent"`
   — der Bereich außerhalb des eigenen Layouts ist zu 100 % durchsichtig. Bewusst **kein** `WindowChrome`,
   **kein** `GlassFrameThickness`, **kein** `DWMWA_SYSTEMBACKDROP_TYPE` (Mica / Mica Alt wären nahezu undurchsichtiges
   Grau und verdichten sich ohne Fensterfokus noch zusätzlich).
2. Der **Milchglas-Blur** ist fokusunabhängig und kommt ausschließlich aus `user32!SetWindowCompositionAttribute`:
   * An  → `ACCENT_ENABLE_ACRYLICBLURBEHIND` (4), `AccentFlags = 2`, `GradientColor = 0x0110141E`. Das Acryl-Gas
     hat festes Minimal-Alpha `0x01` und ist damit quasi durchsichtig — Windows 11 weichzeichnet den Desktop
     hinter dem Widget komplett **ohne den üblichen grauen Schleier**.
   * Aus → `ACCENT_DISABLED` (0): kein Blur, kein Windows-Material — dank `AllowsTransparency="True"`
     ist das Fenster dann zu 100 % glasklar.
3. In `SourceInitialized` setzt `Interop/WindowBackdropHelper.cs` zusätzlich per `DwmSetWindowAttribute`:
   * `20` → `DWMWA_USE_IMMERSIVE_DARK_MODE` (dunkle Rahmen)
   * `33` → `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`
4. Damit der Milchglas-Blur nicht eckig über die abgerundeten Ecken hinausragt, wird das Fenster per GDI-Region beschnitten
   (`CreateRoundRectRgn` + `SetWindowRgn`, Radius = `8 DIP` mal Skalierung mal DPI-Faktor). Die Region folgt Größen-,
   Skala- und DPI-Wechseln automatisch.
5. **Der Slider „Glas-Tönung“ (0–100 %, Standard 0 %) regelt genau EINE XAML-Schicht:**
   das Alpha des RootBorder-Hintergrunds (`#0D111A`) von `5` (0 %, fast freier Durchblick) bis `235` (100 %, stark
   abgedunkelt). Keine sich aufaddierenden Ebenen, kein Eingriff in die Karten und nie in die Blur-Schicht.
6. Die Geräte-Karten sind bewusst extrem durchscheinend (8 % weißer Schimmer `#14FFFFFF`, 1-px-Lichtkante `#33FFFFFF`),
   damit Blur und Wallpaper auch *durch die Karten* sichtbar bleiben; der helle Glanz von oben ist reines Spekular-Highlight.
7. Der eigentliche Bildschirminhalt (Trommeln, Wellen, Partikel, Balken) läuft in einer einzigen
   `CompositionTarget.Rendering`-Schleife mit Delta-Zeit — die Bewegung bleibt dadurch gleichmäßig, auch während im
   Hintergrund Daten geladen werden. Netzwerkarbeit läuft vollständig `async`, die UI wird nur per `Dispatcher.BeginInvoke`
   aktualisiert.

Der Glas-Zustand steht im Fuß des Widgets („Acryl-Blur an“ / „Glasklar“) und im
Einstellungsfenster unter „Glas & Aussehen“ (mit technischem Detailtext).

---

## Fehlerbehebung

| Symptom | Ursache / Abhilfe |
|---|---|
| Kein Hintergrund-Blur, nur dunkles Glas | Windows: *Einstellungen → Personalisierung → Farbeneffekte → Transparenzeffekte* einschalten. Ohne Transparenzeffekte verweigert DWM sowohl Acrylic als auch den Accent-Blur; das Widget bleibt dann bei seiner XAML-Tönung (Fuß zeigt `Glasklar`). |
| Milchglas-Blur ragt eckig aus den abgerundeten Ecken heraus | Wird automatisch per GDI-Fensterregion (`CreateRoundRectRgn` + `SetWindowRgn`) auf den Eckradius beschnitten; die Region folgt Skalierung und DPI-Wechseln. |
| Kein Blur unter einem Spiel | Im exklusiven Vollbild übernimmt das Spiel den Bildschirm; DWM-Blur ist dort technisch nicht möglich. |
| `Live · SmartThings` wird rot / Karte zeigt „Getrennt“ | Token abgelaufen oder widerrufen, Gerät aus der SmartThings-Instanz entfernt, kein Netzwerk. Einstellungen → Token testen. |
| „Keine Geräte im SmartThings-Zugang gefunden“ | Token ohne Leseberechtigung, oder die Geräte hängen an einem anderen Hub/Modus. Token mit *Read devices*-Scope verwenden, `--reset` hilft bei verstellter Konfiguration. |
| Karte zeigt „Das Gerät hat keine washerOperatingState-Anzeige“ | Falsches Gerät zugeordnet (z. B. der TV). Einstellungen → Gerätezuordnung manuell setzen. |
| Widget nirgends sichtbar, aber im Tray | Es wurde ausgeblendet: Tray-Symbol Doppelklick. Bei „Position gesperrt“ zuerst in den Einstellungen entsperren. |
| Widget außerhalb des sichtbaren Bereichs (nach Monitorwechsel) | `run.bat --reset`, oder `window.left`/`top` in `config.json` anpassen. |
| Etwas läuft nicht | `%LOCALAPPDATA%\SmartWashWidget\error.log` ansehen (UI-Ausnahmen und Konfigurationsfehler werden dort protokolliert). |

---

## Bekannte Grenzen

* SmartThings nennt **keine Gesamtlaufzeit** von Programmen. Fortschritt und Restzeit werden aus `remainingTime`, `completionTime` und dem gemeldeten `progress` kombiniert; fehlt alles, schätzt das Widget über die gemeldete Phase.
* Bei älteren Firmware-/Hub-Kombinationen fehlen `remainingTime`/`progress` komplett — dann zeigt die Karte `--:-- h` und der Balken folgt der Phasenschätzung.
* Nach Abschluss eines Laufs bleibt „Fertig“ bis zum nächsten Poll (45 s) sichtbar, danach kehrt die Karte auf `Bereit` zurück.
* Im Standby meldet die Samsung-Firmware oft noch die Werte des letzten Laufs (`progress: 1`, alte Restzeit). Das Widget ignoriert sie, solange der Zustand nicht `Läuft`/`Pausiert` ist — deshalb erscheint im Bereit-Zustand `0 %` und `--- h`.
* Das Widget ist bewusst **kein Steuerungsclient**: es startet, pausiert oder stoppt keine Geräte (Schreibrechte am Token bleiben unnötig).
* Der weiche Desktop-Durchblick (`Acrylic`) läuft über die undokumentierte Accent-Policy (`SetWindowCompositionAttribute`). Sie ist seit Windows 10 stabil, kann aber von Windows-Updates in ihrem Verhalten geändert werden; dann zeigt der Fuß `Glasklar` und nur die XAML-Tönung bleibt.
* DWM zeigt generisch keinen Blur unter exklusiven Vollbild-Anwendungen (Spielen) und keine Backdrops, wenn die Transparenzeffekte ausgeschaltet sind — das ist eine Systemeinschränkung, kein Widget-Fehler.
* Unter Windows 10 (Build < 22000) gibt es keine DWM-Backdrops: dort bleibt nur die XAML-Tönung.

---

## Datenhaltung und Datenschutz

* Token: DPAPI (`ProtectedData`, Scope `CurrentUser`, eigene Entropie) → nur dieses Windows-Benutzerkonto kann ihn lesen. Kein Token im Quellcode, kein Token im Installer.
* Sonstige Einstellungen, Geräte-IDs und Anzeigenamen: lokale `config.json` im eigenen AppData-Ordner.
* Fehlerprotokoll: `%LOCALAPPDATA%\SmartWashWidget\error.log`.
* Es werden keine Daten an Dritte gesendet; die einzigen Netzwerkverbindungen gehen zu `api.smartthings.com`.
* Das Widget nutzt ausschließlich Lesezugriff (`GET` auf Geräte-Status) und sendet keine Befehle an die Geräte.
* In diesem Repository liegen **keine Zugangsdaten**. `.gitignore` hält alle Token-Dateien (`*Token*`, `*.pat`) und `config.json`-Reste aus der Historie heraus — ein eigenes Token gehört trotzdem nicht in den Projektordner, sondern nur in die verschlüsselte Konfiguration unter `%LOCALAPPDATA%`.

---

## Projektstruktur

```
SmartWashWidget/
  SmartWashWidget.csproj        .NET 9, WPF + WinForms-NotifyIcon, keine NuGet-Pakete
  app.manifest                  DPI-/Kompatibilitätsmanifest
  App.xaml / App.xaml.cs        Einstieg: Einzelinstanz, Demo/Live, Tray, Polling
  MainWindow.xaml(.cs)          Glasfenster, Kopfzeile, Footer, Kontextmenü, DragMove
  SettingsWindow.xaml(.cs)      Einstellungen: Token, Geräte, Darstellung, Demo
  Themes/Win11Styles.xaml       Farben, Typo, Glas-Karten, Toggle, Glyph-Buttons
  Controls/MachineCard.xaml(.cs) Karte: Status-Pill, Fortschrittsbalken, Phasenzeile
  Controls/DrumVisual.cs        Trommel, Wasserwelle, Hitzewellen, Partikel (60 FPS)
  Controls/AnimationEngine.cs   Ein Frame-Treiber für alle Karten + Demo-Tick
  Models/MachineSnapshot.cs     Zustandsmodell, RunState, Phasen, DiscoveredDevice
  Services/SmartThingsClient.cs HttpClient + JSON, Geräteerkennung, Status-Parsing
  Services/PollingCoordinator.cs Intervalle, Backoff, Ereignisse
  Services/AppConfigService.cs   Konfiguration, DPAPI-Token, atomares Speichern
  Services/DemoSimulation.cs     Demo-Zyklen und -Phasen
  Services/TrayIconService.cs    NotifyIcon + Menü
  Interop/WindowBackdropHelper.cs Blur an/aus (Accent-Policy), Tönungs-Alpha 5..235, runde Fensterregion (SetWindowRgn)
  Interop/AccentPolicy.cs        user32!SetWindowCompositionAttribute (fokusunabhängiger Acryl-Blur)
run.bat                         Start (nutzt publish/Release/Debug, baut sonst selbst)
build_release.bat               Single-File-Publish + Inno-Setup-Installer
installer/setup.iss             Inno-Setup-Skript
Assets/SmartWashWidget.ico      Programmsymbol (Bullauge) für EXE und Installer
Assets/preview/                 Vorschauen des Symbols (blatt.png, icon-<groesse>.png)
tools/icon-gen/                 Generator für das Symbol (GDI+, parametrisch, ohne Bildvorlage)
Screenshots/                    Vorschau-GIFs und Einstellungsbilder für dieses README
.github/workflows/build.yml     CI: Kompilieren + Single-File-Publish als Artefakt
LICENSE                         MIT-Lizenz
.editorconfig, .gitattributes   Einheitliche Einrückung und Zeilenenden
```

---

## Kurzreferenz

```bat
run.bat --settings        :: Einstellungen öffnen
run.bat --demo            :: Design ansehen
build_release.bat         :: EXE + Installer erzeugen
```

Version 1.0.0 · Windows 11 (Build 22621 oder neuer empfohlen) · .NET 9 SDK zum Bauen erforderlich (`dotnet --version` → 9.x).

---

## Lizenz

MIT-Lizenz, siehe [LICENSE](LICENSE). Du darfst den Code nutzen, verändern und weiterverteilen — auch kommerziell — solange der Copyright-Hinweis erhalten bleibt.

Samsung, SmartThings und SmartThings-API sind Marken der Samsung Electronics Co., Ltd. Dieses Projekt ist unabhängig und nicht von Samsung gesponsert oder autorisiert; der Zugang erfolgt über deinen eigenen SmartThings-Account und dein eigenes Token.
