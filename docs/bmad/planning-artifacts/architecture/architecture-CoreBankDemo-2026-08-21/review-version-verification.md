# Review: Version & Reality Verification — ARCHITECTURE-SPINE.md

**Reviewer lens:** version verification (were committed stack decisions web-researched / reality-checked, not asserted from training data?)
**Date:** 2026-08-21
**Method:** NuGet flat-container API (`api.nuget.org/v3-flatcontainer/<id>/index.json`), xunit.net release/docs pages, coverlet GitHub issues + MS Learn, AwesomeAssertions GitHub repo/releases, cross-check against `/Users/loekd/projects/CoreBankDemo/Directory.Packages.props`.

**Verdict: PASS with two required annotations** (runner-mode note under AD-9; soften the "verified current" claim for existing pins). Every named version is real, resolvable on NuGet, and — for the new test stack — latest stable as of 2026-08-21. No hallucinated packages or versions.

---

## 1. Are the versions real and current stable? — YES

Checked via flat-container API on 2026-08-21:

| Package | Spine | Latest stable on NuGet | Verdict |
| --- | --- | --- | --- |
| xunit.v3 | 4.0.0 | 4.0.0 (released 2026-08-14/15) | Current stable. Very fresh major — one week old |
| xunit.runner.visualstudio | 4.0.0 | 4.0.0 (co-released with xunit.v3 4.0.0) | Current stable |
| Microsoft.NET.Test.Sdk | 18.9.0 | 18.9.0 | Current stable |
| AwesomeAssertions | 9.6.0 | 9.6.0 (released 2026-08-20 — yesterday) | Current stable |
| Moq | 4.20.72 | 4.20.72 | Current stable (post-SponsorLink-removal line) |
| coverlet.msbuild / coverlet.collector | 10.0.1 | 10.0.1 | Current stable; 10.0.0 added .NET 10 support |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.8 | 10.0.11 | 10.0.8 exists on NuGet; deliberately pinned to repo EF family — correct per AD-9/CPM, see §6 |

The table is consistent with what a same-day NuGet check produces; version tuples like 18.9.0, 4.0.0, 9.6.0, 10.0.1 postdate any plausible training-data recall and match live NuGet exactly. This section shows genuine research.

## 2. xunit.v3 4.0.0 + coverlet + Microsoft.NET.Test.Sdk 18.9.0 — WORKS, but only in VSTest mode; spine must say so

- xunit.v3 supports **two** runner models: classic VSTest (`Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`) and native Microsoft.Testing.Platform (MTP). xunit.runner.visualstudio 4.0.0 was co-released with xunit.v3 4.0.0 specifically to keep the VSTest path working (its release notes fix a `dotnet test` VSTest-mode bug). So Test.Sdk 18.9.0 + runner.visualstudio 4.0.0 + xunit.v3 4.0.0 is a valid, supported, current combination.
- **Critical constraint the spine currently leaves implicit:** coverlet (both `coverlet.msbuild` and `coverlet.collector`) does **not** work under MTP — confirmed by MS Learn ("MTP code coverage" doc), coverlet issue #1715, and xunit discussion #3127 (Brad Wilson: use `Microsoft.Testing.Extensions.CodeCoverage` or the new `coverlet.MTP` package under MTP). `coverlet.msbuild` hooks the VSTest MSBuild targets, which MTP never invokes; coverage silently drops to nothing.
- Under the .NET 10 SDK, `dotnet test` **defaults to VSTest mode**; MTP mode is opt-in via `global.json` `{"test": {"runner": "Microsoft.Testing.Platform"}}` (formerly `dotnet.config`, no longer supported at RTM). So AD-9's "gate must pass from plain `dotnet test`" holds today with zero extra config.
- **Required annotation (AD-9 or Stack):** the coverage gate depends on staying in VSTest mode. The rebuild must NOT set a `global.json` test runner of `Microsoft.Testing.Platform`, and must NOT set `<UseMicrosoftTestingPlatformRunner>` in test projects — either would silently bypass the coverlet Threshold gate. Note also the directional risk: Microsoft recommends MTP going forward and will remove VSTest-mode support in MTP v2 on .NET 10 SDK; if the project later migrates to MTP, the gate must move to `coverlet.MTP` or `Microsoft.Testing.Extensions.CodeCoverage` (`--coverage`) at that time. A one-line note plus the future migration pointer is sufficient; the chosen stack is correct for now.

## 3. coverlet.msbuild Threshold with xunit.v3 — WORKS (VSTest mode only)

`Threshold`/`ThresholdType=line` are `coverlet.msbuild` properties evaluated in the VSTest target pipeline; MS Learn and xunit discussion #3127 confirm coverlet works with xunit.v3 test projects when run via Test.Sdk/VSTest (xunit.v3 projects being `OutputType=Exe` does not break this path). coverlet 10.0.x explicitly added .NET 10 support. Same caveat as §2: the Threshold gate evaporates under MTP.

## 4. AwesomeAssertions 9.6.0 — CONFIRMED maintained fork, xunit.v3-compatible

- It is the community fork of FluentAssertions created after the v8 license change, permanently Apache 2.0, actively maintained (9.6.0 released 2026-08-20; steady 9.x cadence since 2025-05; six listed maintainers).
- xunit.v3 compatibility confirmed at source level: `Src/AwesomeAssertions/Execution/TestFrameworkFactory.cs` detects xunit.v3, and the repo carries dedicated `Tests/TestFrameworks/XUnit3.Specs` and `XUnit3Core.Specs` projects — `Should()`/`AssertionException` routing works under xunit.v3. No dependency conflicts (the package has essentially no dependencies on modern TFMs).
- Note: xunit.v3 4.0.0 is 6 days newer than AwesomeAssertions 9.6.0; the fork pins its xunit.v3 test dependency (PR #220 excludes xunit.v3 from auto-updates), so it was validated against 3.x. Failure-detection in xunit.v3 is exception-type based and unchanged in 4.0.0 — low risk, but if `Should()` failures ever surface as raw exceptions rather than test failures, this seam is where to look.

## 5. Moq 4.20.72 — CONFIRMED current stable

Latest stable on NuGet; the SponsorLink episode (4.20.0–4.20.2) is long resolved. Cadence is slow but the package is functional for port-mocking on .NET 10. Reasonable, verified choice.

## 6. Cross-check against Directory.Packages.props — CONSISTENT, but the header sentence overclaims

Every "existing" version the spine quotes matches `/Users/loekd/projects/CoreBankDemo/Directory.Packages.props` exactly: Aspire 13.4.0, Dapr 1.17.9, CloudNative.CloudEvents 2.8.0, EF Core 10.0.8, Npgsql provider 10.0.2, OTel 1.15.x (props: 1.15.1–1.15.3). Sqlite 10.0.8 "pinned to EF Core family" is correct discipline under CPM.

However, the Stack preamble says "Verified current on NuGet 2026-08-21" and then lists existing pins that are **not** current: EF Core 10.0.8 (latest 10.0.11), Npgsql 10.0.2 (10.0.3), Aspire 13.4.0 (13.5.1), Dapr 1.17.9 (1.18.5), CloudEvents 2.8.0 (2.9.0), OTel 1.15.x (1.18.0). Matching the repo is the right call (AD-1/AD-10: don't churn the substrate mid-rebuild), and the sentence's second clause does say "existing versions from Directory.Packages.props" — but a reader can take "verified current" as covering the whole table. Suggested wording: "New test-stack versions verified current on NuGet 2026-08-21; existing rows deliberately match Directory.Packages.props (some lag latest — upgrades out of scope)."

---

## Findings summary

| # | Severity | Finding |
| --- | --- | --- |
| F1 | Should-fix | AD-9/Stack must state the VSTest-mode dependency: coverlet.msbuild Threshold gate does not run under MTP; forbid `global.json` test runner = Microsoft.Testing.Platform and `UseMicrosoftTestingPlatformRunner` for the rebuild, and note `coverlet.MTP`/MS CodeCoverage as the future MTP migration path |
| F2 | Minor | "Verified current on NuGet 2026-08-21" overclaims for existing pins (EF 10.0.8 vs 10.0.11, Aspire 13.4.0 vs 13.5.1, Dapr 1.17.9 vs 1.18.5, CloudEvents 2.8.0 vs 2.9.0, OTel 1.15.x vs 1.18.0) — reword to scope the claim to the new test stack |
| F3 | Note | xunit.v3 4.0.0 is one week old (2026-08-14) and AwesomeAssertions 9.6.0 one day old; both verified real and mutually compatible, but expect early-adopter patch releases during the rebuild window |
| F4 | Pass | All eight interrogated versions exist on NuGet and are latest stable (Sqlite 10.0.8 intentionally family-pinned); all repo-quoted versions match Directory.Packages.props exactly; AwesomeAssertions confirmed as the maintained Apache-2.0 FluentAssertions fork with source-level xunit.v3 support |

**Sources:** api.nuget.org flat-container indexes (all packages above); xunit.net/releases/v3/4.0.0; xunit.net/docs/getting-started/v3/whats-new; xunit.net/docs/getting-started/v3/code-coverage-with-mtp; github.com/xunit/xunit/discussions/3127; github.com/coverlet-coverage/coverlet/issues/1715 and release v10.0.0; learn.microsoft.com MTP code-coverage and dotnet-test docs; github.com/AwesomeAssertions/AwesomeAssertions (releases, TestFrameworkFactory.cs, XUnit3.Specs).
