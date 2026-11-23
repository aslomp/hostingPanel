# HostingPanel

Een zelfstandige, responsive hostingbeheerinterface geïnspireerd op het dashboard en de navigatiestructuur van DirectAdmin Evolution.

## Direct starten

De eerste versie is dependency-free: open `index.html` in een moderne browser. Er is geen buildstap of webserver nodig.

Je kunt ook GitHub Pages inschakelen voor de `main`-branch, map `/`, om de interface te publiceren.

## Functies

- Nederlandstalig dashboard, adminnavigatie en gebruikerweergave
- Adminsecties: Accountbeheer, Server Beheer, Admin Gereedschap, Systeem Info & Bestanden, Extra Programma's, Support
- Zoeken naar pagina's met Ctrl/Cmd + K
- Lijst- en filterweergaven voor gebruikers, domeinen, DNS, e-mail, databases, bestandsbeheer en services
- Widgets voor voorbeeldstatistieken, gebruik en activiteiten
- Mobiele navigatie en toetsenbordbediening
- Interactieve demo-modal voor nieuwe invoer

## Status

**Frontendprototype.** Alle statistieken en records zijn voorbeeldgegevens. De formulieren hebben geen persistentie; de rollenwissel is slechts een UI-weergave en **geen autorisatie**. Er worden geen requests naar de DirectAdmin-server gestuurd.

## Architectuur naar productie

Voor koppeling aan de eigen hostingcontroller:

1. Vervang `sampleRows` en de statische dashboardstatistieken door geautoriseerde API-aanroepen.
2. Laat een backend sessies, authenticatie en RBAC afdwingen. Vertrouw nooit op de rol in de frontend.
3. Definieer afzonderlijke API-contracten voor gebruikers, domeinen, DNS, webhosting, certificaten, services en back-ups.
4. Laat configuratiewijzigingen via een gecontroleerde reconciliation-loop verlopen in plaats van direct shellcommando's vanuit de UI.
5. Voeg validatie, auditlogs, CSRF-bescherming waar van toepassing en tests toe voordat schrijfoperaties live gaan.

## Herkomst

De labels en informatiearchitectuur zijn gebaseerd op de op 23 november 2026 bekeken DirectAdmin Evolution-dashboardweergave. De implementatie bevat eigen HTML, CSS en JavaScript zonder DirectAdmin-assets of externe afhankelijkheden.
