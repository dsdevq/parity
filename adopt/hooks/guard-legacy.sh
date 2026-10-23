#!/usr/bin/env sh
# Claude Code PreToolUse hook. During the specify and implement phases, any tool call that mentions the
# legacy project path is refused (exit 2 blocks the call and shows the message to the agent).
# Phase: legacy/.phase. Legacy root: PARITY_LEGACY_ROOT, else the directory of "project" in
# legacy/extract.config.json (read with jq when present, otherwise with sed). Fails CLOSED: if the root
# cannot be determined in a guarded phase, every matched tool call is refused until it is configured.
set -u
phase=$(cat legacy/.phase 2>/dev/null | tr -d '[:space:]')
case "$phase" in specify|implement) ;; *) exit 0 ;; esac
root="${PARITY_LEGACY_ROOT:-}"
if [ -z "$root" ] && [ -f legacy/extract.config.json ]; then
  if command -v jq >/dev/null 2>&1; then
    proj=$(jq -r '.project // empty' legacy/extract.config.json)
  else
    proj=$(sed -n 's/^[[:space:]]*"project"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' legacy/extract.config.json | head -1)
  fi
  [ -n "${proj:-}" ] && root=$(dirname "$proj")
fi
if [ -z "$root" ]; then
  echo "parity: phase '$phase' but the legacy root is unknown (set PARITY_LEGACY_ROOT or \"project\" in legacy/extract.config.json). Refusing the call rather than failing open." >&2
  exit 2
fi
input=$(cat)
# normalise slashes both ways so Windows and POSIX paths match
root_fwd=$(printf '%s' "$root" | sed 's#\\#/#g')
if printf '%s' "$input" | sed 's#\\\\#/#g; s#\\#/#g' | grep -qiF "$root_fwd"; then
  echo "parity: phase '$phase' - the legacy source is off limits. Work from legacy/<Module>/manifests/<Form>.md. If the manifest lacks what you need, that is a spec gap or an extractor hole: write it under Open questions or extractor-debt.md and stop." >&2
  exit 2
fi
exit 0
