# Tillförlitlig chatt: införande och verifiering

## Vad som ändras

Nya meddelanden får en unik leveransnyckel per avsändare och mottagare. Samma nyckel och text returnerar det redan sparade meddelandet, utan extra databasrad, livehändelse eller pushförsök. Annan text med samma nyckel ger HTTP 409. Behörighet och blockering kontrolleras även vid omsändning. Nya meddelanden begränsas till tre per två sekunder och avsändare; HTTP 429 innehåller Retry-After. Begränsningen gäller per backendprocess och är inte en distribuerad kvot.

PostgreSQL genererar ett leveransnummer. Ett transaktionslås per konversationspar säkerställer att nya skrivningar från denna backend publiceras i ordning inom samtalet. `/messages/thread-page` hämtar högst 100 meddelanden, som standard 50, med ogenomskinliga före/eftermarkörer. Gamla `/messages/thread` finns kvar för äldre klienter. Läsmarkeringar har en exakt nummergräns så att ett senare meddelande inte felaktigt visas som läst.

Livehändelser skickas till båda deltagarnas egna anslutningar. Ett missat livebesked kan återhämtas från databasen. Firebase-push är fortfarande ett separat försök med tidsgräns, utan beständig utkö. Testerna är inte ett löfte om garanterad pushleverans.

## Införande

1. Granska och merga denna PR samt den tillhörande frontend-PR:n separat. Ingen automatisk merge ingår.
2. Före databasändringen: ta verifierad backup och pausa meddelandeskrivningar. Migrationen lägger till och fyller kolumner samt bygger index; den kan låsa en stor meddelandetabell. Kör först på en testkopia av verklig datastorlek.
3. Kör migrationen `20261027235933_ReliableMessageDelivery` med projektets vanliga säkra databasanslutning och EF Core 8.0.23. Kör inga lasttestskript mot produktion.
4. Starta denna backendversion på samtliga instanser innan skrivningar återupptas. Blanda inte gamla och nya meddelandeskrivare: den gamla versionen använder inte konversationslåset eller leveransnyckeln.
5. Kontrollera inloggning, ömsesidig matchning, skicka/läsa, återanslutning och blockering i testmiljö. Publicera därefter frontend med rätt HTTPS-API-adress. Enbart merge bevisar inte att hosting har driftsatt rätt version; kontrollera projektets egna deployregler.

Migrationen bevarar gamla meddelande-ID:n, text, tider och befintliga klientnycklar, även historiska dubbletter. Dessa kan inte säkert slås ihop automatiskt. `Down` tar bort nya metadata och index; meddelanderaderna bevaras. Föredra en framåtriktad korrigering och behåll schema vid kodåterställning. En återställning till gammal kod behöver också samordnas med frontend och pausade skrivningar.

## Reproducerbara kontroller

```sh
dotnet test Zullo.Api.Tests/Zullo.Api.Tests.csproj -c Release
bash Zullo.Api.Tests/verify-chat-postgres.sh
ZULLO_CHAT_SOAK_SECONDS=600 bash Zullo.Api.Tests/verify-chat-postgres.sh
```

PostgreSQL-skriptet skapar och tar bort en egen slumpnamngiven testdatabas. Standardanslutningen är lokal, användare/databas `zullo`, port 5432. För en separat testserver kan `ZULLO_CHAT_TEST_ADMIN_CONNECTION` anges. Kontot behöver skapa/ta bort testdatabaser. Använd aldrig produktionsanslutning. `ZULLO_CHAT_TEST_RESULTS` väljer resultatfil; standard är `/tmp/zullo-chat-load-results.json`. Inga riktiga användare, enhetstoken eller externa pushanrop används.

## Uppmätt i molnutvecklingsmiljön

- 83 automatiska backendtester godkända, inklusive tidigare autentisering, profil, matchning och blockering.
- Riktig PostgreSQL: uppgradering, upprepad migrering, återställning och ny uppgradering bevarade historiska meddelanden.
- 32 samtidiga omsändningar gav en databasrad; fel text gav 409. 1 000 senare omsändningar gav inga extra rader eller livehändelser.
- Teststeg med 100, 500 och 1 000 konton: totalt 6 400 HTTP-anrop utan fel, högst 1 003 öppna SignalR-anslutningar. HTTP-samtidigheten var 32.
- Uthållighetstest: 609 sekunder, 468 000 ytterligare HTTP-operationer, HTTP-samtidighet 16, 1 003 öppna anslutningar, samtliga kontroller godkända. Hanterat minne efter tvingad GC ökade från cirka 62 till 70 MiB; detta är en observation, inte bevis mot alla minnesläckor.
- JWT nekade obehöriga liveanslutningar. En tredje användare fick inga privata meddelanden. Blockering nekade både omsändning och historik. Återanslutning återhämtade missade meddelanden och läskvitton.

Lastklient och API körde i samma ASP.NET TestServer-process med verklig SignalR-protokollhantering och lokal PostgreSQL. Detta mäter inte Internet/TLS, lastbalanserare, produktionshosting, Firebase eller batteri/bakgrundsläge på riktiga telefoner. Ett separat stagingtest och mobilprov krävs före en publik lansering. GitHub-kontrollen kör den korta lastkontrollen; uthållighetstestet körs uttryckligen vid behov.
