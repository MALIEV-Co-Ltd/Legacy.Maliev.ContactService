# Legacy.Maliev.ContactService

This repository is the public, sanitized legacy compatibility service for the
website contact form that used to live in `R:\maliev-web` as `Maliev.MessageService`.

## Non-negotiable boundaries

- Keep the original `maliev-web` repository private.
- Do not copy monorepo Git history or legacy configuration files.
- Do not commit connection strings, service-account material, JWT keys, SMTP
  credentials, or generated secret-audit evidence.
- Preserve the legacy `/Messages` route and JSON field names until consumers are
  explicitly migrated.
- The database source of truth remains legacy until the PostgreSQL parity and
  cutover gates in `maliev-web` pass.

## Validation

Run from this repository root:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format Legacy.Maliev.ContactService.slnx --verify-no-changes --no-restore
dotnet list package --vulnerable --include-transitive
gitleaks git . --redact=100 --exit-code 0 --no-banner --no-color
```

## Service conventions

- Runtime: .NET 10.
- OpenAPI UI: Scalar through `Maliev.Aspire.ServiceDefaults`; no Swashbuckle.
- Logging: built-in `ILogger<T>` only; do not reintroduce `Maliev.LoggerService`.
- Cache: Redis keys must use the `legacy:contact:` prefix.
- Auth: protected write/read-by-id endpoints use granular
  `legacy-contact.messages.*` permissions.
- Data model: `ContactRequest` maps to the legacy `Message` table and `ID`
  column; do not alter the source SQL Server schema from this service.
