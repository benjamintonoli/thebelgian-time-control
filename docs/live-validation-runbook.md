# Live validation runbook (kantoor)

Offline freeze gebeurt met `--verify-frozen-matcher`. Dit runbook is alleen voor
live bevestiging op kantoor met Plenion. Open de locked holdout niet tijdens
stappen 1–7.

## 1. Repository en branch controleren

```powershell
cd C:\Dev\thebelgian-time-control
git fetch origin
git checkout feat/location-matching-benchmark
git pull --ff-only
git rev-parse HEAD
git status
```

Controleer dat `HEAD` overeenkomt met het commit-id in
`docs/frozen-matcher-manifest.json` (of bewust nieuwer is vóór herbevriezen).

## 2. Secrets en Plenion-verbinding controleren

Controleer lokale configuratie (niet committen):

- `ConnectionStrings:PlenionOdbc` in user-secrets of lokale `appsettings.*.json`
- Powerfleet-credentials indien live slices nodig zijn
- Geoapify-key alleen indien live geocoding nodig is

Snelle ODBC-sanity via een bestaande CLI die Plenion raakt (faalt duidelijk
zonder verbinding):

```powershell
dotnet run --project src/TheBelgian.TimeControl.Web -c Release --no-launch-profile -- --broader-validation
```

## 3. Verse Release-build

```powershell
dotnet clean
dotnet build -c Release
dotnet test -c Release --no-build
```

Gebruik daarna alleen deze Release-build (geen oudere DLL’s).

## 4. Live development-sanitycheck (geen holdout)

Kalibratie (30 cases) + development-sanity in hetzelfde command:

```powershell
dotnet run --project src/TheBelgian.TimeControl.Web -c Release --no-build --no-launch-profile -- --calibration-single-reviewer-eval
```

Recovery-audit (51 cases):

```powershell
dotnet run --project src/TheBelgian.TimeControl.Web -c Release --no-build --no-launch-profile -- --evaluate-recovery-audit
```

Open `docs/location-matching-holdout.json` niet.

## 5. Live en offline resultaten vergelijken

Offline referentie (mag ook op kantoor; raakt geen live providers):

```powershell
dotnet run --project src/TheBelgian.TimeControl.Web -c Release --no-build --no-launch-profile -- --verify-frozen-matcher
```

Vergelijk minstens:

| Metric | Offline rapport | Live output |
| --- | --- | --- |
| Calibration precision / coverage / FP | `docs/frozen-matcher-verification.json` | `--calibration-single-reviewer-eval` hybrid-regel |
| Recovery-only precision | zelfde JSON | `--evaluate-recovery-audit` |
| All-labeled hybrid precision | zelfde JSON | `--evaluate-recovery-audit` |
| Regressie-IDs 276126…280198 | `RegressionChecks` | live hybrid beslissingen |

## 6. Afwijkingen verklaren en rapport bewaren

Bewaar:

- `docs/frozen-matcher-verification.json`
- `docs/calibration-single-reviewer-eval.json`
- `docs/recovery-audit-evaluation.json`

Noteer of verschillen komen door live slices vs offline kandidaten, ontbrekende
driver-ids, of geocode-cache — niet door stille drempelwijzigingen.

## 7. Alleen bij geslaagde live sanitycheck opnieuw bevriezen

Wanneer live metrics de frozen criteria evenaren (of bewust goedgekeurde
afwijkingen hebben) en matchinglogica ongewijzigd blijft:

```powershell
dotnet run --project src/TheBelgian.TimeControl.Web -c Release --no-build --no-launch-profile -- --verify-frozen-matcher
```

Dit herschrijft `docs/frozen-matcher-manifest.json` met commit + configuratiehash.
Commit daarna alleen codewijzigingen; manifest/rapport blijven gitignored.

## 8. Locked holdout: blind labeling, daarna exact één keer evalueren

### 8a. Blind review pack exporteren (geen evaluatie)

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/TheBelgian.TimeControl.Web -c Release --no-build --no-launch-profile -- --export-locked-holdout-review
```

Schrijft:
- `docs/location-matching-holdout-review-pack.md`
- `docs/location-matching-holdout-labels.json` (lege template)

Wijzigt de locked holdout niet. Label handmatig in het labelbestand.

### 8b. One-shot evaluatie (pas na volledige labels)

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/TheBelgian.TimeControl.Web -c Release --no-build --no-launch-profile -- --evaluate-locked-holdout
```

- Weigert wanneer final of started al bestaat.
- Weigert onvolledige labels **vóór** de one-shot (geen started-marker).
- Schrijft `docs/location-matching-holdout-final.json` en `.md`.
- Gebruikt geen live Plenion/Powerfleet/Geoapify.
- Holdoutresultaten niet gebruiken om drempels te tunen.
