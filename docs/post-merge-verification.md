# Kontroll efter merge av backend #7 och frontend #4

## Underlag

Hämtat och verifierat backend `8b64cc0` och frontend `24eb5c1` från `origin/main`. Basversionerna klarade 83 backendtester och 77 Fluttertester. Analyskontrollen godkändes med 128 dokumenterade äldre anmärkningar, inga analysfel. PostgreSQL-kontrollerna för både profil- och chattmigrering klarade uppgradering, upprepning, återställning och ny uppgradering i egna testdatabaser. Chattens laststeg klarade 6 400 anrop och upp till 1 003 öppna anslutningar utan registrerade fel.

## Rättningen i denna PR

Webb-SignalR skickar JWT som `access_token` i WebSocket-URL:n eftersom webbläsaren inte tillåter en Authorization-header där. Backend läste tidigare bara headern. Ett regressionstest gav 401 med giltig webbläsartoken före rättningen.

Token läses nu från frågesträngen endast under `/hubs/chat`, endast om Authorization-header saknas och exakt ett tokenvärde angivits. Samma ordinarie JWT-validering kontrollerar signatur och giltighet. Vanliga meddelande- och matchningsrouter accepterar fortfarande inte token i URL:n.

91 backendtester godkända efter rättningen, inklusive åtta nya tester för giltig/ogiltig/utgången/saknad token, headerprioritet och avvisning på vanliga API-router. PostgreSQL-/SignalR-testet startar dessutom en verklig WebSocket-klient med webbläsarens tokenformat och verifierar meddelandeleverans. Därmed är högsta anslutningsantal i detta utökade test 1 004.

## Gemensamt Flutter-/backendtest

Med den parade frontend-PR:ns explicita integrationstest:

```sh
bash Zullo.Api.Tests/verify-flutter-interop.sh /path/to/zullo-frontend
```

Kräver .NET 8, Flutter 3.38.4, projektens beroenden och separat lokal PostgreSQL (standardkonto/databas `zullo`, port 5432). Testet skapar och tar bort en egen slumpnamngiven databas, startar Kestrel på en ledig loopbackport och kör frontendens verkliga `AuthService`, `MessagesService`, `ThreadHistory` och `SignalRService`. Det provar matchningslistan, sändning, livehändelse, idempotent omsändning, läsgräns, frånkoppling/återanslutning, missad historik, omatchad åtkomst och blockering. Samtliga kontroller godkända. Testvärden använder riktiga controllers/JWT men startar inte produktions-Program.Main och initierar inte extern Firebase.

Tillfälliga JWT-nycklar och konton används. Inga riktiga pushnotiser skickas. Samma `ZULLO_CHAT_TEST_ADMIN_CONNECTION` som PostgreSQL-lasttestet kan ange en separat testserver; använd aldrig produktionsdatabasen. Integrationstestet körs uttryckligen, inte i den vanliga enhetstestsviten.

## Behöver databasen ändras?

Ja, om migrationen från redan mergade #7 inte finns i serverdatabasen: `20261027235933_ReliableMessageDelivery`. Föregående profilförändring heter `20261027231755_PreserveDiscoveryPreferences`; EF tar med alla saknade föregångare. Denna nya behörighets-PR tillför ingen migration.

Kontrollera på servern, med enbart läsbehörighet:

```sql
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
```

Applikationens `Program.cs` kör inte migrationer automatiskt. En merge eller omstart räcker därför inte för att säkert få rätt schema. Saknas chattkolumnerna kan de nya meddelandefrågorna misslyckas. Ta verifierad backup och följ `chat-verification.md` innan en godkänd migrering/driftsättning. Inga produktionsmigrationer har körts i denna kontroll.

## Behöver backend uppdateras på servern?

Den nya frontendens meddelandehistorik kräver backend #7 och rätt schema. Webb-SignalR kräver dessutom denna PR. Om servern kör en äldre version behöver den alltså uppdateras efter godkännande.

Serverns verkliga versions- och databasstatus är inte verifierad: ingen produktions-URL eller serveranslutning finns i den tillgängliga miljön. GitHub returnerade inga registrerade deployments och inga miljöer för backend. Den enda workflow som hittades är tester, inte driftsättning. Extern hosting kan ändå ha egna autodeployregler; frånvaro av GitHub-deployments bevisar inte att sådan hosting saknas.

GitHub Actions startade inte jobben för de mergade PR:erna: `The job was not started because your account is locked due to a billing issue.` Kontoinnehavaren behöver kontrollera GitHubs Settings → Billing and licensing (eller organisationens motsvarande sida), lösa kontospärren och sedan köra kontrollerna igen. Lokala testresultat ovan är verifierade; molnjobben får inte beskrivas som gröna.

## Push och manuella kontroller före lansering

Automatiskt verifieras pushens mottagaridentitet/payload, token-/kontoisolering och att missad liveleverans inte förlorar lagrade meddelanden. En riktig Firebase-leverans till en telefon är inte provad och push saknar fortfarande beständig utkö.

- Android: använd två separata testkonton på två telefoner/emulatorer. Matcha genom ömsesidiga likes, skicka åt båda håll och kontrollera läskvitton och omsändning efter nätavbrott.
- Prova push när mottagaren är i en annan vy, i bakgrunden och när appen är stängd. Ge notisbehörighet och kontrollera rätt Android-kanal (`messages`), Firebaseprojekt, backendens tjänstekonto och enhetstoken. Kontrollera att ett tryck öppnar rätt konto/samtal.
- Byt konto på samma telefon och prova blockering i båda riktningar; ingen notis ska öppna tidigare kontots eller blockerad konversation.
- `firebase_options.dart` är endast konfigurerad för Android. Webb och iOS kastar för närvarande UnsupportedError vid Firebase-start. Dessa plattformar behöver riktiga Firebase-appregistreringar och plattformskonfiguration; konfigurera inte med påhittade app-ID:n. Ett lyckat webbbygge är inte en fungerande webbpush.
- Releasefrontend måste byggas med serverns verkliga HTTPS-adress via `ZULLO_API_URL`. Riktiga Android/iOS-releasebyggen, signering och appbutiksflöde är inte verifierade i denna miljö.
