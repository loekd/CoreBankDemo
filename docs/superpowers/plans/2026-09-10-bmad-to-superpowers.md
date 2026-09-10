# Replace BMAD with superpowers — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete every BMAD artifact from the repository, adopt the superpowers plugin as the workflow, migrate the durable BMAD documents into `docs/superpowers/specs/`, and move the branch-and-PR policy into a hook plus `AGENTS.md`.

**Architecture:** Documentation and configuration only; no C# changes. One branch (`feature/bmad-to-superpowers`, cut from `origin/main` at `ec8a5c8`), one pull request, one commit per task. Workers edit disjoint files in the shared working tree and never commit; the orchestrator reviews and commits.

**Tech Stack:** bash, git, python3 (hook JSON parsing, manifest and link-check scripts), Claude Code project settings and hooks, superpowers 6.3.0.

**Spec:** [`../specs/2026-09-10-bmad-to-superpowers-design.md`](../specs/2026-09-10-bmad-to-superpowers-design.md)

## Global Constraints

- Branch: `feature/bmad-to-superpowers`, cut from `origin/main` after PR 17 (`ec8a5c8`). Never commit to `main`. Push and open the PR only in Task 10.
- Workers never run `git commit`, `git add`, `git rm`, or `git mv`. Only the orchestrator commits, after reviewing each task's output.
- Out of scope: any `.cs`, `.csproj`, `.sln*` file; `docs/adr/`; the 12 project skills under `.claude/skills/` that do not start with `bmad-`; `.github/workflows/ci.yml`; `.claude/settings.local.json`.
- Migrated documents preserve domain content verbatim. Only frontmatter, wrapper tags, `{project-root}/` prefixes, relative links, and BMAD process vocabulary change (spec, "Conversion rules").
- The word "bmad" (any case) may remain only on the `**Migrated from:**` line of a migrated spec and inside `docs/superpowers/plans/`.
- Dates in file names come from the source's `created:` frontmatter, else its `**Date:**` line, else the date in its folder name, else `git log --diff-filter=A --format=%ad --date=short -- <file> | tail -1`.

## Worker assignment and parallelism

| Task | Worker | Runs in parallel with |
|---|---|---|
| 1 Settings, hook, hook tests | Sonnet | 2, 3, 4 |
| 2 CLAUDE.md, AGENTS.md, README, ARCHITECTURE | Haiku | 1, 3, 4 |
| 3 Promote constraints.md | Haiku | 1, 2, 4 |
| 4 Manifest | orchestrator runs the script | 1, 2, 3 |
| 5 Convert (9 batches) | Sonnet, one per batch | 6 (after 4) |
| 6 Backlog | Sonnet | 5 (after 4) |
| 7 Delete BMAD | orchestrator | after 5 and 6 |
| 8 Verify | Haiku runs scripts, orchestrator judges | after 7 |
| 9 Memory update | orchestrator | after 8 |
| 10 Pull request | orchestrator | after 9 |

---

### Task 1: Shared settings, branch-gate hook, and its tests

**Files:**
- Create: `.claude/settings.json`
- Create: `.claude/hooks/branch-gate.sh` (mode 755)
- Create: `.claude/hooks/branch-gate.test.sh` (mode 755)

**Interfaces:**
- Consumes: nothing.
- Produces: the hook path `.claude/hooks/branch-gate.sh`, referenced by `AGENTS.md` in Task 2. The hook reads the Claude Code `PreToolUse` JSON on stdin (`tool_name`, `tool_input.command`) and uses `$CLAUDE_PROJECT_DIR`.

- [ ] **Step 1: Write the failing test**

Write `.claude/hooks/branch-gate.test.sh`:

```bash
#!/usr/bin/env bash
# Tests for branch-gate.sh. Builds a throwaway repository, runs the hook the way
# Claude Code does (JSON on stdin, CLAUDE_PROJECT_DIR set), asserts exit codes.
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
hook="$here/branch-gate.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
fails=0

repo="$tmp/repo"
git init -q -b main "$repo"
git -C "$repo" -c user.email=t@t -c user.name=t commit -q --allow-empty -m init

run() { # run <expected-exit> <project-dir> <json> <label>
  local expected="$1" dir="$2" json="$3" label="$4" actual
  printf '%s' "$json" | CLAUDE_PROJECT_DIR="$dir" bash "$hook" >/dev/null 2>"$tmp/err"
  actual=$?
  if [ "$actual" -eq "$expected" ]; then
    echo "ok   - $label"
  else
    echo "FAIL - $label (expected $expected, got $actual)"; sed 's/^/       /' "$tmp/err"; fails=$((fails+1))
  fi
}

edit='{"tool_name":"Edit","tool_input":{"file_path":"x.txt","old_string":"a","new_string":"b"}}'
write='{"tool_name":"Write","tool_input":{"file_path":"x.txt","content":"hi"}}'
bash_ro='{"tool_name":"Bash","tool_input":{"command":"git status && dotnet build 2>&1 | tail -1"}}'
bash_null='{"tool_name":"Bash","tool_input":{"command":"dotnet test > /dev/null 2>&1"}}'
bash_redir='{"tool_name":"Bash","tool_input":{"command":"echo hi > notes.txt"}}'
bash_sedi='{"tool_name":"Bash","tool_input":{"command":"sed -i s/a/b/ README.md"}}'
bash_commit='{"tool_name":"Bash","tool_input":{"command":"git commit -m oops"}}'
bash_switch='{"tool_name":"Bash","tool_input":{"command":"git fetch origin main && git switch -c feature/x origin/main"}}'
bash_rm='{"tool_name":"Bash","tool_input":{"command":"rm -rf bin obj"}}'

echo "# on main"
run 2 "$repo" "$edit"       "Edit on main is blocked"
run 2 "$repo" "$write"      "Write on main is blocked"
run 0 "$repo" "$bash_ro"    "read-only Bash on main passes"
run 0 "$repo" "$bash_null"  "Bash redirecting to /dev/null on main passes"
run 2 "$repo" "$bash_redir" "Bash redirect on main is blocked"
run 2 "$repo" "$bash_sedi"  "sed -i on main is blocked"
run 2 "$repo" "$bash_commit" "git commit on main is blocked"
run 0 "$repo" "$bash_switch" "git fetch/switch on main passes"
run 2 "$repo" "$bash_rm"    "rm on main is blocked"

echo "# block message names the recovery command"
printf '%s' "$edit" | CLAUDE_PROJECT_DIR="$repo" bash "$hook" >/dev/null 2>"$tmp/err"
if grep -q 'git switch -c feature/<slug> origin/main' "$tmp/err"; then echo "ok   - message"; else echo "FAIL - message"; cat "$tmp/err"; fails=$((fails+1)); fi

echo "# on a feature branch"
git -C "$repo" switch -q -c feature/anything
run 0 "$repo" "$edit"       "Edit on feature branch passes"
run 0 "$repo" "$bash_redir" "Bash redirect on feature branch passes"

echo "# detached HEAD"
git -C "$repo" switch -q --detach main
run 0 "$repo" "$edit"       "Edit on detached HEAD passes"

echo "# not a git repository"
mkdir -p "$tmp/plain"
run 0 "$tmp/plain" "$edit"  "Edit outside git passes"

echo "# CLAUDE_PROJECT_DIR unset falls back to cwd"
git -C "$repo" switch -q main
printf '%s' "$edit" | (cd "$repo" && env -u CLAUDE_PROJECT_DIR bash "$hook" >/dev/null 2>&1); actual=$?
if [ "$actual" -eq 2 ]; then echo "ok   - cwd fallback"; else echo "FAIL - cwd fallback (got $actual)"; fails=$((fails+1)); fi

[ "$fails" -eq 0 ] && echo "all tests passed" || { echo "$fails test(s) failed"; exit 1; }
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `chmod +x .claude/hooks/branch-gate.test.sh && .claude/hooks/branch-gate.test.sh`
Expected: every `run` line reports FAIL or the script errors because `branch-gate.sh` does not exist.

- [ ] **Step 3: Write the hook**

Write `.claude/hooks/branch-gate.sh`:

```bash
#!/usr/bin/env bash
# Branch gate (Claude Code PreToolUse hook).
# Policy: every change is made on a branch cut from freshly fetched origin/main and
# lands on main through a pull request. This hook enforces the half a script can
# check: nothing is written while `main` is checked out. See AGENTS.md, "Workflow".
#
# Input : the PreToolUse JSON on stdin ({"tool_name": ..., "tool_input": {...}}).
# Output: exit 0 to allow; exit 2 with a message on stderr to block.
set -u

root="${CLAUDE_PROJECT_DIR:-$PWD}"
branch="$(git -C "$root" branch --show-current 2>/dev/null || true)"
[ "$branch" = "main" ] || exit 0

input="$(cat)"
tool="$(printf '%s' "$input" | grep -oE '"tool_name"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed -E 's/.*:[[:space:]]*"([^"]*)"/\1/')"

if [ "$tool" = "Bash" ]; then
  # Without python3 the command cannot be decoded safely; allow and rely on the edit-tool block.
  command -v python3 >/dev/null 2>&1 || exit 0
  cmd="$(printf '%s' "$input" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("tool_input",{}).get("command",""))' 2>/dev/null || true)"
  # Redirections that do not write files are not writes.
  cmd="$(printf '%s' "$cmd" | sed -E 's/2>&1//g; s/&?>[[:space:]]*\/dev\/null//g')"
  write_pattern='(^|[;&|][[:space:]]*|[[:space:]])(sed[[:space:]]+-i|rm[[:space:]]|mv[[:space:]]|cp[[:space:]]|mkdir[[:space:]]|touch[[:space:]]|tee[[:space:]]|git[[:space:]]+(commit|add|mv|rm|apply|merge|rebase|reset|cherry-pick|am)([[:space:]]|$))|>'
  printf '%s' "$cmd" | grep -Eq "$write_pattern" || exit 0
fi

cat >&2 <<EOF
Branch gate: '$root' has 'main' checked out, and nothing may be written on main.
Cut a branch from freshly fetched origin/main first:
  git fetch origin main && git switch -c feature/<slug> origin/main
Use docs/<slug> for documentation-only work. Tool blocked: ${tool:-unknown}.
EOF
exit 2
```

- [ ] **Step 4: Run the tests until they pass**

Run: `chmod +x .claude/hooks/branch-gate.sh && .claude/hooks/branch-gate.test.sh`
Expected: every line starts with `ok` and the last line is `all tests passed`. Adjust `write_pattern` only if a listed case fails; do not weaken cases.

- [ ] **Step 5: Write the shared settings file**

Write `.claude/settings.json`:

```json
{
  "enabledPlugins": {
    "superpowers@claude-plugins-official": true
  },
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Edit|Write|MultiEdit|NotebookEdit|Bash",
        "hooks": [
          {
            "type": "command",
            "command": "\"$CLAUDE_PROJECT_DIR\"/.claude/hooks/branch-gate.sh"
          }
        ]
      }
    ]
  }
}
```

Run: `python3 -m json.tool .claude/settings.json >/dev/null && echo valid`
Expected: `valid`

- [ ] **Step 6: Orchestrator review and commit**

Run: `.claude/hooks/branch-gate.test.sh && git add .claude/settings.json .claude/hooks && git commit -m "chore(claude): shared settings pin superpowers and add the branch-gate hook"`

---

### Task 2: CLAUDE.md, AGENTS.md, README.md, ARCHITECTURE.md

**Files:**
- Create: `CLAUDE.md`
- Modify: `AGENTS.md:24-32` (the "BMAD Rebuild (in progress)" section)
- Modify: `README.md:367-368` (the `docs/bmad/` bullet)
- Modify: `ARCHITECTURE.md:3` (the banner)

**Interfaces:**
- Consumes: `.claude/hooks/branch-gate.sh` (Task 1), `docs/constraints.md` (Task 3), `docs/backlog.md` (Task 6), `docs/superpowers/specs/2026-08-21-architecture-spine-design.md` (Task 5). The links may dangle until those tasks finish; Task 8 checks them.
- Produces: nothing other tasks read.

- [ ] **Step 1: Create `CLAUDE.md`**

Content, exactly:

```
@AGENTS.md
```

- [ ] **Step 2: Replace the AGENTS.md section**

Replace everything from the line `## BMAD Rebuild (in progress)` up to (not including) `## Design Patterns` with:

```markdown
## Workflow

Development follows the [superpowers](https://github.com/obra/superpowers) plugin: brainstorm the change, write a design spec, write an implementation plan, implement test-first, land through a pull request. Install it once per machine with `/plugin install superpowers@claude-plugins-official`; `.claude/settings.json` enables it for this repository.

- **Documents:** `docs/superpowers/specs/YYYY-MM-DD-<slug>-design.md` holds design specs, including every migrated PRD, brief, architecture spine and story spec from the 2026 rebuild; `docs/superpowers/plans/` holds implementation plans; `docs/backlog.md` lists open stories and deferred work; `docs/constraints.md` is the binding contract (invariants, external API surface, ports, test rules). `ARCHITECTURE.md` and `docs/adr/` describe the *system*. Contradictions are resolved by writing a new ADR, never by silently diverging.
- **Branch and pull-request policy:** every change, code or documentation, is made on a branch cut from a freshly fetched `origin/main` and lands on `main` through a pull request. Run `git fetch origin main && git switch -c feature/<slug> origin/main` (`docs/<slug>` for documentation-only work) before touching any file. Never commit to `main`, and never build on whatever branch happens to be checked out: if `git log --oneline origin/main..HEAD` shows commits, that is someone else's unmerged work. The `.claude/hooks/branch-gate.sh` hook blocks writes while `main` is checked out. Push and open the PR only when asked.
- **Guardrails:** follow the `conventions`, `messaging-patterns`, and `observability` skills.
- **Test bar:** xUnit + AwesomeAssertions + Moq; ≥90% line coverage (coverlet-enforced) on logic projects; hosting boilerplate excluded. Three tiers (ADR-016): `dotnet test CoreBankDemo.UnitTests.slnf` (Docker-free), `dotnet test CoreBankDemo.IntegrationTests.slnf` (persistence on a pinned `postgres:18.3` Testcontainer), and the k6/Aspire acceptance harness. The build/test gate runs `CoreBankDemo.Rebuild.slnf` (both .NET tiers); story 6.1 in `docs/backlog.md` tracks making the full `.sln` the gate. Never use SQLite or EF Core InMemory as a PostgreSQL substitute.
- **Acceptance harness:** the k6 load test + LoadTestSupport assertions (exactly-once, no message loss, balance conservation, drain, per-key ordering). If code and load tests conflict, the load tests adapt — unless a real invariant is violated.

```

- [ ] **Step 3: Replace the README bullet**

Replace these two lines:

```markdown
- **[docs/bmad/](docs/bmad/)** — planning, implementation and test artifacts, plus
  `constraints.md` (the binding invariants, external API surface, ports and test rules)
```

with:

```markdown
- **[docs/constraints.md](docs/constraints.md)** — the binding invariants, external API surface, ports and test rules
- **[docs/superpowers/](docs/superpowers/)** — design specs (including the migrated 2026 rebuild PRDs, briefs, architecture spine and story specs) and implementation plans
- **[docs/backlog.md](docs/backlog.md)** — open stories and deferred work
```

- [ ] **Step 4: Replace the ARCHITECTURE.md banner**

Replace line 3 (the `> **Brownfield snapshot:** ...` blockquote) with:

```markdown
> **Snapshot:** This document still describes the pre-rebuild system in places and may name obsolete implementations such as `Features:UseDapr` and Dapr-backed locking. Accepted records in [`docs/adr/`](docs/adr/) and the [architecture spine](docs/superpowers/specs/2026-08-21-architecture-spine-design.md) govern new work. Regenerating this document from the code is story 8.1 in [`docs/backlog.md`](docs/backlog.md).
```

- [ ] **Step 5: Check**

Run: `grep -n -i bmad CLAUDE.md AGENTS.md README.md ARCHITECTURE.md; echo "exit=$?"`
Expected: no matches, `exit=1`.

- [ ] **Step 6: Orchestrator review and commit**

Run: `git add CLAUDE.md AGENTS.md README.md ARCHITECTURE.md && git commit -m "docs: describe the superpowers workflow and branch policy in AGENTS.md"`

---

### Task 3: Promote constraints.md

**Files:**
- Create: `docs/constraints.md` (copy of `docs/bmad/constraints.md` with the edits below; the original is deleted in Task 7)

**Interfaces:**
- Produces: `docs/constraints.md`, linked from `AGENTS.md`, `README.md`, and migrated specs.

- [ ] **Step 1: Copy**

Run: `cp docs/bmad/constraints.md docs/constraints.md`

- [ ] **Step 2: Edit the four rebuild-era lines**

Line 1: `# Rebuild Constraints — Binding Contract` becomes `# Constraints — Binding Contract`.

Line 3 becomes:

```markdown
This document is the guardrail contract for every design spec, plan and implementation in CoreBankDemo. It is hand-maintained, not generated. When any other document contradicts this file, this file wins until amended by an ADR.
```

Line 48: `- Build/test gate runs against `CoreBankDemo.Rebuild.slnf` (both tiers) until the rebuild completes.` becomes `- Build/test gate runs against `CoreBankDemo.Rebuild.slnf` (both tiers); story 6.1 in `docs/backlog.md` tracks making the full solution the gate.`

Line 59: in the table row for A5, `Regenerate doc from code at the end (epic E7)` becomes `Regenerate doc from code (story 8.1 in `docs/backlog.md`)`.

- [ ] **Step 3: Check**

Run: `grep -n -i "bmad\|rebuild" docs/constraints.md`
Expected: only the `CoreBankDemo.Rebuild.slnf` line on 48 (the solution filter's real name) and nothing mentioning BMAD.

- [ ] **Step 4: Orchestrator review and commit**

Run: `git add docs/constraints.md && git commit -m "docs: promote constraints.md to docs/ as a living contract"`

---

### Task 4: Migration manifest

**Files:**
- Create: `docs/superpowers/plans/2026-09-10-bmad-to-superpowers-manifest.md` (generated)
- Scratch: `$CLAUDE_JOB_DIR/tmp/manifest.py`

**Interfaces:**
- Produces: a table `| old path | action | new path | reason |` covering all 102 files. Tasks 5 and 6 read the `convert` rows for link rewriting; Task 7 checks it accounts for every file.

- [ ] **Step 1: Write the generator**

Save as `$CLAUDE_JOB_DIR/tmp/manifest.py`:

```python
#!/usr/bin/env python3
"""Classify every tracked docs/bmad file into promote / convert / fold / drop."""
import re, subprocess, sys
from pathlib import Path

files = subprocess.check_output(["git", "ls-files", "docs/bmad"], text=True).split()
SPECS = "docs/superpowers/specs"

FIXED = {  # planning documents with fixed slugs
    "docs/bmad/planning-artifacts/briefs/brief-CoreBankDemo-2026-08-21/brief.md": "corebank-rebuild-brief",
    "docs/bmad/planning-artifacts/briefs/brief-demorunner-console-2026-09-03/brief.md": "demorunner-console-brief",
    "docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-08-21/prd.md": "corebank-rebuild-prd",
    "docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10/prd.md": "evidence-payload-bodies-prd",
    "docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10-resource-lifecycle/prd.md": "resource-lifecycle-prd",
    "docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/ARCHITECTURE-SPINE.md": "architecture-spine",
    "docs/bmad/planning-artifacts/epics.md": "epics-and-stories",
    "docs/bmad/planning-artifacts/sprint-change-proposal-2026-08-30.md": "sprint-change-proposal",
    "docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/DESIGN.md": "demorunner-ux",
    "docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/EXPERIENCE.md": "demorunner-ux-experience",
    "docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/wireframes/operations-stage-focus.md": "demorunner-operations-stage-focus-wireframe",
}
ADDENDA = {  # addendum.md folded into the sibling document
    "docs/bmad/planning-artifacts/briefs/brief-CoreBankDemo-2026-08-21/addendum.md": "brief.md",
    "docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10/addendum.md": "prd.md",
    "docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10-resource-lifecycle/addendum.md": "prd.md",
}
FOLD = {
    "docs/bmad/implementation-artifacts/sprint-status.yaml": "open stories and action items go to docs/backlog.md",
    "docs/bmad/implementation-artifacts/deferred-work.md": "all items condensed into docs/backlog.md",
}

def created(path: str) -> str:
    text = Path(path).read_text(errors="replace")
    m = re.search(r"^created:\s*'?\"?(\d{4}-\d{2}-\d{2})", text, re.M)
    if m: return m.group(1)
    m = re.search(r"\*\*Date:\*\*\s*(\d{4}-\d{2}-\d{2})", text)
    if m: return m.group(1)
    m = re.search(r"(\d{4}-\d{2}-\d{2})", path)
    if m: return m.group(1)
    out = subprocess.check_output(["git", "log", "--diff-filter=A", "--format=%ad", "--date=short", "--", path], text=True).split()
    return out[-1] if out else "undated"

rows = []
for f in files:
    name = Path(f).name
    if f == "docs/bmad/constraints.md":
        rows.append((f, "promote", "docs/constraints.md", "living contract")); continue
    if f in FIXED:
        rows.append((f, "convert", f"{SPECS}/{created(f)}-{FIXED[f]}-design.md", "planning document")); continue
    if f in ADDENDA:
        rows.append((f, "convert", "(folded into the sibling " + ADDENDA[f] + " conversion as a final section)", "addendum")); continue
    if f in FOLD:
        rows.append((f, "fold", "docs/backlog.md", FOLD[f])); continue
    m = re.match(r"docs/bmad/implementation-artifacts/spec-(\d+)-(\d+)-(.+)\.md$", f)
    if m:
        rows.append((f, "convert", f"{SPECS}/{created(f)}-story-{m.group(1)}-{m.group(2)}-{m.group(3)}-design.md", "story spec")); continue
    m = re.match(r"docs/bmad/implementation-artifacts/spec-(.+)\.md$", f)
    if m:
        rows.append((f, "convert", f"{SPECS}/{created(f)}-{m.group(1)}-design.md", "story spec")); continue
    if re.search(r"epic-\d+-retrospective\.md$", f):
        rows.append((f, "fold", "docs/backlog.md", "open action items go to docs/backlog.md; the rest is process record")); continue
    reason = ("memlog" if name == ".memlog.md" else
              "working file" if "/.working/" in f else
              "review artifact" if name.startswith("review-") or name.startswith("validation-report") else
              "compiled build context" if re.search(r"epic-\d+-context\.md$", f) else
              "process artifact" if name in ("implementation-readiness.md", "7-3-continuation-handoff.md", "7-3-acceptance-evidence.md") or name.startswith("bmad-build-auto-result") else
              "inspiration image" if f.endswith(".png") else None)
    if reason is None:
        sys.exit(f"unclassified: {f}")
    rows.append((f, "drop", "", reason))

order = {"promote": 0, "convert": 1, "fold": 2, "drop": 3}
rows.sort(key=lambda r: (order[r[1]], r[0]))
counts = {k: sum(1 for r in rows if r[1] == k) for k in order}
print("# BMAD migration manifest\n")
print("Generated 2026-09-10 by the plan's manifest script. One row per tracked file under `docs/bmad/`.\n")
print("| Action | Files |\n|---|---|")
for k, v in counts.items(): print(f"| {k} | {v} |")
print(f"| **total** | **{len(rows)}** |\n")
print("| Old path | Action | New path | Reason |\n|---|---|---|---|")
for old, action, new, reason in rows:
    print(f"| `{old}` | {action} | {('`' + new + '`') if new and not new.startswith('(') else new} | {reason} |")
```

- [ ] **Step 2: Generate and sanity-check**

Run: `python3 "$CLAUDE_JOB_DIR/tmp/manifest.py" > docs/superpowers/plans/2026-09-10-bmad-to-superpowers-manifest.md && grep -c '^| `docs/bmad' docs/superpowers/plans/2026-09-10-bmad-to-superpowers-manifest.md`
Expected: `102`, and the counts table reads promote 1, convert 64 (61 outputs; three addendum rows fold into siblings), fold 5, drop 32. If any new path is `undated` or two rows share a new path, fix the generator, not the output.

Run: `cut -d'|' -f4 docs/superpowers/plans/2026-09-10-bmad-to-superpowers-manifest.md | grep -o 'docs/superpowers/specs/[^ ]*' | sort | uniq -d`
Expected: no output (no duplicate targets).

- [ ] **Step 3: Orchestrator commit**

Run: `git add docs/superpowers/plans/2026-09-10-bmad-to-superpowers-manifest.md && git commit -m "docs: manifest for the BMAD document migration"`

---

### Task 5: Convert documents (nine parallel batches)

**Files:**
- Create: the 61 `convert` targets listed in the manifest.
- Read only: the source files, the manifest, `docs/constraints.md`.

**Interfaces:**
- Consumes: the manifest (Task 4) for every old→new mapping, used to rewrite links.
- Produces: `docs/superpowers/specs/*.md`. Task 6 links deferred-work items to these paths; Task 8 checks them.

**Batches** (one Sonnet worker each; a worker gets its batch's rows plus the whole manifest):

| Batch | Sources |
|---|---|
| B1 | the 3 PRDs and their 2 addenda, the 2 briefs and the 1 addendum, `ARCHITECTURE-SPINE.md`, `epics.md`, `sprint-change-proposal-2026-08-30.md` |
| B2 | UX `DESIGN.md`, `EXPERIENCE.md`, `wireframes/operations-stage-focus.md` |
| B3 | `spec-1-1` … `spec-2-6` (9 files) |
| B4 | `spec-3-1` … `spec-4-3` (7 files) |
| B5 | `spec-4-4` … `spec-5-3` (7 files) |
| B6 | `spec-5-4` … `spec-6-7` (8 files, including `spec-6-2-runtime-acceptance-completion`) |
| B7 | `spec-7-1` … `spec-7-4`, `spec-add-instant-payment-rail`, `spec-add-instant-rail-load-coverage`, `spec-add-rest-client-devcontainer-extension` (7 files) |
| B8 | `spec-demorunner-*` (4 files), `spec-evidence-payload-bodies` (5 files) |
| B9 | `spec-fix-*` (2), `spec-instant-rail-*` (2), `spec-refine-kiota-and-local-replicas-backlog`, `spec-restore-redis-development-password-default` (6 files) |

- [ ] **Step 1: Worker instructions (identical for every batch; the batch table is the only variable)**

For each source file in the batch:

1. Read the whole source file. Determine the header values:
   - **Status:** `Implemented` when the frontmatter says `status: 'done'` or the file is a story spec whose sprint-status entry is `done` (`grep '<slug>: done' docs/bmad/implementation-artifacts/sprint-status.yaml`); `Reference` for PRDs, briefs, the spine, epics, the change proposal, and UX documents; `Superseded` only when the body itself says so.
   - **Kind:** one of `PRD`, `brief`, `architecture`, `story spec`, `epics`, `change proposal`, `UX design`.
   - **Original date:** the date already embedded in the target file name.
   - **Migrated from:** the old path, in backticks.
   - **Related:** the pull request from `git log --diff-filter=A --format=%s -- <old path> | tail -1` when the subject ends in `(#n)`, written as `PR #n`; plus every `ADR-0nn` the body cites, written as `[ADR-0nn](../../adr/<matching file name>)` (find it with `ls docs/adr | grep 0nn`). Write `none` if nothing applies.
2. Write the target file as:

   ```markdown
   # <title from frontmatter `title:`/`name:`, else the first H1 of the body>

   > **Status:** <value>
   > **Kind:** <value>
   > **Original date:** <YYYY-MM-DD>
   > **Migrated from:** `<old path>` on 2026-09-10
   > **Related:** <value>

   <body>
   ```

   The body is the source minus its YAML frontmatter and minus a leading H1 that duplicates the title. Apply exactly these transformations and nothing else:
   - Remove `<frozen-after-approval ...>` / `</frozen-after-approval>` and any other XML-style wrapper tags; keep their content.
   - Remove every `{project-root}/` prefix.
   - Rewrite relative links so they resolve from `docs/superpowers/specs/`: a link to a `convert` source becomes a link to its manifest target (file name only, no `#L` anchor); a link to `docs/bmad/constraints.md` becomes `../../constraints.md`; a link to code or tests becomes `../../../<path from repo root>`; a link to a `fold` or `drop` source becomes plain text: the file's base name followed by ` (removed in the 2026-09-10 migration; see git history)`.
   - Reword BMAD process vocabulary: `bmad-build`, `bmad-spec`, `bmad-prd` and other skill names become "the build workflow", "the spec workflow", "the PRD workflow"; "BMAD workflow"/"BMAD artifact"/"BMAD stream" become "workflow"/"document"/"stream". Leave every other word alone, including code, tables, acceptance criteria, and rulings.
   - When the batch row says an `addendum.md` folds into this document, append `\n\n## Addendum\n\n` followed by the addendum body (same transformations) at the end.
3. After writing, run `grep -ci bmad <target>`; the count must be exactly `1` (the Migrated-from line). Run `grep -c 'project-root' <target>`; expected `0`.

Do not create, delete, move, or commit any other file. Report the list of files written, the header block of each, and every link you rewrote to plain text.

- [ ] **Step 2: Orchestrator review per batch**

For each batch report, open two random targets and confirm: the header block is complete, the H1 is not duplicated, the body length is within a few percent of the source (`wc -c`), and no domain sentence was paraphrased (spot-check a table or an acceptance-criteria block against the source).

- [ ] **Step 3: Orchestrator commit (after all nine batches)**

Run: `ls docs/superpowers/specs | grep -vc 'bmad-to-superpowers' ` → expected `61`, then
`git add docs/superpowers/specs && git commit -m "docs: migrate BMAD PRDs, briefs, spine, epics, UX and story specs to superpowers specs"`

---

### Task 6: Backlog

**Files:**
- Create: `docs/backlog.md`
- Read only: `docs/bmad/planning-artifacts/epics.md` (stories 6.1, 6.4, 8.1, 8.2), `docs/bmad/implementation-artifacts/deferred-work.md`, `docs/bmad/implementation-artifacts/sprint-status.yaml`, `docs/bmad/implementation-artifacts/epic-{1,2,3}-retrospective.md`, the manifest.

**Interfaces:**
- Consumes: manifest targets, so each deferred item can name the new path of its source spec.
- Produces: `docs/backlog.md`, linked from `AGENTS.md`, `README.md`, `ARCHITECTURE.md`, `docs/constraints.md`.

- [ ] **Step 1: Worker instructions**

Write `docs/backlog.md` with this structure:

```markdown
# Backlog

Open work carried over from the 2026 rebuild. Stories keep their original numbers so
the migrated epics document (`superpowers/specs/2026-08-21-epics-and-stories-design.md`)
still lines up. Deferred items are review follow-ups that were consciously left open.

## Open stories

### Story 6.1: Aspire application graph
<the story's full text from epics.md, verbatim, starting at "As the demo owner">

### Story 6.4: Chaos opt-in and demo smoke
<verbatim>

### Story 8.1: Regenerate ARCHITECTURE.md
<verbatim>

### Story 8.2: ADRs and skill updates
<verbatim>

## Deferred work

<one `### ` heading per source spec, using the spec's new path in backticks as a
relative link from docs/, e.g. `[story 3.2](superpowers/specs/2026-08-2x-story-3-2-...-design.md)`;
under it one bullet per deferred-work item from that spec: the `summary` condensed to
at most two sentences, followed by " — " and the `evidence` condensed to one sentence.
Keep the source order. All 81 items must appear; count them.>

## Open retrospective action items

<every action item from the three epic retrospectives whose status is not done, one
bullet each, prefixed by the epic number. If a retrospective lists none, write
"None recorded for epic N.">
```

After writing: `grep -c '^- ' docs/backlog.md` must be at least 81. `grep -ci bmad docs/backlog.md` must be `0`. Report both numbers and list any deferred item you could not attribute to a migrated spec.

- [ ] **Step 2: Orchestrator review and commit**

Compare three random deferred items against `deferred-work.md` for meaning, then
`git add docs/backlog.md && git commit -m "docs: backlog of open stories and deferred work"`

---

### Task 7: Delete BMAD

**Files:**
- Delete: `_bmad/`, `.claude/skills/bmad-*/`, `docs/bmad/`
- Modify: `.gitignore` (remove the two lines `# BMAD personal (user-scope) config` and `_bmad/config.user.toml`)

**Interfaces:**
- Consumes: Tasks 3, 5, 6 complete (the promoted, converted and folded content exists).

- [ ] **Step 1: Confirm nothing outside the deletion set still links into it**

Run: `grep -rn "docs/bmad\|_bmad\|skills/bmad-" --exclude-dir=.git --exclude-dir=_bmad --exclude-dir=bin --exclude-dir=obj --exclude-dir=docs . | grep -v '^./.claude/skills/bmad-'`
Expected: no output. If a line appears, fix that file first (it is a Task 2 or Task 3 miss).

- [ ] **Step 2: Remove tracked files**

Run:
```bash
git rm -r -q _bmad docs/bmad
git rm -r -q .claude/skills/bmad-*
```

- [ ] **Step 3: Remove untracked leftovers (gitignored config, caches)**

Run: `rm -rf _bmad docs/bmad .claude/skills/bmad-* && ls .claude/skills && git status --porcelain | grep -v '^D ' | head`
Expected: the 12 project skills listed; no untracked leftovers.

- [ ] **Step 4: Edit `.gitignore`**

Delete these two lines:
```
# BMAD personal (user-scope) config
_bmad/config.user.toml
```

- [ ] **Step 5: Commit**

Run: `git add -A .gitignore && git commit -m "chore: remove BMAD tooling, skills and rebuild artifacts"`
Expected: the commit touches 1,113 tracked files (41 + 970 + 102) plus `.gitignore`. Check with `git show --stat HEAD | tail -1`.

---

### Task 8: Verify

**Files:**
- Scratch: `$CLAUDE_JOB_DIR/tmp/linkcheck.py`

- [ ] **Step 1: bmad residue**

Run: `git grep -il bmad -- . ':!docs/superpowers/plans' | sort`
Expected: only files under `docs/superpowers/specs/`. Then:
`for f in docs/superpowers/specs/*.md; do n=$(grep -ci bmad "$f"); [ "$n" -le 1 ] || echo "$f: $n"; done`
Expected: no output (each spec has at most the Migrated-from line).

- [ ] **Step 2: link check**

Save as `$CLAUDE_JOB_DIR/tmp/linkcheck.py` and run `python3 "$CLAUDE_JOB_DIR/tmp/linkcheck.py"`:

```python
#!/usr/bin/env python3
"""Every relative markdown link in the docs must resolve to an existing path."""
import re, sys
from pathlib import Path
targets = [*Path("docs").rglob("*.md"), Path("AGENTS.md"), Path("README.md"), Path("ARCHITECTURE.md")]
link = re.compile(r"\]\(([^)\s]+)\)")
bad = 0
for md in targets:
    for raw in link.findall(md.read_text(errors="replace")):
        if re.match(r"^[a-z]+:", raw) or raw.startswith("#"):
            continue
        path = raw.split("#", 1)[0]
        if not path:
            continue
        resolved = (md.parent / path).resolve() if not path.startswith("/") else Path(path)
        if not resolved.exists():
            print(f"{md}: broken link {raw}"); bad += 1
print("broken links:", bad)
sys.exit(1 if bad else 0)
```

Expected: `broken links: 0`. Pre-existing broken links in `docs/adr/` are out of scope: list them separately and do not fix them.

- [ ] **Step 3: hook, build, tree**

Run:
```bash
.claude/hooks/branch-gate.test.sh
dotnet tool restore >/dev/null && dotnet build CoreBankDemo.Rebuild.slnf -nologo -v q 2>&1 | tail -3
ls -d _bmad docs/bmad .claude/skills/bmad-* 2>&1 | head -3
ls .claude/skills | wc -l
```
Expected: `all tests passed`; `Build succeeded`; three "No such file" lines; `12`.

- [ ] **Step 4: fresh session**

Run: `claude -p 'List the names of every available skill whose name starts with "bmad-" or "superpowers:". Output only the names, one per line.' 2>/dev/null | sort`
Expected: superpowers skill names only. If the command cannot run in this environment, note that and rely on Step 3's `ls .claude/skills` check.

- [ ] **Step 5: Orchestrator judgement**

No commit unless a fix was needed. Any fix is committed as `fix(docs): <what>`.

---

### Task 9: Update the session memory

**Files:**
- Modify: `/home/agent/.claude/projects/-Users-loekd-projects-CoreBankDemo/memory/feedback_branch_first.md`

- [ ] **Step 1: Replace the enforcement sentence**

In "How to apply", replace the sentence beginning `The repo now enforces this through branch-gate `activation_steps_prepend` entries in `_bmad/custom/*.toml`` through the end of that paragraph with:

```
The repo enforces the on-`main` half of this through `.claude/hooks/branch-gate.sh`, a
PreToolUse hook registered in `.claude/settings.json` (since 2026-09-10, when BMAD was
replaced by superpowers). The "cut from fresh origin/main" half is words only, in
AGENTS.md, because a hook cannot tell a stale base from a legitimately moved main.
```

- [ ] **Step 2: Check the index still points at it**

Run: `grep branch_first /home/agent/.claude/projects/-Users-loekd-projects-CoreBankDemo/memory/MEMORY.md`
Expected: one line.

---

### Task 10: Pull request

- [ ] **Step 1: Push**

Run: `git push -u origin feature/bmad-to-superpowers`

- [ ] **Step 2: Open the PR**

Run:
```bash
gh pr create --base main --title "chore: replace BMAD with superpowers" --body-file "$CLAUDE_JOB_DIR/tmp/pr-body.md"
```
where the body lists: what was removed (counts), what replaced it (settings, hook, docs layout), the migration manifest link, the verification results from Task 8 verbatim, and the note that contributors must run `/plugin install superpowers@claude-plugins-official` once. End with the attribution lines required for this session.

- [ ] **Step 3: Report**

Give the user the PR URL, the verification results, and the one manual follow-up (installing the plugin on other machines).
