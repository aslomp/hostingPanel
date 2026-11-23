# DirectAdmin Swagger contract

The authoritative **complete** Swagger 2.0 document is shipped by **your own DirectAdmin server** at `/static/swagger.json`. The snapshot is intentionally not fabricated or taken from a third-party installation.

To download the exact version used by your deployment:

```bash
DA_ORIGIN=https://your-directadmin-server:2222 ./scripts/sync-directadmin-swagger.sh
```

That writes `spec/directadmin.swagger.json`. Commit the file **only after reviewing it** for internal hostnames, examples and other sensitive instance-specific metadata. Do not include login keys.

The HostingPanel API also mirrors the complete specification live at `/openapi/directadmin.json`, cached for five minutes. This is for an internally deployed control plane, not a publicly exposed anonymous discovery service.

Each modern /api/* operation is forwarded to the fixed DirectAdmin upstream under the requesting user's DirectAdmin credentials. **HostingPanel does not yet provide a native implementation of those operations.** Once a Kubernetes-native provider implements a route, regression tests must demonstrate its DirectAdmin-equivalent behavior.
