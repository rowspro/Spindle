# Spindle 2.0 — Plan: de beste offline muziekbibliotheek-tool

Status: vastgesteld 10 jun 2026 (Lucas + Fable 5). Dit document is leidend voor de herbouw.

## Visie

Spindle wordt het thuis van de collectie, geen verzameling losse gereedschappen.

1. **De bieb is altijd al ingelezen** — persistente index, geen scan-knoppen, alles direct.
2. **Alles is omkeerbaar** — één globale undo-geschiedenis over tags, verplaatsingen, verwijderingen.
3. **Eén doorlopende lijn** — Nicotine+ dropt → Nieuw vlagt → goedkeuren → bieb perfect → iPod synct.
4. **De bieb is iets om te zien** — covers overal; de Galaxy als kroonjuweel.

## Architectuur

- **Avalonia blijft** (heroverwogen): de motor (ATL, ALAC-conversie, MusicBrainz/Apple/Discogs, AcoustID) is C# en bewezen. Herbouw gefaseerd in deze repo; elke fase opleverbaar.
- **3D zonder engine**: de Galaxy is een custom SkiaSharp-control (eigen perspectief-projectie, depth-sort, orbit/zoom). 15k punten @ 60fps haalbaar.

## Fase 0 — Fundament (eerst!)

- **SQLite-index** (`~/Library/Application Support/SeekDownloader/library.db`): elke track met pad, tags, formaat, bitrate, mtime, cover-aanwezigheid, vlaggen; album/artiest-views eroverheen.
- **FileSystemWatcher** op bieb + Nieuw: incrementele updates, app openen = bieb staat er.
- **Job-systeem**: één achtergrond-wachtrij (scans/conversies/transfers) met één globale voortgangsbalk onderin.
- **Undo-journal**: before-state van elke bestandsverplaatsing en tag-write; Cmd+Z overal + Geschiedenis-paneel.

## Fase 1 — Bibliotheek-browser (het nieuwe hart)

- Albumgrid met covers (thumbnails gecachet in de index); groeperen op artiest/genre/jaar/toegevoegd.
- Browser + inspector: selectie → rechterpaneel toont/bewerkt tags; multi-select = batch.
- Vlag-filters als chips ("lossy", "zonder hoes", "niet op iPod") — Gezondheid-cijfers zijn klikbare filters op deze browser.
- Globaal zoeken-als-je-typt (Cmd+F) over de hele index.
- Audio-preview: selectie + spatie = 10 sec horen (afplay).

## Fase 2 — Taggen pro (het beste van Mp3tag)

| Mp3tag | Spindle |
|---|---|
| Grid-view | Tracks als bewerkbare tabel, kolommen = tagvelden, Tab/Enter-navigatie |
| Tag-panel multi-edit | Inspector met `<verschillend>`; invullen = hele selectie; album-velden album-breed |
| Filename → Tag | Format-string-parser (`%artist% - %track% %title%`) met live preview per bestand |
| Tag → Filename | Zelfde template-taal, zelfde preview |
| Actions | "Acties": opgeslagen reeksen (case-correctie, regex-replace, genre-normalisatie, Apple-format) |
| Web sources | Bestaande album-match (Apple/Discogs/MB) + AcoustID |
| Cover-beheer | Plakken/slepen/ophalen; album-breed |

Alles met preview vóór toepassen en undo erna.

## Fase 3 — Workflow-gate 2.0

- Nieuw: albumkaarten met cover + vlag-chips + kwaliteitsscore; inline fixen (inspector + grid in hetzelfde scherm).
- Dubbele-versies: vergelijkingsweergave (formaat/bitrate/duur + audio-preview), kies & merge.
- Goedkeuren → template-organize met **diff-preview** (van → naar) → bieb.
- Pipeline-teller: Nieuw N → Gecontroleerd N → In bieb N → Op iPod N.
- Mapstructuur-templates met condities (compilaties → Various Artists); sanering centraal (map `AC-DC`, tag `AC/DC`).

## Fase 4 — Galaxy (3D-pointmap)

- **Default: track-niveau** (besluit Lucas). Elk nummer = punt; positie = verwantschap, kleur = genre, grootte = duur/playcount; hover = cover+titel, klik = naar browser; clusters met genre-labels.
- **Drempel-waarschuwing**: boven **20.000 tracks** in de index (≈ 600 GB–1 TB, afhankelijk van formaat) verschijnt een melding dat een gewone laptop (bv. MacBook Air) dit niet vloeiend rendert, met de suggestie om in Instellingen naar **album-niveau** te schakelen. Instelling: Galaxy-niveau = tracks | albums.
- v1: features uit tags (genre, jaar, artiest-nabijheid) → PCA + lichte force-directed pass → 3D, gecachet in de index.
- v2 (experiment, later): audio-features (tempo/spectraal via ffmpeg) voor echte klank-gelijkenis.
- Galaxy is functioneel: filteren (bv. alleen lossy), selecteren in 3D → batch-actie.

## Fase 5 — Polish

- **Dark mode als volwaardig app-thema** (besluit Lucas) — tweede token-set; Galaxy op donker canvas.
- Motion: hover-lift, vloeiende paneel-overgangen, skeleton-states; lege-states met richting.

## Vastgelegde productregels

- **ALAC-uitvoer (iPod): Artist = AlbumArtist** in het geconverteerde bestand, zodat de iPod-artiestenlijst geen "feat."-waslijst wordt. Bronbestanden in de bieb blijven onaangetast. (Geïmplementeerd 10 jun 2026 in Overzetten + ALAC-converter.)
- Bestaande tab-indeling is ondergeschikt aan de flow; Sorteren/Organiseren smelten op in de workflow-gate.
- Verwijderen is altijd verplaatsen naar `_Verwijderd (Spindle)` buiten de bieb (omkeerbaar).

## Risico's

- Fase 0 raakt alle schermen (migratie naar de index) — daarom eerst en geïsoleerd.
- Galaxy v1 clustert op tags (genre/artiest/periode), nog niet op klank — v2 verbetert dat.
- GUI niet renderbaar in agent-sessies — visuele verificatie door Lucas na elke fase.
