# P0 storage and runtime acceptance

## Static review
- The storage provisioner checks path components, rejects symbolic links at inspection time, checks the export root mode, and preserves existing ownership. Existing child directory ownership, ACLs and effective access still require validation.
- The shared Apache service runs non-root with dropped capabilities and a read-only site mount. Read-only is not a confidentiality boundary.
- The ConfigMap reader has namespace-wide list access; the label selector is not an RBAC boundary. Keep hosting resources in a dedicated namespace and do not store confidential data in ConfigMaps.
- Shared PHP is a trusted-application mode, not an isolation guarantee. Keep the opt-in disabled for untrusted hosting. Do not weaken storage or runtime protections to resolve compatibility problems.

## Acceptance evidence required
Record the image digests, rendered chart, loaded Apache modules, configuration test output, public listener and health listener smoke-test results, and deployment revision. Use only synthetic accounts and disposable test data in an isolated test environment.

Storage acceptance must establish correct ownership, effective access for intended identities, account identity uniqueness, and absence of unintended cross-account access. Review ACLs, inherited permissions, NFS export policy and concurrent provisioning semantics with the storage administrator.

Runtime acceptance must establish intended HTTP and PHP functionality, tenant confidentiality, resource limits and least-privilege service accounts. A successful HTTP request is not evidence of isolation. Record pass/fail and unresolved risks separately.

No production approval until these gates have been independently verified.

## Recorded CI evidence

The baseline smoke workflow passed on commit 7977f9fa78ed79c1f0e8b100f4368d03494a9fbd, including real httpd syntax validation, event MPM and listener smoke tests. Helm validation and installation templates also passed. The .NET compilation succeeded but the navigation contract test failed on duplicate XML resource keys in the merge result. Both equivalent resource entries were deduplicated in subsequent commits 25dcda7 and 8b2ca68; a fresh successful run is still required. These results do not certify actual NFS permissions, PHP execution or production isolation.

Apache's NFS mount remains read-only by design. PHP and future SFTP/file-management services require account-scoped write permissions to the same storage. Do not change Apache to a writable mount to support application uploads.
