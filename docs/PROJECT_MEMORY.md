# ZULLO – bestående projektanteckningar

Uppdaterat 2026-11-26. Se också funktions- och lanseringsguiderna i docs/. Detta är sparade projektanteckningar, inte en schemalagd avisering.

## Uppskjutet av användaren
Resend/domän/SMTP skjuts upp uttryckligen. Påminn vid nästa arbete med registrering eller lanseringsförberedelser om: egen domän, Resend-konto, DNS-verifiering, API-nyckel som Render-hemlighet och verkligt inkorgstest. Ingen betaltjänst ska aktiveras. Guiden ligger i backend docs/RESEND_SETUP.md. Ny e-postregistrering är avsiktligt stängd med503 utan fungerande SMTP; befintlig login bevaras. Återinför aldrig obekräftade konton som genväg.

## Tidigare arbete
- Flutter-officiell lokalisering: 40 språk,544 kompletta UI-texter per språk, automatiskt/manuellt språkval, engelskt reservspråk och RTL. Modersmålsgranskning kvarstår.
- Profil-/chattgalleri, blockeringskort, modernare swipe/profil/navigation, religion och valfria samtalsfrågor. Upp till3 frågor med äldre svar bevarade. Hitta nya vänner.
- Automatisk GPS under användning/resume efter tillåtelse, ingen bakgrundsspårning; verklig radie-/åldersfallback med18+- och sekretessgränser.
- Verifierad mejlkod före nytt User/JWT, TLS-SMTP och begränsade försök/återförsök.
- Matchnotiskö, privat generisk meddelandepush och versionerad installationsägare vid kontobyten.
- Ett Premium med månad/år, separat Superlike/Boost-plånbok, verifierade serverrättigheter och kvittoskydd. Betalningar fortsatt avstängda. Okända avbrutna köp utan kvitto behöver auktoritativ återhämtning och butikstester före aktivering.
- GPS-kompatibilitetspatch och Java17-byggverktyg; varningar döljs inte.
- Senaste föregående kontroll:394 Fluttertester,311 servertester, ren Flutteranalys, PostgreSQL migration/konkurrens och servercontainer gröna. Slutlig Android/iOS-CI måste kontrolleras på rätt revision, tidigare resultat ska inte antas gälla nya ändringar.
- PR26 frontend (5aa569e) och PR22 backend (eb76ad8), ingen agentmerge eller produktion. Aktuell GitHubstatus behöver hämtas.

## Nytt uppdrag
Granska frontend och backend, förbättra konkret påvisade problem från A–Ö utan att förstöra befintligt. Fokus snabb mobilnavigation, lätta swipegester, snabba tydliga köpflöden och iOS/Android. Referensvideo: Instagram8.44sek,60fps, snabb flikväxling med bevarat innehåll och glidande markering. Emulatortest i debug kan inte bevisa fysisk telefons bildfrekvens.

## Tillstånd och lanseringsgränser
Tekniska beslut/fixar/tester och parallella agenter godkända. Ingen destruktiv databasändring, viktig dataradering, produktionspublicering eller betaltjänstaktivering utan separat godkännande. GPS, verkliga bilder/push, köp-/kvittosandbox, säker lagringsuppgradering, signering/APNs och språkgranskning måste verifieras före lansering.

## Dagens förbättringar av mobilflyt
Navigation behåller flikarnas innehåll och pausar dolda animationer; chattkvitton bygger inte om hemskärmen. Profil-/plånbokshämtningar samordnas och gamla kontosvar ignoreras. Swipe börjar animeras före nätverkssvar, förhindrar dubbeltryck och återställer vid fel/avbruten gest. Rewind, feed och swipe skyddas mot överlapp. Serverns feedfrågor har minskats från8 till3 och kvittohistorik hämtas i en batch med oförändrade ägar-/återbetalningsskydd. Köpknappen visar arbete direkt och kontot kontrolleras före butikens köpstart. Inga riktiga betalningar eller mejltjänster aktiverade. Se MOBILE_SMOOTHNESS_REVIEW.md (frontend) respektive MOBILE_PERFORMANCE_REVIEW.md (backend).
