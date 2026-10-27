# Profiluppdateringar och upptäcktspreferenser

## Kontrakt

POST och PATCH `/me/profile` använder samma säkra deluppdatering. Utelämnade fält bevaras. En ny profil kräver displayName, age och gender. Tom sträng eller tom lista rensar ett uttryckligt fält; null tillåts enbart för att rensa HeightCm. Övriga uttryckliga null-värden nekas. Ålder, stränglängder, höjd, koordinatpar, listor och HTTPS-bildadresser valideras innan någon ändring sparas.

`matchRadiusKm` kan sparas i samma request som profilfält (1–300 km); User och Profile sparas med samma SaveChanges. Den äldre radius-endpointen finns kvar med samma intervall. En ogiltig uppdatering ska inte ändra vare sig profil eller radie.

GET `/me/profile` returnerar även `interestedIn` och `matchRadiusKm`. `interestedIn` använder appens befintliga alternativ Män/Kvinnor/Alla. Tomt värde bevarar äldre användares ofiltrerade beteende. Swipe-feeden filtrerar på den aktuella användarens val före kandidaturvalet; detta är inte en ny regel om ömsesidiga preferenser. Like-, matchnings- och blockeringsregler behålls. Inställningen publiceras inte i andra användares SwipeProfileDto.

IsVisible räknas om när bilder uttryckligen ändras. En orelaterad uppdatering ska inte återaktivera en redan dold profil.

Samtidig ändring av samma fält använder fortfarande senaste skrivningen; ETag/versionskonflikter ingår inte. Valideringen verifierar inte bildägarskap eller att en HTTPS-adress faktiskt innehåller en godkänd bild. Ortstext geokodas inte och inga koordinater fabriceras.

## Tester

`dotnet test Zullo.Api.Tests/Zullo.Api.Tests.csproj` kör HTTP-tester med MVC, JWT och SQLite. Profiltesterna kontrollerar samtliga fält i en komplett profil, svenska värden, äldre POST-anrop, deluppdateringar, explicit rensning, ogiltiga payloads utan sidoeffekter, behörighet och urval med blockerade profiler.

För migration mot PostgreSQL:

```sh
bash Zullo.Api.Tests/verify-profile-migration.sh
```

Det separata kontrollprogrammet skapar en unik testdatabas, applicerar tidigare migrationer, lägger in en äldre profil och kontrollerar uppgradering, upprepning, nedgradering och återapplicering. Alla ursprungliga profilfält jämförs. Enbart den skapade testdatabasen raderas i finally. Standardanslutningen är den lokala utvecklingsdatabasen på 127.0.0.1:5432 med databas/användare zullo. Vid annan isolerad testserver kan anslutningen anges i `ZULLO_PROFILE_TEST_ADMIN_CONNECTION`; värdet ska hanteras säkert och skrivs inte till loggen. Kontot behöver rätt att skapa testdatabaser.

Den nya migrationen lägger enbart till `Profiles.InterestedIn` med tomt standardvärde. Befintliga fält och radier skrivs inte om. Kör migreringen före backendversionen tas i bruk. Kod kan återställas medan den extra kolumnen behålls. Down tar bort nytillkomna preferenser; export/backup krävs om dessa ska bevaras. Nedgradering till en klient vars slider bara stöder 200 km kräver också kompatibilitetshantering för nya radier över 200 km.
