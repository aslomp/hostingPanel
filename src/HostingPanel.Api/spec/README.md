# Static DirectAdmin Swagger

`directadmin.swagger.json` is a **pinned, complete Swagger 2.0 snapshot** from the public DirectAdmin demo. It is not fetched at runtime.

Run GitHub Actions workflow **Pin DirectAdmin OpenAPI snapshot** once to add or refresh the JSON file, or fetch it locally:

```bash
DA_ORIGIN=https://demo.directadmin.com:2222 ./scripts/sync-directadmin-swagger.sh src/HostingPanel.Api/spec/directadmin.swagger.json
```

Review and commit the result. HostingPanel.Api serves this exact file at `/openapi/directadmin.json`. It copies to build and publish output.

Do not infer that your older DirectAdmin server supports every endpoint in a newer snapshot. The static file describes a **reference contract**. Running endpoints remain proxied to the configured server, and absent routes must be handled as unsupported.
