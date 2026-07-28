# Klantlocatiematching – ontwerp fase 2

Fase 1 implementeert alleen het model, `IGeocodingService`,
`IDistanceCalculator` en de geteste Haversine-berekening. Er worden nog geen externe
geocodingaanroepen uitgevoerd.

## Voorgestelde fase-2-stroom

1. Lees elk uniek Plenion `LEVADR`-adres en normaliseer straat, postcode, plaats en land.
2. Geocodeer een adres éénmalig via `IGeocodingService`.
3. Bewaar latitude, longitude, provider/status, geocodedatum en een instelbare radius
   lokaal bij `CustomerLocation`.
4. Reconstrueer Powerfleet-stops als de tijd tussen het einde van een rit en het begin
   van de volgende rit van hetzelfde voertuig.
5. Bereken met `IDistanceCalculator` de Haversine-afstand van stop tot klantlocatie.
6. Accepteer een ruimtelijke kandidaat wanneer de afstand binnen de locatiegebonden
   radius in meter valt.
7. Bereken de tijdsoverlap tussen de stop en de Plenion-prestatie.
8. Rangschik kandidaten op ruimtelijke afstand, tijdsoverlap en bronkwaliteit en stuur
   ambigue resultaten naar manuele controle.

## Ontwerpregels

- Radius is per klantlocatie instelbaar; één globale radius is slechts een default.
- Onvoldoende of verouderde coördinaten leiden niet tot een automatische afwijking.
- Adressen en coördinaten worden niet onnodig gelogd.
- Geocoding krijgt caching, rate limiting en herprobeerbeleid.
- Tijdelijke Powerfleet-gebieden zijn niet nodig: stops worden uit opeenvolgende ritten
  gereconstrueerd en lokaal met klantcoördinaten vergeleken.
- De geocoder moet vervangbaar en expliciet geconfigureerd zijn.
