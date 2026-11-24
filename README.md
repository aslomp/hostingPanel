# HostingPanel

[![License: AGPL-3.0-only](https://img.shields.io/badge/License-AGPL--3.0--only-blue.svg)](LICENSE)

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/aslomp)

Een zelfstandige, responsive hostingbeheerinterface geïnspireerd op het dashboard en de navigatiestructuur van DirectAdmin Evolution.

## Installatie / Installation

HostingPanel includes an ASP.NET Core API and a Blazor Server UI. The DirectAdmin provider is the default; the Kubernetes/SQLite provider is an experimental desired-state foundation, not a production hosting controller.

- [Installation guide — Docker Compose, Helm, native .NET](docs/installation.md)
- [Docker Compose configuration](deploy/compose/compose.yaml)
- [Helm configuration](deploy/helm/hostingpanel/values.yaml)
- [Kubernetes backend architecture and limitations](docs/kubernetes-sqlite-backend.md)

The original static UI prototype remains available via `index.html` and GitHub Pages. The following historical prototype notes refer to that static demo, not the .NET application.

## Schermdekking

Alle 49 zichtbare navigatie-items in de bekeken DirectAdmin Evolution Admin-/Gebruiker-navigatie hebben een afzonderlijke UI-definitie in `pages.js`. Elke pagina toont relevante formulieren, tabellen of tabbladen op basis van het type scherm. Dit is een **functionele UI-schets**, geen pixel-perfecte reconstructie van elk intern DirectAdmin-scherm: alleen de dashboardnavigatie was direct geïnspecteerd, en de gedetailleerde velden zijn daarom op basis van hun functie gereconstrueerd.

De pagina-opmaak staat in `pages.css`. Zowel `pages.js` als `pages.css` worden vanuit `index.html` geladen.

## Verfijnde workflows

De vier prioriteitsgebieden hebben nu eigen schermstromen in plaats van uitsluitend generieke formulieren:

- **Gebruikersbeheer:** gebruikersoverzicht, accountdetails, rollen, hostinglimieten, beveiliging, nieuwe accounts en pakketten.
- **Domeinbeheer:** overzicht, toevoegen, document root, PHP, HTTPS/SSL en subdomeinen.
- **DNS:** zones, records (A, AAAA, CNAME, MX, TXT en NS), records toevoegen/bewerken, SOA en nameservers.
- **Serverbeheer:** IP-beheer, HTTPD-configuraties, multi-server, services en configuratietabbladen voor PHP, TLS en andere serverinstellingen.

Alle weergegeven records zijn synthetisch. Formulieren en serviceacties hebben bewust **geen** opslag, backendconnectie of productie-effect. Dit zijn inhoudelijke UI-reconstructies; nog geen pixel-perfecte kopieën van alle oorspronkelijke DirectAdmin-pagina's.

## Functies

- Nederlandstalig dashboard, adminnavigatie en gebruikerweergave
- Adminsecties: Accountbeheer, Server Beheer, Admin Gereedschap, Systeem Info & Bestanden, Extra Programma's, Support
- Zoeken naar pagina's met Ctrl/Cmd + K
- Lijst- en filterweergaven voor gebruikers, domeinen, DNS, e-mail, databases, bestandsbeheer en services
- Widgets voor voorbeeldstatistieken, gebruik en activiteiten
- Mobiele navigatie en toetsenbordbediening
- Interactieve demo-modal voor nieuwe invoer

## Status

**Frontendprototype.** Alle statistieken en records zijn voorbeeldgegevens. De afzonderlijke paginaformulieren hebben geen persistentie; de rollenwissel is slechts een UI-weergave en **geen autorisatie**. Er worden geen requests naar de DirectAdmin-server gestuurd.

## Architectuur naar productie

Voor koppeling aan de eigen hostingcontroller:

1. Vervang `sampleRows` en de statische dashboardstatistieken door geautoriseerde API-aanroepen.
2. Laat een backend sessies, authenticatie en RBAC afdwingen. Vertrouw nooit op de rol in de frontend.
3. Definieer afzonderlijke API-contracten voor gebruikers, domeinen, DNS, webhosting, certificaten, services en back-ups.
4. Laat configuratiewijzigingen via een gecontroleerde reconciliation-loop verlopen in plaats van direct shellcommando's vanuit de UI.
5. Voeg validatie, auditlogs, CSRF-bescherming waar van toepassing en tests toe voordat schrijfoperaties live gaan.

## Herkomst

De labels en informatiearchitectuur zijn gebaseerd op de op 23 november 2026 bekeken DirectAdmin Evolution-dashboardweergave. De implementatie bevat eigen HTML, CSS en JavaScript zonder DirectAdmin-assets of externe afhankelijkheden.

## License and support

HostingPanel is licensed under the GNU Affero General Public License, version 3 only (`AGPL-3.0-only`). See [LICENSE](LICENSE). This applies to original HostingPanel code unless explicitly stated otherwise; third-party components retain their own licenses.

The project is provided without commercial support, SLAs, or guaranteed maintenance. Donations are entirely voluntary and do not create support obligations.
