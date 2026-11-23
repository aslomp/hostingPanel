# DirectAdmin Swagger – controllerverdeling en implementatieplan

> Status: ontwerp/inventarisatie, **niet geïmplementeerd**. Inventarisatie op 2026-11-23, branch `feature/blazor-directadmin-api`.
> Contractbron: `spec/swagger.json` (blob `d0728d2155edb2b38863418052820355787bfe99`), 274 paden / **326 HTTP-operaties**. Dit is een pinned snapshot; verifieer verschillen met de live Swagger bij elke sync. De legacy `CMD_*` / `CMD_API_*` endpoints vallen buiten deze Swagger.

## Architectuur- en compatibiliteitsinvarianten

1. **Exacte DirectAdmin-compatibiliteit**: behoud HTTP-method, volledig pad, query/path/body parameters, headers, content types, response/statuscodes, foutpayloads, streams/SSE en Swagger-schema's. Controllers groeperen code, maar introduceren geen nieuwe publieke `/api` routes.
2. **Backend is autoritatief** voor rechten, account-hiërarchie, toegankelijke domeinen en ondersteunde functionaliteit. Geen `admin/reseller/user`-heuristiek, RBAC-filtering, autorisatie of gesimuleerde 501 op API-/Blazor-laag. De backendresponse, inclusief echte 401/403/404/501, gaat door. Authenticatie/session-transport naar de backend blijft nodig.
3. **Eén eigenaar per method+pad**: routes uit `DirectAdminController` atomair verhuizen naar gespecialiseerde controllers; geen dubbele ASP.NET Core endpointregistraties. Houd de generieke controller tijdelijk alleen voor nog niet gemigreerde routes.
4. **Scope**: de externe `/hosting/*` convenience-routes en legacy adapters blijven apart van de officiële `/api/*` Swagger. Beschrijf ze in eigen OpenAPI, niet als officiële DA-operaties. `DomainController` en `DnsController` hebben op deze snapshot geen moderne domein-/DNS CRUD endpoints; dat is geen reden routes te verzinnen.
5. **Swagger-behoud**: volledige pinned DA-specificatie ongewijzigd beschikbaar houden; tests moeten elke method+path, request/response-schema en operation metadata controleren. De runtime Swagger van ASP.NET mag niet stilzwijgend de DA-spec vervangen.

## Bestaande controllers

| Bestaande controller | Huidige verantwoordelijkheid | Geplande aanpassing |
|---|---|---|
| UserController | gebruikers, gebruikerszoekfuncties en `/hosting` accountoverzichten | behouden; overige users/login-history onderbrengen zonder dubbele routes |
| DomainController | `/hosting` domeinbeheer en huidige `/api/domain-tls/*` | behoud legacy domeinroutes; verhuis modern TLS naar TlsController |
| DnsController | legacy `/hosting` DNS zones/records | behouden; geen moderne DNS-CRUD in deze snapshot |
| DatabaseController | `/hosting` databases, `db-manage`, `db-show`, `db-monitor` | behouden; phpMyAdmin SSO toevoegen |
| FileManagementController | moderne `filemanager` en `filemanager-actions` | behouden |
| FileAccessController | legacy FTP `/hosting/file-access/ftp` | behouden; SFTP/SSH niet verwarren met FTP |
| DirectAdminController | generieke opvang van overige moderne operaties | gefaseerd leegmaken; daarna verwijderen of uitsluitend expliciet als fallback behandelen |

## Verdelingsoverzicht (alle 326 Swagger-operaties)

| Doelcontroller | Operaties | Status |
|---|---:|---|
| AccountManagementController | 3 | **ONTBREEKT** |
| AuthenticationController | 24 | **ONTBREEKT** |
| CustomBuildController | 25 | **ONTBREEKT** |
| DatabaseController | 29 | Bestaat |
| EmailController | 8 | **ONTBREEKT** |
| EmailMigrationController | 4 | **ONTBREEKT** |
| ExecutionController | 1 | **ONTBREEKT** |
| FileManagementController | 23 | Bestaat |
| GitController | 10 | **ONTBREEKT** |
| LicenseController | 3 | **ONTBREEKT** |
| MaintenanceController | 3 | **ONTBREEKT** |
| MessageController | 5 | **ONTBREEKT** |
| MetricsController | 4 | **ONTBREEKT** |
| MigrationController | 7 | **ONTBREEKT** |
| PackageController | 3 | **ONTBREEKT** |
| PluginController | 8 | **ONTBREEKT** |
| ProfileController | 2 | **ONTBREEKT** |
| RedisController | 3 | **ONTBREEKT** |
| ResellerController | 2 | **ONTBREEKT** |
| SearchController | 3 | **ONTBREEKT** |
| SecurityController | 13 | **ONTBREEKT** |
| ServerController | 2 | **ONTBREEKT** |
| ServerSettingsController | 16 | **ONTBREEKT** |
| ServiceController | 8 | **ONTBREEKT** |
| SessionController | 11 | **ONTBREEKT** |
| SkinController | 40 | **ONTBREEKT** |
| SystemInfoController | 7 | **ONTBREEKT** |
| SystemUpdateController | 6 | **ONTBREEKT** |
| TerminalController | 1 | **ONTBREEKT** |
| TicketController | 2 | **ONTBREEKT** |
| TlsController | 21 | **ONTBREEKT** |
| UserController | 5 | Bestaat |
| VersionController | 3 | **ONTBREEKT** |
| WebProtectionController | 7 | **ONTBREEKT** |
| WidgetController | 1 | **ONTBREEKT** |
| WordPressController | 13 | **ONTBREEKT** |

**Samenvatting:** 33 doelcontrollers ontbreken. 69 operaties zijn al op gespecialiseerde controllers gerouteerd; 257 staan in de generieke controller; 0 operaties ontbreken in de huidige attributeroutes. 0 dubbel toegewezen method+pad combinaties aangetroffen.

## Implementatievolgorde

1. **Contractbaseline vastleggen**: genereer een machineleesbare manifestlijst uit `spec/swagger.json`: method+path+operationId+tag+request/response-schemahash; tel en controleer exact 326 operaties en 274 paden. Gebruik deze lijst als referentie voor migraties.
2. **Routes herstellen vóór refactoring**: onderzoek ontbrekende operaties en eventuele dubbele routes; herstel het exacte contract zonder nieuwe zelfbedachte paden.
3. **Sessions/auth & accountbeheer**: AuthenticationController, SessionController, AccountManagementController, ResellerController, PackageController, ProfileController, SearchController. Impersonation = `/api/session/login-as/*`; geen eigen autorisatiebeslissingen.
4. **Domein-/dataservices**: TlsController (domain-tls en server-tls), EmailController, EmailMigrationController, GitController, WebProtectionController; DatabaseController krijgt phpMyAdmin SSO. Domain/Dns/FileAccess blijven voorlopig legacy.
5. **Server & operations**: ServerController, ServerSettingsController, SystemInfoController, ServiceController, MetricsController, MaintenanceController, SystemUpdateController, VersionController, LicenseController, CustomBuildController.
6. **Security/tools/extensions**: SecurityController, MigrationController, ExecutionController, TerminalController, PluginController, RedisController, WordPressController, SkinController, MessageController, TicketController, WidgetController.
7. **Verwijder generieke routes pas na groen contract**: automatische per-operatie tests vergelijken de gehele pinned Swagger met de werkelijk geregistreerde endpoints; voer forwardingtests uit (auth, query, headers, raw body, status, content-type, binary, upload, SSE) en test backend-gestuurde 501/403/404 zonder transformatie.
8. **Contractdiff bij updates**: synchroniseer live DA Swagger naar nieuwe snapshot via bestaande sync-workflow; CI faalt op toegevoegde/verwijderde/gewijzigde operaties zonder expliciete herindeling en contractreview.

## Complete methode-/padmatrix

*Huidig* verwijst naar het controllerattribuut in de featurebranch. Een streepje betekent momenteel geen gerouteerde operatie. De onderstaande routegroepen volgen de officiële Swagger-tags; hergroeperen verandert geen enkele route.

### AccountManagementController (3) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `POST /api/change-user-creator` | Migrate | DirectAdminController |
| `POST /api/convert-reseller-to-user` | Migrate | DirectAdminController |
| `POST /api/convert-user-to-reseller` | Migrate | DirectAdminController |

### AuthenticationController (24) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/login-keys/keys/{id}` | Login keys | DirectAdminController |
| `DELETE /api/login-keys/urls/{id}` | Login keys | DirectAdminController |
| `GET /api/login-keys/commands` | Login keys | DirectAdminController |
| `GET /api/login-keys/keys` | Login keys | DirectAdminController |
| `GET /api/login-keys/keys/{id}` | Login keys | DirectAdminController |
| `GET /api/login-keys/keys/{id}/history` | Login keys | DirectAdminController |
| `GET /api/login-keys/urls` | Login keys | DirectAdminController |
| `GET /api/multi-factor-auth/recovery-codes` | Two-step authentication | DirectAdminController |
| `PATCH /api/login-keys/keys/{id}` | Login keys | DirectAdminController |
| `PATCH /api/multi-factor-auth/settings` | Two-step authentication | DirectAdminController |
| `POST /api/change-password` | Password | DirectAdminController |
| `POST /api/login` | Session control | DirectAdminController |
| `POST /api/login-keys/keys` | Login keys | DirectAdminController |
| `POST /api/login-keys/urls` | Login keys | DirectAdminController |
| `POST /api/login/url` | Session control | DirectAdminController |
| `POST /api/logout` | Session control | DirectAdminController |
| `POST /api/lost-password/confirm` | lost password | DirectAdminController |
| `POST /api/lost-password/request` | lost password | DirectAdminController |
| `POST /api/multi-factor-auth/disable` | Two-step authentication | DirectAdminController |
| `POST /api/multi-factor-auth/enable` | Two-step authentication | DirectAdminController |
| `POST /api/multi-factor-auth/otp/generate-secret` | Two-step authentication | DirectAdminController |
| `POST /api/multi-factor-auth/recovery-codes` | Two-step authentication | DirectAdminController |
| `POST /api/session/login-as/return` | Session control | DirectAdminController |
| `POST /api/session/login-as/switch` | Session control | DirectAdminController |

### CustomBuildController (25) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/custombuild/compile-scripts-custom/{app}` | Custombuild | DirectAdminController |
| `DELETE /api/custombuild/logs/{logname}` | Custombuild | DirectAdminController |
| `DELETE /api/custombuild/versions-custom/{app}` | Custombuild | DirectAdminController |
| `GET /api/custombuild/actions` | Custombuild | DirectAdminController |
| `GET /api/custombuild/compile-scripts` | Custombuild | DirectAdminController |
| `GET /api/custombuild/compile-scripts-custom/{app}` | Custombuild | DirectAdminController |
| `GET /api/custombuild/compile-scripts/{app}` | Custombuild | DirectAdminController |
| `GET /api/custombuild/logs` | Custombuild | DirectAdminController |
| `GET /api/custombuild/logs/{logname}/sse` | Custombuild | DirectAdminController |
| `GET /api/custombuild/options` | Custombuild | DirectAdminController |
| `GET /api/custombuild/options-v2` | Custombuild | DirectAdminController |
| `GET /api/custombuild/options/validate` | Custombuild | DirectAdminController |
| `GET /api/custombuild/removals` | Custombuild | DirectAdminController |
| `GET /api/custombuild/software` | Custombuild | DirectAdminController |
| `GET /api/custombuild/state` | Custombuild | DirectAdminController |
| `GET /api/custombuild/state/sse` | Custombuild | DirectAdminController |
| `GET /api/custombuild/updates` | Custombuild | DirectAdminController |
| `GET /api/custombuild/versions` | Custombuild | DirectAdminController |
| `GET /api/custombuild/versions-custom` | Custombuild | DirectAdminController |
| `PATCH /api/custombuild/options` | Custombuild | DirectAdminController |
| `PATCH /api/custombuild/options-v2` | Custombuild | DirectAdminController |
| `POST /api/custombuild/kill` | Custombuild | DirectAdminController |
| `POST /api/custombuild/run` | Custombuild | DirectAdminController |
| `PUT /api/custombuild/compile-scripts-custom/{app}` | Custombuild | DirectAdminController |
| `PUT /api/custombuild/versions-custom/{app}` | Custombuild | DirectAdminController |

### DatabaseController (29)

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/db-manage/databases/{database}` | Database | DatabaseController |
| `DELETE /api/db-manage/users/{dbuser}` | Database | DatabaseController |
| `GET /api/db-manage/databases/{database}/export` | Database | DatabaseController |
| `GET /api/db-manage/databases/{database}/export-definition` | Database | DatabaseController |
| `GET /api/db-monitor/processes` | Database monitor | DatabaseController |
| `GET /api/db-show/databases` | Database | DatabaseController |
| `GET /api/db-show/databases/{database}` | Database | DatabaseController |
| `GET /api/db-show/databases/{database}/users` | Database | DatabaseController |
| `GET /api/db-show/info` | Database | DatabaseController |
| `GET /api/db-show/users` | Database | DatabaseController |
| `GET /api/db-show/users/{dbuser}` | Database | DatabaseController |
| `GET /api/db-show/users/{dbuser}/databases` | Database | DatabaseController |
| `POST /api/db-manage/clone-db` | Database | DatabaseController |
| `POST /api/db-manage/clone-dbuser` | Database | DatabaseController |
| `POST /api/db-manage/create-db` | Database | DatabaseController |
| `POST /api/db-manage/create-db-with-user` | Database | DatabaseController |
| `POST /api/db-manage/create-user` | Database | DatabaseController |
| `POST /api/db-manage/databases/{database}/check` | Database | DatabaseController |
| `POST /api/db-manage/databases/{database}/fix-definers` | Database | DatabaseController |
| `POST /api/db-manage/databases/{database}/import` | Database | DatabaseController |
| `POST /api/db-manage/databases/{database}/import-definition` | Database | DatabaseController |
| `POST /api/db-manage/databases/{database}/optimize` | Database | DatabaseController |
| `POST /api/db-manage/databases/{database}/repair` | Database | DatabaseController |
| `POST /api/db-manage/users/{dbuser}/change-hosts` | Database | DatabaseController |
| `POST /api/db-manage/users/{dbuser}/change-password` | Database | DatabaseController |
| `POST /api/db-monitor/processes/{id}/kill` | Database monitor | DatabaseController |
| `POST /api/phpmyadmin-sso/account-access` | Database | DirectAdminController |
| `POST /api/phpmyadmin-sso/database-access/{database}` | Database | DirectAdminController |
| `PUT /api/db-manage/users/{dbuser}/databases/{database}/change-privs` | Database | DatabaseController |

### EmailController (8) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/emailvacation/{domain}/{user}` | Vacation | DirectAdminController |
| `GET /api/email-config/mobileconfig` | Email | DirectAdminController |
| `GET /api/email-logs` | Email | DirectAdminController |
| `GET /api/email-logs-summary` | Email | DirectAdminController |
| `GET /api/email-logs/user` | Email | DirectAdminController |
| `GET /api/emailvacation/{domain}` | Vacation | DirectAdminController |
| `GET /api/emailvacation/{domain}/{user}` | Vacation | DirectAdminController |
| `PUT /api/emailvacation/{domain}/{user}` | Vacation | DirectAdminController |

### EmailMigrationController (4) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/imapsync/migrations/{id}` | Imapsync | DirectAdminController |
| `GET /api/imapsync/migrations` | Imapsync | DirectAdminController |
| `POST /api/imapsync/export` | Imapsync | DirectAdminController |
| `POST /api/imapsync/import` | Imapsync | DirectAdminController |

### ExecutionController (1) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `POST /api/execute` | Exec | DirectAdminController |

### FileManagementController (23)

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/filemanager-actions/trash` | Filemanager | FileManagementController |
| `DELETE /api/filemanager-actions/trash/{id}` | Filemanager | FileManagementController |
| `GET /api/filemanager/disk-usage` | Filemanager | FileManagementController |
| `GET /api/filemanager/download` | Filemanager | FileManagementController |
| `GET /api/filemanager/download-archive` | Filemanager | FileManagementController |
| `GET /api/filemanager/list` | Filemanager | FileManagementController |
| `GET /api/filemanager/list-archive` | Filemanager | FileManagementController |
| `GET /api/filemanager/search-files` | Filemanager | FileManagementController |
| `GET /api/filemanager/search-text` | Filemanager | FileManagementController |
| `GET /api/filemanager/stat` | Filemanager | FileManagementController |
| `GET /api/filemanager/trash` | Filemanager | FileManagementController |
| `GET /api/filemanager/tree` | Filemanager | FileManagementController |
| `POST /api/filemanager-actions/chmod` | Filemanager | FileManagementController |
| `POST /api/filemanager-actions/copy` | Filemanager | FileManagementController |
| `POST /api/filemanager-actions/copy-to` | Filemanager | FileManagementController |
| `POST /api/filemanager-actions/create-archive` | Filemanager | FileManagementController |
| `POST /api/filemanager-actions/extract-archive` | Filemanager | FileManagementController |
| `POST /api/filemanager-actions/mkdir` | Filemanager | FileManagementController |
| `POST /api/filemanager-actions/move` | Filemanager | FileManagementController |
| `POST /api/filemanager-actions/move-to` | Filemanager | FileManagementController |
| `POST /api/filemanager-actions/remove` | Filemanager | FileManagementController |
| `POST /api/filemanager-actions/trash/{id}/restore` | Filemanager | FileManagementController |
| `POST /api/filemanager-actions/upload` | Filemanager | FileManagementController |

### GitController (10) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/git/uuid/{uuid}` | Git | DirectAdminController |
| `GET /api/git/domain/{domain}` | Git | DirectAdminController |
| `GET /api/git/uuid/{uuid}` | Git | DirectAdminController |
| `GET /api/git/uuid/{uuid}/branch/{branch}` | Git | DirectAdminController |
| `GET /api/git/uuid/{uuid}/commit/{commit}` | Git | DirectAdminController |
| `POST /api/git/domain/{domain}` | Git | DirectAdminController |
| `POST /api/git/user/{username}/uuid/{uuid}/webhook` | Git | DirectAdminController |
| `POST /api/git/uuid/{uuid}/deploy` | Git | DirectAdminController |
| `POST /api/git/uuid/{uuid}/fetch` | Git | DirectAdminController |
| `PUT /api/git/uuid/{uuid}` | Git | DirectAdminController |

### LicenseController (3) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/license` | Licensing | DirectAdminController |
| `GET /api/license/proof` | Licensing | DirectAdminController |
| `POST /api/license/update-key` | Licensing | DirectAdminController |

### MaintenanceController (3) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/maintenance` | Maintenance | DirectAdminController |
| `POST /api/maintenance/{task}/check` | Maintenance | DirectAdminController |
| `POST /api/maintenance/{task}/fix` | Maintenance | DirectAdminController |

### MessageController (5) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/messages` | Messages | DirectAdminController |
| `GET /api/messages/id/{id}` | Messages | DirectAdminController |
| `GET /api/messages/list` | Messages | DirectAdminController |
| `POST /api/messages/delete` | Messages | DirectAdminController |
| `POST /api/messages/read` | Messages | DirectAdminController |

### MetricsController (4) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/global-resource-usage/history/{user}` | Resource metrics | DirectAdminController |
| `GET /api/global-resource-usage/latest` | Resource metrics | DirectAdminController |
| `GET /api/resource-usage/history` | Resource metrics | DirectAdminController |
| `GET /api/resource-usage/latest` | Resource metrics | DirectAdminController |

### MigrationController (7) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/cpanel-import/tasks/{id}` | Cpanel | DirectAdminController |
| `GET /api/cpanel-import/tasks` | Cpanel | DirectAdminController |
| `GET /api/cpanel-import/tasks/{id}` | Cpanel | DirectAdminController |
| `GET /api/cpanel-import/tasks/{id}/log` | Cpanel | DirectAdminController |
| `GET /api/cpanel-import/tasks/{id}/log-sse` | Cpanel | DirectAdminController |
| `POST /api/cpanel-import/check-remote` | Cpanel | DirectAdminController |
| `POST /api/cpanel-import/tasks/start` | Cpanel | DirectAdminController |

### PackageController (3) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/reseller-packages/{id}` | Reseller packages | DirectAdminController |
| `GET /api/reseller-packages` | Reseller packages | DirectAdminController |
| `GET /api/reseller-packages/{id}` | Reseller packages | DirectAdminController |

### PluginController (8) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/plugin-manager/plugins` | Plugin manager | DirectAdminController |
| `GET /api/plugins/list` | Plugins | DirectAdminController |
| `POST /api/plugin-manager/plugins/{id}/activate` | Plugin manager | DirectAdminController |
| `POST /api/plugin-manager/plugins/{id}/deactivate` | Plugin manager | DirectAdminController |
| `POST /api/plugin-manager/plugins/{id}/delete` | Plugin manager | DirectAdminController |
| `POST /api/plugin-manager/plugins/{id}/update` | Plugin manager | DirectAdminController |
| `POST /api/plugin-manager/plugins/install-file` | Plugin manager | DirectAdminController |
| `POST /api/plugin-manager/plugins/install-url` | Plugin manager | DirectAdminController |

### ProfileController (2) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/profile/settings` | Profile | DirectAdminController |
| `PATCH /api/profile/settings` | Profile | DirectAdminController |

### RedisController (3) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/redis/status` | Redis | DirectAdminController |
| `POST /api/redis/disable` | Redis | DirectAdminController |
| `POST /api/redis/enable` | Redis | DirectAdminController |

### ResellerController (2) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/resellers/{username}/config` | Resellers | DirectAdminController |
| `GET /api/resellers/{username}/usage` | Resellers | DirectAdminController |

### SearchController (3) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/search/resources` | Backend search | DirectAdminController |
| `GET /api/search/users` | Backend search | UserController |
| `GET /api/search/users-extended` | Backend search | UserController |

### SecurityController (13) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/clamav/{pid}` | ClamAV | DirectAdminController |
| `DELETE /api/modsecurity/configs/{hostname}` | Modsecurity | DirectAdminController |
| `GET /api/clamav` | ClamAV | DirectAdminController |
| `GET /api/modsecurity-audit-log/entry` | Modsecurity | DirectAdminController |
| `GET /api/modsecurity-audit-log/summary` | Modsecurity | DirectAdminController |
| `GET /api/modsecurity/all-configs` | Modsecurity | DirectAdminController |
| `GET /api/modsecurity/configs/{hostname}` | Modsecurity | DirectAdminController |
| `GET /api/modsecurity/global-config` | Modsecurity | DirectAdminController |
| `GET /api/modsecurity/user-configs` | Modsecurity | DirectAdminController |
| `GET /api/security-txt/status` | security.txt | DirectAdminController |
| `POST /api/clamav` | ClamAV | DirectAdminController |
| `PUT /api/modsecurity/configs/{hostname}` | Modsecurity | DirectAdminController |
| `PUT /api/modsecurity/global-config` | Modsecurity | DirectAdminController |

### ServerController (2) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/admin-usage` | Admin | DirectAdminController |
| `POST /api/restart` | Misc | DirectAdminController |

### ServerSettingsController (16) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/server-settings/db-config` | Server settings | DirectAdminController |
| `GET /api/server-settings/directadmin-conf/active` | Server settings | DirectAdminController |
| `GET /api/server-settings/directadmin-conf/default` | Server settings | DirectAdminController |
| `GET /api/server-settings/directadmin-conf/local` | Server settings | DirectAdminController |
| `GET /api/server-settings/email/config` | Server settings | DirectAdminController |
| `GET /api/server-settings/email/outbound-filter` | Server settings | DirectAdminController |
| `GET /api/server-settings/timezone/current` | Server settings | DirectAdminController |
| `GET /api/server-settings/timezone/list` | Server settings | DirectAdminController |
| `PATCH /api/server-settings/directadmin-conf/local` | Server settings | DirectAdminController |
| `POST /api/server-settings/change-hostname` | Server settings | DirectAdminController |
| `POST /api/server-settings/db-config-test` | Server settings | DirectAdminController |
| `POST /api/server-settings/timezone/set` | Server settings | DirectAdminController |
| `PUT /api/server-settings/db-config` | Server settings | DirectAdminController |
| `PUT /api/server-settings/directadmin-conf/local` | Server settings | DirectAdminController |
| `PUT /api/server-settings/email/config` | Server settings | DirectAdminController |
| `PUT /api/server-settings/email/outbound-filter` | Server settings | DirectAdminController |

### ServiceController (8) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/system-services/list` | system services | DirectAdminController |
| `GET /api/system-services/service/{service}` | system services | DirectAdminController |
| `GET /api/system-services/service/{service}/log` | system services | DirectAdminController |
| `POST /api/system-services-actions/service/{service}/reload` | system services | DirectAdminController |
| `POST /api/system-services-actions/service/{service}/restart` | system services | DirectAdminController |
| `POST /api/system-services-actions/service/{service}/start` | system services | DirectAdminController |
| `POST /api/system-services-actions/service/{service}/stop` | system services | DirectAdminController |
| `PUT /api/system-services-actions/service/{service}/watchdog` | system services | DirectAdminController |

### SessionController (11) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/session` | Sessions | DirectAdminController |
| `GET /api/session/acme-data` | Sessions | DirectAdminController |
| `GET /api/session/login-as/user-list` | User search | DirectAdminController |
| `GET /api/session/reseller-config` | Sessions | DirectAdminController |
| `GET /api/session/state` | Sessions | DirectAdminController |
| `GET /api/session/user-config` | Sessions | DirectAdminController |
| `GET /api/session/user-usage` | Sessions | DirectAdminController |
| `GET /api/sessions` | Sessions | DirectAdminController |
| `POST /api/session/switch-active-domain` | Sessions | DirectAdminController |
| `POST /api/sessions/destroy-all-other` | Sessions | DirectAdminController |
| `POST /api/sessions/destroy/{public_id}` | Sessions | DirectAdminController |

### SkinController (40) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/skin-customization/{skin}/images` | Skin customization | DirectAdminController |
| `DELETE /api/skin-customization/{skin}/images/favicon` | Skin customization | DirectAdminController |
| `DELETE /api/skin-customization/{skin}/images/logo` | Skin customization | DirectAdminController |
| `DELETE /api/skin-customization/{skin}/images/logo2` | Skin customization | DirectAdminController |
| `DELETE /api/skin-customization/{skin}/images/symbol` | Skin customization | DirectAdminController |
| `DELETE /api/skin-customization/{skin}/images/symbol2` | Skin customization | DirectAdminController |
| `DELETE /api/skin-customization/{skin}/local` | Skin customization | DirectAdminController |
| `DELETE /api/skin-customization/{skin}/local/{filename}` | Skin customization | DirectAdminController |
| `DELETE /api/skin-customization/{skin}/menu` | Skin customization | DirectAdminController |
| `DELETE /api/skin-global-options/{skin}` | Skin options | DirectAdminController |
| `DELETE /api/skin-options/{skin}` | Skin options | DirectAdminController |
| `GET /api/session/skin-customization/{skin}` | Skin customization | DirectAdminController |
| `GET /api/session/skin-customization/{skin}/{filename}` | Skin customization | DirectAdminController |
| `GET /api/session/skin-customization/{skin}/images/favicon` | Skin customization | DirectAdminController |
| `GET /api/session/skin-customization/{skin}/images/logo` | Skin customization | DirectAdminController |
| `GET /api/session/skin-customization/{skin}/images/logo2` | Skin customization | DirectAdminController |
| `GET /api/session/skin-customization/{skin}/images/symbol` | Skin customization | DirectAdminController |
| `GET /api/session/skin-customization/{skin}/images/symbol2` | Skin customization | DirectAdminController |
| `GET /api/session/skin-customization/{skin}/menu` | Skin customization | DirectAdminController |
| `GET /api/skin-customization/{skin}/creator` | Skin customization | DirectAdminController |
| `GET /api/skin-customization/{skin}/creator/{filename}` | Skin customization | DirectAdminController |
| `GET /api/skin-customization/{skin}/local` | Skin customization | DirectAdminController |
| `GET /api/skin-customization/{skin}/local/{filename}` | Skin customization | DirectAdminController |
| `GET /api/skin-customization/{skin}/menu` | Skin customization | DirectAdminController |
| `GET /api/skin-customization/{skin}/metadata` | Skin customization | DirectAdminController |
| `GET /api/skin-global-options/{skin}` | Skin options | DirectAdminController |
| `GET /api/skin-options/{skin}` | Skin options | DirectAdminController |
| `GET /api/skin-translation/{skin}/{lang}` | Skin translations | DirectAdminController |
| `PATCH /api/skin-global-options/{skin}` | Skin options | DirectAdminController |
| `PATCH /api/skin-options/{skin}` | Skin options | DirectAdminController |
| `POST /api/skin-customization/{skin}/images/favicon` | Skin customization | DirectAdminController |
| `POST /api/skin-customization/{skin}/images/logo` | Skin customization | DirectAdminController |
| `POST /api/skin-customization/{skin}/images/logo2` | Skin customization | DirectAdminController |
| `POST /api/skin-customization/{skin}/images/symbol` | Skin customization | DirectAdminController |
| `POST /api/skin-customization/{skin}/images/symbol2` | Skin customization | DirectAdminController |
| `POST /api/skin-customization/{skin}/local` | Skin customization | DirectAdminController |
| `PUT /api/skin-customization/{skin}/local/{filename}` | Skin customization | DirectAdminController |
| `PUT /api/skin-customization/{skin}/menu` | Skin customization | DirectAdminController |
| `PUT /api/skin-global-options/{skin}` | Skin options | DirectAdminController |
| `PUT /api/skin-options/{skin}` | Skin options | DirectAdminController |

### SystemInfoController (7) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/info` | Info | DirectAdminController |
| `GET /api/system-info/cpu` | Info | DirectAdminController |
| `GET /api/system-info/fs` | Info | DirectAdminController |
| `GET /api/system-info/load` | Info | DirectAdminController |
| `GET /api/system-info/memory` | Info | DirectAdminController |
| `GET /api/system-info/services` | Info | DirectAdminController |
| `GET /api/system-info/uptime` | Info | DirectAdminController |

### SystemUpdateController (6) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/system-packages/history` | System Update | DirectAdminController |
| `GET /api/system-packages/history/{id}/log` | System Update | DirectAdminController |
| `GET /api/system-packages/history/{id}/sse` | System Update | DirectAdminController |
| `GET /api/system-packages/updates` | System Update | DirectAdminController |
| `POST /api/system-packages/update-run` | System Update | DirectAdminController |
| `POST /api/system-packages/update-test` | System Update | DirectAdminController |

### TerminalController (1) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/terminal` | Xterm | DirectAdminController |

### TicketController (2) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/ticket-requests` | Tickets | DirectAdminController |
| `GET /api/tickets` | Tickets | DirectAdminController |

### TlsController (21) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/domain-tls/domains/{domain}/certs/{id}` | TLS | DomainController |
| `GET /api/domain-tls/domains` | TLS | DomainController |
| `GET /api/domain-tls/domains/{domain}/acme-config` | TLS | DomainController |
| `GET /api/domain-tls/domains/{domain}/acme-logs` | TLS | DomainController |
| `GET /api/domain-tls/domains/{domain}/acme-logs/{log_id}` | TLS | DomainController |
| `GET /api/domain-tls/domains/{domain}/certs` | TLS | DomainController |
| `GET /api/domain-tls/domains/{domain}/certs/{id}/files` | TLS | DomainController |
| `GET /api/server-tls/acme-config` | TLS | DirectAdminController |
| `GET /api/server-tls/certificate` | TLS | DirectAdminController |
| `GET /api/server-tls/files` | TLS | DirectAdminController |
| `GET /api/server-tls/status` | TLS | DirectAdminController |
| `POST /api/domain-tls/domains/{domain}/certs/{id}/create-csr` | TLS | DomainController |
| `POST /api/domain-tls/domains/{domain}/certs/{id}/install-self-signed` | TLS | DomainController |
| `POST /api/domain-tls/domains/{domain}/provision-certs` | TLS | DomainController |
| `POST /api/domain-tls/domains/{domain}/provision-certs-dry-run` | TLS | DomainController |
| `POST /api/server-tls/enable` | TLS | DirectAdminController |
| `POST /api/server-tls/obtain` | TLS | DirectAdminController |
| `PUT /api/domain-tls/domains/{domain}/acme-config` | TLS | DomainController |
| `PUT /api/domain-tls/domains/{domain}/certs/{id}/files` | TLS | DomainController |
| `PUT /api/server-tls/acme-config` | TLS | DirectAdminController |
| `PUT /api/server-tls/files` | TLS | DirectAdminController |

### UserController (5)

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/login-history` | Users | DirectAdminController |
| `GET /api/users/{username}/config` | Users | UserController |
| `GET /api/users/{username}/login-history` | Users | UserController |
| `GET /api/users/{username}/usage` | Users | UserController |
| `GET /api/users/list` | Users | UserController |

### VersionController (3) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/version` | Versioning | DirectAdminController |
| `PATCH /api/version` | Versioning | DirectAdminController |
| `POST /api/version/update` | Versioning | DirectAdminController |

### WebProtectionController (7) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/web-protect/dirs/{id}` | Web Protect | DirectAdminController |
| `GET /api/web-protect/dirs/{id}` | Web Protect | DirectAdminController |
| `GET /api/web-protect/list` | Web Protect | DirectAdminController |
| `POST /api/web-protect/add-dir` | Web Protect | DirectAdminController |
| `POST /api/web-protect/dirs/{id}/delete-users` | Web Protect | DirectAdminController |
| `POST /api/web-protect/dirs/{id}/update-realm` | Web Protect | DirectAdminController |
| `POST /api/web-protect/dirs/{id}/update-user` | Web Protect | DirectAdminController |

### WidgetController (1) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `GET /api/widgets/list` | Widgets | DirectAdminController |

### WordPressController (13) — ontbreekt

| Methode + exact pad | Officiële tag | Huidig |
|---|---|---|
| `DELETE /api/wordpress/locations/{location_id}` | WordPress | DirectAdminController |
| `GET /api/wordpress/locations` | WordPress | DirectAdminController |
| `GET /api/wordpress/locations/{location_id}/config` | WordPress | DirectAdminController |
| `GET /api/wordpress/locations/{location_id}/options` | WordPress | DirectAdminController |
| `GET /api/wordpress/locations/{location_id}/users` | WordPress | DirectAdminController |
| `GET /api/wordpress/locations/{location_id}/wordpress` | WordPress | DirectAdminController |
| `PATCH /api/wordpress/locations/{location_id}/options` | WordPress | DirectAdminController |
| `POST /api/wordpress/install` | WordPress | DirectAdminController |
| `POST /api/wordpress/install-quick` | WordPress | DirectAdminController |
| `POST /api/wordpress/locations/{location_id}/users/{user_id}/change-password` | WordPress | DirectAdminController |
| `POST /api/wordpress/locations/{location_id}/users/{user_id}/sso-login` | WordPress | DirectAdminController |
| `PUT /api/wordpress/locations/{location_id}/config` | WordPress | DirectAdminController |
| `PUT /api/wordpress/locations/{location_id}/config/auto-update` | WordPress | DirectAdminController |

## Afbakening

- De officiële Swagger is de **nieuwe** `/api/*` API; legacy CMD-endpoints zijn geen afwezige operaties maar een apart compatibiliteitsspoor.
- De indeling is logisch en contractneutraal, geen claim dat al deze features ondersteund worden door de achterliggende hostingbackend. Niet-ondersteunde operaties moeten de backenduitkomst retourneren.
- Buiten scope van deze inventarisatie: details van alle legacy CMD-acties en een volledige conformance van de request/response DTO's; die verdienen aparte mappings en tests.
