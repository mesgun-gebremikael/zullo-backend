# Separat Zullo-testmiljö på Render

## Vad denna PR gör – och inte gör

Förbereder serverpaket och inställningar. Inga Render-resurser skapas, inga betalningar görs och ingen produktion eller befintlig BookQuoteApp ändras. Backend/frontend fortsätter fungera lokalt som tidigare. Staging är en separat server och databas för testkonton.

`deploy/render.staging.example.yaml` är ett granskningsunderlag, inte en instruktion att klicka på Deploy nu. Den ligger inte som en automatiskt upptäckt render.yaml i roten. Att faktiskt tillämpa den skapar **betalda resurser**. Ägaren måste först godkänna aktuellt pris för webbtjänsten, databasen och lagringen i Render. Bekräfta också att de valda resursnamnen inte redan används av något annat projekt.

## Paketet

Docker bygger .NET-appen och ett separat migrationsverktyg (`/app/efbundle`). Slutbilden använder ASP.NET-runtime, kör som icke-root och innehåller inte utvecklingsdatabasens anslutning, JWT-nyckel, Firebase-tjänstekonto, SDK eller testkod. En ny minimal appsettings.json innehåller endast loggnivåer och AllowedHosts.

Entrypoint lyssnar på Render PORT, som standard 10000. Staging använder inte utvecklingssidornas Swagger eller detaljerade felmeddelanden. Docker fungerar med både befintlig Npgsql-anslutningssträng och Render-formatet `postgresql://...`. URL-uppgifter avkodas genom NpgsqlConnectionStringBuilder så att till exempel semikolon i lösenord inte blir extra anslutningsinställningar. Render-intern URL får TLS-läget Require som standard; det är rätt för dess självsignerade interna certifikat. Övriga URL-parametrar än sslmode avvisas. Lokala anslutningssträngar ändras inte.

`/health/live` visar att processen kör. `/health/ready` kräver nåbar databas och att alla EF-migrationer är införda. Svaret innehåller inga lösenord, interna SQL-fel eller användaruppgifter. Hälsokontrollen har en femsekundersgräns. Varken appstart eller health-anrop uppdaterar databasen.

## Resurser att godkänna senare

Exemplet använder separata namn och samma region (Frankfurt):

- `zullo-staging-api`: Docker-webbtjänst, planen Starter.
- `zullo-staging-db`: Postgres-planen `0.1c-256mb`, 5 GB lagring.
- Databasen stängs för publika anslutningar med `ipAllowList: []`; webbtjänsten använder privat anslutning i samma region.
- Automatisk driftsättning är **avstängd** (`autoDeployTrigger: 'off'`). Merge är inte en instruktion att driftsätta. Första resursuppläggning/Blueprint-synk kan ändå starta ett initialt bygge/deploy och ska därför inte göras före godkännande och hemlighetsinställning.

Dessa är små **testplaner**, inget löfte om produktionskapacitet. Använd inte tidsbegränsad gratisdatabas för data som måste bevaras. Priset ska kontrolleras i Render vid beslut; ingen kostnad har beställts i denna PR.

## Hemligheter före första serverstart

Konfigurera i den **nya Zullo-tjänsten**, inte i BookQuoteApp:

| Inställning | Värde/källa |
|---|---|
| ASPNETCORE_ENVIRONMENT | Staging |
| ConnectionStrings__Default | Den nya stagingdatabasens **Internal Database URL**. Blueprint-exemplet kopplar den automatiskt från just den databasen. |
| Deployment__ExpectedDatabaseName | `zullo_staging`. Både servern och migrationsverktyget stoppar om anslutningen anger ett annat databasnamn. Det minskar risken för att klistra in bibliotekets anslutning av misstag. |
| Jwt__Key | Ny, slumpmässig hemlighet separat från lokal utveckling och framtida produktion. Blueprint kan generera den. Ändring loggar ut befintliga staging-sessioner. |
| Firebase__CredentialsPath | `/etc/secrets/firebase-key.json` |
| Render Secret File | Filnamn `firebase-key.json`, innehåll från rätt Firebase **Admin-tjänstekonto**. |

Firebase Admin-nyckeln är en privat serverhemlighet. Ladda upp den direkt som Secret File i Render när driftsättningen godkänts. Skicka den inte i chatten, Git eller en PR. Den redan uppladdade iPhone-filen GoogleService-Info.plist är **offentlig klientkonfiguration och kan inte användas som servernyckel**. Servernyckeln ska höra till samma Firebaseprojekt som Android/iPhone (`zullo-13dac`). Om korrekt Admin-nyckel saknas startar servern inte; vi stänger inte av push i smyg för att få grön status.

Secret Files kan behöva läggas till manuellt i Render efter att tjänsten skapats. Exempel-Blueprinten innehåller ingen privat fil och kan därför inte starta servern helt själv. Vid manuell konfiguration ska Dockerfile vara `/Dockerfile`, health path `/health/ready` och Pre-Deploy Command `/app/efbundle`. Kontrollera att den valda planen stödjer pre-deploy och Secret Files före skapandet. Undvik upprepade initialdeploys innan inställningarna finns på plats.

## Databas och godkänd driftsättning

1. Granska PR, pris och att resurserna verkligen är **Zullo staging**. Bevara BookQuoteApp orörd.
2. Efter ägarens godkännande: skapa de separata resurserna och lägg in rätt miljövärden/Secret File. Kontrollera att databasen är ny och tom; importera inte lokal användardata automatiskt.
3. Granska `dotnet ef migrations script` för den aktuella versionen. Den redan mergade chattmigrationen måste införas tillsammans med alla tidigare migrationer. Ta backup om databasen innehåller något att bevara. Denna PR tillför ingen ny schemaändring.
4. Vid uttryckligen beställd deploy kör Render `/app/efbundle` före trafikväxling. Detta är en **avsiktlig databasuppdatering**, inte en bieffekt av appstart. Fel ska stoppa deployen. Befintlig schemaändring från #7 fyller meddelandekolumner och bygger index; pausa meddelandeskrivningar och blanda inte gamla/nya skrivare vid den första uppgraderingen av en använd databas.
5. Kontrollera status och båda health-adresserna. En 503 på ready betyder att något återstår; kringgå inte detta genom att välja live som Render-healthcheck.
6. Bygg testappen med den faktiska HTTPS-adress Render tilldelar tjänsten. Vi känner inte adressen förrän tjänsten finns. Android: `--dart-define=ZULLO_API_URL=https://DEN-VERKLIGA-ADRESSEN`. iPhone behöver dessutom `--dart-define-from-file=config/ios.firebase.json` från frontend-PR #7. En serveradress ändras inte automatiskt i redan installerade appar.
7. Prova två testkonton: registrering, bilder, ömsesidig matchning, meddelanden, läskvitton, nätavbrott/återanslutning, blockering och riktiga pushnotiser. Ingen testversion ska marknadsföras som publik lansering.

Migrationsverktyget körs också manuellt med `docker run ... IMAGE migrate` eller `IMAGE /app/efbundle`; använd bara en godkänd anslutning till avsedd miljö. Det skriver inte ut anslutningssträngen. Återställ inte schema automatiskt vid kodrollback: backuper, data och kompatibilitet måste granskas först.

## Från test till publik lansering

Render kan behållas. Skapa då **en annan produktionsdatabas och webbtjänst**, med egna hemligheter, rätt kapacitet, verifierade backuper/återställning, övervakning och releaseprocess. Behåll staging för framtida tester. Samma kod kan användas med olika konfiguration; produktionens riktiga användardata ska inte blandas med testkonton.

Skala inte blint till flera backendinstanser: SignalR behöver en gemensam backplane och kvoter behöver granskas för flera processer. Nuvarande IP-baserade inloggningsbegränsning använder socketens käll-IP. Bakom Render kan den därför gruppera flera användare under en proxyadress. Den skyddas fortsatt mot förfalskad X-Forwarded-For; en verifierad lösning för betrodda proxyadresser krävs före större publik trafik, inte okritiskt förtroende för valfria klientheaders.

Backend riktar sig fortfarande mot .NET 8. Dess supportslut är 10 november 2026; en separat uppgradering till en stödd LTS-version behöver planeras före fortsatt publik drift. Denna testmiljö-PR byter inte ramverk och matchnings-/chattlogik i onödan.

## Tester utan Render-konto eller riktiga hemligheter

```sh
dotnet test Zullo.Api.Tests/Zullo.Api.Tests.csproj -c Release
bash Zullo.Api.Tests/verify-chat-postgres.sh
docker build --tag zullo-staging-check:local .
python3 Zullo.Api.Tests/verify-container.py
```

Containerkontrollen skapar ett eget Docker-nät och en egen Postgres-container. Den använder ett lokalt genererat syntetiskt Firebase-tjänstekonto enbart för uppstart; inga externa Firebase-anrop eller riktiga pushleveranser görs. Den provar riktigt Program.Main, portval, icke-root, frånvaro av inbyggda utvecklingshemligheter, migrering från tom databas, upprepad migrering, JWT/registrering/inloggning och health vid databasavbrott. Endast testets egna resurser tas bort i finally.

I en byggmiljö med egen HTTPS-proxy kan Docker-byggaren vid behov få en offentlig rot-CA via BuildKit-secret `build_ca` samt vanliga proxy-build-args. Certifikatet används bara i SDK-steget och följer inte med runtimebilden. Render och GitHub behöver normalt ingen sådan specialinställning.

Officiella underlag som granskats: [Render Blueprint-specifikation](https://render.com/docs/blueprint-spec) och [Render Postgres-anslutningar](https://render.com/docs/postgresql-creating-connecting). Ingen anslutning till Render-kontot eller dess befintliga tjänster har gjorts av testverktygen.
