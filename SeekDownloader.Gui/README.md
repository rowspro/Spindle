# Spindle

*Everything on one spindle.* — een muziekbibliotheek-app (Avalonia, macOS) bovenop de SeekDownloader-kern:
downloaden (Soulseek), sorteren op tags, FLAC→ALAC converteren, metadata + albumhoezen bewerken,
en een Apple Music-overzicht. De command-line SeekDownloader-tool blijft los bestaan.
De command-line tool blijft volledig werken; deze GUI hergebruikt dezelfde
`DownloadService`/`FileSeekService` (de Soulseek-logica) en vervangt alleen de
argument-parsing en console-output door een venster.

## Draaien

De .NET 8 SDK is nodig. Op macOS via Homebrew geïnstalleerd is `dotnet` keg-only,
dus zet eerst het pad:

```bash
export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
cd SeekDownloader.Gui
dotnet run -c Release
```

## macOS .app-bundle

```bash
export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
cd SeekDownloader.Gui
./package-macos.sh osx-arm64      # of osx-x64 voor Intel
```

Resultaat: `dist/SeekDownloader.app` (self-contained, dubbelklikbaar).

## Functies

- Verbinding (Soulseek-gebruiker/wachtwoord/poort), map- en bestandskiezers.
- Zoekterm of zoekbestand.
- **Album-modus**: haalt één compleet album op (beste map van één uploader),
  ontdubbeld op tracknaam, met voorkeur voor lossless (flac).
- Geavanceerde opties: threads, match-percentages, extensies, filters, tag-checks,
  in-memory downloads, **submap per uploader** (standaard uit), archiefbestand.
- Live voortgang per download-thread, statistieken en foutenlog.
- Instellingen worden bewaard in
  `~/Library/Application Support/SeekDownloader/settings.json`.

## Opbouw

- De kern-broncode wordt via `<Compile Include>` ingelinkt uit `../SeekDownloader/`.
- `SeekRunner.cs` bevat de orchestratie (overgenomen uit de CLI) met annuleer-
  ondersteuning, album-modus en zonder de console-`ProgressThread`.
- De UI peilt elke 0,5 s de publieke status van `DownloadService`.
- Visueel ontwerp gebaseerd op een Google Stitch-design (donker, indigo-accent, Inter).
