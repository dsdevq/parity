#!/usr/bin/env sh
# Claude Code Stop hook. Lints every spec changed in the working tree; in the implement phase it gates.
# The registries (#17) are checked from state, not from the diff: every module with a registry/ is checked in every
# phase, so committing first does not hide a stale one. In extract a stale registry never blocks (the extract agent
# cannot review it): the stale modules are named and the map phase is next. In map, specify and implement it blocks
# with REGISTRY-STALE until map has reviewed and stamped it. In map a module whose registry/ or evidence/ changed is
# checked without --gate: any error blocks, while unverified and unreferenced scenarios are warnings listed for the user
# (specs that reference them come in specify, lastVerified only after a run against the host). Any change outside legacy/<module>/registry/ and evidence/ (other than the kit's own files:
# legacy/hooks/, legacy/templates/ and the files directly in legacy/) is refused, the net for writes made from a shell.
# Exit 2 keeps the turn open with the findings shown to the agent.
set -u
bin="${PARITY_BIN:-parity}"
command -v "$bin" >/dev/null 2>&1 || { echo "parity: binary not found (set PARITY_BIN)" >&2; exit 0; }
phase=$(cat legacy/.phase 2>/dev/null | tr -d '[:space:]')
changed=$( (git diff --name-only; git diff --name-only --cached; git ls-files --others --exclude-standard) 2>/dev/null | sort -u)
status=0; out=""

if [ "$phase" = map ]; then
  outside=$(printf '%s\n' "$changed" | grep -vE '^legacy/[^/]+/(registry|evidence)/|^legacy/[^/]+$|^legacy/(hooks|templates)/|^$' | tr '\n' ' ' | sed 's/ *$//')
  if [ -n "$outside" ]; then echo "parity: map phase changed files outside legacy/<module>/registry/ and evidence/: $outside; map writes only the registries and evidence, revert the rest" >&2; exit 2; fi
fi
handoff=""; notes=""
case "$phase" in
  extract|map|specify|implement)
    touched=$(printf '%s\n' "$changed" | sed -nE 's#^(legacy/[^/]+)/(registry|evidence)/.*#\1#p' | sort -u)
    for d in legacy/*/registry; do
      [ -d "$d" ] || continue
      m=${d%/registry}
      if [ "$phase" = map ] && printf '%s\n' "$touched" | grep -qx "$m"; then
        r=$("$bin" registry check "$m" 2>&1); rc=$?
        out="$out
$r"
        [ $rc -eq 0 ] || status=2
        case "$r" in *"[warning]"*) notes="$notes
$r" ;; esac
        continue
      fi
      r=$("$bin" registry check "$m" 2>&1) || true
      case "$r" in *REGISTRY-STALE*) ;; *) continue ;; esac
      if [ "$phase" = extract ]; then handoff="$handoff $m"; else out="$out
$r"; status=2; fi
    done ;;
esac

specs=$(printf '%s\n' "$changed" | grep -E '^legacy/.+/spec/.+\.md$')
for s in $specs; do
  [ -f "$s" ] || continue
  if [ "$phase" = implement ]; then r=$("$bin" lint "$s" --gate ${PARITY_IMPLEMENT_DIR:+--implement "$PARITY_IMPLEMENT_DIR"} 2>&1); else r=$("$bin" lint "$s" 2>&1); fi
  rc=$?
  out="$out
$r"
  [ $rc -eq 0 ] || status=2
done
[ -z "$handoff" ] || echo "parity: the registry of$handoff is STALE against this extract; run the map phase next to review and stamp it" >&2
if [ $status -ne 0 ]; then echo "parity lint is red; fix before stopping:$out" >&2; exit 2; fi
[ -z "$notes" ] || echo "parity: registry warnings for the user (they do not block map):$notes" >&2
exit 0
