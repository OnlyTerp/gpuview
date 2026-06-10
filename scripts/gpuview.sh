#!/usr/bin/env bash
# gpuview — GPU-native screen capture (DXGI Desktop Duplication) from WSL.
# Usage:
#   gpuview.sh frame [mon]                 one full frame -> prints WSL png path
#   gpuview.sh watch <secs> [mon] [minpx]  watch for changes; prints JSON events
#                                          (crops land in T:\gpuview\watch_<ts>)
#   gpuview.sh rebuild                     recompile the exe from src
set -euo pipefail

EXE_WIN='T:\gpuview\gpuview.exe'
EXE_WSL='/mnt/t/gpuview/gpuview.exe'
SRC="$(dirname "$0")/../src/gpuview.cs"
CSC='/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'

win2wsl() { sed -E 's|^([A-Za-z]):\\|/mnt/\L\1\E/|; s|\\|/|g'; }

rebuild() {
  mkdir -p /mnt/t/gpuview
  cp "$SRC" /mnt/t/gpuview/gpuview.cs
  "$CSC" /nologo /target:exe /platform:x64 /unsafe \
    /out:"$EXE_WIN" 'T:\gpuview\gpuview.cs'
  echo "built $EXE_WSL"
}

[ -x "$EXE_WSL" ] || rebuild >/dev/null

case "${1:-frame}" in
  frame)
    MON="${2:-0}"
    OUT="T:\\gpuview\\frame_$(date +%s).png"
    "$EXE_WSL" frame "$OUT" "mon=$MON"
    echo "$OUT" | win2wsl
    ;;
  watch)
    SECS="${2:-10}"; MON="${3:-0}"; MINPX="${4:-400}"
    DIR="T:\\gpuview\\watch_$(date +%s)"
    "$EXE_WSL" watch "$SECS" "$DIR" "$MINPX" "mon=$MON"
    ;;
  rebuild) rebuild ;;
  *) echo "usage: gpuview.sh frame [mon] | watch <secs> [mon] [minpx] | rebuild" >&2; exit 1 ;;
esac
