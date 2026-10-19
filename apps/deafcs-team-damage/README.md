# DeafCS Team Damage 1.0.0

Separat plugin for SwiftlyS2 1.4.14 / .NET 10.

## Regler
- Blokkerer bullet, buckshot og slash (kniv) mellom forskjellige spillere på samme aktive lag.
- Fiendeskade, selvskade, HE, ild og annen skade går uendret videre til CS2.
- Endrer ingen cvars, HP direkte, FiveStack-filer eller kampflyt.
- Bruker TakeDamage.Pre og CancelOriginal; avregistrerer hooken ved unload.
- Prosentene for HE/ild styres fortsatt av serverens gjeldende konfigurasjon.

## Installering for server-test
Pakk ut addons-mappen under game/csgo på en SwiftlyS2 1.4.14-testserver.
DLL-en skal ligge i addons/swiftlys2/plugins/DeafCSTeamDamage/DeafCSTeamDamage.dll.
For DeafCS-containerens custom-plugin-mount kan addons-mappen legges under
/opt/custom-plugins før containeren starter, etter eksisterende mount-oppsett.
Start testserveren på nytt og kontroller at pluginen lastes uten feil.
Ingen automatisk installasjon i Docker-imaget er lagt til.

Eksisterende kampkonfigurasjon bruker mp_friendlyfire 1, grenade 0.85,
other 0.4 og grenade_self 1. Pluginen endrer ikke disse.
Knife-round-konfigurasjon med mp_friendlyfire 0 beholdes uendret.

## Validering
- Kompilert mot NuGet SwiftlyS2.CS2 1.4.14: 0 kompileringsfeil.
- Restore rapporterte NU1900 fordi nettverket til NuGet-sikkerhetsdata var utilgjengelig.
- Uendret FiveStack ble også bygget: 0 feil, 0 advarsler.
- 58 regeltester er skrevet og kompilert, men Windows programkontroll blokkerte kjøring.
  Testene dekker begge lag, kniv/skudd/hodeskudd/hagle,
  fiender, selvskade, HE/ild/andre skadeformer og ikke-spillende lag.
- Ikke kjørt inne i en CS2-server. Build og regeltester bekrefter ikke native runtime-oppførsel.

## Server-test før produksjon
1. Med og uten armor: skyt og bruk begge knivangrep på en lagkamerat.
   Forvent ingen HP- eller armor-skade. Gjenta på begge lag og med bots.
2. Sammenlign HE og molotov/incendiary mot baseline uten plugin,
   med samme posisjon og armor. Skaden skal være identisk med baseline.
3. Sammenlign fiendeskade, selv-HE, selv-ild og fallskade med baseline.
4. Test våpenbytte etter granatkast og skade etter at kasteren dør.
5. Kontroller FiveStacks player_hurt-rapportering for reell skade,
   rundeoverganger, knife round og plugin reload. Blokkerte treff skal ikke
   rapporteres som faktisk påført skade. HE/ild skal ikke dobbeltskaleres.

## Bygg fra kilde
Kjør dotnet build source/DeafCSTeamDamage.csproj -c Release med .NET 10 SDK.
SwiftlyS2-runtime skal leveres av serveren, ikke kopieres fra NuGet-pakken.

## Referanser
DEAFCS/game-server main: fbc8177fb768594d410e190726cc984b065cd056
SwiftlyS2 v1.4.14: bfffbdb137b94eb7dc0e06786b499a185066e285
API verifisert i ITakeDamage.cs, TakeDamage.cs, HookResult.cs og DamageTypes_t.cs.
Pluginen er skrevet for SWS2; den krever ikke CounterStrikeSharp.

Kjør regeltestene: dotnet run --project tests/PolicyTests.csproj -c Release

