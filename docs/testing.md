# Verification and test coverage

Current CI is defined in .github/workflows/dotnet.yml, helm.yml, install-templates.yml and pin-directadmin-swagger.yml. Source review and build/test success are distinct; record the actual result for the commit under review.

| Suite | Purpose |
| --- | --- |
| HostingPanel.ContractTests | Scope, navigation, Swagger route coverage, backend mutation semantics, DNS forwarding, database inventory, tickets |
| HostingPanel.ApiIntegrationTests | Fake-upstream HTTP forwarding and application integration |
| tests/browser-smoke.py | Real browser interaction regressions including account creation |
| tests/required-login-smoke.py | Required login and authenticated browser behavior |
| Helm and install template workflows | Render/deployment template checks |
| Swagger pin workflow | Explicit upstream specification synchronization |

Build/test commands from repository root:

```sh
dotnet build src/HostingPanel.Web -c Release
dotnet run --project tests/HostingPanel.ContractTests -c Release
dotnet run --project tests/HostingPanel.ApiIntegrationTests -c Release
```

Use CI's exact environment and setup for browser tests. Add negative cases for scope, denied writes and unsupported capabilities; include partial inventories and ambiguous upstream results when applicable. Kubernetes tests should separate persisted intent, external reconciliation and observed readiness. Fake transports and isolated synthetic fixtures are not production validation. Do not run destructive tests against real customer resources. Use the current chart and pinned runtime images for Apache/PHP/NFS, DNS delegation, cert-manager and Gateway acceptance testing.
