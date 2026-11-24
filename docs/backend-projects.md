# Backend project boundaries

The canonical project and dependency map is [architecture](architecture.md). HostingPanel.Web is the single host; independent capabilities are registered by each provider. Common persistence is in HostingPanel.Persistence. Do not reintroduce the removed universal backend contract or split the host into separate API and UI projects.
