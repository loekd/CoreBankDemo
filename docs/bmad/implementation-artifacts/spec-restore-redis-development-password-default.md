---
title: 'Restore Redis development password default'
type: 'bugfix'
created: '2026-08-30'
status: 'done'
route: 'one-shot'
---

# Restore Redis development password default

## Intent

**Problem:** The regular AppHost declared `redis-password` without a default, leaving the parameter in `ValueMissing` and preventing Aspire from creating the Redis container.

**Approach:** Restore the intended development default already used by the Dapr Redis components, while retaining parameter override support.

## Suggested Review Order

- Restore the shared development password so Redis can be provisioned on a clean AppHost.
  [`AppHost.cs:33`](../../../CoreBankDemo.AppHost/AppHost.cs#L33)
