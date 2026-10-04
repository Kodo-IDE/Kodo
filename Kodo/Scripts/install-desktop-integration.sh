#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"

applications_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
icons_dir="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
desktop_target="$applications_dir/kodo.desktop"
icon_target="$icons_dir/kodo.png"

uninstall() {
  local removed=0
  if [ -f "$desktop_target" ]; then rm -f "$desktop_target"; echo "removed $desktop_target"; removed=1; fi
  if [ -f "$icon_target" ]; then rm -f "$icon_target"; echo "removed $icon_target"; removed=1; fi
  if [ "$removed" -eq 0 ]; then echo "nothing to remove"; fi
  refresh_caches
  exit 0
}

refresh_caches() {
  if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$applications_dir" >/dev/null 2>&1 || true
  fi
  if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -t "$icons_dir" >/dev/null 2>&1 || true
  fi
}

find_asset() {
  local name="$1"
  local candidate
  for candidate in \
    "$repo_root/Kodo/Assets/$name" \
    "$script_dir/../Assets/$name" \
    "$script_dir/$name" \
    "$repo_root/Assets/$name"; do
    if [ -f "$candidate" ]; then printf '%s\n' "$candidate"; return 0; fi
  done
  return 1
}

find_binary() {
  if [ -n "${1:-}" ] && [ -x "$1" ]; then printf '%s\n' "$1"; return 0; fi
  local candidate
  for candidate in \
    "$script_dir/../Kodo" \
    "$script_dir/Kodo" \
    "$repo_root/Kodo/Source/bin/Debug/net10.0/Kodo" \
    "$repo_root/Kodo/Source/bin/Release/net10.0/Kodo"; do
    if [ -x "$candidate" ]; then printf '%s\n' "$candidate"; return 0; fi
  done
  if command -v Kodo >/dev/null 2>&1; then command -v Kodo; return 0; fi
  return 1
}

if [ "${1:-}" = "--uninstall" ]; then
  uninstall
fi

if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ]; then
  echo "usage: $(basename "$0") [path-to-kodo-binary]"
  echo "       $(basename "$0") --uninstall"
  exit 0
fi

desktop_source="$(find_asset kodo.desktop)" || { echo "error: kodo.desktop not found" >&2; exit 1; }
icon_source="$(find_asset kodo-logo.png)" || { echo "error: kodo-logo.png not found" >&2; exit 1; }
kodo_binary="$(find_binary "${1:-}")" || {
  echo "error: could not locate the Kodo binary; pass it as the first argument" >&2
  exit 1
}

kodo_binary="$(cd -- "$(dirname -- "$kodo_binary")" && pwd)/$(basename -- "$kodo_binary")"

mkdir -p "$applications_dir" "$icons_dir"

sed \
  -e "s|^Exec=.*|Exec=$kodo_binary %F|" \
  -e "s|^Icon=.*|Icon=kodo|" \
  -e "s|^Path=.*|Path=$(dirname -- "$kodo_binary")|" \
  "$desktop_source" > "$desktop_target"
chmod 0644 "$desktop_target"

if command -v convert >/dev/null 2>&1; then
  convert "$icon_source" -resize 256x256 "$icon_target"
elif command -v magick >/dev/null 2>&1; then
  magick "$icon_source" -resize 256x256 "$icon_target"
else
  cp "$icon_source" "$icon_target"
fi
chmod 0644 "$icon_target"

refresh_caches

echo "installed $desktop_target"
echo "installed $icon_target"
echo "launcher points at $kodo_binary"
echo "done"
