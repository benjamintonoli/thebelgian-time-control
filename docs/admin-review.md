# Admin Review MVP (HumanReviewRequired)

## MatcherUsageMode

`MatcherUsageMode = HumanReviewRequired`

- Matcherresultaten zijn voorstellen, geen definitieve acceptatie.
- Finale locked holdout: **NO-GO** voor automatische acceptatie.
- Iedere voorgestelde match start als **Pending** tot een admin beslist.
- Geen Plenion-writeback.

## Holdoutresultaat (eenmalig, niet herhaald)

| Metric | Waarde |
|---|---:|
| Cases | 59 |
| Automatisch geaccepteerd | 3 |
| Correct accepted | 2 |
| False positive | 1 |
| False negative | 1 |
| Precision | 0.6667 |
| Coverage | 0.0508 (informatief) |
| Conclusie | uitsluitend HumanReviewRequired |

Beperking: de holdout bevatte **één technieker** (Jasper De Smet, okt–dec 2025) en
weinig positieve (`CorrectCandidate`) cases. Gebruik holdoutresultaten niet om
matchingdrempels te tunen.

## Routes

- `/Admin/Reviews` — overzicht met filters en spotcheck-sortering
- `/Admin/Reviews/{performanceId}` — detail + adminbeslissing

## Adminstatussen (SQLite append-only)

`Pending`, `Confirmed`, `Rejected`, `NeedsMoreInformation`, `NoReliableMatch`

Auditvelden: PerformanceId, oorspronkelijke matcheruitkomst, voorgestelde
VisitCandidate, adminbeslissing, eventueel gekozen kandidaat, opmerking,
reviewer, timestamp, matchercommit, configuratiehash.

## Spotcheckprioriteit

- ≤ 3 min: informatief
- ≥ 5 min: patroonrelevant
- ≥ 15 min: individuele uitzondering
- ≥ 30 min: hoge prioriteit

Terugkerende kleine voordelen per technieker worden gemarkeerd zonder
disciplinaire of loonconclusies.
