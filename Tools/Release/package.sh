#!/usr/bin/env bash
# Packs a release player for download: Windows as .zip, Linux as .tar.gz (keeps the executable bit). The folders
# Unity marks as not to be shipped (*_DoNotShip, *_ButDontShipItWithYourGame) stay out. The archive holds one folder
# Infinitile-<version>-<platform>/. It fails when the player has no Burst library.
#   Tools/Release/package.sh <player folder> <windows-x64|linux-x64> <version> <output folder>
# Prints the archive's path. Runs on the GitHub runner (Ubuntu) and in Git Bash on Windows.
set -euo pipefail

if [ $# -ne 4 ]; then
  echo "usage: $0 <player folder> <windows-x64|linux-x64> <version> <output folder>" >&2
  exit 2
fi
player=$1 platform=$2 version=$3 out=$4
product=Infinitile
name="$product-$version-$platform"

case "$platform" in
  windows-x64) executable="$product.exe" ;;
  linux-x64) executable="$product.x86_64" ;;
  *)
    echo "unknown platform '$platform'" >&2
    exit 2
    ;;
esac
if [ ! -f "$player/$executable" ]; then
  echo "no $executable in $player" >&2
  exit 1
fi
# Without the Burst library the player runs every job as managed code, several times slower, and the build still
# reports success (a Burst cross-compile failure is only a warning). Windows: Plugins/x86_64/, Linux: Plugins/ or
# Plugins/x86_64/ depending on the Unity version.
case "$platform" in
  windows-x64) burst=lib_burst_generated.dll ;;
  linux-x64) burst=lib_burst_generated.so ;;
esac
if [ -z "$(find "$player/${product}_Data/Plugins" -name "$burst" -type f 2> /dev/null)" ]; then
  echo "no $burst under $player/${product}_Data/Plugins: Burst did not compile for $platform (see the Unity log)" >&2
  exit 1
fi

mkdir -p "$out"
out=$(cd "$out" && pwd)
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT

mkdir "$stage/$name"
for entry in "$player"/* "$player"/.[!.]*; do
  [ -e "$entry" ] || continue
  case "$(basename "$entry")" in
    *_DoNotShip | *_ButDontShipItWithYourGame) echo "left out: $(basename "$entry")" >&2 ;;
    *) cp -R "$entry" "$stage/$name/" ;;
  esac
done

case "$platform" in
  windows-x64)
    archive="$out/$name.zip"
    rm -f "$archive"
    if command -v zip > /dev/null; then
      (cd "$stage" && zip -q -r -9 "$archive" "$name")
    else # Git Bash without zip: Windows' own bsdtar writes zip archives
      "$SYSTEMROOT/System32/tar.exe" -a -c -f "$(cygpath -w "$archive")" -C "$(cygpath -w "$stage")" "$name"
    fi
    ;;
  linux-x64)
    archive="$out/$name.tar.gz"
    tarball="$stage/$name.tar"
    # Everything as 644/755 owned by root, the executable added with 755 (Windows file systems have no exec bit).
    tar --owner=0 --group=0 --numeric-owner --mode='u+rwX,go+rX,go-w' --exclude="$name/$executable" \
      -C "$stage" -cf "$tarball" "$name"
    tar --owner=0 --group=0 --numeric-owner --mode=0755 -C "$stage" -rf "$tarball" "$name/$executable"
    gzip -9 -c "$tarball" > "$archive"
    ;;
esac
echo "$archive"
