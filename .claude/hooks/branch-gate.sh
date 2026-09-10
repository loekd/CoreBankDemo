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
