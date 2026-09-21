#!/usr/bin/env bash
# Copyright 2026 Charles Lee
# SPDX-License-Identifier: PolyForm-Small-Business-1.0.0
#
# Builds FFmpeg 7.1 as LGPL shared libraries for osx-arm64, self-contained.
#
# WHY THIS EXISTS
# ===============
# Every other RID takes a pinned, sha256-verified LGPL artifact from BtbN.
# BtbN builds no macOS, so scripts/fetch-ffmpeg.cs copies from an installed
# Homebrew ffmpeg@7 keg instead. That works on a developer's own machine and
# nowhere else:
#
#   - The keg is GPL-3 (x264, x265), against ADR-0046's LGPL-only choice.
#   - Its dylibs reference libvpx, libsoxr and friends at absolute paths inside
#     the Homebrew prefix, so they load only on a machine that has Homebrew and
#     that formula. This is why publish.yml skips the osx RIDs and the package
#     ships no macOS natives.
#   - There is no pinned artifact, so runtime-manifest.json carries no sha256
#     for macOS and the version floats with whatever Homebrew shipped that week.
#
# This build answers all three. Nothing external is linked, so there is nothing
# to resolve outside the folder the dylibs sit in.
#
# WHAT IT PRODUCES
# ================
# out/
#   libavcodec.61.dylib  libavformat.61.dylib  libavutil.59.dylib
#   libavdevice.61.dylib libavfilter.10.dylib
#   libswscale.8.dylib   libswresample.5.dylib
#   SHA256SUMS
#   BUILD-INFO.txt          <- configure line and versions
#
# The soname-versioned names match what scripts/fetch-ffmpeg.cs already expects
# for osx-arm64, so the consumer side needs no new naming rules.
#
# LIBRARIES ONLY. No ffmpeg or ffprobe binary, deliberately. The tools exist in
# this repo for generate-test-corpus.cs, whose fixtures name libopenh264,
# libkvazaar, libvpx-vp9, libaom-av1 and libx264. Those are external encoders
# that --disable-autodetect excludes on purpose, so a tool built here could not
# generate the corpus and shipping one would only look like it could.
# generate-test-corpus.cs already prefers a PATH or Homebrew ffmpeg on macOS
# (FindFfmpeg), and that stays the arrangement. The corpus is a develop-time
# concern; these dylibs are the runtime.
#
# RUN IT
#   scripts/build-ffmpeg-macos.sh [output-dir]
# Needs: macOS on Apple silicon, Xcode command line tools, nasm, pkg-config.

set -euo pipefail

# ── Pin ──────────────────────────────────────────────────────────────────────
# A release tag, not a branch. The tarball for a tag is immutable, so the
# checksums below stay meaningful. Moving this means regenerating every osx
# sha256 in scripts/runtime-manifest.json and the build identity in
# THIRD-PARTY-NOTICES.md, which is the LGPL corresponding-source pointer.
FFMPEG_TAG="n7.1.5"
FFMPEG_REPO="https://github.com/FFmpeg/FFmpeg.git"

OUT_DIR="${1:-$(pwd)/out}"
WORK_DIR="${TMPDIR:-/tmp}/frameflow-ffmpeg-build"

echo "FFmpeg $FFMPEG_TAG -> $OUT_DIR"

# ── Preconditions ────────────────────────────────────────────────────────────
if [ "$(uname -s)" != "Darwin" ]; then
    echo "error: macOS only (this is uname -s = $(uname -s))." >&2
    exit 1
fi

# arm64 only, deliberately. macos-latest runners are Apple silicon, and an
# x86_64 dylib would need a cross-compile with a separate SDK. Every Mac sold
# since 2020 is arm64. osx-x64 stays absent from the package rather than being
# shipped half-built: nuget/FrameFlow.Native.Runtime.csproj refuses a partial
# RID, and absent is a supported state there.
if [ "$(uname -m)" != "arm64" ]; then
    echo "error: this script builds osx-arm64 and this host is $(uname -m)." >&2
    exit 1
fi

for tool in git nasm pkg-config otool; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "error: '$tool' not on PATH. brew install nasm pkg-config" >&2
        exit 1
    }
done

# ── Source ───────────────────────────────────────────────────────────────────
mkdir -p "$WORK_DIR"
SRC_DIR="$WORK_DIR/FFmpeg"

if [ -d "$SRC_DIR/.git" ]; then
    echo "==> Reusing $SRC_DIR"
    git -C "$SRC_DIR" fetch --depth 1 origin "refs/tags/$FFMPEG_TAG:refs/tags/$FFMPEG_TAG" --force
    git -C "$SRC_DIR" checkout --force "$FFMPEG_TAG"
else
    echo "==> Cloning FFmpeg $FFMPEG_TAG"
    git clone --depth 1 --branch "$FFMPEG_TAG" "$FFMPEG_REPO" "$SRC_DIR"
fi

BUILD_DIR="$WORK_DIR/build"
PREFIX="$WORK_DIR/prefix"
rm -rf "$BUILD_DIR" "$PREFIX"
mkdir -p "$BUILD_DIR"

# ── Configure ────────────────────────────────────────────────────────────────
# --disable-autodetect is what makes this reproducible. Without it configure
# links whatever happens to be installed on the build machine, so a runner with
# Homebrew present would silently produce a different binary than a clean one,
# and the sha256 in runtime-manifest.json would mean nothing.
#
# Consequences worth naming, because they are chosen rather than incidental:
#
#   --disable-gpl --disable-nonfree   ADR-0046's LGPL-only requirement. Also
#                                     drops x264 and x265, which the Homebrew
#                                     keg links and which made it GPL-3.
#
#   --disable-version3                Keeps this at LGPL-2.1. The BtbN builds
#                                     the other RIDs use carry enable-version3
#                                     and link Apache-2.0 and LGPL-3 components
#                                     (libaribb24, libopencore-amr, gmp), which
#                                     is why the package declares
#                                     "LGPL-3.0-or-later AND Apache-2.0". With
#                                     autodetect off none of those are present
#                                     here anyway; this states it rather than
#                                     leaving it to a configure default.
#                                     THIRD-PARTY-NOTICES.md says to read the
#                                     licence off the built binary rather than
#                                     off flags. With no ffmpeg binary to run,
#                                     BUILD-INFO.txt records configure's own
#                                     enabled-component report instead, which is
#                                     the same information from the same build.
#
#   --enable-videotoolbox             Not optional. H264EncoderOptions resolves
#                                     h264_videotoolbox on macOS, and hardware
#                                     decode (ADR-0033) probes it. Autodetect
#                                     off would remove it.
#
#   --install-name-dir=@loader_path   Each dylib refers to its siblings relative
#                                     to its own location, so the folder is
#                                     self-contained wherever it lands. This is
#                                     what fetch-ffmpeg.cs currently patches in
#                                     afterwards with install_name_tool; doing
#                                     it at link time means there is nothing to
#                                     patch and nothing to re-sign.
echo "==> Configuring"
cd "$BUILD_DIR"

CONFIGURE_ARGS=(
    --prefix="$PREFIX"
    --enable-shared
    --disable-static
    --disable-autodetect
    --disable-gpl
    --disable-nonfree
    --disable-version3
    --disable-doc
    --disable-debug
    --enable-pic
    --enable-videotoolbox
    --enable-audiotoolbox
    --disable-programs
    --arch=arm64
    --install-name-dir=@loader_path
)

"$SRC_DIR/configure" "${CONFIGURE_ARGS[@]}"

echo "==> Building"
make -j"$(sysctl -n hw.ncpu)"
make install

# ── Collect ──────────────────────────────────────────────────────────────────
# Copy the soname-versioned real files, not the unversioned symlinks. .NET's
# NativeLibrary.Load follows a symlink fine, but the names below are what
# fetch-ffmpeg.cs and the package's Content globs expect, and a tarball of
# symlinks pointing at files it does not contain is a trap.
echo "==> Collecting into $OUT_DIR"
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

LIBS=(
    libavcodec.61.dylib
    libavdevice.61.dylib
    libavfilter.10.dylib
    libavformat.61.dylib
    libavutil.59.dylib
    libswresample.5.dylib
    libswscale.8.dylib
)

for lib in "${LIBS[@]}"; do
    src="$PREFIX/lib/$lib"
    if [ ! -f "$src" ]; then
        echo "error: $lib was not built. The soname may have moved off $FFMPEG_TAG;" >&2
        echo "       check $PREFIX/lib and update LIBS plus the osx-arm64 entry in" >&2
        echo "       scripts/fetch-ffmpeg.cs." >&2
        ls -1 "$PREFIX/lib" >&2
        exit 1
    fi
    cp "$src" "$OUT_DIR/$lib"
done

# ── Verify self-containment ──────────────────────────────────────────────────
# The check the Homebrew path cannot pass. Every load command must point at a
# system location or at @loader_path; anything naming /opt/homebrew, /usr/local
# or the build prefix would load here and fail on a consumer's machine, which is
# the failure this whole script exists to remove. Fail the build, do not warn:
# a warning produces an artifact that looks fine until someone installs it.
echo "==> Checking for external dependencies"
leaked=0
for f in "$OUT_DIR"/*.dylib; do
    while read -r dep; do
        case "$dep" in
            /usr/lib/*|/System/Library/*|@loader_path/*|@rpath/*) ;;
            *)
                echo "  LEAK $(basename "$f") -> $dep" >&2
                leaked=1
                ;;
        esac
    done < <(otool -L "$f" | tail -n +2 | awk '{print $1}')
done

if [ "$leaked" -ne 0 ]; then
    echo "error: the build links something outside the bundle. It would load on this" >&2
    echo "       machine and fail elsewhere, which is the Homebrew failure mode this" >&2
    echo "       build replaces. Check that --disable-autodetect took effect." >&2
    exit 1
fi
echo "  clean: every load command is a system path or @loader_path"

# ── Record ───────────────────────────────────────────────────────────────────
{
    echo "FrameFlow FFmpeg build for osx-arm64"
    echo
    echo "tag:        $FFMPEG_TAG"
    echo "commit:     $(git -C "$SRC_DIR" rev-parse HEAD)"
    echo "built:      $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "host:       $(uname -srm)"
    echo "sdk:        $(xcrun --show-sdk-version 2>/dev/null || echo unknown)"
    echo "clang:      $(clang --version | head -1)"
    echo
    echo "configure:"
    printf '  %s\n' "${CONFIGURE_ARGS[@]}"
    echo
    echo "--- configure: enabled external libraries ---"
    echo "The licence of these bytes follows from this list, not from the flags"
    echo "above. An empty list is the expected result of --disable-autodetect and"
    echo "is what keeps this build LGPL-2.1 with no Apache-2.0 or LGPL-3 parts."
    grep -E '^(EXTRALIBS|CONFIG_)' "$BUILD_DIR/ffbuild/config.mak" 2>/dev/null         | grep -Ei 'lib(x264|x265|vpx|aom|openh264|kvazaar|aribb24|opencore|vmaf|soxr)|gmp|version3'         || echo "  (none)"
    echo
    echo "--- configure: full component report ---"
    sed -n '1,200p' "$BUILD_DIR/ffbuild/config.log" 2>/dev/null         | grep -E '^(enabled|disabled) ' || true
} > "$OUT_DIR/BUILD-INFO.txt"

cd "$OUT_DIR"
shasum -a 256 ./*.dylib | sed 's#\./##' > SHA256SUMS

echo
echo "==> Done"
ls -la "$OUT_DIR"
echo
cat "$OUT_DIR/SHA256SUMS"
