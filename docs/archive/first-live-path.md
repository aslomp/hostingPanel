> Historical snapshot: not current instructions. See [current documentation](../README.md).

# Eerste werkende pad — Blazor → Web API → DirectAdmin

## Geïmplementeerd

1. De beheerder opent HostingPanel.Web en voert zijn DirectAdmin-gebruikersnaam en login key in.
2. Blazor roept de onafhankelijke HostingPanel.Api via HTTPS aan.
3. De API stuurt het verzoek door naar de vast geconfigureerde DirectAdmin HTTPS-origin. Er is **geen gedeeld admin-wachtwoord**.
4. Blazor valideert de aanmelding door daadwerkelijk `CMD_API_SHOW_ALL_USERS?json=yes` aan te roepen en het JSON-arrayantwoord te controleren.
5. De pagina **Toon alle gebruikers** haalt dezelfde lijst opnieuw live op, toont de gebruikersnamen, en ondersteunt lokaal filteren en vernieuwen.
6. De API-browser laat de ruwe HTTP-respons zien voor read-only GET-verzoeken naar DirectAdmin API-routes.

Dit is een werkende code-implementatie, maar nog **niet uitgevoerd tegen de echte DirectAdmin-server**. De web- en API-projecten vereisen .NET 10.

## Lokale configuratie

Vervang het voorbeeldhostadres niet in versiebeheer door een privéadres of credentials. Gebruik omgevingsvariabelen:

```bash
export DirectAdmin__BaseUrl='https://YOUR-DA-HOST:2222/'
export HostingPanel__ApiBaseUrl='https://localhost:7101/'
dotnet dev-certs https --trust
dotnet run --project src/HostingPanel.Api --urls https://localhost:7101
# Tweede terminal met HostingPanel__ApiBaseUrl ingesteld:
dotnet run --project src/HostingPanel.Web --urls https://localhost:7201
```

Open `https://localhost:7201/`. Voer een DirectAdmin-gebruikersnaam en bij voorkeur een login key met uitsluitend de noodzakelijke leesrechten in. Voor `CMD_API_SHOW_ALL_USERS` zijn adminrechten nodig.

Draai de offline contracttests:

```bash
dotnet build src/HostingPanel.Api -c Release
dotnet build src/HostingPanel.Web -c Release
dotnet run --project tests/HostingPanel.ContractTests -c Release
```

## Compatibiliteitsgrens

- De publieke API accepteert momenteel `CMD_API_*`, `CMD_*`, `api/*` en `static/swagger.json`.
- Ongewijzigde statuscodes, body, content-type en de meeste responseheaders worden doorgegeven. Cookies worden bewust niet doorgegeven; dit ondersteunt login-key/basic-auth CLI-clients, niet het volledige browsersessieprotocol.
- Voor de eerste verticale slice gebruiken we uitsluitend GET vanuit Blazor. Legacy POST-verzoeken kunnen door de compatibiliteitsproxy worden doorgegeven, maar worden niet als geverifieerd beschouwd.
- Errorobjecten met HTTP 200 worden niet als geldige gebruikerslijsten geaccepteerd.
- De Web API bevat geen wachtwoorden of gedeelde admin-credentials. De eindgebruiker levert zijn eigen DirectAdmin login key.
- Om clientcompatibiliteit met de echte DirectAdmin-release aan te tonen, moeten HTTP- en response-contracttests tegen die specifieke versie worden uitgevoerd.

## Beveiliging vóór live gebruik

Houd zowel de API als Blazor achter interne toegang, TLS en een externe authenticatie-/toegangslaag totdat server-side gebruikerssessies, rate limiting, auditing en authorization gereed zijn. Zet de compatibiliteitsproxy **niet publiek**: iedere toegelaten caller met geldige credentials kan elke DirectAdmin API-route aanroepen waar zijn rechten dat toestaan.

Beperk de egress van HostingPanel.Api naar het exacte DirectAdmin IP en poort 2222. Gebruik geen ongeldige-certificaat-workarounds. De webapp bewaart de login key alleen in Blazor circuit-memory, niet in localStorage of cookies. Hij blijft daarmee wel onderdeel van serverprocesgeheugen.

### Functionele controle

- Correcte login key + adminrechten → gebruikerslijst.
- Ongeldige key → zichtbare foutmelding en geen aangemelde sessie.
- Reseller-/gebruikersaccount zonder recht op de adminlijst → geen valse succesmelding.
- GET `/CMD_API_SHOW_ALL_USERS?json=yes` direct via HostingPanel.Api geeft hetzelfde antwoord als DirectAdmin.
- Refresh, zoeken en uitloggen werken zonder demo-records.
- Gebruikersnamen uit het antwoord worden door Razor ge-escaped.
