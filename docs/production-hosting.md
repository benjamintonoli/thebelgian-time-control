# TimeControl productiehosting op TB001800

## Hostingmodel

Gebruik een native ASP.NET Core Windows Service met Kestrel, uitsluitend op
`http://127.0.0.1:5260`. Dit sluit aan bij PlenionWriteService, vereist geen
IIS-site of inbound firewallpoort en geeft Cloudflare Tunnel later een vaste
lokale origin. De applicatie bevat alleen een dependencyvrije `/health`
liveness-check; externe bronnen maken geen deel uit van die check.

De vaste service verwijst naar `C:\Apps\TheBelgian.TimeControl\current`. De
installer maakt `current.next` als junction naar een immutable release, stopt
de service, maakt een DB-backup, wisselt de junction en start alleen met
`-StartService`. `current.previous` blijft als directe rollbackpointer staan.

## Serviceaccount en rechten

Voorkeur indien Active Directory/KDS dit ondersteunt:
`THEBELGIAN\gmsa_timecontrol$`. Anders:
`THEBELGIAN\svc_timecontrol` met een door IT beheerd lang wachtwoord.

Benodigde rechten:

- `Log on as a service`;
- `Log on as a batch job` voor de twee Scheduled Tasks;
- read/execute op `releases` en `current`;
- modify op `data` en `logs`;
- de Data Protection keys staan onder `data\data-protection-keys`, niet in de release;
- read op de beveiligde productieconfig;
- outbound HTTPS naar `eu.ecofleet.com`;
- read-only ODBC-toegang via de machine-DSN `PlenionWriteLive`;
- HTTP naar `http://localhost:5090`;
- geen lokale administratorrechten tijdens normaal bedrijf.

## Secrets

Maak buiten Git:
`C:\Apps\TheBelgian.TimeControl\config\appsettings.Production.json`.
Vertrek van `config\appsettings.Production.template.json` en vul uitsluitend
op de server de PowerFleet API-key in. Een PWS API-key is pas nodig als PWS die
authenticatie gebruikt; de correctieflags blijven altijd false in deze fase.

De installer beperkt de ACL van het configbestand tot SYSTEM, Administrators
en read voor het serviceaccount. Daarna maakt hij in de actieve release een
hardlink naar dit beveiligde bestand. Daardoor gebruiken webservice, vehicle
sync en monthly prepare dezelfde configuratie zonder secrets in argumenten,
Scheduled Tasks, Git of releasekopieën.

## Installatie of release-update

Publiceer eerst naar een nieuwe immutable commitmap en voer daarna als
administrator uit. Stop vóór de eerste installatie de tijdelijke handmatig
gestarte host op poort 5260; de installer weigert veilig wanneer de DB daardoor
nog vergrendeld is.

```powershell
$account = 'THEBELGIAN\svc_timecontrol'
$credential = Get-Credential -UserName $account
& C:\Dev\thebelgian-time-control\scripts\Install-TimeControlService.ps1 `
  -ReleasePath 'C:\Apps\TheBelgian.TimeControl\releases\<commit>' `
  -ServiceAccount $account -Credential $credential -StartService
```

Voor een gMSA:

```powershell
& C:\Dev\thebelgian-time-control\scripts\Install-TimeControlService.ps1 `
  -ReleasePath 'C:\Apps\TheBelgian.TimeControl\releases\<commit>' `
  -ServiceAccount 'THEBELGIAN\gmsa_timecontrol$' `
  -ServiceAccountIsGmsa -StartService
```

## Scheduled Tasks

Installeer ze pas nadat dezelfde accountcontext via de service bewezen is:

```powershell
$account = 'THEBELGIAN\svc_timecontrol'
$credential = Get-Credential -UserName $account
$current = 'C:\Apps\TheBelgian.TimeControl\current'
$database = 'C:\Apps\TheBelgian.TimeControl\data\time-control.db'

& C:\Dev\thebelgian-time-control\scripts\Install-VehicleAssignmentSyncTask.ps1 `
  -PublishPath $current -DatabasePath $database `
  -LogPath 'C:\Apps\TheBelgian.TimeControl\logs\vehicle-sync.log' `
  -ServiceAccount $account -Credential $credential

& C:\Dev\thebelgian-time-control\scripts\Install-MonthlyReviewPrepareTask.ps1 `
  -PublishPath $current -DatabasePath $database `
  -LogPath 'C:\Apps\TheBelgian.TimeControl\logs\monthly-prepare.log' `
  -ServiceAccount $account -Credential $credential
```

De actors zijn respectievelijk `SYSTEM_VEHICLE_SYNC` en
`SYSTEM_MONTHLY_PREPARE`. Logs roteren bij 25 MB en 50 MB; per taak blijven
standaard twaalf geroteerde bestanden behouden.

## SQLite en backups

De database draait in WAL-mode. Microsoft.Data.Sqlite wacht standaard op een
kortdurende writer-lock en vehicle sync heeft daarnaast een eigen
single-execution guard. De taken draaien woensdag/zondag om 03:00 en iedere
15e om 04:00 en overlappen normaal niet. Er is geen extra SQLite-wijziging
nodig; draai nooit twee monthly-prepareprocessen tegelijk.

Maak dagelijks een consistente backup met een korte servicestop:

```powershell
& C:\Dev\thebelgian-time-control\scripts\Backup-TimeControlDatabase.ps1 `
  -Reason Daily -RetentionDays 35 -StopService
```

Gebruik vóór deployments `PreDeploy` met 90 dagen retentie en vóór
maandfinalisatie `PreFinalization` met 400 dagen retentie. Het script weigert
een online filecopy, controleert WAL en vergelijkt SHA-256 vóór publicatie van
de backup. Externe/cloudbackup valt buiten deze configuratie.

## Logging en acceptatie

De Windows Service schrijft startup-, applicatie-, PWS- en correctionlogs
naar Windows Application Event Log onder `TheBelgian.TimeControl`. Taskoutput
staat onder `C:\Apps\TheBelgian.TimeControl\logs`. Tokens en passwords worden
niet bewust gelogd.

Na installatie:

```powershell
& C:\Dev\thebelgian-time-control\scripts\Test-TimeControlProduction.ps1
```

Deze controle is read-only en valideert service, loopbacklistener, `/health`,
cockpit, juli, LastVehicleSync, productie-DB, PWS-health en beide uitgeschakelde
correctieflags.

## Cloudflare-handoff

- server: `TB001800`;
- origin: `http://127.0.0.1:5260`;
- publiek: `https://timecontrol.thebelgian-api.be`;
- authenticatie: Cloudflare Access met Microsoft Entra ID;
- geen inbound firewallpoort nodig.
