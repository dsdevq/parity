#!/usr/bin/env sh
# Claude Code PreToolUse hook. During the specify and implement phases a tool call may only touch the
# allowed roots: the consuming repo minus the legacy source root, plus "guard.allowRoots" from
# legacy/extract.config.json (relative entries are relative to that file). Exit 2 blocks the call and shows
# the one-line reason to the agent; every denial is also appended to legacy/.guard-denials.log
# (date, phase, tool, reason; tab separated).
#   path tools (Read Edit Write MultiEdit NotebookEdit Grep Glob): every path, resolved against the hook's cwd, must be allowed
#   shell tools (Bash PowerShell Monitor): every path-like token must be allowed, no cd may leave the allowed roots;
#     a cd is followed when it is the first word of a command (after ; | & ( ) { } or a newline outside quotes, a
#     reserved word such as then or do, or a wrapper such as command or VAR=x); the quoted argument of sh -c,
#     bash -c, powershell -command or eval is a command of its own, as is a ( ) subshell, so its cd does not
#     outlive it; any other cd word before a path refuses the call
#   mcp__* tools: refused when the name matches the code-index deny-list below or "guard.denyTools"; every string
#     value in the input that contains / or \, or is . .. or ~, must be an allowed path
#   any other tool, or any input that cannot be decided: refused (fail closed)
# After those checks the legacy root is still looked for as a whole path anywhere in the input (second net).
# The hook works from CLAUDE_PROJECT_DIR (else its cwd), so a session cwd below the repo root cannot hide the phase.
# Shell tokens are judged per path segment, with \ both as an escape and as a separator: a segment a wildcard or a
# quote/backslash splice can turn into '..' (.''./x  .\./x  .?/x  .*/x  [.]*), a '..' after a wildcard segment and
# brace expansion are refused, since the guard cannot see where they resolve. A wildcard segment cuts the path checked.
# Phase: legacy/.phase. Legacy root: PARITY_LEGACY_ROOT, else the directory of "project" in the config.
# jq is required to read tool input; without it a restricted phase refuses every call. The sed fallback below
# only finds the legacy root (and a label for the log) so the refusal can say what is wrong.
set -u
set -f
cd "${CLAUDE_PROJECT_DIR:-.}" 2>/dev/null || { printf '%s\n' "parity: CLAUDE_PROJECT_DIR '${CLAUDE_PROJECT_DIR:-}' is not a directory, so the phase cannot be read. Refusing the call rather than failing open" >&2; exit 2; }
phase=$(cat legacy/.phase 2>/dev/null | tr -d '[:space:]')
case "$phase" in specify|implement) ;; *) exit 0 ;; esac
input=$(cat)
tool=$(printf '%s' "$input" | sed -n 's/.*"tool_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
cfg=legacy/extract.config.json
log=legacy/.guard-denials.log
# glob patterns (case-insensitive, ? is any one character) for MCP tools that read or index source code
default_deny='*code?index* *codeindex* *code?graph* *codegraph* *code?search* *codesearch* *codebase* *repo?map* *repomap* *symbol* *semantic?search* *serena* *sourcegraph* *tree?sitter* *treesitter* *ast?grep* *lsp* *language?server* *roslyn* *ctags* *cocoindex* *gitnexus* *graphify* *claude?context*'

lc() { printf '%s' "$1" | tr 'A-Z' 'a-z'; }
fwd() { printf '%s' "$1" | sed 's#\\\\#/#g; s#\\#/#g'; }

# segs PATH: refuse a segment that a wildcard or a quote/backslash splice can turn into '..', and a '..' after a wildcard
segs() {
  _w=0; _ifs=$IFS; IFS=/
  for _g in $1; do
    IFS=$_ifs
    case $_g in *[\'\"\`\\]*) _s=$(printf '%s' "$_g" | tr -d "\"'\`\\\\") ;; *) _s=$_g ;; esac
    # shellcheck disable=SC2194,SC2254
    case $_g in
      *[\'\"\`\\\?\*\[]*) case $_s in [.[]*) case .. in $_s) deny "the path '$1' has a segment ('$_g') that can stand for '..' and cannot be resolved, so it may be outside the allowed roots" ;; esac ;; esac ;;
    esac
    case $_s in ..) [ "$_w" = 0 ] || deny "the path '$1' climbs with '..' after a wildcard and cannot be resolved, so it may be outside the allowed roots" ;; esac
    case $_s in *[\?\*\[]*) _w=1 ;; esac
  done
  IFS=$_ifs
}

# clean TOKEN: drop the quotes around a shell token, check its segments as written and with \ as a separator; result in C
clean() {
  _t=$1
  while :; do case $_t in [\'\"\`]*) _t=${_t#?} ;; *) break ;; esac; done
  while :; do case $_t in *[\'\"\`]) _t=${_t%?} ;; *) break ;; esac; done
  segs "$_t"
  case $_t in *\\*) segs "$(fwd "$_t")" ;; esac
  C=$_t
}

# deny REASON [HINT]: log, tell the agent in one line, block
deny() {
  _hint=${2-"Work from legacy/<Module>/manifests/<Form>.md; if the manifest lacks what you need, write it under Open questions or extractor-debt.md and stop."}
  _reason=$(printf '%s' "$1" | tr '\t\r\n' '   ')
  _d=$(date -u +%Y-%m-%dT%H:%M:%SZ 2>/dev/null) || _d=unknown
  printf '%s\t%s\t%s\t%s\n' "$_d" "$phase" "${tool:--}" "$_reason" >> "$log" 2>/dev/null || true
  printf '%s\n' "parity: phase '$phase' - $_reason${_hint:+. $_hint}" >&2
  exit 2
}

# norm PATH BASE: lower-case, forward-slash, absolute, no . or .. segments, drive letters as /c; result in N
norm() {
  _p=$(fwd "$1")
  _p=$(lc "$_p")
  case $_p in
    [a-z]:/*|[a-z]:) _p="/${_p%%:*}${_p#?:}" ;;
    /mnt/[a-z]/*|/mnt/[a-z]) _p="/${_p#/mnt/}" ;;
    /cygdrive/[a-z]/*|/cygdrive/[a-z]) _p="/${_p#/cygdrive/}" ;;
  esac
  case $_p in /*) ;; *) _p="$2/$_p" ;; esac
  _out=
  _ifs=$IFS; IFS=/
  for _seg in $_p; do
    case $_seg in
      ''|.) ;;
      ..) _out=${_out%/*} ;;
      *) _out="$_out/$_seg" ;;
    esac
  done
  IFS=$_ifs
  N=${_out:-/}
}

# in_list PATH LIST: PATH is a listed root or below one (LIST is newline separated)
in_list() {
  _q=$1; _ifs=$IFS; IFS='
'
  set -- $2
  IFS=$_ifs
  for _r in "$@"; do
    case $_q in "$_r"|"$_r"/*) return 0 ;; esac
  done
  return 1
}

# allowed PATH (already normalised)
allowed() {
  case $1 in /dev/null|/dev/stdin|/dev/stdout|/dev/stderr|/dev/tty) return 0 ;; esac
  case $1 in "$legacy"|"$legacy"/*) return 1 ;; esac
  in_list "$1" "$allow"
}

# need_allowed PATH BASE WHAT: resolve and deny when outside
need_allowed() {
  norm "$1" "$2"
  allowed "$N" && return 0
  case $N in "$legacy"|"$legacy"/*) deny "$3 '$N' is outside the allowed roots (it is under the legacy source root)" ;; esac
  deny "$3 '$N' is outside the allowed roots (the consuming repo plus guard.allowRoots in $cfg)"
}

# --- the legacy root --------------------------------------------------------------------------------------
repo=$(pwd)
repo=$(lc "$(fwd "$repo")")
root="${PARITY_LEGACY_ROOT:-}"
rootbase=$repo
if [ -z "$root" ] && [ -f "$cfg" ]; then
  if command -v jq >/dev/null 2>&1; then
    proj=$(jq -r '.project // empty' "$cfg" 2>/dev/null)
  else
    proj=$(sed -n 's/^[[:space:]]*"project"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$cfg" | head -1)
  fi
  if [ -n "${proj:-}" ]; then
    proj=$(fwd "$proj")
    root=$(dirname "$proj")
    rootbase=$repo/legacy
  fi
fi
[ -n "$root" ] || deny "the legacy root is unknown (set PARITY_LEGACY_ROOT or \"project\" in $cfg). Refusing the call rather than failing open" ""
rootl=$(lc "$(fwd "$root")")
norm "$rootl" "$rootbase"
legacy=$N

# --- the second net: the legacy root named anywhere in the input -------------------------------------------
lowin=$(printf '%s' "$input" | tr 'A-Z' 'a-z' | sed 's#\\\\#/#g; s#\\#/#g')
needles=$legacy
case $rootl in /*|[a-z]:/*) needles="$needles
$rootl" ;; esac
case $legacy in /[a-z]/*) needles="$needles
$(printf '%s' "$legacy" | sed 's#^/\([a-z]\)/#\1:/#')" ;; esac
second_net() {
  _ifs=$IFS; IFS='
'
  set -- $needles
  IFS=$_ifs
  for _n in "$@"; do
    [ -n "$_n" ] && [ "$_n" != / ] && printf '%s\n' "$lowin" | awk -v n="$_n" -v q="'" '
      { s = $0; while ((i = index(s, n)) > 0) { c = substr(s, i + length(n), 1); if (c == "" || c == "/" || c == "\"" || c == q || c == "`" || c == " " || c == "\t" || c == "\r") { f = 1; exit } s = substr(s, i + 1) } }
      END { exit !f }' &&
      deny "the legacy source is off limits (the call names '$_n')"
  done
  return 0
}

# --- everything below needs the parsed input --------------------------------------------------------------
if ! command -v jq >/dev/null 2>&1; then
  second_net
  deny "jq is required to check tool input in a restricted phase and was not found" "Install it (apt install jq, brew install jq, winget install jqlang.jq or scoop install jq), or set legacy/.phase to extract"
fi
jget() { printf '%s' "$input" | jq -r "$1" 2>/dev/null | tr -d '\r'; }
printf '%s' "$input" | jq -e 'type == "object"' >/dev/null 2>&1 || deny "the hook input is not a JSON object, so it cannot be checked"
tool=$(jget '.tool_name // empty | strings')
[ -n "$tool" ] || deny "the hook input has no tool_name, so it cannot be checked"

# allowed roots: the repo, plus guard.allowRoots (relative to the config file)
allow=$repo
deny_extra=
if [ -f "$cfg" ]; then
  roots=$(jq -r '(.guard // {} | .allowRoots // [])[] | strings' "$cfg" 2>/dev/null) || deny "guard.allowRoots in $cfg is not a list of strings" ""
  deny_extra=$(jq -r '(.guard // {} | .denyTools // [])[] | strings' "$cfg" 2>/dev/null) || deny "guard.denyTools in $cfg is not a list of strings" ""
  roots=$(printf '%s' "$roots" | tr -d '\r')
  _ifs=$IFS; IFS='
'
  set -- $roots
  IFS=$_ifs
  for r in "$@"; do
    norm "$r" "$repo/legacy"
    allow="$allow
$N"
  done
  deny_extra=$(lc "$(printf '%s' "$deny_extra" | tr -d '\r')")
fi

cwd=$(jget '.cwd // empty | strings')
[ -n "$cwd" ] || cwd=$repo
norm "$cwd" "$repo"
cwd=$N

case $tool in
  Read|Edit|Write|MultiEdit|NotebookEdit)
    need_allowed "$cwd" "$repo" "the working directory"
    p=$(jget '(.tool_input.file_path // .tool_input.notebook_path) // empty | strings')
    [ -n "$p" ] || deny "no path in the tool input to check"
    need_allowed "$p" "$cwd" "the path"
    ;;
  Grep|Glob)
    need_allowed "$cwd" "$repo" "the working directory"
    base=$(jget '.tool_input.path // empty | strings')
    if [ -n "$base" ]; then need_allowed "$base" "$cwd" "the search path"; base=$N; else base=$cwd; fi
    # a glob may climb out of the search path (../ or an absolute prefix): check its literal prefix
    if [ "$tool" = Glob ]; then g=$(jget '.tool_input.pattern // empty | strings'); else g=$(jget '.tool_input.glob // empty | strings'); fi
    segs "$g"
    alts=$(printf '%s\n' "$g" | awk '{ while (match($0, /\{[^{}]*\}/)) { print substr($0, RSTART + 1, RLENGTH - 2); $0 = substr($0, RSTART + RLENGTH) } }' | tr ',' '\n')
    _ifs=$IFS; IFS='
'
    set -- $alts
    IFS=$_ifs
    for a in "$@"; do
      # shellcheck disable=SC2254,SC2194
      case $a in *..*) ;; *) case .. in $a) ;; *) continue ;; esac ;; esac
      deny "the glob '$g' has a brace alternative ('$a') that can stand for '..' and cannot be resolved, so it may be outside the allowed roots"
    done
    g=$(printf '%s' "$g" | sed 's#[^/]*[*?[{].*##')
    [ -z "$g" ] || need_allowed "$g" "$base" "the glob prefix"
    ;;
  Bash|PowerShell|Monitor)
    need_allowed "$cwd" "$repo" "the working directory"
    cmd=$(jget '.tool_input.command // empty | strings')
    [ -n "$cmd" ] || deny "no command in the tool input to check"
    cmd=$(printf '%s' "$cmd" | tr 'A-Z' 'a-z' | tr '\n\t' ';;')
    [ "$tool" = PowerShell ] && cmd=$(fwd "$cmd")
    printf '%s' "$cmd" | grep -qE '\{[^ }]*(,|\.\.)[^ }]*\}' && deny "the command uses brace expansion, which cannot be resolved, so it may reach outside the allowed roots"
    ps=0; [ "$tool" = PowerShell ] && ps=1
    cmd=$(printf '%s\n' "$cmd" | awk -v q="'" -v ps="$ps" '{
      o = ""
      for (i = 1; i <= length($0); i++) {
        c = substr($0, i, 1)
        if (c == "\\" && (v != "" ? v : w) != q) { o = o c substr($0, i + 1, 1); i++; continue }
        if (w != "" && !nest) { if (c == w) w = ""; else if (c ~ /[;|&(){}]/) c = " " }
        else if (v != "") { if (c == v) v = ""; else if (c ~ /[;|&(){}]/) c = " " }
        else if (nest && c == w) { w = ""; nest = 0; c = " ) " }
        else if (c == q || c == "\"") {
          if (nest) v = c
          else {
            w = c; t = substr(o, length(o) - 80)
            if (t ~ /(^|[ ;(\/])(sh|bash|zsh|dash|ksh|powershell|pwsh)(\.exe)? +(-[a-z]+ +)*-[a-z]*c(ommand)? *$/ || t ~ /(^|[ ;(])eval *$/) { nest = 1; c = " ( " }
          }
        }
        else if (c ~ /[;|&{}]/ || (ps && c ~ /[()]/)) c = " ; "
        else if (c == "(" || c == ")") c = " " c " "
        if (c ~ /^[<>,]$/) c = " "
        else if (c == "=") c = "= "
        o = o c
      }
      print o
    }')
    set -- $cmd
    at=1; cw=; stack=
    while [ $# -gt 0 ]; do
      clean "$1"; t=$C; shift
      case $t in
        ';') at=1; continue ;;
        '(') stack="$cwd
$stack"; at=1; continue ;;
        ')') [ -z "$stack" ] || { cwd=${stack%%
*}; stack=${stack#*
}; }; at=1; continue ;;
      esac
      w=$at; at=0
      if [ "$w" = 1 ]; then
        case $t in then|do|else|elif|if|while|until|!|time|command|builtin|env|exec|[a-z_]*=) at=1; continue ;; esac
        cw=$t
      fi
      case $w$t in
        0cd|0chdir|0pushd|0set-location|0sl|0push-location)
          case $cw in echo|git|printf) ;; *)
            [ $# -gt 0 ] && clean "$1" && case $C in
              */*|*\\*|.|..|~*|-) deny "the directory change '$t $C' is not the first word of a command, so the guard cannot follow it and later relative paths may be outside the allowed roots" ;;
            esac ;;
          esac
          ;;
        1cd|1chdir|1pushd|1set-location|1sl|1push-location)
          tgt=
          while [ $# -gt 0 ]; do
            case $1 in
              \;|\(|\)) break ;;
              -) tgt=-; shift; break ;;
              -*:*) tgt=${1#*:}; shift; break ;;
              -[a-z]*[./\\]*) tgt=${1#-?}; shift; break ;;
              -*) shift ;;
              *) tgt=$1; shift; break ;;
            esac
          done
          [ -n "$tgt" ] && clean "$tgt" && tgt=$C
          [ -n "$tgt" ] || deny "a directory change without a target goes to the home directory, which is outside the allowed roots"
          case $tgt in
            -|~*|*'$'*|*%*%*) deny "the directory change to '$tgt' cannot be resolved, so it may leave the allowed roots" ;;
          esac
          norm "$tgt" "$cwd"
          allowed "$N" || deny "the directory change to '$N' leaves the allowed roots"
          cwd=$N
          continue
          ;;
      esac
      case $t in
        -[a-z][a-z]:/*|-[a-z][a-z]:) t=${t#-?} ;;
        -[a-z]*:*) t=${t#*:} ;;
        -[a-z]?*) t=${t#-?} ;;
      esac
      case $t in \\*) ;; *\\*) t=$(fwd "$t") ;; esac
      case $t in
        ~*) deny "the path '$t' (the home directory) is outside the allowed roots" ;;
        *'$'*/*|*%*%*/*|*/*'$'*|*/*%*%*) deny "the path '$t' cannot be resolved (shell expansion), so it may be outside the allowed roots" ;;
        */*|.|..|[a-z]:) ;;
        *) continue ;;
      esac
      t=$(printf '%s' "$t" | sed 's#[^/]*[*?[].*##')
      [ -n "$t" ] || continue
      need_allowed "$t" "$cwd" "the path"
    done
    ;;
  mcp__*)
    name=$(lc "$tool")
    patterns="$default_deny
$deny_extra"
    _ifs=$IFS; IFS='
 '
    set -- $patterns
    IFS=$_ifs
    for pat in "$@"; do
      [ -n "$pat" ] || continue
      # shellcheck disable=SC2254
      case $name in $pat) deny "'$tool' is a code-index tool (matches '$pat'; guard.denyTools in $cfg extends the list) and could read the legacy source" ;; esac
    done
    vals=$(jget '.tool_input | .. | strings | select(test("[/\\\\]") or test("^(\\.\\.?|~)$")) | gsub("[\r\n]+"; " ")')
    _ifs=$IFS; IFS='
'
    set -- $vals
    IFS=$_ifs
    for v in "$@"; do
      case $v in \~|\~/*|\~\\*) deny "the path '$v' (the home directory) is outside the allowed roots" ;; esac
      need_allowed "$v" "$cwd" "the path"
    done
    ;;
  *) deny "unrecognised tool '$tool' in a restricted phase" "Only path tools, shell tools and MCP tools are checked; narrow the hook matcher or extend the guard" ;;
esac

second_net
exit 0
