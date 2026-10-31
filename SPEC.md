# DiskScope – Spezifikation

Eigenständiges Open-Source-Werkzeug für Windows zur Analyse, Bewertung und Überwachung von Laufwerken (HDD, SATA-SSD, NVMe, USB). Vollständig in C# / .NET, Oberfläche in WinUI 3.

Diese Spezifikation beschreibt **was** das Programm tun soll. Grundlage sind öffentlich zugängliche Informationen (Funktionsübersichten vergleichbarer Werkzeuge, Beobachtung ihrer Bedienoberfläche von außen), die Microsoft-Dokumentation zu Windows-Storage-APIs sowie die öffentlichen Standards ATA/ACS, SAT (SCSI/ATA Translation) und NVMe Base Specification. Architektur, Algorithmen und Gestaltung sind eigenständig zu entwerfen.

---

## 1. Ziele und Rahmen

- **Plattform:** Windows 10 (1903+) und Windows 11, x64 (ARM64 optional).
- **Laufzeit:** .NET 10 (LTS), `net10.0-windows10.0.19041.0`.
- **Rechte:** Rohzugriff auf Laufwerke erfordert Administratorrechte. Das Programm erkennt fehlende Rechte und zeigt einen klaren Hinweis; Funktionen ohne Rohzugriff (Ordnernutzung, Datei-Benchmark) bleiben ohne Admin nutzbar.
- **Zwei Oberflächen:** WinUI-3-App (Hauptanwendung) und CLI (Automatisierung, Tests). Beide nutzen dieselbe Kernbibliothek.
- **Sicherheit vor Funktionsumfang:** Jede schreibende Operation auf Rohgeräte ist standardmäßig gesperrt (Abschnitt 9).
- **Gestaltung:** Bewährter Aufbau dieser Werkzeugklasse (Laufwerksauswahl oben, Funktionen als Tabs, Diagramm links, Steuerung und Kennzahlen rechts), aber zeitgemäß im Fluent-Design von Windows 11. Eigene Icons, Farben und Texte.

## 2. Architektur

```
diskscope/
  DiskScope.sln
  Directory.Build.props          # gemeinsame Einstellungen: Nullable, TreatWarningsAsErrors, LangVersion latest
  src/
    DiskScope.Core/              # Klassenbibliothek: Gerätezugriff, Protokolle, Tests, Datenmodelle
      Native/                    # P/Invoke (CsWin32) und Strukturen; einziger Ort mit unsafe-Code
      Devices/                   # Laufwerksliste, Info, Features, Volumes
      Smart/                     # ATA-SMART, NVMe-Health, Attributtabelle, Bewertung
      Benchmarks/                # Benchmark, Zufallszugriff, Zusatztests, Datei-Benchmark
      Scanning/                  # Fehlerscan
      Monitoring/                # ETW-Monitor, Temperatur
      Destructive/               # alle schreibenden Rohgerät-Operationen (Abschnitt 9)
      Data/smart-attributes.json # Attributnamen und Darstellungsarten
    DiskScope.Cli/               # Konsolen-App "diskscope"
    DiskScope.App/               # WinUI 3 (Windows App SDK), MVVM
  tests/
    DiskScope.Core.Tests/        # Unit-Tests
    DiskScope.Integration.Tests/ # VHD-basierte Tests (Abschnitt 11)
```

**Pakete (NuGet, aktuelle stabile Versionen):**

| Zweck | Paket |
|---|---|
| Win32-Aufrufe | `Microsoft.Windows.CsWin32` (Source Generator, `NativeMethods.txt`) |
| CLI | `System.CommandLine` |
| JSON | `System.Text.Json` (mit Source Generation) |
| Logging | `Microsoft.Extensions.Logging` + `Serilog.Extensions.Logging` + `Serilog.Sinks.File` |
| ETW | `Microsoft.Diagnostics.Tracing.TraceEvent` |
| GUI | `Microsoft.WindowsAppSDK`, `CommunityToolkit.Mvvm`, `CommunityToolkit.WinUI.Controls` |
| Diagramme | `LiveChartsCore.SkiaSharpView.WinUI` oder `ScottPlot.WinUI` |
| Blockraster | `Microsoft.Graphics.Win2D` (`CanvasControl`) |
| Tests | `xunit`, `FluentAssertions` (oder `Shouldly`) |

**Grundsätze:**
- `DiskScope.Core` hat keine Abhängigkeit zur GUI; alle lang laufenden Operationen sind `async`, nehmen ein `CancellationToken` und melden Fortschritt über `IProgress<T>`.
- Gerätehandles als `SafeFileHandle`, Puffer für ungepuffertes I/O seitenausgerichtet über `NativeMemory.AlignedAlloc`, Freigabe in `IDisposable`.
- `unsafe` nur in `DiskScope.Core/Native`.

## 3. Datenmodell (Kern)

```csharp
public readonly record struct DriveId(int Number);           // PhysicalDrive-Nummer

public enum BusType { Sata, Nvme, Usb, Sas, Scsi, Raid, Virtual, Other }
public enum MediaKind { Hdd, Ssd, Unknown }

public sealed record DriveInfo(
    DriveId Id,
    string DevicePath,                  // \\.\PhysicalDriveN
    string Model,
    string? Vendor,
    string? Serial,
    string? Firmware,
    BusType Bus,
    string? Link,                       // z. B. "SATA 3 (6.0 Gb/s)", "PCIe 4.0 x4"
    MediaKind Media,
    ulong SizeBytes,
    uint LogicalSectorSize,
    uint PhysicalSectorSize,
    ulong? BufferSize,
    bool IsSystemDisk,
    IReadOnlyList<VolumeInfo> Volumes,
    IReadOnlyList<Feature> Features);

public sealed record VolumeInfo(
    string Label, string MountPoint,    // "Daten", "D:"
    ulong Capacity, ulong Free,
    string FileSystem, string Serial,
    ulong Alignment);                   // Startoffset der Partition

public sealed record Feature(FeatureKind Kind, bool Supported, bool? Enabled);
```

Alle Ergebnistypen sind unveränderliche `record`s und per `System.Text.Json` serialisierbar (CLI-Ausgabe, Export).

## 4. Funktionen

Priorität: **P1** = erste lauffähige Version, **P2** = danach, **P3** = optional.

### 4.1 Hauptfenster (P1)

- **Kopfzeile:** Auswahlliste der Laufwerke (Modellname, Größe), daneben Temperaturanzeige des gewählten Laufwerks mit Farbe nach Schwellwert, Schaltflächen für „Ergebnisse kopieren“, „Screenshot speichern“, „Einstellungen“.
- **Tabs** (Reihenfolge): Benchmark · Info · Gesundheit · Fehlerscan · Ordnernutzung · Löschen · Datei-Benchmark · Monitor · Zufallszugriff · Zusatztests.
- **Standard-Aufbau eines Test-Tabs:** großes Diagramm links; rechte Seitenleiste mit Start/Stopp-Schaltfläche, Optionen und Kennzahlen-Kacheln.
- **Bereich festlegen:** Tabs mit Rohgerät-Tests haben eine Option „Bereich“ (Checkbox + Dialog mit Start/Ende in GB oder %).
- Während ein Test läuft, sind Laufwerkswechsel und andere Tests gesperrt.

### 4.2 Info (P1)

**Geräteinformation:** Name/Modell, Firmware, Seriennummer, Puffergröße, Kapazität (gerundet und exakt in Bytes), Schnittstelle mit Link-Geschwindigkeit.

Quellen: `IOCTL_STORAGE_QUERY_PROPERTY` (`StorageDeviceProperty`, `StorageAccessAlignmentProperty`, `StorageDeviceSeekPenaltyProperty`, `StorageDeviceTrimProperty`), `IOCTL_DISK_GET_LENGTH_INFO`, `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX`, `IOCTL_SCSI_GET_ADDRESS`; bei ATA IDENTIFY DEVICE (Command `0xEC`, Wörter 10–19 Seriennummer, 23–26 Firmware, 27–46 Modell, byte-vertauscht), Link-Geschwindigkeit aus SATA-Capabilities (Wort 76/77); bei NVMe Identify Controller.

**Unterstützte Funktionen** als Liste mit Haken/Kreuz, aus IDENTIFY DEVICE (ATA) bzw. Identify Controller (NVMe, sinngemäß abbilden):
S.M.A.R.T., Firmware-Update (DOWNLOAD MICROCODE), Power Management, Advanced Power Management, Interface Power Management (HIPM/DIPM), 48-Bit-Adressierung, NCQ, TRIM, Automatic Acoustic Management, Power-Up in Standby, Read Look-Ahead, Write Cache, SCT-Befehle, Host Protected Area, Device Configuration Overlay, Security-Modus.

**Volume-Tabelle:** Bezeichnung + Laufwerksbuchstabe, Kapazität, frei, Belegung in %, Dateisystem, Volume-Seriennummer, Partitionsausrichtung. Zuordnung über `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`, Werte über `GetVolumeInformationW`, `GetDiskFreeSpaceExW`.

### 4.3 Gesundheit / S.M.A.R.T. (P1)

**Tabelle** mit Spalten: ID (hex) + Name, Wert (lesbar interpretiert, z. B. „28 °C“, „1003 GB“, „100 %“), Aktuell, Schlechtester, Schwellwert, Rohdaten, Status (OK/Warnung/Fehler).

**Fußzeile:** Gesamtstatus (farbig), Fortschrittsbalken bis zur nächsten automatischen Aktualisierung mit Restzeit (Intervall einstellbar, Standard 5 min), Schaltflächen „Aktualisieren“ und „Protokoll“ (Verlauf gespeicherter Abfragen mit Änderungen je Attribut; P2).

**ATA/SATA** (über `IOCTL_ATA_PASS_THROUGH`, 512-Byte-Puffer):
- SMART READ DATA (Command `0xB0`, Feature `0xD0`, LBA Mid `0x4F`, LBA High `0xC2`)
- SMART READ THRESHOLDS (Feature `0xD1`)
- SMART RETURN STATUS (Feature `0xDA`)
- Attributtabelle: 30 Einträge à 12 Byte ab Offset 2 (ID, Flags, aktuell, schlechtester, 6 Byte Rohwert); Schwellwerte gleich aufgebaut.

**Attributnamen und Interpretation:** Eigene Datentabelle `Data/smart-attributes.json` (eingebettete Ressource), getrennt in allgemeine IDs (z. B. 5, 9, 12, 194, 197, 198, 199) und herstellerspezifische Abschnitte, ausgewählt über Modell-Präfix. Je Eintrag: Name, Darstellungsart (Zähler, Stunden, Temperatur = niedrigstes Byte, Prozent, Datenmenge mit Einheit). Inhalte aus öffentlichen Herstellerunterlagen und Standards; unbekannte IDs als „Unbekanntes Attribut“.

**Rückfallebenen:** USB-Gehäuse per SAT (`IOCTL_SCSI_PASS_THROUGH_DIRECT`, ATA PASS-THROUGH (16), Opcode `0x85`); ältere Treiber per `SMART_GET_VERSION` / `SMART_RCV_DRIVE_DATA` / `SMART_SEND_DRIVE_COMMAND`; zuletzt `IOCTL_STORAGE_PREDICT_FAILURE` für eine Ja/Nein-Aussage; sonst „SMART nicht verfügbar“.

**NVMe** (`IOCTL_STORAGE_QUERY_PROPERTY`, `StorageDeviceProtocolSpecificProperty`, `ProtocolTypeNvme`, `NVMeDataTypeLogPage`, Log `0x02`): Felder des SMART/Health-Logs als Tabellenzeilen (Critical Warning, Composite Temperature in Kelvin → °C, Available Spare, Spare-Schwelle, Percentage Used, Data Units Read/Written ×512 000 Byte, Host Read/Write Commands, Power Cycles, Power-On Hours, Unsafe Shutdowns, Media Errors, Error Log Entries).

**Bewertung:** Dokumentierte eigene Regeln, z. B. Warnung bei 5/197/198 > 0 oder NVMe Available Spare < Schwelle, Fehler bei überschrittenem Schwellwert oder gesetztem Critical Warning.

**Selbsttests (P2):** ATA SMART EXECUTE OFF-LINE IMMEDIATE (Feature `0xD4`; 1 = kurz, 2 = erweitert, 127 = abbrechen), Fortschritt aus SMART READ DATA. NVMe-Selbsttest nur, wenn Windows ihn zulässt; sonst „nicht unterstützt“.

### 4.4 Benchmark (P1 lesen, P3 schreiben)

**Optionen:** Lesen/Schreiben (Schreiben nur unter Abschnitt 9), Bereich, Anzeige ein/aus für Transferrate, Zugriffszeit, Burst-Rate.

**Messung:**
- Gerät öffnen mit `GENERIC_READ`, `FILE_FLAG_NO_BUFFERING`; Puffer seitenausgerichtet, Offsets/Längen sektorausgerichtet; Zugriff über `RandomAccess.Read` auf dem `SafeFileHandle` oder direkt `ReadFile`.
- Transferrate: Messpunkte gleichmäßig über den Bereich, je Punkt Block wählbarer Größe (Standard 64 KiB, Einstellungen 512 B–8 MiB) mehrfach lesen; Zeit über `Stopwatch.GetTimestamp`.
- Zugriffszeit: zufällige Einzelzugriffe kleiner Größe über den Bereich.
- Burst-Rate: wiederholtes Lesen desselben Bereichs (aus dem Laufwerks-Cache).
- CPU-Last während des Tests über `GetSystemTimes`.
- Messschleife in einem eigenen Thread mit erhöhter Priorität, ohne Allokationen pro Iteration.

**Darstellung:** Diagramm mit Laufwerksposition (GB) auf der x-Achse, MB/s links (Linie) und ms rechts (Punkte für Zugriffszeiten); Achsen skalieren automatisch, live aktualisiert. Kennzahlen: Minimum, Maximum, Durchschnitt, Zugriffszeit, Burst-Rate, CPU-Last.

### 4.5 Fehlerscan (P1)

**Optionen:** Schnellscan (Stichproben statt vollständig), Kartentyp (Fehlerkarte; optional Geschwindigkeitskarte, die jeden Block nach Lesegeschwindigkeit einfärbt), Bereich.

**Ablauf:** Bereich in eine feste Anzahl gleich großer Blöcke teilen (Rastergröße passt sich der Fensterfläche an), jeden Block lesen, fehlerhafte Blöcke auf Sektorgröße verfeinern. Rein lesend.

**Darstellung:** Raster von Kacheln mit Legende (beschädigt / gut / ungeprüft, bei Geschwindigkeitskarte Farbskala). Kennzahlen: beschädigte Blöcke (Anzahl und %), Scan-Geschwindigkeit, aktuelle Position. Liste fehlerhafter LBAs exportierbar (JSON/CSV).

### 4.6 Ordnernutzung (P2)

Volume des gewählten Laufwerks rekursiv durchlaufen (`Parallel.ForEachAsync`, `FileSystemEnumerable`), Baumansicht mit Spalten Ordner, Größe, Anteil (Balken), Dateianzahl; sortierbar, aufklappbar. Schaltfläche „Scannen“; Reparse-Points (Symlinks/Junctions) nicht verfolgen, Zugriffsfehler überspringen und zählen.

### 4.7 Löschen (P3)

**Optionen:** Prüflesen, Bereich, Füllmuster (Nullen, Zufallsdaten, festes Byte), Anzahl Durchgänge (1–n).

**Darstellung:** Blockraster wie Fehlerscan (überschrieben / Fehler / offen), Fortschritt je Durchgang, Geschwindigkeit, Fehler, Position.

Vorher alle Volumes sperren und aushängen (`FSCTL_LOCK_VOLUME`, `FSCTL_DISMOUNT_VOLUME`). Strengste Regeln aus Abschnitt 9.

### 4.8 Datei-Benchmark (P2)

**Optionen:** Ziel-Volume, Dateigröße (Standard 500 MiB), Datenmuster (Nullen, Zufall), Warteschlangentiefe für den Mehrfachtest (Standard 32).

**Messungen:**
- Transfergeschwindigkeit über die Dateiposition (Lesen und Schreiben als zwei Kurven).
- Ergebnistabelle Lesen/Schreiben: sequenziell, 4 KiB zufällig einzeln (QD1), 4 KiB zufällig mehrfach (QD n).
- Optional „Blockgrößenmessung“: Durchsatz Lesen/Schreiben je Blockgröße von 0,5 KiB bis 8 MiB (Zweierpotenzen) als Balkendiagramm; Optionen Testdateigröße und Pause zwischen den Messungen.

Datei mit `FileOptions.WriteThrough | FileOptions.Asynchronous` plus `FILE_FLAG_NO_BUFFERING` (Flag `0x20000000` über `File.OpenHandle`-Optionen), QD > 1 über parallele `RandomAccess.ReadAsync`/`WriteAsync`. Testdatei auch bei Abbruch löschen (`FileOptions.DeleteOnClose`). Ohne Admin nutzbar.

### 4.9 Monitor (P2)

Echtzeit-Erfassung von Datenträger-I/O des gewählten Laufwerks über ETW (Kernel-Disk-I/O-Ereignisse via `TraceEvent`; Admin).

**Oben:** Zeitverlauf der Rate (KB/s) für Lesen und Schreiben, gleitendes Fenster (Standard 30 min). Seitenleiste je Richtung: aktuelle Rate, Maximum, I/O-Anzahl, Gesamtmenge; verstrichene Zeit.

**Unten, Unter-Tabs:**
- **Blockgröße:** Histogramm der Anfragen nach Größe (0,5 K … 2 M, ≤ 64 K, > 64 K), umschaltbar zwischen Anzahl I/O und Datenmenge.
- **Position:** Zugriffe über die Laufwerksposition (Heatmap).
- **Programme:** Tabelle je Prozess mit gelesener/geschriebener Menge und I/O-Anzahl.
- **Statistik:** Summen, Mittelwerte, Spitzen.

ETW-Sitzung eindeutig benennen und beim Start eine verwaiste Sitzung gleichen Namens stoppen.

### 4.10 Zufallszugriff (P1 lesen, P3 schreiben)

**Optionen:** Lesen/Schreiben, Bereich, 4-KiB-Ausrichtung, Auswahl der Transfergrößen 512 B, 4 KiB, 64 KiB, 1 MiB und „zufällig“ (gemischte Größen).

**Darstellung:** Streudiagramm Zugriffszeit (ms) über Position, je Transfergröße eigene Farbe. Tabelle je Größe: Operationen/s, mittlere Zugriffszeit, maximale Zugriffszeit, mittlere Geschwindigkeit.

### 4.11 Zusatztests (P2 lesen, P3 schreiben)

**Liste** mit Auswahl-Checkbox und Ergebnisspalten I/O/s, Zeit (ms), Transfer (MB/s):
- Zufällige Positionierung (Sektorgröße), 4 KiB, 64 KiB, 8 MiB
- „Butterfly“-Positionierung (abwechselnd Anfang und Ende, Abstand schrumpft zur Mitte)
- Sequenziell außen / Mitte / innen
- Burst-Rate

**Optionen:** Lesen/Schreiben, Bereich, 4-KiB-Ausrichtung.

**Cache-Test (Checkbox):** Durchsatz bei wachsender Bereichsgröße (0–64 MiB) als Kurve, um die Cache-Größe abzuschätzen.

### 4.12 Temperatur (P1)

Quellen: SMART-Attribut 194 bzw. 190 (ATA, niedrigstes Rohbyte), Composite Temperature (NVMe), `StorageDeviceTemperatureProperty`. Anzeige in der Kopfzeile, °C/°F umschaltbar, Warnschwelle einstellbar (Standard 50 °C HDD, 70 °C SSD), Verlauf min/max/aktuell, Abfrageintervall einstellbar.

### 4.13 Laufwerksstatus-Übersicht (P2)

Kompaktes Zusatzfenster oder Infobereich-Symbol: alle Laufwerke mit Modell, Temperatur, Gesundheits-Ampel; Benachrichtigung (App-Notification) bei Statuswechsel.

### 4.14 Akustik- und Energieverwaltung (P3)

AAM: ATA SET FEATURES (`0xEF`), Feature `0x42`, Stufe im Sector Count (`0x80` leise … `0xFE` schnell), `0xC2` aus; nur wenn IDENTIFY Wort 83 Bit 9 gesetzt. APM analog mit `0x05`/`0x85`. Aktuellen Wert vorher anzeigen (Wort 94 bzw. 91). Erreichbar über den Info-Tab.

### 4.15 Export (P2)

- „Ergebnisse kopieren“: aktuelle Tab-Ergebnisse als Text in die Zwischenablage.
- „Screenshot speichern“: Tab-Inhalt als PNG/JPG (`RenderTargetBitmap`); optional automatisch nach jedem Test mit Muster `{modell}_{test}_{datum}.png`.
- Alle Ergebnisse zusätzlich als JSON.

## 5. CLI

```
diskscope list [--json]
diskscope info <drive> [--json]
diskscope smart <drive> [--json]
diskscope bench <drive> [--block 64K] [--range 0-100] [--json]
diskscope random <drive> [--sizes 512,4K,64K,1M] [--align4k] [--json]
diskscope extra <drive> [--tests ...] [--json]
diskscope scan <drive> [--quick] [--json]
diskscope filebench <volume> [--size 500M] [--pattern zero|random] [--qd 32] [--json]
diskscope temp [--watch 60s]
```

`<drive>` = PhysicalDrive-Nummer. Exit-Codes: 0 OK, 1 Fehler, 2 fehlende Rechte, 3 Gesundheitswarnung. Als Single-File-Anwendung veröffentlichbar (`PublishSingleFile`).

## 6. GUI (WinUI 3)

- MVVM mit `CommunityToolkit.Mvvm` (`ObservableObject`, `RelayCommand`/`AsyncRelayCommand`); je Tab ein ViewModel, das Kernoperationen mit `CancellationToken` startet und `IProgress<T>`-Meldungen auf dem UI-Thread verarbeitet.
- Dependency Injection mit `Microsoft.Extensions.DependencyInjection`.
- `TabView` oder `NavigationView` (oben) für die Tabs; Mica-Hintergrund, Hell/Dunkel nach Systemeinstellung, hohe DPI.
- Diagramme mit dunklem Plotbereich und klaren Farben je Datenreihe; Blockraster mit Win2D für Tausende Kacheln.
- Kennzahlen als Kacheln (Wert groß, Bezeichnung klein) in der rechten Spalte.
- Einstellungen als JSON in `%APPDATA%\DiskScope\settings.json`.
- Kein erzwungenes `requireAdministrator`: Startet die App ohne Admin, Hinweisleiste (`InfoBar`) mit „Als Administrator neu starten“ (`ProcessStartInfo` mit `Verb = "runas"`).
- Paketierung: unverpackte, self-contained App; optional MSIX.

## 7. Fehlerbehandlung

- Win32-Fehler → `DiskScopeException` mit Funktionsname und Win32-Code (`Marshal.GetLastPInvokeError`).
- „Nicht unterstützt“ ist kein Fehler, sondern ein Ergebniszustand (`SmartResult.Unsupported(reason)` o. ä.).
- Abbruch über `OperationCanceledException`; GUI zeigt ihn als „Abgebrochen“, nicht als Fehler.

## 8. Logging

`Microsoft.Extensions.Logging` mit Serilog-Datei-Sink nach `%APPDATA%\DiskScope\logs\` (rotierend), CLI optional `--verbose` auf die Konsole. Seriennummern standardmäßig maskieren.

## 9. Schutzregeln für schreibende Operationen

1. Schreibzugriff auf Rohgeräte nur im Namespace `DiskScope.Core.Destructive`; alle anderen Teile öffnen Geräte nur lesend.
2. Nie auf das System-/Boot-Laufwerk schreiben – ohne Ausnahme.
3. Schreibtests nur auf Laufwerken ohne eingehängte Volumes oder nach explizitem Aushängen.
4. Bestätigung in der GUI durch Eintippen der Modellbezeichnung; in der CLI durch `--i-understand-data-loss <seriennummer>`.
5. Dry-Run-Modus, der nur die betroffenen Bereiche anzeigt.

## 10. Meilensteine

| # | Inhalt | Fertig, wenn |
|---|---|---|
| M1 | Solution, `DiskScope.Core` mit Laufwerksliste und Info (inkl. Feature-Liste, Volumes), CLI `list`/`info` | `diskscope info N --json` liefert alle Felder aus 4.2 |
| M2 | SMART ATA + NVMe inkl. Rückfallebenen, Attributtabelle, Temperatur, CLI `smart`/`temp` | Werte stimmen mit `Get-PhysicalDisk \| Get-StorageReliabilityCounter` überein |
| M3 | Benchmark lesen, Zufallszugriff lesen, CLI `bench`/`random` | Messungen auf VHD und echtem Laufwerk reproduzierbar |
| M4 | Fehlerscan, CLI `scan` | Scan einer VHD vollständig, Abbruch funktioniert |
| M5 | WinUI-App mit Kopfzeile und Tabs Benchmark, Info, Gesundheit, Fehlerscan, Zufallszugriff | Alle P1-Funktionen in der GUI bedienbar, Live-Diagramme |
| M6 | P2: Datei-Benchmark, Zusatztests (lesen), Monitor, Ordnernutzung, Statusübersicht, Selbsttests, Export | |
| M7 | P3: Schreibtests, Löschen, AAM/APM | Schutzregeln aus Abschnitt 9 durch Tests abgedeckt |

## 11. Tests

- **Unit-Tests:** Parser für SMART-Tabellen, NVMe-Log, IDENTIFY-Felder und Feature-Bits mit selbst erzeugten Testpuffern; Attributtabelle (Interpretation je Darstellungsart); Bewertungsregeln.
- **Integrationstests mit virtuellen Laufwerken:** VHDX per `New-VHD`/`Mount-VHD` oder `diskpart` (`create vdisk`, `attach vdisk`); Benchmarks, Scan und alle schreibenden Funktionen nur gegen diese VHDs. Admin-Tests mit eigener Kategorie (`[Trait("Category", "Admin")]`) und gesondert ausführen.
- **Schutzregeln:** Tests belegen, dass Schreibzugriffe auf Systemlaufwerk und Laufwerke mit Volumes abgelehnt werden.
- **Prüfungen:** `dotnet build -warnaserror`, `dotnet test`, `dotnet format --verify-no-changes`.

## 12. Lizenz

MIT.

## Anhang A: Schnittstellen-Übersicht

Alle Schnittstellen sind öffentlich von Microsoft dokumentiert; Details in den genannten Abschnitten. Mit CsWin32 über `NativeMethods.txt` generieren, wo verfügbar; fehlende Strukturen (z. B. `ATA_PASS_THROUGH_EX`, NVMe-Protokollstrukturen) selbst als `[StructLayout(LayoutKind.Sequential)]` deklarieren.

| Schnittstelle | Zweck | Abschnitt |
|---|---|---|
| `IOCTL_STORAGE_QUERY_PROPERTY` | Geräteeigenschaften, NVMe-Identify und -Logs, Temperatur | 4.2, 4.3, 4.12 |
| `IOCTL_ATA_PASS_THROUGH` | ATA-Befehle (IDENTIFY, SMART, SET FEATURES) | 4.2, 4.3, 4.14 |
| `IOCTL_SCSI_PASS_THROUGH_DIRECT` | SAT für USB-Gehäuse | 4.3 |
| `SMART_GET_VERSION` / `SMART_RCV_DRIVE_DATA` / `SMART_SEND_DRIVE_COMMAND` | SMART über ältere Treiber | 4.3 |
| `IOCTL_STORAGE_PREDICT_FAILURE` | Minimaler Gesundheitsstatus | 4.3 |
| `IOCTL_SCSI_GET_ADDRESS` | Port/Bus/Target | 4.2 |
| `IOCTL_DISK_GET_DRIVE_GEOMETRY(_EX)` / `IOCTL_DISK_GET_LENGTH_INFO` | Größe und Geometrie | 4.2 |
| `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` | Volume ↔ Laufwerk | 4.2 |
| `FSCTL_LOCK_VOLUME` / `FSCTL_DISMOUNT_VOLUME` | Volumes vor Schreibtests aushängen | 4.7 |
| ETW-Kernel-Trace (über `TraceEvent`) | Monitor | 4.9 |
