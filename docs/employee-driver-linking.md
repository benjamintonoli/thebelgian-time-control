# Medewerkerkoppeling Plenion ↔ Powerfleet

## Bronvelden

| Bron | Veld | Rol |
|---|---|---|
| Plenion `Resource` | `IDRESOURCE` | Interne sleutel van de medewerker; gebruikt om `PROJ_Prest` te filteren |
| Plenion `Resource` | `RESCODE` | Unieke zoekcode wanneer de naam meerdere treffers geeft |
| Plenion `Resource` | `OMSCHR` | Weergavenaam; tokens hiervan matchen op Powerfleet `drivername` bij ontdekking |
| Powerfleet trip | `driverid` | **Primaire** koppelsleutel voor ritten in de bredere validatie |
| Powerfleet trip | `drivername` | Ontdekkingssleutel om `driverid` te vinden; niet de uiteindelijke filter |
| Powerfleet trip | `objectid` / `objectname` / `objectPlate` | Uitsluitend informatief (bv. `FDE`, `JDE`, `JDS`); nooit koppelsleutel |

## Stappen

1. Zoek medewerker: `SOORT = 1` en (`OMSCHR LIKE %query%` of exacte `RESCODE`).
2. Ontdek `driverid`: ritten mét `driverid` waarvan alle naamtokens van `OMSCHR` in `drivername` voorkomen; neem de meest frequente `driverid`.
3. Koppel ritten: exacte gelijkheid op `driverid`. Ritten zonder `driverid` → `MissingDriver` (geen uren-/locatieconclusie).
4. Bewaar `objectname`/`objectPlate` alleen als context (andere wagen wijzigt de koppeling niet).
