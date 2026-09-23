#!/usr/bin/env bash
# Downloads the BtbN FFmpeg linux-x64 GPL *shared* build and copies the native .so into
# src/GimmeCapture/ffmpeg-lib/. Linux counterpart of scripts/ensure-ffmpeg-libs.ps1.
#
# FFmpeg 8.1 (libavcodec.so.62 / libavformat.so.62 / libavutil.so.60, the ABI FFmpeg.AutoGen 8.0.0.1
# binds to), pinned by URL AND SHA-256 to one exact build mirrored in this repository — see url below.
#
# BtbN's gpl-shared build statically links the external codecs (x264/x265/…) into libavcodec, so
# the av*/sw* libs have no external .so deps beyond glibc — no rpath/LD_LIBRARY_PATH needed.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="$root/src/GimmeCapture/ffmpeg-lib"
mkdir -p "$dest"

# Pinned to ONE exact build mirrored in this repository's `deps-ffmpeg-n8.1-20260922` prerelease (byte-identical
# to BtbN's "Latest Auto-Build (2026-09-22 13:18)"): BtbN's own "latest" asset is rebuilt continuously and its
# dated autobuild-* releases are pruned, so neither could be pinned. Move to a newer FFmpeg by mirroring the new
# build the same way and updating url + sha256 together (and the Windows script).
url="https://github.com/HouseAlwaysWin/GimmeCapture/releases/download/deps-ffmpeg-n8.1-20260922/ffmpeg-n8.1-20260922-linux64-gpl-shared.tar.xz"
sha256="3aa46ea020ea11039256e4d5baefbf6a639b1476fe1b1e952074332c77aea85c"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
archive="$tmp/ffmpeg.tar.xz"

echo "Downloading $url ..."
curl -fSL "$url" -o "$archive"

# The hash is the whole guarantee: a failed download (an HTML error page), a truncated file or a different build
# all stop here, before anything is extracted into the app.
if ! echo "$sha256  $archive" | sha256sum -c --status -; then
  echo "FFmpeg archive SHA-256 mismatch (expected $sha256). Refusing to use it." >&2
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
