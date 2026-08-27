# Automatische voertuigtoewijzingen

De sync koppelt `PowerFleet Vehicles/get: Name` uitsluitend aan een unieke, actieve
`Plenion RESOURCE.RESCODE`. De vergelijking negeert hoofdletters en witruimte.
`ObjectId` is de fysieke voertuigidentiteit. DriverId, persoonsnaam, fuzzy matching,
GPS-fit en kenteken worden nooit gebruikt om een technieker te kiezen.

Een nieuwe unieke koppeling opent een actuele assignment op `ObservedAt`. Een latere
unieke ObjectId voor dezelfde technieker sluit de vorige assignment en opent de
nieuwe transactioneel. De vorige en huidige observatietijd blijven in assignment en
audittrail staan; het syncmoment is bewijs van een observatievenster, niet van de
exacte feitelijke transfertijd.

Ambiguous situaties wijzigen geen assignments. Dit geldt voor dubbele actieve
RESCODEs en meerdere voertuigen met dezelfde Name. Een onbekende of ontbrekende
Name kan wel als PhysicalVehicle worden geobserveerd maar maakt geen assignment.
Een voertuig dat één snapshot ontbreekt, sluit geen bestaande assignment.
`NoTrackAndTrace` wordt uit persistente eligibility-masterdata gelezen en blokkeert
automatische assignment- en transferwijzigingen.

## Handmatig uitvoeren

Publiceer eerst de webproject-output. Gebruik vanuit de publishmap de echte
framework-dependent uitvoering:

```powershell
dotnet .\TheBelgian.TimeControl.Web.dll --vehicle-assignment-sync `
  --database 'D:\TimeControl\data\time-control.db' `
  --actor 'manual-it'
```

Bij een self-contained publicatie kan `TheBelgian.TimeControl.Web.exe` met dezelfde
argumenten worden gebruikt. De CLI gebruikt per absoluut databasepad een named
Windows single-instance gate. Een tweede run eindigt als `SkippedAlreadyRunning`.

## Scheduled Task

Het standaardschema is woensdag 03:00 en zondag 03:00. Installeer pas op de server,
na publicatie en met het echte database- en logpad:

```powershell
.\scripts\Install-VehicleAssignmentSyncTask.ps1 `
  -PublishPath 'D:\TimeControl\app' `
  -DatabasePath 'D:\TimeControl\data\time-control.db' `
  -LogPath 'D:\TimeControl\logs\vehicle-assignment-sync.log' `
  -ServiceAccount 'THEBELGIAN\svc-timecontrol'
```

Het script vraagt interactief om het Windows-wachtwoord en bewaart geen wachtwoord
in broncode. Voor een gMSA gebruikt men `-ServiceAccountIsGmsa`. Registratie met
dezelfde `TaskName` werkt de bestaande taak bij en maakt geen duplicaat. De taak kan
ook handmatig vanuit Task Scheduler worden gestart.

Benodigde applicatieconfiguratie blijft dezelfde als voor de handmatige CLI:
PowerFleet API-configuratie, read-only Plenion ODBC en een schrijfbaar lokaal
TimeControl SQLite-pad. Console-output wordt aan het configureerbare logbestand
toegevoegd.

De laatste succesvolle uitvoering staat in `VehicleAssignmentSyncRuns` met status
`Succeeded` en wordt klein getoond op `/Admin/TimeControl/VehicleAssignments`.

Bijwerken gebeurt door het installatiescript opnieuw met dezelfde tasknaam uit te
voeren. Verwijderen:

```powershell
Unregister-ScheduledTask -TaskName 'TheBelgian TimeControl Vehicle Assignment Sync' -Confirm
```
