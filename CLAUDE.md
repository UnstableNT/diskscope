# DiskScope

Open-Source-Laufwerkswerkzeug für Windows in C# / .NET 10 mit WinUI-3-Oberfläche und CLI. Die vollständige Spezifikation steht in `SPEC.md`; sie ist die einzige fachliche Vorlage.

## Clean-Room-Regeln

- Implementiere ausschließlich aus `SPEC.md`, öffentlicher Microsoft-Dokumentation und öffentlichen Standards (ATA/ACS, SAT, NVMe).
- Öffne, analysiere oder dekompiliere keine Binärdateien, Handbücher oder Screenshots anderer Laufwerkswerkzeuge, auch nicht mit REA, Ghidra oder ähnlichen Tools.
- Die Oberfläche wird eigenständig im Fluent-Design gestaltet (eigene Icons, Farben, Texte).

## Arbeitsweise

- Meilensteine aus `SPEC.md` Abschnitt 10 der Reihe nach umsetzen; nach jedem Meilenstein `dotnet build -warnaserror`, `dotnet test` und `dotnet format --verify-no-changes`.
- Schreibende Operationen auf Rohgeräte nur gegen Test-VHDs ausführen, nie gegen echte Laufwerke (SPEC Abschnitt 9 und 11).
- Fehlen .NET SDK oder Windows App SDK, den Nutzer fragen, bevor etwas installiert wird.
- Kommunikation mit dem Nutzer auf Deutsch.
