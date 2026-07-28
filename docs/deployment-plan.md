# Deploymentplan

Dit document beschrijft veilige vervolgstappen; fase 1 wordt niet naar productie
gedeployed.

## Gate 1 – lokale validatie

- Release-build en unittests zonder warnings.
- Controle dat repository en Git-history geen secrets of SQLite-data bevatten.
- Handmatige inspectie van ODBC-queryresultaten tegen een toegestane niet-productiebron.
- Bevestiging van Powerfleet-authenticatie, endpoint en timestampformaat.
- Vergelijk een kleine set dagen handmatig met beide bronsystemen.

## Gate 2 – acceptatieomgeving

- Least-privilege Plenion-account met aantoonbaar alleen leesrechten.
- Secrets via een beheerde secret store, nooit via bestanden of environment dumps.
- EF Core-migraties in plaats van `EnsureCreated`.
- Retentie, back-up, persoonsgegevensbeleid en toegangscontrole vastleggen.
- Health checks die geen externe verbinding of secretinformatie lekken.
- Auditlog voor synchronisatieruns en lokale reviewacties.

## Gate 3 – productiebesluit

Productietoegang vereist expliciete toestemming van de projecteigenaar, security- en
privacyreview, operationeel eigenaarschap en een terugvalplan. Een deployment mag
geen writeback, automatische correctie of wijziging aan `PlenionWriteService`
introduceren. Die mogelijkheden vallen buiten fase 1 en vragen een afzonderlijk
ontwerp en goedkeuring.
