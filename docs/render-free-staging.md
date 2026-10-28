# Kort gratis test på Render

Detta är ett tillägg för en enda gratis webbtjänst och en egen tillfällig testdatabas. Det ändrar inte standardstarten eller produktionsflödet i `render-staging.md`. Ingen betald resurs eller extern databas skapas/uppdateras av denna PR.

## Vad användaren redan gjort

En gratis `zullo-staging-db` har skapats i Frankfurt med PostgreSQL 16. Den visade slutdagen är **27 november 2026**; därefter kan Render radera databasen om den inte uppgraderas. Använd bara testkonton och data som kan kastas. Gratis webbtjänster kan somna och ha långsam första uppstart. Det här är inte en publik produktionsmiljö.

## Varför ett särskilt startläge behövs

Gratis webbtjänster saknar det betalda pre-deploy-steget. I detta tillfälliga upplägg kan ägaren därför uttryckligen aktivera databasuppdatering före serverstart. Flaggan är av som standard. Den godtas endast med `ASPNETCORE_ENVIRONMENT=Staging`, förväntat databasnamn `zullo_staging` och en läsbar Firebase Admin-fil. Databasens verkliga namn kontrolleras fortfarande av migrationsverktyget innan SQL körs.

Servern börjar inte lyssna förrän migrationen lyckats. Fel stoppar starten. Omstart kör en idempotent kontroll och bevarar befintliga data. Läget får bara användas på **en instans och en isolerad testdatabas**. Det är inte en lösning för rullande produktionsuppgraderingar eller samtidiga migrationsprocesser.

## Inställningar i den nya Zullo-webbtjänsten

Behåll bibliotekstjänsten orörd. Repository ska vara `mesgun-gebremikael/zullo-backend`, branch `main` efter att denna PR mergats av ägaren. Namn `zullo-staging-api`, Docker, tom Root Directory, Frankfurt, Free. Låt Docker Command och Pre-Deploy Command vara tomma så Dockerfilens entrypoint används. Health Check Path ska vara `/health/ready`. Auto Deploy ska vara **Off**, så en framtida merge inte startar en driftsättning utan beslut.

| Miljövariabel | Värde |
|---|---|
| ASPNETCORE_ENVIRONMENT | `Staging` |
| Deployment__ExpectedDatabaseName | `zullo_staging` |
| ConnectionStrings__Default | Kopiera just Zullo-testdatabasens **Internal Database URL** direkt från Render till detta fält. Skicka den inte i chatten. |
| Jwt__Key | En separat, slumpmässig hemlighet för staging. Dela eller visa inte värdet. |
| Firebase__CredentialsPath | `/etc/secrets/firebase-key.json` |
| ZULLO_STAGING_MIGRATE_ON_START | `true` — detta är ett uttryckligt val att uppdatera den isolerade testdatabasen vid start. |

Lägg in rätt Firebase Admin-tjänstekonto som Render Secret File med filnamn `firebase-key.json`, direkt i Render. Den publika GoogleService-Info.plist från iPhone är **inte** servernyckeln. Återanvänd bara en befintlig lokal servernyckel om den hör till rätt projekt och är avsedd för detta; ingen privat nyckel ska laddas upp i chatten eller Git.

Om Secret Files bara kan läggas till efter att tjänsten skapats kan den första uppstarten stoppas med ett tydligt meddelande. Lägg då till filen innan en ny manuell deploy. Skyddet förhindrar att denna startväg migrerar databasen när filen saknas. Filens korrekta typ/private key valideras av Firebase SDK vid backendstart; en läsbar felaktig fil räcker inte för fungerande push.

## Koppla den skapade databasen

Vänta tills databasens status är Available. Kontrollera att själva **Database**-namnet är `zullo_staging` — resursnamnet `zullo-staging-db` är något annat. Om Render genererat ett annat databasnamn måste det korrigeras/granskas före deploy; stäng inte av måldatabasskyddet bara för att komma vidare.

Kopiera Internal Database URL till serverns miljövariabel. Båda resurserna ska ligga i Frankfurt. Ändra inte arbetsytans globala nätverksregler: de kan även beröra BookQuoteApp. Gå igenom Zullo-databasens egna regler separat när resurserna är klara; ett standardval som tillåter externa anslutningar är inte samma sak som att lösenord saknas.

När ägaren har godkänt start med ovanstående inställningar kan en manuell Deploy beställas. Inga sådana anslutningar eller SQL-uppdateringar mot Render utförs av våra automatiska tester. Kontrollera sedan `/health/ready`, serverloggar och testa registrering/inloggning innan riktiga telefonprov. Appens `ZULLO_API_URL` ska sättas till den HTTPS-adress den nya webbtjänsten får.

## När gratisperioden är slut

Säkerhetskopiera data som ska behållas före slutdagen, eller uppgradera/granska en betald databas i tid. Utgå inte från att gratisdata går att återskapa efter radering. Till produktion ska en separat miljö med egna hemligheter, verifierad backup och kontrollerad releaseprocess användas. Stäng av startflaggan och välj ett granskat pre-deploy-/migrationsflöde när betald/produktionsdrift införs.

.NET 8:s supportslut 10 november 2026 kvarstår som ett separat lanseringskrav enligt huvudguiden. Den nya flaggan gör inte hela appen lanseringsklar.

## Automatiska kontroller

Det utökade container-testet provar:

- Standardstarten migrerar inte automatiskt.
- Opt-in nekas i Production, med fel förväntat databasnamn och utan Admin-filen.
- En ny tom testdatabas blir klar före första lyssnande servern.
- En kall omstart bevarar kontot och tillåter inloggning igen.
- Ett migrationsfel/databasavbrott gör att startprocessen avslutas i stället för att börja servera.

Testerna använder en egen lokal Docker/Postgres-miljö och syntetiska Firebase-uppgifter, inte Render-databasen eller användarens tjänstekonto.
