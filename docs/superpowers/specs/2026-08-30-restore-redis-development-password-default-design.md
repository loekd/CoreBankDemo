# Restore Redis development password default

> **Status:** Implemented
> **Kind:** story spec
> **Original date:** 2026-08-30
> **Migrated from:** `docs/bmad/implementation-artifacts/spec-restore-redis-development-password-default.md` on 2026-09-10
> **Related:** PR #2


## Intent

**Problem:** The regular AppHost declared `redis-password` without a default, leaving the parameter in `ValueMissing` and preventing Aspire from creating the Redis container.

**Approach:** Restore the intended development default already used by the Dapr Redis components, while retaining parameter override support.

## Suggested Review Order

- Restore the shared development password so Redis can be provisioned on a clean AppHost.
  [`AppHost.cs:33`](../../../CoreBankDemo.AppHost/AppHost.cs#L33)
