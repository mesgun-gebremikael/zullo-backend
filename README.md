# ZULLO – Backend

ZULLO är en dejtingapplikation som utvecklats som ett fullstackprojekt. Detta repository innehåller applikationens backend och REST API.

## Tekniker
- C# och ASP.NET Core Web API (.NET 8)
- PostgreSQL, Entity Framework Core och Npgsql
- JWT-autentisering och BCrypt
- SignalR för realtidschatt
- Firebase Admin SDK för pushnotiser
- Swagger/OpenAPI för API-dokumentation i utvecklingsmiljö

## Funktioner
- Registrering, inloggning och användarprofiler
- Swipe, likes och matchningar
- Meddelanden och realtidskommunikation
- Pushnotiser
- Blockering och rapportering

## Projektstruktur
- `Controllers/` – API-endpoints
- `Data/` – databaskontext
- `Dtos/` – modeller för API-anrop och svar
- `Hubs/` – SignalR-hubbar
- `Migrations/` – databasmigreringar
- `Models/` – datamodeller
- `Serivces/` – tjänsteklasser (mappnamnet i projektet)
- `Program.cs` – konfiguration och uppstart

## Starta lokalt
Du behöver .NET 8 SDK, PostgreSQL och Firebase-konfiguration för projektet.

1. Klona projektet och öppna backendmappen.
2. Konfigurera PostgreSQL under `ConnectionStrings:Default` och en säker JWT-nyckel under `Jwt:Key`. Använd exempelvis .NET User Secrets eller miljövariabler.
3. Konfigurera Firebase-tjänstekontot. Vid uppstart läser koden filen `firebase-key.json` från arbetskatalogen.
4. Kör:

```bash
dotnet restore
dotnet ef database update
dotnet run
```

Om kommandot `dotnet ef` saknas behöver ett kompatibelt EF Core CLI-verktyg installeras.

I utvecklingsläge finns Swagger på `/swagger`. SignalR-hubben finns på `/hubs/chat`.

**Säkerhet:** Lägg aldrig databaslösenord, JWT-hemligheter eller privata Firebase-nycklar i GitHub.

## Frontend
[ZULLO – Flutter-frontend](https://github.com/mesgun-gebremikael/zullo-frontend)

## Status
Projektet körs i utvecklingsmiljö och är inte publicerat som en färdig tjänst.

**Utvecklare:** Mesgun Gebremikael
