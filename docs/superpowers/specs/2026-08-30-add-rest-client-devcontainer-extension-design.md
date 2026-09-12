# Add REST Client devcontainer extension

> **Status:** Implemented
> **Kind:** story spec
> **Original date:** 2026-08-30
> **Migrated from:** `docs/bmad/implementation-artifacts/spec-add-rest-client-devcontainer-extension.md` on 2026-09-10
> **Related:** PR #2

## Intent

**Problem:** The devcontainer does not install an editor extension capable of executing the repository's `.http` request files.

**Approach:** Add `humao.rest-client` to the VS Code extensions installed with the devcontainer.

## Suggested Review Order

- Install REST Client alongside the existing C# development extensions.
  [`devcontainer.json:22`](../../../.devcontainer/devcontainer.json#L22)
