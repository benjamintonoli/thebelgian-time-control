# Bevindingen locatie-resolutiepilot

## Technisch bewezen

- Plenion en Powerfleet worden uitsluitend read-only gelezen en server-side begrensd.
- Powerfleet-ritten worden tot stops gereconstrueerd met gecontroleerde latitude/longitude.
- Azure Maps en Geoapify zijn selecteerbare typed `HttpClient`-providers achter `IGeocodingService`.
- Geoapify gebruikt het volledige adres, Belgische filtering, maximaal vijf resultaten en bewaart confidence en alternatieven.
- De lokale EF Core/SQLite-cache hergebruikt succesvolle resultaten op basis van een genormaliseerde adres-hash.
- Afstand, adreskenmerken en tijdsoverlap worden gecombineerd; een afstand alleen beslist nooit.

## Resultaten voor Filip Dekuyper

De begrensde pilot voor 23 en 24 juli 2026 onderzocht drie unieke Plenion-adressen, verspreid over zes prestaties:

- Starrenfoflaan 7A werd waarschijnlijk gekoppeld aan de stop Starrenhoflaan 6: 143 meter, 79 minuten overlap, score 83/100.
- Harelbekestraat 70 werd waarschijnlijk gekoppeld aan Harelbekestraat 60 op 23 juli en Harelbekestraat 39 op 24 juli: 101–114 meter en voor twee prestaties sterke tijdsoverlap. Eén prestatie had geen betrouwbare tijdskoppeling.
- Hotel Europe, Kapucijnenstraat 52 werd gekoppeld aan Hofstraat 7: 90 meter. Eén prestatie is bevestigd met 67 minuten overlap en score 90/100; een aansluitende prestatie is waarschijnlijk met score 75/100.

## Geoapify-resultaten

- `Starrenfoflaan 7A` → `Starrenhoflaan 7A, 2950 Kapellen, België`, confidence 0,836.
- `Harelbekestraat 70` → `Home Boudewijn, Harelbekestraat 70, 9000 Gent, België`, confidence 1. Een tweede naam op dezelfde locatie is Studentenresto Boudewijn.
- `Kapucijnenstraat 52` → `Kapucijnenstraat 52, 8400 Oostende, België`, confidence 1. Hotel Europe wordt als alternatief op dezelfde locatie teruggegeven.

## Datakwaliteit en manuele bevestiging

- `Starrenfoflaan` bevat waarschijnlijk een typfout en moet operationeel worden bevestigd als `Starrenhoflaan`.
- Harelbekestraat 70 lijkt geldig; de stops op nummers 39 en 60 zijn vermoedelijk verschillende toegangen of parkeerplaatsen van dezelfde site.
- Kapucijnenstraat 52 lijkt geldig; Hofstraat 7 is waarschijnlijk de voertuigtoegang of parking. Esperantolaan hoort volgens afstand en tijd niet bij deze locatie.
- Starrenhoflaan 6 en de relevante toegangen aan Harelbekestraat moeten nog manueel worden bevestigd.

## Beperkingen

- De pilot omvat één technieker, twee werkdagen en drie unieke adressen.
- Er bestaat nog geen reviewtool, permanente manuele override of automatische correctie.
- Er is geen writeback naar Plenion en geen productie-deployment.
- Scores en afstandsgrenzen zijn diagnostisch en nog niet organisatiebreed gevalideerd.

## Aanbevolen volgende fase

Voer een bredere read-only validatie uit met meerdere techniekers, periodes en locatiecategorieën voordat een reviewtool of enige writeback wordt ontworpen.
