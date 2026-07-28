# TheBelgian.TimeControl

Technische fase-1-proof-of-concept die geregistreerde prestaties uit Plenion read-only
vergelijkt met ritten uit Powerfleet. De toepassing importeert brondata in een lokale
SQLite-database, berekent afwijkingen en laat uitsluitend lokale reviews toe.

## Vereisten

- .NET SDK **10.0.302** of een nieuwere stabiele 10.0-patch (`global.json` weigert previews).
- Een 64-bits ODBC-driver en de lokaal geconfigureerde Plenion-DSN.
- Geldige Powerfleet-rapportinstellingen voor lokale integratietests.

Controleer de SDK:

```powershell
dotnet --version
```

## Bouwen

```powershell
dotnet restore TheBelgian.TimeControl.sln --configfile NuGet.Config
dotnet build TheBelgian.TimeControl.sln --configuration Release --no-restore
```

De repository bevat `eng/dotnet.cmd` voor afgeschermde ontwikkelomgevingen; normale
werkstations kunnen rechtstreeks `dotnet` gebruiken.

## Tests

```powershell
dotnet test TheBelgian.TimeControl.sln --configuration Release --no-build --no-restore
```

De tests gebruiken lokale XML en voeren geen echte HTTP-aanroepen uit.

## Lokale secrets instellen

Voer deze opdrachten uit in `src/TheBelgian.TimeControl.Web`. Vervang de
plaatsaanduidingen lokaal; commit de waarden nooit.

```powershell
dotnet user-secrets set "ConnectionStrings:PlenionOdbc" "DSN=..."
dotnet user-secrets set "Powerfleet:BaseUrl" "..."
dotnet user-secrets set "Powerfleet:ApiKey" "..."
dotnet user-secrets set "Powerfleet:ReportId" "..."
dotnet user-secrets set "Powerfleet:StateId" "..."
```

De huidige lokale DSN-naam is `Plenion_TB_Sys_v22_64`, maar staat bewust niet in code
of standaardconfiguratie. `appsettings.Example.json` bevat alleen veilige placeholders.

## SQLite aanmaken

De webapp maakt bij het starten automatisch `data/time-control.db` en het schema aan
met EF Core `EnsureCreated`. De map en alle SQLite-bestanden staan in `.gitignore`.
Voor deze proof-of-concept zijn nog geen productiemigraties voorzien.

## Applicatie starten

```powershell
dotnet run --project src/TheBelgian.TimeControl.Web
```

Open de URL uit de console en ga naar `/Exceptions`. Een synchronisatie gebeurt alleen
wanneer een gebruiker daar expliciet op **Synchroniseren** klikt. Reviews wijzigen
uitsluitend SQLite.

## Nog ontbrekende configuratie en bevestiging

- Een lokaal bereikbare Plenion ODBC-DSN en toegestane read-only accountrechten.
- Powerfleet BaseUrl, API-key, ReportId en StateId.
- Bevestiging van het precieze Powerfleet-authenticatieschema, rapportpad en
  timestampformaat.
- Praktijkvalidatie van Plenion-veldtypen voor `VAN`, `TOT`, `PAUZE` en `KM`.
- Een gevalideerde voertuigtoewijzing per technieker.

Er is geen verbinding met productie gemaakt. Plenion is in fase 1 strikt read-only:
de oplossing bevat uitsluitend `SELECT`-queries, wijzigt geen bestaande
`PlenionWriteService`, heeft geen writebackservice en corrigeert nooit automatisch uren.
Pushen en productie-deployment vallen buiten fase 1.

## Documentatie

- [Architectuur](docs/architecture.md)
- [Datamodel](docs/data-model.md)
- [Matchingregels](docs/matching-rules.md)
- [Klantlocaties fase 2](docs/customer-location-matching.md)
- [Deploymentplan](docs/deployment-plan.md)
