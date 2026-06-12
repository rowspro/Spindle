# Spindle — doelstellingen & uitgangspunten

> Het kompas. Bij elke feature of wijziging checken we tegen dit document: **draagt het bij
> aan het doel, past het binnen de principes, en schendt het geen non-goal?** Verwant:
> [docs/IPOD_BEHAVIOR.md](IPOD_BEHAVIOR.md) (apparaat-gedrag).

## Wat Spindle is

Een macOS-app (Avalonia/.NET) die bovenop een Soulseek-client (Nicotine+) een **complete
muziekbibliotheek-pijplijn** legt: van binnengehaald bestand → schone, consistent geordende
collectie → klaar voor de iPod. Apple-getrouw in uiterlijk én tag-conventies.

## Voor wie

Iemand die zijn eigen muziek bezit en beheert, een grote/groeiende FLAC-collectie netjes
wil houden, en die op een (Rockbox- of stock-)iPod wil zetten — zonder handmatig sleur-
werk met hernoemen, taggen, converteren en dubbelen opsporen.

## De pijplijn (twee helften)

1. **Onboarding** — van de "New music"-inbox naar de georganiseerde bibliotheek
   (`Artiest / Album (Jaar) / NN Titel.ext`). Reviewen (Inbox-gate), taggen, matchen
   (Apple/MusicBrainz/Discogs), opschonen, ordenen.
2. **Sync** — van de bibliotheek naar de iPod, in Rockbox- of stock/iTunes-modus.

Ondersteunende functies: Library-browser, Galaxy (visuele map), Duplicates, Health/Doctor,
Wantlist (volg artiesten, vergelijk discografie, shoppinglijst voor Nicotine+), speler.

## Kernprincipes (hieraan toetsen we)

1. **De bron is heilig.** De bibliotheek- en inbox-bestanden worden nooit stilletjes
   gewijzigd. Opschonen/hertaggen/verplaatsen is altijd óf expliciet (knop/preview) óf
   een opt-in voorkeur — en altijd ongedaan te maken.
2. **Alles ongedaan te maken.** Elke verplaatsing en tag-batch gaat via het UndoJournal
   (Cmd+Z). Destructieve acties tonen eerst een preview.
3. **Apparaat-bewust.** We weten hoe Rockbox en de stock-iPod zich gedragen en spelen
   daarop in zonder onnodig data weg te laten (zie IPOD_BEHAVIOR.md).
4. **Apple-getrouw.** UI én tag-normalisatie volgen Apple/iTunes-conventies
   (Album-artiest-groepering, "A, B & C", titelhoofdletters, genre-canon).
5. **De gebruiker bepaalt.** Gedrag dat smaak raakt (artiest-splitsing, genres,
   platslaan, opschonen) staat als schakelaar in **Personalisations**, met nette defaults.
6. **Snel op een grote bibliotheek.** Persistente index (SQLite), incrementele rescans,
   werk over alle cores. Geen volledige passes waar incrementeel kan.
7. **Robuust bij losse hardware.** Externe SSD of iPod kan ontkoppelen midden in een
   actie zonder corruptie (RAM-staging, `.part`-bestanden, vrije-ruimte-checks vooraf).

## Non-goals (bewust níét)

- Geen streaming-dienst of online catalogus; je beheert je eigen bestanden.
- Geen ingebouwde Soulseek-client; Spindle leunt op Nicotine+ voor het downloaden zelf.
- Geen zware DAW/audio-editing; we taggen en ordenen, we bewerken geen audio
  (behalve formaat-conversie voor de iPod).
- Geen cloud-sync of accounts; lokaal en privé.

## Hoe we beslissen

Twijfel je over een feature? Vraag: _welke helft van de pijplijn dient het, raakt het de
bron (principe 1), is het ongedaan te maken (2), en hoort het in Personalisations (5)?_
Een feature die de bron stilletjes wijzigt of niet undo-baar is, herontwerpen we eerst.
