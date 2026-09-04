---
status: blocked
---

# BMad Build Auto Result

Status: blocked
Blocking condition: dirty working tree

Story 6.3 was resolved and its stale Epic 6 context was regenerated, but implementation did not start because version-control sanity found existing tracked and untracked changes. The dirty set includes an unrelated modification to `spec-5-3-contract-generated-kiota-corebank-client.md`, so build-auto cannot establish a clean Story 6.3 baseline safely.
