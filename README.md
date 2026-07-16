# Legacy.Maliev.ContactService

[![PR validation](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.ContactService/actions/workflows/pr-validation.yml/badge.svg)](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.ContactService/actions/workflows/pr-validation.yml)
[![Main CI](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.ContactService/actions/workflows/ci-main.yml/badge.svg)](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.ContactService/actions/workflows/ci-main.yml)

Temporary .NET 10 compatibility service extracted from `maliev-web`. It preserves
the legacy integer-key `Message` schema and `/Messages` JSON contract while the
new `Maliev.ContactService` is developed independently.

## Architecture

The service uses clean dependency direction: `Api` calls `Application`, domain rules live in
`Domain`, and PostgreSQL/Redis adapters live in `Data`. It depends on the public MALIEV Aspire
and messaging-contract source repositories during CI and image builds, so no private package
credentials are required.

## API endpoints

| Purpose | Method | Route | Access |
| --- | --- | --- | --- |
| Legacy contact-message list | `GET` | `/Messages` | `legacy-contact.messages.read` |
| Legacy contact-message lookup | `GET` | `/Messages/{messageId}` | `legacy-contact.messages.read` |
| Legacy contact-message create | `POST` | `/Messages` | `legacy-contact.messages.create` |
| Legacy contact-message update | `PUT` | `/Messages/{messageId}` | `legacy-contact.messages.update` |
| Legacy contact-message delete | `DELETE` | `/Messages/{messageId}` | `legacy-contact.messages.delete` |
| Scalar UI | `GET` | `/messages/scalar` | Anonymous |

## Runtime boundaries

- Legacy route: `/Messages`
- Scalar: `/messages/scalar`
- PostgreSQL database: `Message` on `legacy-postgres-main`
- Redis key prefix: `legacy:contact:`
- Public contact-message listing remains anonymous; protected operations require granular
  `legacy-contact.messages.*` permissions.

This service does not modify the SQL Server source. PostgreSQL promotion requires
the artifact-backed parity and cutover gates tracked in `MALIEV-Co-Ltd/maliev-web`.

Deployment is intentionally validation-only until a dedicated
`legacy-maliev-contact` Workload Identity Federation provider and
`maliev-gitops/3-apps/_legacy-contact-service` manifest path exist. The existing
`maliev-contact-service` GitOps path belongs to the new implementation and must
not be overwritten by this legacy compatibility service.

## Validate

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format Legacy.Maliev.ContactService.slnx --verify-no-changes --no-restore
dotnet list package --vulnerable --include-transitive
```
