# Replace BMAD with superpowers — design

> **Status:** Approved 2026-09-10. Implemented by
> [`../plans/2026-09-10-bmad-to-superpowers.md`](../plans/2026-09-10-bmad-to-superpowers.md).

## Goal

Remove every BMAD-METHOD artifact from the repository and make the
[superpowers](https://github.com/obra/superpowers) plugin the development workflow:
brainstorm, spec, plan, test-driven implementation, pull request. Keep the knowledge
that BMAD documents hold by migrating the durable ones into superpowers-style design
documents, promoting the two living documents, and folding open work into a backlog.
Nothing about the running system changes.

## What exists today

| Area | Tracked files | Role |
|---|---|---|
| `_bmad/` | 41 | Installer config, module manifests, one Python helper, and `_bmad/custom/*.toml` overrides that carry the branch-and-PR policy as per-skill "branch gates" |
| `.claude/skills/bmad-*` | 970 (59 folders) | The BMAD skill library |
| `docs/bmad/` | 102 | PRDs, briefs, architecture spine, epics, 50 story specs, sprint status, deferred-work log, UX design, reviews, memlogs |
| Prose | 4 files | `AGENTS.md` "BMAD Rebuild" section, `README.md` documentation list, `ARCHITECTURE.md` banner, `.gitignore` entry for `_bmad/config.user.toml` |

`docs/adr/` (20 records) never references BMAD and is untouched. The 12 project
skills (`aspire-launch`, `aspire-mcp`, `build`, `conventions`, `corebank-trace-analysis`,
`dapr-install`, `devproxy-install`, `dotnet-install`, `load-test`, `messaging-patterns`,
`observability`, `sandbox-bootstrap`) and `.claude/commands/run-load-tests.md` stay.

superpowers 6.3.0 is installed at user scope on the development machine and the
sandbox. Its `SessionStart` hook injects the `using-superpowers` skill, and its
brainstorming and writing-plans skills write to `docs/superpowers/specs/` and
`docs/superpowers/plans/`. No repository file references superpowers yet.

## Target state

### Tooling

- `_bmad/`, all 59 `.claude/skills/bmad-*` folders, and the `.gitignore` line for
  `_bmad/config.user.toml` are deleted.
- A new shared `.claude/settings.json` (checked in; distinct from the personal
  `settings.local.json`) pins `"superpowers@claude-plugins-official": true` under
  `enabledPlugins` and registers the branch-gate hook. Pinning does not auto-install
  the plugin for a new contributor (claude-code issue 41669), so `AGENTS.md` also gives
  the install command.
- A root `CLAUDE.md` containing the single line `@AGENTS.md`, so Claude Code loads the
  orientation file that GitHub Copilot and opencode already read.

### Branch gate

The BMAD TOML gates enforced: fetch `origin/main`, refuse to work on a dirty tree, cut
`feature/<slug>` or `docs/<slug>` from `origin/main`, land through a pull request. That
policy moves to two places:

1. **Words** in `AGENTS.md`, stating the full policy including "cut from freshly fetched
   `origin/main`", which a hook cannot check reliably.
2. **Enforcement** in `.claude/hooks/branch-gate.sh`, a `PreToolUse` hook matching
   `Edit|Write|MultiEdit|NotebookEdit|Bash`. When the repository at
   `$CLAUDE_PROJECT_DIR` has `main` checked out it exits 2 (block) for every edit tool,
   and for Bash commands that look like writes: output redirection, `sed -i`, `rm`, `mv`,
   `cp`, `mkdir`, `touch`, `tee`, and `git commit|add|mv|rm|apply|merge|rebase|reset|
   cherry-pick|am`. Redirections to `/dev/null` and `2>&1` are not writes. Any other
   branch, a detached HEAD, or a directory that is not a git repository passes. The block
   message prints the exact recovery command. The script depends on bash, git, and
   python3 (for JSON parsing; without python3 the Bash check is skipped and edit tools
   are still blocked). It ships with `.claude/hooks/branch-gate.test.sh`, a plain bash
   test that builds a throwaway repository and asserts the exit codes.

### Documents

Target layout:

```
docs/
  constraints.md                      promoted from docs/bmad/constraints.md
  backlog.md                          new: open stories + deferred work + open retro actions
  adr/                                unchanged
  superpowers/
    specs/YYYY-MM-DD-<slug>-design.md migrated BMAD documents, plus this design
    plans/YYYY-MM-DD-<slug>.md        the migration plan and its manifest
```

Every one of the 102 files under `docs/bmad/` gets exactly one of four actions,
recorded in `docs/superpowers/plans/2026-09-10-bmad-to-superpowers-manifest.md`:

| Action | Files | Rule |
|---|---|---|
| **promote** | `constraints.md` | Moves to `docs/constraints.md`. Title becomes "Constraints — binding contract"; the intro sentence no longer speaks of BMAD workflow invocations or a rebuild in progress; the two remaining rebuild-era lines (Rebuild.slnf gate, "epic E7") are reworded to present tense. |
| **convert** | 3 PRDs (with their `addendum.md` folded in as a final section), 2 briefs (2026-08-21 with its addendum folded in), `ARCHITECTURE-SPINE.md`, `epics.md`, `sprint-change-proposal-2026-08-30.md`, the UX `DESIGN.md`, `EXPERIENCE.md`, and `wireframes/operations-stage-focus.md`, and all 50 `spec-*.md` story specs | Becomes `docs/superpowers/specs/<created>-<slug>-design.md` (61 files). See conversion rules below. |
| **fold** | `sprint-status.yaml`, `deferred-work.md`, the four open stories in `epics.md`, and the action-item sections of the three `epic-N-retrospective.md` files | Content is summarised into `docs/backlog.md`; the source file is then deleted. |
| **drop** | `.memlog.md` (5), `.working/*` (2), every `review-*.md` (9), `validation-report.{md,html}`, `implementation-readiness.md`, `bmad-build-auto-result-*.md` (2), `7-3-continuation-handoff.md`, `7-3-acceptance-evidence.md`, `epic-N-context.md` (7), `epic-N-retrospective.md` (3, after folding), `imports/operator-console-inspiration.png` | Deleted. Git history keeps them; the manifest records the reason. |

Naming for converted files:

- `<created>` is the `created:` value in the source's YAML frontmatter, else the
  `**Date:**` line, else the date in the enclosing folder name, else the date of the
  commit that first added the file.
- Numbered story specs `spec-<e>-<s>-<slug>.md` become `story-<e>-<s>-<slug>`; other
  `spec-<slug>.md` become `<slug>`. Planning documents get fixed slugs, listed in the plan.

Conversion rules (applied per file, content otherwise preserved verbatim):

1. The YAML frontmatter is replaced by a blockquote header:
   `**Status:**` (Implemented / Superseded / Reference), `**Kind:**` (PRD, brief,
   architecture, story spec, epics, change proposal, UX design), `**Original date:**`,
   `**Migrated from:**` the old path (the only place the word "bmad" may appear),
   `**Related:**` the pull request named in the first commit that added the file, when
   its subject carries `(#n)`, plus any ADRs the body already cites.
2. `<frozen-after-approval ...>` and similar wrapper tags are removed; their content stays.
3. `{project-root}/` prefixes are removed.
4. Relative links are recomputed so they resolve from the new location. Links to a
   converted file point at its new name without line anchors. Links to a dropped or
   folded file are replaced by plain text naming the document and "(removed in the
   2026-09-10 migration; see git history)". Links into `docs/bmad/constraints.md` point
   at `docs/constraints.md`.
5. Mentions of BMAD process machinery in the body (skill names such as `bmad-build` or
   `bmad-spec`, "BMAD workflow", "BMAD artifact") are reworded to neutral terms ("the
   build workflow", "the spec"). Domain content is never rewritten.

`docs/backlog.md` has three sections: **Open stories** (6.1, 6.4, 8.1, 8.2 with their
acceptance criteria copied from `epics.md`), **Deferred work** (the 81 items from
`deferred-work.md`, each condensed to at most two sentences and grouped under the new
path of its source spec), and **Open retrospective action items**.

### Prose updates

- `AGENTS.md`: the "BMAD Rebuild (in progress)" section is replaced by a "Workflow"
  section describing superpowers, the branch-and-PR policy, the install command, the
  document layout, and the test bar (which stays word for word). The rebuild is
  described as complete apart from the stories in `docs/backlog.md`.
- `README.md` documentation list: the `docs/bmad/` bullet becomes bullets for
  `docs/constraints.md`, `docs/superpowers/`, and `docs/backlog.md`.
- `ARCHITECTURE.md` banner: the `feature/bmad` and spine links are replaced by links to
  `docs/adr/` and the migrated spine; "Story 8.1" becomes a pointer to `docs/backlog.md`.

## Delivery

One branch, `feature/bmad-to-superpowers`, cut from `origin/main` after PR 17 merged,
landing through a single pull request. Commits are per task so the deletion commit is
reviewable on its own. Workers (Sonnet for conversion, hook, and backlog; Haiku for
mechanical edits and checks) edit disjoint files in the shared working tree and never
commit; the orchestrating session reviews every result and commits.

Acceptance, all verified before the PR opens:

- `git grep -il bmad` on the branch lists only files under `docs/superpowers/specs/`
  and `docs/superpowers/plans/`, and in each migrated spec the word appears only on the
  `**Migrated from:**` line or as the literal git ref `feature/bmad`. This design spec
  itself is exempt because it documents the migration.
- Every relative link in `docs/**/*.md`, `AGENTS.md`, `README.md`, and
  `ARCHITECTURE.md` resolves to an existing file (links quoted inside fenced code
  blocks are not counted).
- `.claude/hooks/branch-gate.test.sh` passes.
- `dotnet build CoreBankDemo.Rebuild.slnf` succeeds (proves no tracked build input was
  removed by accident).
- A fresh `claude` session in the repository lists the superpowers skills and no
  `bmad-*` skill.
- The manifest accounts for all 102 files and the tree contains no `docs/bmad/`,
  `_bmad/`, or `.claude/skills/bmad-*` path.

## Out of scope

C# code, `docs/adr/`, the 12 project skills, `.github/workflows/ci.yml`,
`.claude/settings.local.json`, and the parent-directory `CLAUDE.md` outside the repo.
