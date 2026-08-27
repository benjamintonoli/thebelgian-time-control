# Maandelijkse urencontrole

TimeControl zet op de 15e de vorige kalendermaand klaar. Vóór de 15e is de
standaardmaand twee maanden terug. De cockpit staat op `/Admin/TimeControl` en toont
vooral te controleren uitzonderingen; onvoldoende voertuig/GPS-bewijs staat apart
en `NoTrackAndTrace` staat onder niet van toepassing.

## Handmatig voorbereiden

Voer vanuit de echte publishmap uit:

```powershell
dotnet .\TheBelgian.TimeControl.Web.dll --prepare-monthly-review `
  --month 2026-07 `
  --database 'D:\TimeControl\data\time-control.db' `
  --actor 'SYSTEM'
```

Zonder `--month` geldt automatisch de 15e-businessregel. De voorbereiding leest
Plenion en PowerFleet read-only, gebruikt historische voertuigtoewijzingen en legt
een evidence-snapshot vast. De laatste succesvolle voertuigsynchronisatie wordt
geregistreerd; er wordt niet automatisch een extra voertuigsync gestart.

`Vernieuw gegevens` maakt voor een niet-afgesloten maand opnieuw read-only evidence.
Een beslissing blijft behouden wanneer het bewijs identiek is. Gewijzigd bewijs
behoudt de oude audittrail en opent de case als `Gegevens gewijzigd — opnieuw
controleren`. Een afgesloten maand wordt nooit automatisch vernieuwd.

`Maand afsluiten` blokkeert standaard zolang gewone reviewcases openstaan. Na een
bewuste finalisatie worden afsluiter, tijdstip, algoritmeversie, cutoff en de
definitieve snapshot bewaard. Het HTML-rapport is voordien duidelijk voorlopig en
nadien definitief. Er zijn geen Plenion-writes.

GPS-quick-corrections gebruiken dezelfde minuutprecisie als de bestaande cockpit:
seconden worden met `AddSeconds(30)` naar de dichtstbijzijnde minuut afgerond. De
originele GPS-timestamp blijft ongewijzigd in de evidence-snapshot bewaard.

## Scheduled Task

Installeer op de Windows Server pas na publicatie:

```powershell
.\scripts\Install-MonthlyReviewPrepareTask.ps1 `
  -PublishPath 'D:\TimeControl\app' `
  -DatabasePath 'D:\TimeControl\data\time-control.db' `
  -LogPath 'D:\TimeControl\logs\monthly-review-prepare.log' `
  -ServiceAccount 'THEBELGIAN\svc-timecontrol'
```

De taak `TimeControl Monthly Review Prepare` draait iedere 15e om 04:00, kan
handmatig gestart worden en wordt met dezelfde tasknaam bijgewerkt zonder duplicaat.
Het script ondersteunt een normaal serviceaccount (veilig credentialprompt) en gMSA.
Verwijderen:

```powershell
Unregister-ScheduledTask -TaskName 'TimeControl Monthly Review Prepare' -Confirm
```
