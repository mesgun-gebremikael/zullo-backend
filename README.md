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

## Render-testmiljö

Se [förberedelse, kostnadsgranskning och separat staging](docs/render-staging.md). Docker-/Render-filerna skapar inga resurser på egen hand. Ingen produktion eller befintlig BookQuoteApp ska ändras.

### Standalone activity privacy

`zullo_hide_activity_monthly` is a separate monthly native subscription mapped to the RevenueCat entitlement `activity_privacy`. It grants only the ability to hide online/last-active timestamps. Full `premium` also includes that ability. Server-verified expiry, store/sandbox checks, grace, refunds and reconciliation use the same authenticated provider flow. Incognito, rewind, ordinary-like limits and included consumables remain Premium-only.

Apply `AddStandaloneActivityPrivacy` before deploying the matching frontend. It adds nullable `CommerceAccounts.ActivityPrivacyUntilUtc`; existing accounts get no new rights. `Commerce__Enabled` remains false by default. Prices, actual store products, sandbox purchase/restore/refund/expiry and cross-product upgrade/cancellation are still to be configured and tested; no live purchase or payout is enabled. Separate subscriptions must be managed explicitly if later buying Premium to avoid overlapping renewals.

### Account lifecycle

Apply migration `AccountLifecycle` before deploying JWT account-state validation. Pause/reactivate and confirmed password-protected closure/erasure requests are documented in [account lifecycle](docs/account-lifecycle.md). Closing removes the published profile and conversations, rejects old JWTs and records external-asset follow-up. Legacy external images still require a reviewed cleanup process; an accepted request does not certify full external erasure. No production account is modified by the migration itself.
