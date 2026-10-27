# Backendtester

Kör från backendmappen:

```sh
dotnet test Zullo.Api.Tests/Zullo.Api.Tests.csproj
```

`ChatAuthorizationTests` skickar HTTP-anrop genom ASP.NET Core TestServer med verklig MVC-routing, JWT-validering, behörighetsfilter, controllers och en separat SQLite-minnesdatabas per test. Testnycklar skapas slumpmässigt. Testvärden startar inte `Program.Main` eller Firebase och skickar inga externa pushnotiser. Testerna ersätter därför inte provning av produktionsuppstart, PostgreSQL-samtidighet eller mobilklienten.

Testerna kontrollerar:

- Att `/matches/force-match` saknas i både Production och Development, utan nya likes eller matchningar.
- Att saknade, felaktiga och utgångna JWT nekas på samtliga tre meddelanderouter.
- Att omatchade användare inte får läsa, skicka eller läsmarkera meddelanden.
- Att en blockering via `/blocks` stoppar befintlig chatt i båda riktningarna och döljer matchningen.
- Att direkta like-anrop inte kringgår blockeringar eller förbrukar kvot.
- Att borttagen matchning upphäver chattåtkomst.
- Att ömsesidiga likes fortfarande skapar en matchning och tillåter sändning, läsning och läsmarkering.
- Att en matchning inte ger tillgång till motpartens andra konversationer.

Nekade meddelandeanrop kontrolleras också mot databasen: inga nya meddelanden eller läsmarkeringar får sparas. De två befintliga matchningstesterna bevaras.
