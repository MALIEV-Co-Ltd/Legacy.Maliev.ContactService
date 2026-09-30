# Contact documentation acceptance

Bounded issue #29, parent #27. Baseline is the exact tree accepted in PR #28 at main `ed1d3b98859bc7c9108c2016d2600c0113188b73`. Individual source owner `72eb9f1949176392141951d35e6e06f7c30af4c2` contains maintained XML/build inputs; this documentation subset does not retire its logging/build/operational owners or the entire Contact cohort.

Actual normal-host HTTP consumption found two defects: no bearer security scheme and no maintained controller summaries. Development/Staging exposure, title, Scalar routing and Production suppression already worked. An initial broad route selector incorrectly included the intentionally public `/messages/aspire-liveness` probe. Corrected exact Contact route selection, removed the proposed registration, rebuilt successfully and reproduced both genuine failures with three passing environment controls (`root-contact-docs-exact-routes-red`). No blanket authentication was added to probes.

The service-local literal `AddOpenApi("v1", ...)` invokes .NET's compile-time XML interceptor for this assembly. Shared versioning/title registration remains; HTTP assertions prove ten actual Contact operations, original title and both aliases are retained. The narrowly scoped `Microsoft.AspNetCore.OpenApi.Generated` interceptor namespace is necessary because the package comes through a project dependency; the first build diagnostic identified the missing compiler namespace and was not counted as acceptance. No package/version or generated-file changes.

Security metadata derives from actual `Authorize`/`AllowAnonymous` endpoint metadata. This changes documentation only, not permissions, JWT validation or request handling. No source XML duplication, handwritten summary inventory, runtime XML file parser or coverage suppression.

Independent validation:

- Release build: zero warnings/errors.
- Actual HTTP documentation focus: 5 passed, zero skips.
- Full affected solution: 75 passed, zero skips; `TestResults/root-contact-docs-full/contact-docs-full.trx`.
- Whole solution `dotnet format --verify-no-changes --no-restore`: passed.
- Raw API coverage: 187/306 (61.11%), versus prior 51/271 (18.81%). Maintained generated documentation is now genuinely consumed, not excluded. Application 86/88, Data 287/311, Domain 10/10 remain unchanged. API 80% is still unmet; parent #27 remains open.
- Coverage report: `TestResults/root-contact-docs-full/1f3353cb-4c46-4769-b796-f50d3a8ee6d2/coverage.cobertura.xml`.

All five project transitive vulnerability audits passed; final staged secret scan passed (12,628 bytes); diff checks passed. Protected-head and exact-main CI are separate acceptance gates. No persistent production/local database writes, application deployment, real notification sends, Auth-provider readiness or production-derived Aspire parity is claimed.
