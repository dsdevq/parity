#!/usr/bin/env sh
# Publishes a self-contained single-file binary. No .NET runtime needed on the target machine.
#   bin/publish.sh            -> dist/<rid>/parity for the current OS
#   bin/publish.sh win-x64    -> dist/win-x64/parity.exe   (the usual work laptop)
set -e
here="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:-$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)}"
dotnet publish "$here/src/Parity" -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -p:PublishReadyToRun=true -o "$here/dist/$rid" -nologo -v q
ls -la "$here/dist/$rid" | grep -i parity
