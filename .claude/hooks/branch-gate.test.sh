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

edit_json() { # edit_json <file_path> [cwd]
  local fp="$1" cwd="${2:-}"
  if [ -n "$cwd" ]; then
    printf '{"tool_name":"Edit","tool_input":{"file_path":"%s","old_string":"a","new_string":"b"},"cwd":"%s"}' "$fp" "$cwd"
  else
    printf '{"tool_name":"Edit","tool_input":{"file_path":"%s","old_string":"a","new_string":"b"}}' "$fp"
  fi
}
bash_cwd_json() { # bash_cwd_json <command> <cwd>
  printf '{"tool_name":"Bash","tool_input":{"command":"%s"},"cwd":"%s"}' "$1" "$2"
}

echo "# worktree: decide branch from where the write happens, not only from CLAUDE_PROJECT_DIR"
git -C "$repo" switch -q main
git -C "$repo" worktree add -q "$tmp/wt" -b feature/wt
run 0 "$repo" "$(edit_json "$tmp/wt/x.txt")" \
  "worktree on feature branch while root is on main: Edit in worktree passes"
run 0 "$repo" "$(bash_cwd_json "echo hi > notes.txt" "$tmp/wt")" \
  "worktree on feature branch while root is on main: Bash with cwd in worktree passes"
run 0 "$repo" "$(edit_json "$tmp/wt/x.txt" "$repo")" \
  "cwd on main overrides nothing when file_path is in worktree"
run 2 "$repo" "$(edit_json "$repo/x.txt" "$tmp/wt")" \
  "edit inside root while root is on main still blocks (file location wins over cwd)"
run 2 "$tmp/wt" "$(bash_cwd_json "echo hi > notes.txt" "$repo")" \
  "Bash with cwd on main blocks even when project dir is on a feature branch"

[ "$fails" -eq 0 ] && echo "all tests passed" || { echo "$fails test(s) failed"; exit 1; }
