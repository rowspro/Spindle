# iPod & Rockbox — gedrag en hoe Spindle daarop inspeelt

> **Doel.** Dit is een levende checklist van hoe de twee doel-omgevingen (Apple-firmware iPod
> en Rockbox) zich gedragen rond bestandsformaten, tags en het filesystem. Bij elke wijziging
> aan de sync-/onboarding-pijplijn checken we tegen dit document: **spelen we hierop in, kunnen
> we het beter doen, of laten we onnodig data weg?**
>
> Regels: claims die nog geverifieerd moeten worden zijn gemarkeerd met **(verify)**.
> Open ontwerpvragen staan onderaan elke sectie onder _Open vragen_.

Spindle heeft twee helften:

1. **Onboarding** — bestanden vanuit de "New music"-inbox naar de georganiseerde library
   (`Artist / Album (Year) / NN Title.ext`). Tags blijven hier **ongemoeid**, tenzij de
   gebruiker expliciet een opschoon-actie draait (Apple-format artiest, genre normaliseren).
2. **Sync** — de library naar de iPod, in één van twee modi (zie hieronder). Hier gebeurt het
   eventuele "platslaan" voor de doel-omgeving. **De bron blijft altijd intact.**

---

## Doel-omgevingen in één oogopslag

| | **Rockbox** | **Stock iPod (Apple-firmware / iTunes)** |
|---|---|---|
| Spindle-modus | `RockboxMode` (default) | `ItunesMode` |
| Sync-mechanisme | Transfer-tab kopieert rechtstreeks uit library | Achtergrond-**ALAC-mirror** die iTunes/Finder synct |
| FLAC | ✅ native | ❌ niet ondersteund |
| ALAC | ✅ | ✅ |
| MP3 / AAC | ✅ | ✅ |
| WAV / AIFF | ✅ | ✅ (groot, onpraktisch) |
| Conversie nodig? | nee | ja, lossless → ALAC |
| Database | tag-cache `.tcd` in `/.rockbox/` | `iTunesDB` in `iPod_Control/iTunes/` |
| Bladeren op | tags (database) of mappen (file browser) | uitsluitend via iTunesDB |

---

## Stock iPod (Apple-firmware)

### Wat het kan / verwacht
- **Formaten:** ALAC, AAC, MP3, AIFF, WAV. **Geen FLAC, geen Opus/Vorbis.**
- **Database-gedreven.** De stock-firmware toont alleen wat in `iTunesDB` staat. Losse bestanden
  naar het volume kopiëren werkt niet — daarom laat Spindle in iTunes-modus een **ALAC-mirror**
  bijhouden waar de gebruiker iTunes/Finder op richt; iTunes schrijft de iTunesDB.
- **Groepering op Album-artiest.** Tracks met verschillende `Artist` ("feat."-varianten) maar
  dezelfde `Album Artist` vallen onder één artiest. Zonder Album-artiest spawnt elke
  "feat."-variant een eigen artiest-entry.
- **Compilations-vlag** ("Part of a compilation") groepeert losse-artiest-albums onder
  "Compilations" i.p.v. per track-artiest. **(verify: zetten we deze vlag ergens?)**
- **Gapless** vereist correcte encoder-delay/padding-info in de ALAC-output. **(verify)**

### Hoe Spindle nu inspeelt
- `AlacMirrorService` + `AudioConvert.CopyTags(..., artistFromAlbumArtist: true)`:
  FLAC/WAV → ALAC met **`Artist = Album Artist`** zodat de artiestenlijst niet volloopt met
  "feat."-varianten; MP3/AAC worden as-is gekopieerd; verwijderde albums worden opgeruimd.
- Embedded artwork wordt meegekopieerd (`EmbeddedPictures`).

### Gotchas / risico op dataverlies
- `Artist = Album Artist` op de ALAC-kopie **verbergt de gast-artiest** in de trackweergave.
  `AlbumArtist` blijft apart bewaard, maar de oorspronkelijke `Artist` (met feat-info) gaat in
  de iPod-kopie verloren. Acceptabel voor de iPod-lijstweergave, maar het is bewust weglaten —
  **documenteren als keuze, niet als bug.**
- WAV/AIFF meenemen is technisch geldig maar verspilt ruimte; lossless hoort ALAC te worden.

### Open vragen
- Compilations-vlag automatisch zetten bij multi-artiest-albums?
- Gapless-metadata bij ALAC-conversie behouden?

---

## Rockbox

### Wat het kan / verwacht
- **Formaten:** FLAC, ALAC, MP3, AAC, Vorbis, Opus, WAV, AIFF … vrijwel alles native.
  Geen conversie nodig — daarom kopieert de Transfer-tab rechtstreeks uit de library.
- **Indexeert op extensie.** De database-scan pakt **elk** bestand met een audio-extensie op.
  → Dit is de bron van het `._`-bug: op FAT32/exFAT splitst macOS metadata af in AppleDouble-
  bestanden (`._Track.flac`), die Rockbox als losse, naamloze tracks indexeert. Zie _Filesystem_.
- **Tag-cache database** (`.tcd`-bestanden in `/.rockbox/`). Bij wijzigingen moet de DB opnieuw
  geïnitialiseerd worden (Settings → Database → Initialize Now); anders blijven oude/dubbele
  entries staan.
- **Artiest-tag = letterlijke string.** Rockbox splitst een tag als `"A; B"` **niet** in twee
  artiesten; de hele string wordt één artiest-entry in de browse-index. Echte meervoudige
  Vorbis-`ARTIST`-velden worden niet betrouwbaar als aparte artiesten getoond. **(verify exact
  gedrag per Rockbox-versie)**
- **File-browser** als alternatief voor de database — bladert puur op mapstructuur, negeert tags.

### Hoe Spindle nu inspeelt
- Transfer-tab kopieert FLAC/lossless 1:1 (geen conversie, geen tag-herschrijving).
- De Doctor/cleanup-tools kunnen de iPod opschonen (`._`-junk + verweesde `.tcd`).

### Gotchas / risico op dataverlies
- `"Artiest; Gast"`-strings tonen als één rommelige artiest in de database-weergave
  (precies wat we zagen bij `3risco;Tommy Loco`, `AC Slater;Curbi`). Platslaan naar de
  primaire artiest helpt de browse-ervaring, maar mag de **bron** niet aanraken.
- Hetzelfde nummer op meerdere releases (album + single + compilatie) stapelt bij
  bladeren op **artiest → nummers**. Dat is correcte library-inhoud, geen duplicaat —
  bladeren op **album** lost het visueel op.

### Open vragen
- Willen we bij Rockbox-sync optioneel een platgeslagen `Artist`-tag schrijven (kopie!),
  of laten we de database-rommel voor wat het is?

---

## Filesystem (geldt voor beide bij FAT32/exFAT)

- FAT32/exFAT kan **geen** extended attributes / resource forks opslaan. macOS' kopieer-API's
  (`NSFileManager.copyItem`, `File.Copy`, `cp` zonder flags, `copyfile()`) splitsen die metadata
  daarom af in `._<naam>`-AppleDouble-bestanden — mét de originele extensie, dus Rockbox ziet
  `._Track.flac` als audio.
- **Voorkomen bij kopiëren:**
  - .NET: kopieer rauwe bytes (`File.OpenRead` → `CopyTo`) i.p.v. `File.Copy` (die roept
    `copyfile()` mét metadata aan).
  - shell: `COPYFILE_DISABLE=1` vóór `cp`.
- **Vangnet bij eject:** `dot_clean -m <volume>` merget/verwijdert alle `._`-bestanden.

> **Checklist voor de sync-code:** schrijven we de bytes rauw weg én draaien we `dot_clean`
> (of een eigen `._`-filter) vóór het veilig verwijderen?

---

## Tag-opschoon-gedrag (onboarding-kant, opt-in)

Deze helpers bepalen wat er met multi-value tags gebeurt. Ze draaien **alleen** op
gebruikersactie (knoppen in Metadata-editor / TagGrid), nooit automatisch tijdens onboarding.

| Helper | Bestand | Doet | Splitst op |
|---|---|---|---|
| `PrimaryArtist` | `TextFormat.cs` | eerste gecrediteerde artiest → Album-artiest | `; / , & feat ft featuring with vs x` |
| `FormatArtists` | `TextFormat.cs` | standaardiseert naar de gekozen scheider (`CleanupOptions.ArtistJoin`: as-is / `"A, B & C"` / `;` / `/` / `,`), dedupe | idem |
| `GenreFormat.Normalize` | `GenreFormat.cs` | reduceert multi-genre tot primaire + canoniek | `/ ; , \| \\` |

### Bekende beperking — `"Achternaam, Voornaam"`-sorteernamen
De artiest-split bevat de **komma** als scheidingsteken. Daardoor:
- `PrimaryArtist("The White Stripes; White, Jack")` → `"The White Stripes"` ✅ correct.
- `FormatArtists("The White Stripes; White, Jack")` (Apple) → `"The White Stripes, White & Jack"` ❌ —
  de sorteernaam `"White, Jack"` wordt versnipperd tot twee neppe artiesten.

Een komma kan niet onderscheiden worden tussen een collab (`Beyoncé, Jay-Z`) en een
sorteernaam (`White, Jack`). Dit is de directe aanleiding om het scheidingsgedrag
**instelbaar** te maken (zie `SETTINGS`-werk).

### Open vragen
- `;` als enige "echte" multi-artiest-scheider, komma alleen optioneel?
- Apart sort-artiest-veld i.p.v. een tweede `Artist`-waarde?
- Genre: optie om méérdere genres te behouden i.p.v. reduceren tot één?
