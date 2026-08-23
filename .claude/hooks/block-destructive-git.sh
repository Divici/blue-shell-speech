#!/bin/bash
# Blocks git operations that destroy work an agent cannot recover.
# Long autonomous builds across many worktrees are exactly where a stray --force
# costs hours. Every block names a safe alternative.
#
# No jq: not present in this environment, and a hook that fails open is worse than no
# hook at all. Scanning the raw payload is the fail-safe direction — over-blocking is
# recoverable, under-blocking is not.

INPUT=$(cat)

CMD=$(printf '%s' "$INPUT" \
  | sed -n 's/.*"command"[[:space:]]*:[[:space:]]*"\(\([^"\\]\|\\.\)*\)".*/\1/p')
SCAN="${CMD:-$INPUT}"

deny() {
  printf 'BLOCKED: %s\n\nSafe alternative: %s\n' "$1" "$2" >&2
  exit 2
}

match() { printf '%s' "$SCAN" | grep -qE "$1"; }

if match 'git[[:space:]]+push([[:space:]]+[^;|&]*)?[[:space:]]+(--force([^-]|$)|--force-with-lease|-f([[:space:]]|$))'; then
  deny "force push rewrites published history" \
       "push a commit that reverts, or have the human force-push deliberately"
fi

if match 'git[[:space:]]+reset[[:space:]]+[^;|&]*--hard'; then
  deny "git reset --hard discards uncommitted work with no recovery" \
       "git stash, or git reset --soft / --mixed to keep the changes"
fi

if match 'git[[:space:]]+clean[[:space:]]+-[a-zA-Z]*f'; then
  deny "git clean -f permanently deletes untracked files" \
       "git clean -n first to see what would go, then remove specific paths"
fi

if match 'git[[:space:]]+branch[[:space:]]+-D[[:space:]]+(main|master|develop)([[:space:]]|$)'; then
  deny "refusing to delete a primary branch" \
       "delete the feature branch instead, or ask the human"
fi

if match 'git[[:space:]]+checkout[[:space:]]+--[[:space:]]+\.([[:space:]]|$)'; then
  deny "git checkout -- . discards every unstaged change in the tree" \
       "restore specific files by path, or git stash first"
fi

if match 'git[[:space:]]+(filter-branch|filter-repo)'; then
  deny "history rewriting affects every clone of the repo" \
       "if a secret was committed, stop and tell the human — the key must be rotated regardless"
fi

exit 0
