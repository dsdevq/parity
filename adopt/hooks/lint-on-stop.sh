#!/usr/bin/env sh
# Claude Code Stop hook. Lints every spec changed in the working tree; in the implement phase it gates.
# The registries (#17): in the extract phase a module whose manifests changed and that has a registry/ is checked
# (a pass is not done until the registry is reviewed and stamped); in the map phase every module whose registry/ or
# evidence/ changed is checked with --gate, and any change outside legacy/<module>/registry/ and evidence/ (other than
# the kit's own files: legacy/hooks/, legacy/templates/ and the files directly in legacy/) is refused, the net for writes made from a shell.
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
case "$phase" in
  map) modules=$(printf '%s\n' "$changed" | sed -nE 's#^(legacy/[^/]+)/(registry|evidence)/.*#\1#p' | sort -u) ;;
  extract) modules=$(printf '%s\n' "$changed" | sed -nE 's#^(legacy/[^/]+)/manifests/.*#\1#p' | sort -u) ;;
  *) modules= ;;
esac
for m in $modules; do
  [ -d "$m/registry" ] || continue
  if [ "$phase" = map ]; then r=$("$bin" registry check "$m" --gate 2>&1); else r=$("$bin" registry check "$m" 2>&1); fi
  rc=$?
  out="$out
$r"
  [ $rc -eq 0 ] || status=2
done

specs=$(printf '%s\n' "$changed" | grep -E '^legacy/.+/spec/.+\.md$')
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
