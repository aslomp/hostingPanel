# HostingPanel

[![License: AGPL-3.0-only](https://img.shields.io/badge/License-AGPL--3.0--only-blue.svg)](LICENSE)

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/aslomp)

A standalone, responsive hosting management interface inspired by the DirectAdmin Evolution dashboard and navigation structure.

## Installation

HostingPanel includes an ASP.NET Core API and a Blazor Server UI. The DirectAdmin provider is the default; the Kubernetes/SQLite provider is experimental and is not yet a production-ready hosting controller.

- [Installation guide — Docker Compose, Helm, native .NET](docs/installation.md)
- [Docker Compose configuration](deploy/compose/compose.yaml)
- [Helm configuration](deploy/helm/hostingpanel/values.yaml)
- [Kubernetes backend architecture and limitations](docs/kubernetes-sqlite-backend.md)

The original static UI prototype remains available via `index.html` and GitHub Pages. The following historical prototype notes refer to that static demo, not the .NET application.

## Page coverage

All 49 visible navigation items in the inspected DirectAdmin Evolution administrator and user menus have individual UI definitions in `pages.js`. Each page displays relevant forms, tables, or tabs according to its purpose. This is a **functional UI prototype**, not a pixel-perfect reconstruction of every internal DirectAdmin page: only the dashboard navigation was directly inspected, so detailed fields were reconstructed based on their intended functionality.

Page styling is defined in `pages.css`. Both `pages.js` and `pages.css` are loaded from `index.html`.

## Refined workflows

The four priority areas have dedicated workflows rather than relying exclusively on generic forms:

- **User management:** user lists, account details, roles, hosting limits, security, account creation, and packages.
- **Domain management:** domain lists, domain creation, document roots, PHP, HTTPS/SSL, and subdomains.
- **DNS:** zones, records (A, AAAA, CNAME, MX, TXT, and NS), record creation and editing, SOA, and nameservers.
- **Server management:** IP management, HTTPD configuration, multi-server management, services, and configuration tabs for PHP, TLS, and other server settings.

All records displayed in the static prototype are synthetic. Forms and service actions intentionally have **no** persistence, backend connection, or production impact. These are functional UI reconstructions, not pixel-perfect copies of the original DirectAdmin pages.

## Features of the static prototype

- Dutch-language dashboard, administrator navigation, and user view
- Administrator sections: Account Manager, Server Manager, Admin Tools, System Info & Files, Extra Features, and Support
- Page search using Ctrl/Cmd + K
- List and filter views for users, domains, DNS, email, databases, file management, and services
- Widgets displaying sample statistics, usage, and activity
- Mobile navigation and keyboard controls
- Interactive demo modal for creating entries

## Prototype status

**Frontend prototype.** All statistics and records in the static demo are sample data. Individual page forms have no persistence; switching roles changes only the UI presentation and does **not** constitute authorization. The static prototype does not send requests to a DirectAdmin server.

## Architecture towards production

For integration with a dedicated hosting controller, the original prototype roadmap outlined the following steps:

1. Replace `sampleRows` and static dashboard statistics with authorized API calls.
2. Enforce sessions, authentication, and RBAC in the backend. Never trust the role displayed in the frontend.
3. Define separate API contracts for users, domains, DNS, web hosting, certificates, services, and backups.
4. Process configuration changes through a controlled reconciliation loop rather than executing shell commands directly from the UI.
5. Add validation, audit logs, CSRF protection where applicable, and tests before enabling live write operations.

## Origins

The labels and information architecture are based on the DirectAdmin Evolution dashboard inspected on November 23, 2026. The original implementation uses its own HTML, CSS, and JavaScript without DirectAdmin assets or external dependencies.

## License and support

HostingPanel is licensed under the GNU Affero General Public License, version 3 only (`AGPL-3.0-only`). See [LICENSE](LICENSE). This applies to original HostingPanel code unless explicitly stated otherwise; third-party components retain their own licenses.

The project is provided without commercial support, SLAs, or guaranteed maintenance. Donations are entirely voluntary and do not create support obligations.
