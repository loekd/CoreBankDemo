---
title: 'Add REST Client devcontainer extension'
type: 'chore'
created: '2026-08-30'
status: 'done'
route: 'one-shot'
---

# Add REST Client devcontainer extension

## Intent

**Problem:** The devcontainer does not install an editor extension capable of executing the repository's `.http` request files.

**Approach:** Add `humao.rest-client` to the VS Code extensions installed with the devcontainer.

## Suggested Review Order

- Install REST Client alongside the existing C# development extensions.
  [`devcontainer.json:22`](../../../.devcontainer/devcontainer.json#L22)
