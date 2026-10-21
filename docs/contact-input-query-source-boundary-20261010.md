# Contact message input and query boundary

Producer baseline `4951254e7368b0bec5e80419b96832edd998e072`. Original source checkpoint
`135e526d0dab85c415b3afdcefd7b70fe2c82e2f`; full historical path identities and parents are retained
in the off-repository source evidence packet. All source obligations remain OPEN.

Original direct CLR create/update null inputs return 400 with `Message is required`
before service calls. Four frozen direct-controller cases establish this parity
and retain the versioned nonpositive-ID precedence. Nonnullable HTTP parameters
and automatic MVC JSON-null validation remain unchanged.

Twenty frozen normal HTTP source regressions cover JSON-null POST/PUT, each of six
search fields with twenty exact matches and eighty-one distractors, and unique ID
substring search on both routes. Full stored fields and PostgreSQL concurrency
versions are compared before/after each operation.

Twelve additional normal HTTP regressions cover valid Int32 page-offset overflow
and ordinary exhausted, unbounded-size and nonpositive-normalization controls on
both routes. The overflow is an inherited correctness defect in the original
shared helper as well as the current repository; repairing it is not literal
parity with the original arithmetic bug. Query defaults, six page JSON keys,
search, sort, route authorization, physical fields and versions must be retained.

This tests-only draft adds 36 cases while preserving every original test and
production file. Only runtime assertion failures after a strict Release build
with zero warnings/errors can establish behavioral RED. Setup/compiler/dependency
or timeout failures cannot. Review null and overflow failure evidence separately
before the proposed two-production-file repair.

Local .NET/container execution is NOT RUN: no finite coordinated native custody.
Fresh process/memory observations authorize source helpers only. Use the existing
ordinary hosted PR-validation workflow under the owner-approved migration draft
timing exception; no checks or coverage thresholds are waived. Required gates:
strict Release, focused/full original TRX roster and execution-definition joins,
raw per-assembly coverage, formatting, package audit, secret/JWT/static/scaffold
checks, exact proposed-head protection checks and exact protected-main CI.

Production environment uses normal RS256 permission middleware and disposable
PostgreSQL with memory cache in these focused fixtures. Synthetic token transport
does not prove deployed IAM or consumer acceptance; real Redis, original SQL-host
execution and genuine consumer traffic are NOT RUN. No deployment/data work,
shared tracking closure, Defaults pin adoption, or other writer changes.

Original business paths retain historical SHA `5fac706a7983a6d359b39acbd670e6800afe020e` (parents: none):

- `Maliev.MessageService.Api/Controllers/MessagesController.cs`
- `Maliev.MessageService.Tests/Messages/CreateMessageAsync_UnitTest.cs`
- `Maliev.MessageService.Tests/Messages/GetPaginatedAsync_UnitTest.cs`
- `Maliev.MessageService.Tests/Messages/UpdateMessageAsync_UnitTest.cs`

Shared original pagination helper: `Maliev.Entities/ViewModels/PaginatedListWebApi.cs`; arithmetic correctness is separately scoped.

## Reviewed producer repair

Actual tests-only baseline `e5a2e4aea3ba5e9a309a2155d523a61f7af4a8cf` and its independently reviewed raw hosted proof precede this repair. Direct CLR null inputs now return the original exact 400 string before service use, retaining the versioned identifier guard first. Pagination computes its offset as Int64, returns the existing exhausted-page metadata before querying, and casts only after the filtered Int32 count proves the offset fits. Every new and original test remains unchanged. Exact repair-head hosted validation is pending. Source SQL-host, genuine deployed IAM/consumer acceptance and whole source closure remain unqualified.
