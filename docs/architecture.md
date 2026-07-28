# Architectuur

## Laagverdeling

`Core` bevat domeinmodellen, bron- en repositoryinterfaces, toleranties,
tijdsintervalberekeningen, tijdlijnnormalisatie en matchinglogica. De laag kent geen
ODBC, HTTP, EF Core of Razor Pages.

`Infrastructure` implementeert de read-only ODBC-reader, typed Powerfleet `HttpClient`,
XML-parser, SQLite-context en repositories. `SynchronizationService` orkestreert een
expliciete import en schrijft alleen naar SQLite.

`Web` is een dunne Razor Pages-interface. PageModels spreken services aan; ze bevatten
geen matching-, database- of HTTP-logica.

`Tests` valideert Core-regels en XML-parsing met lokale fixtures.

## Gegevensstroom

1. De gebruiker start lokaal een synchronisatie voor een periode.
2. `IPlenionReader` voert uitsluitend vaste, geparametriseerde `SELECT`-queries uit.
3. `IPowerfleetClient` vraagt het ingestelde rapport op.
4. Beide bronnen worden genormaliseerd naar Core-modellen.
5. Bronrecords worden op hun stabiele externe sleutel in SQLite geüpsert.
6. Per technieker en dag wordt een `DailyTechnicianTimeline` gebouwd.
7. `ITimeControlMatchingService` berekent uitzonderingen.
8. Reviews wijzigen alleen `DetectedException.ReviewDecision` in SQLite.

## Veiligheidsgrenzen

- Geen Plenion-writeinterface of SQL-mutatie bestaat.
- Externe verbindingen worden niet tijdens startup geopend.
- Secrets komen uit user-secrets/configuratie en worden niet gelogd.
- HTTP-fouten vermelden statuscode, niet de API-key of volledige request-URL.
- Fase 1 voert geen geocoding, automatische correctie of deployment uit.

## Bekende integratieaannames

Powerfleet gebruikt momenteel een bearer-authenticatieheader, `reportId` en `stateId`
als queryparameters en Unix-seconden voor `parameters[begTimestamp]` en
`parameters[endTimestamp]`. Deze drie contractdetails moeten tegen de echte
Powerfleet-documentatie of een niet-productieomgeving worden bevestigd vóór een
integratietest.
