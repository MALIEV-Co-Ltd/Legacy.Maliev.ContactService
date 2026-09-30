# Contact Runtime Parity Implementation Plan

> **For agentic workers:** Root owns independent acceptance and protected-main integration. Initial test/design-only gates below are historical; root approved the narrow runtime repair after terminal RED on 2026-10-01. Deployment and persistent data/schema writes remain prohibited.

## Current runtime phase — 2026-10-01

Root independent validation: Release build 0 warnings/errors, focused 33/33 and
full affected solution 70/70, zero skips (`TestResults/root-contact-focus` and
`TestResults/root-contact-full`). Root inspected both aliases, exact response
serialization, tracked xmin conflict translation, caller versus provider
cancellation, source framework binding and the existing Web persistence consumer.
Bounded issue #26 does not retire the remaining 23 source-owner entries or waive
the separate raw API coverage gap. Protected-head and exact-main CI remain
required before completion.

**Superseding correction:** whitespace-search was an incorrect new-test assumption, not source HTTP parity. Source controller at `bed10c7d15e0698e0b75f1329d0f312937f5d77f` has plain `string search`, no metadata override; source project pins .NET8/MVC8.0.28. Scoped source grep found no binder/ConvertEmptyStringToNull override. [Official ASP.NET8.0.28 SimpleTypeModelBinder](https://raw.githubusercontent.com/dotnet/aspnetcore/v8.0.28/src/Mvc/Mvc.Core/src/ModelBinding/Binders/SimpleTypeModelBinder.cs), lines53–58, converts whitespace to null with default metadata. The temporary repository IsNullOrEmpty change was restored; no raw-query or metadata override was added. New tests now explicitly cover both aliases' absent/empty/whitespace unfiltered defaults. Retained `contact-runtime-focus.trx` (27 pass/2 fail) and `contact-runtime-full.trx` (64 pass/2 fail) document the erroneous assumption, not a runtime regression. Earlier literal-search claims below are historical planning, superseded here.

Current runtime repairs are legacy `totalRecords` serialization and separately classified PostgreSQL concurrency/cache correctness. Search runtime is unchanged. The Application exposes a typed, redacted concurrency exception without EF coupling; Data translates only genuine ContactRequest xmin conflicts for update/delete, preserving tracked versions. Controller maps only that exception to 409; unknown failures remain 500. Cache invalidation alone tolerates provider cancellation when the caller is not canceled; genuinely canceled callers still propagate. List/detail remain PostgreSQL authoritative. No notification, idempotency, schema, grants or provider behavior was added.

The added DELETE race, missing-row, unknown-failure and actually-canceled caller controls built with 0 warnings/0 errors. Pre-runtime focused terminal: **9 failed / 17 passed / 0 skipped**, `TestResults/contact-controls-red.trx` (two failures were the subsequently corrected whitespace assumption). Unknown update/delete persistence failures preserved PostgreSQL state and remained 500; DELETE race was 204+500 rather than required 204+409. Prior full RED was 8 failed/54 passed/0 skipped; all 40 original tests passed. The original invalidation cancellation fixture was explicitly corrected to call `Cancel()` while retaining its Throws assertion, not weakened.

Final agent candidate gates: Release **0 warnings/0 errors** (`contact-final-build.log`), focused **33/33 passed** (`contact-final-focus.trx`), relevant full solution **70/70 passed, zero skips** (`contact-final-full.trx`), full solution formatting PASS (`contact-final-format.log`), all five projects' transitive vulnerability audit reports none (`contact-runtime-vulnerabilities.log`). The superseding independent root results and protected-main gate are recorded above. No deployment or persistent writes occurred.

Secret/static gate: all nine changed/new files scanned redacted with zero findings; committed-history gitleaks also zero findings. `git diff --check` passed. Both owned dependency HEADs still equal the exact pins above. Secret audit artifacts are ignored under owned `TestResults`; no credentials were printed/copied. No live Redis/SMTP/notifications, source SQLServer or persistent data was exercised; real PostgreSQL18 fixtures were disposable synthetic tests only.

Raw coverage remains a separate unmet measurement. The historical 422/669 (63.07%) was the owned-assembly aggregate, not API coverage. Independent final coverage executed all 70 tests with zero skips: API 51/271 (18.81%), Application 86/88 (97.72%), Data 287/311 (92.28%), Domain 10/10 (100%); owned aggregate 434/680 (63.82%). Evidence: `TestResults/root-contact-coverage/7583df6d-6fd4-4168-81e4-5fb998096faf/coverage.cobertura.xml`. External private dependency packages are not included in the owned aggregate. Generated OpenAPI code remains included; no exclusions or waiver were introduced. This slice cannot close the entire Contact owner/source cohort or its 80% API gate.

**Goal:** Establish real RS256 HTTP/PostgreSQL evidence for the remaining Contact source cohort without inventing contracts or notification ownership.

**Architecture:** Exercise the unmodified production authentication/permission/controller/application/repository chain with ephemeral RSA keys and PostgreSQL18 Testcontainers. Keep literal source wire regressions distinct from post-migration concurrency and partial-effect proposals. No test authentication scheme or fabricated database/upstream success.

**Tech Stack:** .NET10, xUnit, WebApplicationFactory, Npgsql EFCore, PostgreSQL18 Alpine, current exact CI dependencies.

**Spec:** Root's Contact test/design-only brief of 2026-10-01 and this document's source matrix/boundary requirements.

## Global constraints and baseline

- Exclusive worktree `B:\maliev-legacy\.worktrees\contact-runtime-parity-20261001`, branch `codex/contact-runtime-parity-20261001`, base `14449bb228b17135632579c1c8fa867b39f1e595`.
- Canonical was clean and identical to local `origin/main`; old merged `contact-f064-failure-tracing-20260928` worktree is preserved. No owner conflict found.
- Own non-hardlinked, clean private dependency clones: Defaults `003b255f0fb0f0bce032f5b5ff15d28be0c8c391`, Contracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`.
- Original committed source mirror checkpoint `bed10c7d15e0698e0b75f1329d0f312937f5d77f` is read-only; Workflows ledger committed checkpoint `e06959be255581c8cba145899996bc434a75dfcc` is read-only.
- Initial allowed changes: NEW test and design files only. No runtime/config/routes/schema/dependency/old-test changes; no SMTP/GCS/notification/provider/PII/persistent-data writes; no GitHub/commit/push/deploy.
- Initial restore succeeded. Release solution baseline 0 warnings/0 errors; full baseline 40 passed/0 failed/0 skipped (`TestResults/contact-baseline.trx`). This is not whole-owner parity evidence.

## Actual boundary and ownership findings

Latest source `Maliev.MessageService.Api/Controllers/MessagesController.cs` remains the initial CRUD behavior from `5fac706a7983a6d359b39acbd670e6800afe020e`. The only later model delta, `72eb9f1949176392141951d35e6e06f7c30af4c2`, adds documentation, not fields. Current service deliberately renames internal types to ContactRequest while retaining `/Messages`, integer `messageId`, camel-case JSON `id`, nullable fields, server-owned creation/modification dates and `Message`/`ID` database names. Granular permissions and PostgreSQL are adopted architecture, not a claim of identical old SQLServer/JWT behavior.

Concrete parity regressions:

1. Source `Maliev.Entities/ViewModels/PaginatedListWebApi.cs` (initial SHA above) emits `totalRecords`; target `PaginatedContactRequestResponse.TotalItems` currently emits `totalItems`. Preserve internal CLR name if useful; root must review the minimal serialization repair. The v1 alias is advertised as the same legacy contract, not a separate incompatible count field.
2. **Corrected initial assumption, not a defect:** the source action's nonempty predicate does not prove HTTP whitespace semantics. Its plain `string search` parameter uses default MVC binding; absent/empty/whitespace queries become null and retain the unfiltered default. The target matches this behavior, and its repository predicate remains unchanged.

Current list reads always call PostgreSQL; `IContactRequestCache.GetAllAsync/SetAllAsync` are not used by the application. Therefore invalidation outage cannot cause stale HTTP list data today, and enabling list caching is not this slice. Mutations do invalidate after commit, so an adapter cancellation can turn a completed mutation into a failed response. This is an unknown-effect/retry concern, not license to promise idempotency or forge success. Source has no idempotency key/store, cache or notification dispatch in MessageService.

Source contact page `Maliev.Web/Pages/Contact/Index.cshtml.cs` persists via authenticated `/messages/` before analytics/confirmation delivery. Current committed Web `Legacy.Maliev.Web.Infrastructure/ContactClient.cs` sends Bearer service identity, accepts only201 JSON with positive `id`, and returns no reference on non201/transport/invalid JSON; `Pages/Contact/Index.cshtml.cs` sends notifications only after a returned persisted reference and distinguishes delivery failure. No browser token/money authority is introduced. This lane does not call real Web/NotificationService or send messages. Committed source/target Intranet searches find no ContactRequest/MessageService HTTP consumer (source package-lock `@gulpjs/messages` is unrelated); no employee UI integration can be claimed.

## Review focus

- Both legacy/versioned aliases must exercise identical real positive auth+PG paths, not route reflection alone.
- Unknown/wrong-key/expired JWT and missing permission must not persist synthetic rows.
- Null fields and overposted ID/dates must preserve server identity/timestamps and omitted-null serialization.
- Concurrent tracked modifications must preserve PostgreSQL xmin; a409 HTTP proposal is a post-migration correctness decision, not source409 parity.
- Failed post-commit invalidation must report the actual persisted outcome in diagnostics; do not infer safe retry or add an invented idempotency header.

## Task1: Literal wire/search RED plus positive production boundary controls

**Files:** Create `Legacy.Maliev.ContactService.Tests/Controllers/ContactRuntimeParityBoundaryTests.cs`; this document.

**Interfaces:** Consumes actual `Program`, `ContactRequestDbContext`, controller routes, RS256 Bearer tokens and granular `legacy-contact.messages.*` claims. Produces captured HTTP status/JSON plus fresh-context PG assertions.

- [x] Read instructions, inspect ownership, exact pins and build/full baseline.
- [ ] Add both-alias literal `totalRecords` and whitespace-search cases; expected initial RED is missing source count field/wrong result set, not auth/database setup failure.
- [ ] Add real CRUD/null/overpost/server dates, auth denial, literal wildcard search and empty/out-of-range404 controls.
- [ ] Fresh Release0W0E, focused typed tests, capture actual intended RED and GREEN counts; full suite must retain the RED until runtime approval.
- [ ] Root review minimal runtime ownership: response serialization attribute only and repository search nonempty predicate if source behavior approved. No runtime edits yet.

## Task2: Concurrency/cache/partial-effect evidence before any repair

**Files:** The new owned test file only, with test-local EF SaveChanges gate or distributed-cache failure adapter when needed.

**Interfaces:** Actual PG tracked `xmin` contexts and actual HTTP controller/app/repository; only the faulting cache boundary is controlled. No authentication replacement, fake persistence, live Redis/provider success or external sends.

- [ ] Prove actual optimistic concurrency preservation; simultaneous HTTP writes should not silently overwrite. Document current500 versus proposed409 explicitly before runtime gate.
- [ ] Exercise post-commit invalidation error/cancellation and inspect persisted row in a fresh context; distinguish best-effort error swallowed today from propagated cancellation.
- [ ] Demonstrate list reads do not depend on cache (a failing cache read adapter cannot become invented positive upstream proof).
- [ ] Keep absence of idempotency/notification atomicity as residual design, not a source parity defect that authorizes new schema/wire.

## Individual pending source-owner matrix

The actual ledger has25 Contact records:23 pending (21 nonmerge +2 merge rollups) and2 independently accepted nonmerge records below. Every row retains Contact ownership; shared infrastructure/security owners remain separate. These are proposed bounded dispositions, NOT ledger mutations or blanket completion.

| Full source SHA | Contact scope / bounded evidence and exclusion |
| --- | --- |
| `5fac706a7983a6d359b39acbd670e6800afe020e` | Initial Messages CRUD/model/sort/tests; runtime wire/search evidence required. Old SQLServer/config/history/private resources excluded by sanitized PG architecture, not copied. Docker/deployment/provider readiness separate. |
| `3a393215d883fa35e1461f69c876bf2ead7ce36e` | Split deployment/service ingress; no runtime CRUD delta. Existing publication architecture needs owner evidence; no deployment execution here. |
| `0822636e5e2d46e4db20a79d27037aab426d85aa` | Deployment resource limits/node selection; infrastructure validation excluded. |
| `3a104503328cc3c0d57ff9ae2deafba06d1e46d5` | Deployment node-selection removal; infrastructure validation excluded. |
| `72eb9f1949176392141951d35e6e06f7c30af4c2` | Message fields unchanged, XML docs + logging setup/API build inputs. Current field/PG tests relevant; no legacy NLog or generated XML restoration. |
| `5458b7ddc81a15d72087fa69fb4cfcc27ae75747` | Deployment-only frontend removal/resource configuration; no Contact runtime delta. |
| `53f4baf373ef04a3ed5ab5c1ef39bd61404c5258` | Deployment requests/limits; excludes paid infrastructure and rollout. |
| `93f9f99522fbe6c128acb5d049f2b448e07dba95` | Deployment resource tuning; current policy owner separate. |
| `90f34b389c298d1ce85abe2ae7ac92877dbbf7af` | Project/build-input deltas; net10 exact dependency build evidence, no old framework/library restoration. |
| `00ec830615c15b5e4e227046712247b11df0100f` | Hardened deployment script; publication workflow behavior not provider/deploy execution. |
| `2aab25eb07894fc0267b03b85bad96490219d2fa` | External design-time DB credentials; sanitized factory/config boundaries, never reproduce resource credentials or write source SQLServer. |
| `7d6f46f53cbab853ca9c25e385af067cfff6238a` | Runtime/scaffold secret externalization; no legacy config copying, provider credential validation separate. |
| `eb8ed86672bd9afccc6560b547b734d0fcd7363b` | Merge rollup of secret-remediation cohort; individual above/accepted JWT evidence, not duplicate feature or whole-SHA closure. |
| `a649db99a27bda65274fe1b18866ae226d3c69cf` | Merge rollup of same security paths; retain per-path ownership/exclusions. |
| `03eaff1194c3ae2a54ceefeae31deffaff90436f` | Docker-ignore build context; static packaging evidence only, no image publish. |
| `72163e9ae11f39f6579423841a2e20529b986fab` | Deployment exit propagation; existing publication automation evidence separate from runtime tests. |
| `f8921b1b1d5846eeaff999af10b640011655d1d4` | Throwaway manifest rendering; no source manifest mutation/deploy. |
| `143f53ba0a1c81c78d252864ca131d42ed79dc1b` | Runtime secret references in deployment; current sanitized environment architecture, no secret value readback. |
| `9e51e6c5da29de8e617b65b59d46882cde6d3b64` | Retire LoggerService/native logging; current ILogger/standard middleware, no remote logger or NLog restoration. |
| `c660de68b633618cb0c857a287020f5ed9c42683` | Cluster capacity/rollout tuning; no infrastructure mutation or production health claim. |
| `a7d0a4517ef1cfef638763cb1092088a5932fa2f` | Single-replica rollout availability; actual deployment acceptance separate. |
| `03dc9a1271c16e6535934445e9dd6e3f30e8fffe` | Generated XML excluded from source; current build/source cleanliness, no generated history/config copy. |
| `5ac7d045c51194edd9e64d8564f1b726b001be34` | Application-local logging/build/package; current native logging reviewed with accepted failure-tracing controls, no old logger SDK. |

Accepted rows retained separately, not re-opened or silently expanded:

- `cbac7d7155da2208c77d56103b6a2cb19196fc83`: ledger migrated Contact#24/PR25, target `14449bb228b17135632579c1c8fa867b39f1e595`, CI36433615000. New tests strengthen actual HTTP/PG evidence, not a replacement auth implementation.
- `f0640fe0719b2eb6becda378bff08153d955be07`: ledger migrated PR21 target `7c61b4c2f49a634a5d455f6965f42d0a61c165c2`, CI36419162082; failure tracing remains unmodified. Shared Notification#32 ownership does not authorize sends here.

## Exact validation commands / execution ledger

```powershell
dotnet restore Legacy.Maliev.ContactService.slnx -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/contact-runtime-parity-20261001/.dependencies -p:UseLocalMalievDependencies=true
dotnet build Legacy.Maliev.ContactService.slnx --configuration Release --no-restore -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/contact-runtime-parity-20261001/.dependencies -p:UseLocalMalievDependencies=true -v:minimal
dotnet test Legacy.Maliev.ContactService.slnx --configuration Release --no-build --no-restore --logger 'trx;LogFileName=contact-baseline.trx' --results-directory TestResults -v:minimal
```

Ruling: root explicitly owns test/design review and limits writes to newtests/docs; use this owned document as the phase ledger rather than creating skill-default extra files or dispatching reviewer agents. Keep runtime RED unresolved until the root gate. No commit applies during this phase.
