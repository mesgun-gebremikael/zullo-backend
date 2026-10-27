# Autentiseringsskydd

`AddAuthenticationProtection` registrerar samma regler i Program och HTTP-testvärdarna:

- Login: högst 5 försök per normaliserad e-postadress och minut, delat mellan requests/IP-adresser.
- Login och register tillsammans: högst 20 anrop per klient-IP och minut, även ogiltiga requestkroppar.
- Ingen kö. Överskriden gräns ger HTTP 429 med JSON-meddelande och `Retry-After` i sekunder.
- Lyckade försök räknas också; ett lyckat login får inte användas för att nollställa spärren.
- Saknade konton, fel lösenord och konton utan lösenord får samma inloggningsfel. BCrypt körs även för saknade/lösenordslösa konton. Detta är inte en garanti för identiska nätverkstider.

Begränsningen spärrar inte konton permanent och ändrar inga lösenord eller JWT-kontrakt. Fast fönster innebär att trafik kan komma på båda sidor om en minutgräns.

## Driftgränser före publik lansering

Limiter-tillstånd ligger i minnet per backendprocess och försvinner vid omstart. Vid flera repliker krävs samordnat skydd i gateway eller distribuerad lagring. Rate limiting ersätter inte e-postverifiering, tokenåterkallelse, övervakning eller skydd mot distribuerade attacker.

IP-nyckeln är `RemoteIpAddress`. Klientens `X-Forwarded-For` accepteras inte direkt. Bakom reverse proxy måste driftmiljön konfigurera uttryckligen betrodda proxyadresser och ForwardedHeaders före rate limiter. Annars kan alla klienter dela proxyns IP-budget. Aktivera inte godtycklig tillit till externa forwardingheaders. Verifiera detta i den faktiska driftmiljön innan publik trafik tas emot.

## Tester

`dotnet test Zullo.Api.Tests/Zullo.Api.Tests.csproj` inkluderar HTTP-tester med verkliga controllers, modellvalidering och samma limiterregistrering som Program. Testerna täcker gränser för kända/okända konton, olika klient-IP, förfalskad forwardingheader, fortsatt normal registrering/login samt parallella försök och återhämtning av kvot. Testvärden startar inte Firebase eller hela produktionskonfigurationen.
