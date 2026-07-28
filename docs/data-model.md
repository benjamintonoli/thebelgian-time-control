# Datamodel

## Brongegevens

- `Technician`: genormaliseerde Plenion `Resource` met `SOORT = 1`.
- `PlenionPerformance`: prestatie met unieke `IDPROJ_PREST`, tijden, pauze, kilometers,
  omschrijving en bronverwijzingen.
- `PlenionWorkOrder`: read-only `BON`-projectie, beperkt tot `BSRTCD` 61, 62 en 63.
- `PlenionProject`: read-only `PROJ`-projectie.
- `PowerfleetTrip`: rit met unieke `tripid`, voertuig/bestuurder, tijden, afstand en
  start-/eindlocatievelden.
- `CustomerLocation`: read-only `LEVADR`-bron met optionele toekomstige coördinaten.

## Koppeling

- `Vehicle` vertegenwoordigt het Powerfleet-object/nummerplaat.
- `VehicleAssignment` koppelt technieker en voertuig met `ValidFrom` en optioneel
  `ValidUntil`.
- Een dag met meer dan één herkenbaar voertuig krijgt in fase 1
  `Onzekere voertuigtoewijzing`; tijdelijke wissels worden niet automatisch opgelost.

## Berekeningen en review

- `DailyTechnicianTimeline` is een niet-gepersistente dagsamenvatting.
- `DetectedException` bewaart type, reden, prioriteit, berekende verschillen, gebruikte
  toleranties, bronmomenten, aanmaakdatum en laatste berekeningsdatum.
- `ReviewDecision` kent uitsluitend de vijf toegestane lokale acties plus `Unreviewed`.
- `SynchronizationRun` bewaart periode, aantallen, status en veilige foutmelding.

## Integriteit

EF Core maakt unieke indexen voor `IDPROJ_PREST`, `tripid`, technieker-ID,
voertuig-ID, klantlocatie-ID en de samengestelde exceptionsleutel
`technieker:datum:type`. Een herberekening behoudt de bestaande reviewstatus en
aanmaakdatum.

SQLite is lokale POC-opslag. `EnsureCreated` is bewust eenvoudig; vóór een later
deploymentpad zijn expliciete migraties, retentie en back-upbeleid nodig.
