# Third-party notices

FrameFlow itself is licensed under the PolyForm Small Business License 1.0.0
(`LICENSE.md`). That license does not extend to the third-party components
below, which keep their own terms.

This file is informational. Each component's own license text is authoritative,
and nothing here is legal advice.

---

## FFmpeg — LGPL-3.0-or-later (win/linux) and LGPL-2.1-or-later (osx-arm64)

> The heading said `LGPL-2.1-or-later` until 2026-08-31. That was wrong for the
> BtbN build, and the correction is explained under **Configuration** and
> **FFmpeg's own code** below: `--enable-version3` is not optional there, so 3 is
> the version that reaches a recipient. FFmpeg's *source* remains offered as
> "2.1 or later".

> **Two builds, two licences, since 2026-09-21.** Everything in this section
> describes the BtbN build unless it says otherwise. The osx-arm64 libraries come
> from a different build made in this repository, and it is LGPL-2.1-or-later
> because it links no external libraries at all. Its identity and terms are under
> **The osx-arm64 build** at the end of this section. The
> `FrameFlow.Native.Runtime` package declares `LGPL-3.0-or-later AND Apache-2.0`,
> which governs the package as an aggregate and overstates the macOS files
> specifically; this file is where the per-build terms are recorded.

Distributed as **unmodified pre-built shared libraries** in the
`FrameFlow.Native.Runtime` and `FrameFlow.Native` packages: `avcodec`,
`avformat`, `avutil`, `swresample`, `swscale` — the five FrameFlow actually
P/Invokes. `avfilter` and `avdevice` were dropped from the packages in 2026-08;
they are still fetched for the bundled `ffmpeg`/`ffprobe` tools, which are a
develop-time convenience and are not redistributed.

- **Upstream project:** https://ffmpeg.org — source at https://git.ffmpeg.org/ffmpeg.git
- **License:** LGPL-3.0-or-later for this build — https://www.ffmpeg.org/legal.html
  (FFmpeg's source is offered as LGPL-2.1-or-later; see below for why this build is 3)
- **Build used:** FFmpeg 7.1 (`n7.1.5-12-g1fdbca85aa`), LGPL shared build from
  BtbN/FFmpeg-Builds, release tag `autobuild-2026-07-31-14-10`.
- **Corresponding source:** commit
  **`1fdbca85aaea513c9cc6c14d347f76543346d3da`** in
  https://git.ffmpeg.org/ffmpeg.git, browsable at
  https://github.com/FFmpeg/FFmpeg/commit/1fdbca85aaea513c9cc6c14d347f76543346d3da.

  ```
  git clone https://git.ffmpeg.org/ffmpeg.git && git -C ffmpeg checkout 1fdbca85aa
  ```

  The build name encodes it as `n7.1.5-12-g1fdbca85aa`, which is `git describe`
  output: 12 commits after tag `n7.1.5`, at commit `1fdbca85aa`. **The leading
  `g` means "git" and is not part of the hash** — `g1fdbca85aa` is not a valid
  object id and will not resolve. The full hash is given above so nobody has to
  decode that to exercise this offer.

  The offer does not depend on the binary host: the FFmpeg commit stays fetchable
  whatever BtbN does with its releases. The build recipe is published at
  https://github.com/BtbN/FFmpeg-Builds.

  All of these identifiers move together with the pin in
  `scripts/fetch-ffmpeg.cs`. They went stale once, when the previously named
  release tag was deleted upstream; keep them in step when the pin changes.
- **Configuration:** read from the artifact itself, `ffmpeg -buildconf`, not
  inferred. **Neither `--enable-gpl` nor `--enable-nonfree` is present.** That is
  what establishes the absence of GPL components: FFmpeg's own compliance
  checklist (https://ffmpeg.org/legal.html) gives exactly those two omissions as
  the LGPL condition, and configure refuses to build a GPL component without
  `--enable-gpl` rather than quietly including one.

  The individual `--disable-libx264 --disable-libx265 --disable-libxvid
  --disable-librubberband --disable-libvidstab --disable-frei0r` switches follow
  from that; they are a consequence of the LGPL variant, not the proof of it.
  Citing them alone would be the weaker claim, since it says nothing about any
  GPL component nobody thought to list.

- **FFmpeg's own code here is LGPL-3.0-or-later, not LGPL-2.1.** The build
  carries `--enable-version3`, which FFmpeg's `LICENSE.md` describes as upgrading
  the licence to version 3 of the (L)GPL. It is required rather than incidental:
  the build enables `gmp` and `libaribb24` (LGPL v3) and `libvmaf`,
  `libopencore-amrnb` and `libopencore-amrwb` (Apache-2.0), which are the two
  groups FFmpeg names as needing the upgrade.

  This is a statement about **FFmpeg's own source only**. The libraries that
  forced the upgrade are not relicensed by it — `libvmaf` and the OpenCORE codecs
  remain Apache-2.0 and carry their own attribution and notice obligations, `gmp`
  and `libaribb24` remain LGPL v3 in their own right, and every other bundled
  external keeps whatever terms it ships under. `--enable-version3` makes FFmpeg's
  code compatible with them; it does not absorb them.

  So `LICENSE-LGPL-2.1.txt` alone does not state the terms of FFmpeg's code in
  this build, and no single licence states the terms of the archive as a whole.

FrameFlow does **not** modify FFmpeg. It links against these shared libraries at
runtime through P/Invoke, so an end user can replace them with their own build of
the same soname — which is the relinking freedom LGPL-2.1 §6 exists to preserve.

The NuGet packages keep this property. In `FrameFlow.Native`,
`FrameFlow.Native.Runtime`, `FrameFlow.MotionClip` and `FrameFlow.Audio.OpenAL`
— the four packages that carry LGPL binaries — the libraries are ordinary loose
files under `runtimes/<rid>/native/`, replaceable in place. Those four also ship
`LICENSE-LGPL-2.1.txt`.

The downloadable MotionClip release binary keeps it too. It is published
`PublishSingleFile=true` with `IncludeNativeLibrariesForSelfExtract=false`, so the
managed side is one executable while the LGPL natives sit loose beside it and can
be replaced in place. The artifact is a zip containing that executable, the
natives and an installer script.

> **If you build your own single-file app on FrameFlow**, note that setting
> `IncludeNativeLibrariesForSelfExtract=true` embeds the LGPL libraries into your
> executable, and a recipient of that executable cannot substitute their own build
> of them. The `win-x64-single-file` publish profile in the SdlPlayer example does
> set it — that example is not distributed, and exists partly to exercise the
> bundle-extraction probe in `FrameFlowBootstrapper`. Take advice on what
> LGPL-2.1 §6 requires before shipping a build shaped that way.

### The osx-arm64 build

BtbN publishes no macOS binaries. Until 2026-09 `scripts/fetch-ffmpeg.cs` copied
macOS libraries from an installed Homebrew `ffmpeg@7` keg, which could not be
redistributed: the keg is GPL-3 (it links x264 and x265), and its dylibs resolve
transitive dependencies out of the Homebrew prefix, so they load only on a machine
that has Homebrew and that formula. The packages therefore shipped no macOS
natives at all.

The osx-arm64 libraries now come from a build made in this repository.

- **License:** LGPL-2.1-or-later. Not 3, and no Apache-2.0 component. The reason
  is the absence rather than a flag: this build is configured
  `--disable-autodetect`, so it links **no external libraries**, and none of
  `gmp`, `libaribb24`, `libopencore-amrnb/wb` or `libvmaf` — the components that
  force `--enable-version3` on the BtbN build — is present. It also carries
  `--disable-version3` explicitly.
- **Build recipe:** `scripts/build-ffmpeg-macos.sh`, run by
  `.github/workflows/ffmpeg-macos.yml`. The full configure line is recorded in
  the release's `BUILD-INFO.txt`.
- **Build used:** `ffmpeg-macos-n7.1.5-1`, at
  https://github.com/charles8051/frame-flow/releases/tag/ffmpeg-macos-n7.1.5-1
- **Corresponding source:** commit
  **`3a0867c2bfda4a4d4309ca1a8cbdc6175e67f587`** in
  https://git.ffmpeg.org/ffmpeg.git, browsable at
  https://github.com/FFmpeg/FFmpeg/commit/3a0867c2bfda4a4d4309ca1a8cbdc6175e67f587.

  ```
  git clone https://git.ffmpeg.org/ffmpeg.git && git -C ffmpeg checkout 3a0867c2bf
  ```

  Upstream calls that commit `n7.1.5`. The build pins the **commit**, and checks
  the tag against it rather than trusting the tag to produce it, because a tag is
  a mutable ref.
- **Modifications:** none to the source. The build is configured, not patched.
  `--install-name-dir=@loader_path` changes only how each library names its
  siblings, so the seven files are self-contained in whatever directory they land
  in. The build fails rather than ships if any load command points outside that
  directory or outside the system paths.
- **Relinking:** the same freedom the other platforms have. The libraries are
  loose `.dylib` files beside the application, replaceable with another build of
  the same soname. `LICENSE-LGPL-2.1.txt` in this repository states the terms for
  these files; the LGPL-3 and GPL-3 texts that follow are for the BtbN build.
- **Not shipped from this build:** `ffmpeg` and `ffprobe`. The build excludes the
  external encoders those tools need for corpus generation, so it produces
  libraries only.

**osx-x64 is absent, not broken.** The build is arm64-only, so Intel Macs have no
artifact and `scripts/fetch-ffmpeg.cs` still resolves them from a Homebrew keg for
develop-time use. Those keg files are not redistributed and no osx-x64 RID is
packed.

## OpenCORE AMR — Apache-2.0

Not a separate download: `libopencore-amrnb` and `libopencore-amrwb` are compiled
into the `avcodec` library shipped in `FrameFlow.Native.Runtime` and
`FrameFlow.Native`. They are the reason the FFmpeg build carries
`enable-version3` — Apache-2.0 cannot be combined with LGPL-2.1, and LGPL-3 is
the version that accommodates it.

- **Upstream project:** https://sourceforge.net/projects/opencore-amr/
- **License:** Apache-2.0 — full text in `LICENSE-Apache-2.0.txt`, shipped in
  every package that carries these binaries and in the MotionClip release zip.
- **Attribution:** Copyright the OpenCORE AMR authors and contributors. The
  upstream distribution ships no NOTICE file, so Apache-2.0 §4(d) adds no
  further obligation beyond carrying the licence text and this attribution.
- **Not modified.** FrameFlow neither patches nor rebuilds these codecs; they
  arrive inside the pre-built FFmpeg libraries described above.

LGPL-3 governs the combined work. It does not relicense these components — they
remain Apache-2.0 in their own right, which is why the package declares
`LGPL-3.0-or-later AND Apache-2.0` rather than the copyleft alone.

## OpenAL Soft — LGPL-2.1-or-later

Distributed as an unmodified pre-built shared library, pulled in as a transitive
NuGet dependency of `FrameFlow.Audio.OpenAL` via `Silk.NET.OpenAL.Soft.Native`
(1.23.x). FrameFlow does not build, modify or repackage it.

- **Upstream project:** https://openal-soft.org — source at
  https://github.com/kcat/openal-soft
- **License:** LGPL-2.1-or-later

## ONNX Runtime, DirectML, CUDA and cuDNN

Referenced as NuGet packages under their vendors' terms; consult each package.
CUDA and cuDNN redistributables are marked `CopyToPublishDirectory="Never"` and
are **not** published in any FrameFlow package.

## YOLO / Ultralytics model weights — AGPL-3.0

**Not redistributed.** No weights are committed to this repository or included in
any package. `FrameFlow.Yolo` can download them at runtime into a local cache at
the user's direction. AGPL-3.0 obligations attach to whoever obtains and uses the
weights — see ADR-0051 for why acquisition is left to the consumer.

## SDL

`FrameFlow.Sdl` references `Silk.NET.SDL`. SDL is distributed under the zlib
license.

## Other dependencies

FrameFlow does not redistribute these — NuGet delivers them from upstream and
nothing is bundled — so no attribution obligation attaches to this repository.
They are listed because this file claims to be an inventory, and an inventory
that documents SDL while omitting an entire shipped subsystem is not one.

| Package | Licence | Notes |
|---|---|---|
| `Whisper.net`, `Whisper.net.Runtime` | MIT | Backs `FrameFlow.Whisper`. The runtime package carries whisper.cpp natives, also MIT. |
| `Avalonia`, `.Desktop`, `.Themes.Fluent` | MIT | UI presenters. |
| `Vortice.Direct3D11`, `.DXGI`, `.D3DCompiler` | MIT | Windows zero-copy presenter. |
| `Stateless` | Apache-2.0 | Playback state machine. |
| `Velopack` | MIT | MotionClip updater. |
| `Periphery`, `Periphery.Camera` | **PolyForm Small Business 1.0.0** | See below. |

**`Periphery` deserves its own note.** It is licensed under the same PolyForm
Small Business terms as FrameFlow itself, by the same licensor — and it is a
hard dependency of `FrameFlow.Camera` and `FrameFlow.MotionClip`. A consumer of
either package therefore takes on a *second* source-available dependency, with
the same company-size restriction, rather than only FrameFlow's. That is worth
knowing before adopting those two packages specifically; the other 20 `src/`
packages have no such dependency.

