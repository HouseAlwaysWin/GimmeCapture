#!/usr/bin/env bash
# Downloads the BtbN FFmpeg linux-x64 GPL *shared* build and copies the native .so into
# src/GimmeCapture/ffmpeg-lib/. Linux counterpart of scripts/ensure-ffmpeg-libs.ps1.
#
# Pinned to BtbN's non-pruned "latest" tag, n8.1 asset (FFmpeg 8.1 -> libavcodec.so.62 /
# libavformat.so.62 / libavutil.so.60, the ABI FFmpeg.AutoGen 8.0.0.1 binds to). Like the
# Windows script we validate FUNCTIONALLY (archive size + expected 8.x .so present) rather than
# pinning a brittle hash, because BtbN rebuilds the "latest" asset on 8.1.x point releases.
#
# BtbN's gpl-shared build statically links the external codecs (x264/x265/…) into libavcodec, so
# the av*/sw* libs have no external .so deps beyond glibc — no rpath/LD_LIBRARY_PATH needed.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="$root/src/GimmeCapture/ffmpeg-lib"
mkdir -p "$dest"

url="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-linux64-gpl-shared-8.1.tar.xz"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
archive="$tmp/ffmpeg.tar.xz"

echo "Downloading $url ..."
curl -fSL "$url" -o "$archive"

# Sanity: a real archive is tens of MB. Anything tiny is a failed download (e.g. an HTML error page).
size=$(stat -c%s "$archive")
if [ "$size" -lt 20000000 ]; then
  echo "FFmpeg archive looks wrong: only $size bytes (expected tens of MB). Download likely failed." >&2
  exit 1
fi

tar -xf "$archive" -C "$tmp"
libdir="$(find "$tmp" -type d -name lib | head -1)"
[ -n "$libdir" ] || { echo "Could not find lib/ folder in extracted archive." >&2; exit 1; }

# Copy each major-version symlink (libavcodec.so.62 …) dereferenced into a flat real file,
# which is exactly the name FFmpeg.AutoGen dlopen()s from ffmpeg.RootPath.
copied=0
for f in "$libdir"/lib*.so.*; do
  base="$(basename "$f")"
  if [[ "$base" =~ ^lib.*\.so\.[0-9]+$ ]] && [ -L "$f" ]; then
    cp -L "$f" "$dest/$base"
    echo "Copied $base"
    copied=$((copied + 1))
  fi
done

# Validate the FFmpeg 8.x ABI the app binds to is present (catches a wrong/changed build).
for name in libavcodec.so.62 libavformat.so.62 libavutil.so.60; do
  if [ ! -f "$dest/$name" ]; then
    echo "Expected FFmpeg 8.x lib '$name' not found after copy (wrong/changed build)." >&2
    exit 1
  fi
done

echo "Done. Copied $copied FFmpeg .so into $dest"
