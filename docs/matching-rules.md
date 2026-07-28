# Matchingregels fase 1

## Dagtijdlijn

Per technieker en datum worden bepaald:

- eerste Plenion-start en laatste Plenion-eindtijd;
- geregistreerde minuten na pauze, totale pauze en kilometers;
- geregistreerde verplaatsingsminuten op basis van omschrijvingsmarkeringen
  `verplaats`, `rijtijd`, `transport` en `onderweg`;
- eerste Powerfleet-rit, laatste Powerfleet-rit, rijminuten en afstand.

Verschillen in het voordeel van de medewerker zijn positief:

- start: `eerste rit - geregistreerde start`;
- einde: `geregistreerd einde - laatste rit`;
- verplaatsing: `geregistreerde verplaatsing - Powerfleet-rijtijd`.

## Toleranties

| Optie | Standaard | Betekenis |
|---|---:|---|
| `IgnoreDifferenceMinutes` | 3 | Verschillen tot en met 3 minuten worden genegeerd. |
| `PatternDifferenceMinutes` | 5 | Vanaf 5 minuten telt een dag mee voor patronen. |
| `IndividualExceptionMinutes` | 15 | Vanaf 15 minuten verschijnt een individuele uitzondering. |
| `HighPriorityExceptionMinutes` | 30 | Vanaf 30 minuten is de prioriteit hoog. |
| `PatternWindowDays` | 20 | Aantal meest recente werkdagen in het patroonvenster. |
| `PatternMinimumOccurrences` | 8 | Minimaal aantal afwijkende werkdagen. |
| `PatternCumulativeMinutes` | 60 | Minimale cumulatieve afwijking. |

Voor patroonanalyse telt per technieker en datum alleen het grootste relevante
positieve verschil. Start, einde en verplaatsing op dezelfde dag worden dus niet
opgeteld; dezelfde onderliggende afwijking kan niet dubbel meetellen.

## Uitkomsten

De service gebruikt uitsluitend neutrale statussen: geen afwijking, te vroege start,
te late eindtijd, hogere geregistreerde verplaatsing, structureel patroon,
onvoldoende Powerfleet-data, onzekere voertuigtoewijzing en manuele controle.
Een afwijkende of onbekende auto is onzekerheid, geen automatische foutconclusie.

## Grenzen

De herkenning van Plenion-verplaatsingen is een POC-aanname op omschrijving. Voor
praktijkgebruik moet een stabiele taakcode (`IDHFDTAAK`) worden bevestigd. Negatieve
verschillen worden niet als voordeelpatroon gemarkeerd. Er worden geen correcties
berekend of naar Plenion geschreven.
