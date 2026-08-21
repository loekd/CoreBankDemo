# Epic 1 (E0) Retrospective — Test Infrastructure & Scaffolding

**Date:** 2026-08-21 · **Stories:** 1.1, 1.2, 1.3 (all done) · **Commits:** beb6f2b, ac146d1, + 1.3 close

## Verdict: ACCEPTED

The gate exists, is enforced from plain `dotnet test`, and is proven in both directions on real projects (fail at <90 with canary, exit 1; pass clean, exit 0). AD-10 is empirically established: the `CoreBankDemo.Rebuild.slnf` gate stayed green with a deliberate `#error` in PaymentsAPI.

## Evidence

- Gate mechanics: `tests/Directory.Build.props` (VSTest mode, MTP double-disabled, Threshold=90/line, generated-code excludes, `.Tests` name guard, guarded parent import).
- Both gate outcomes + negative-tested filter guard recorded verbatim in `spec-1-3-gate-proof.md` Design Notes; permanent `GateProofTests` protects against vacuous Include filters.
- 5 tests green across 4 projects; full `.sln` still builds.

## What the reviews caught (worth keeping)

1. **xunit.v3 4.0 defaults to Microsoft.Testing.Platform**, which hard-errors the VSTest target on .NET 10 and silently drops coverlet enforcement — both disable switches now set. (Version-lens finding.)
2. **`[*]*.Program` doesn't match global-namespace top-level-statement `Program`** — widened to `[*]*.Program,[*]Program`. (Would have broken the gate in epic 5.)
3. **ProjectReferences defeat solution filters** — MSBuild builds them transitively, so API test projects reference nothing until their epics. (Adversarial finding; AD-10 would have been dead on arrival.)

## Carry-forward obligations (tracked in deferred-work.md + csproj TODOs)

- Stories 2.1 / 3.1 remove their `Threshold=0` override; stories 4.1 / 5.1 additionally add ProjectReference + `Include` filter.
- NU1903 advisory on transitive `SQLitePCLRaw.lib.e_sqlite3` via EF Sqlite 10.0.8 — informational, revisit if EF pins bump.

## Process notes

- Three-layer review per story earns its cost on infra stories (three gate-killing defects caught before any production code depends on the gate).
- Targeted-fix-instead-of-full-re-derive for bad_spec findings worked; spec change logs record the deviation each time.
