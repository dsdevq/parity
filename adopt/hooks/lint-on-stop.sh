#!/usr/bin/env sh
# Claude Code Stop hook. Lints every spec changed in the working tree; in the implement phase it gates.
# Exit 2 keeps the turn open with the findings shown to the agent.
set -u
bin="${PARITY_BIN:-parity}"
command -v "$bin" >/dev/null 2>&1 || { echo "parity: binary not found (set PARITY_BIN)" >&2; exit 0; }
phase=$(cat legacy/.phase 2>/dev/null | tr -d '[:space:]')
specs=$( (git diff --name-only; git diff --name-only --cached; git ls-files --others --exclude-standard) 2>/dev/null | grep -E '^legacy/.+/spec/.+\.md$' | sort -u)
[ -n "$specs" ] || exit 0
status=0; out=""
for s in $specs; do
  [ -f "$s" ] || continue
  if [ "$phase" = implement ]; then r=$("$bin" lint "$s" --gate ${PARITY_IMPLEMENT_DIR:+--implement "$PARITY_IMPLEMENT_DIR"} 2>&1); else r=$("$bin" lint "$s" 2>&1); fi
  rc=$?
  out="$out
$r"
  [ $rc -eq 0 ] || status=2
done
if [ $status -ne 0 ]; then echo "parity lint is red; fix before stopping:$out" >&2; exit 2; fi
exit 0
