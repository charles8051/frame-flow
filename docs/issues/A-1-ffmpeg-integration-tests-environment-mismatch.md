# A-1: FFmpeg Integration Tests — Environment Detection Mismatch

**Severity:** Should Fix Soon
**Status:** Resolved
**Responsible Agent:** Decoding Agent
**Detected:** 2026-03-29
**Resolved:** 2026-03-29
**Phase Gate:** Phase 08

## Problem

Integration tests in `FrameFlow.Decoding.Tests` crashed with a native process abort rather than a managed exception. Investigation revealed two root causes.

## Root Cause 1 — `avcodec_get_name` string marshaling crash

`FFAvCodec.avcodec_get_name` was declared with `[return: MarshalAs(UnmanagedType.LPUTF8Str)]`.
This marshaling attribute instructs the .NET runtime to call `CoTaskMemFree` on the returned string pointer after copying it into a managed string.
However, `avcodec_get_name` returns a pointer to a **statically-allocated** FFmpeg string — it is never heap-allocated and must not be freed.
Calling `CoTaskMemFree` on a static string pointer corrupts the FFmpeg heap and causes an immediate native `abort()`.

**Fix:** Changed the P/Invoke declaration to return `nint` (with `EntryPoint = "avcodec_get_name"`), then called `Marshal.PtrToStringUTF8(ptr)` in a managed wrapper — no memory is freed.

## Root Cause 2 — `AVERROR_EOF` constant was wrong

`FFAvUtil.AvErrorEof` was defined as `unchecked((int)0xBFB5B0BB)`.
The correct value, derived from `FFERRTAG('E','O','F',' ')`, is `-(('E') | ('O' << 8) | ('F' << 16) | (' ' << 24)) = -541478725 = 0xDFB9B0BB`.
The wrong constant caused `av_read_frame` returning `AVERROR_EOF` at end-of-file to be treated as an unknown error rather than normal termination, throwing `InvalidOperationException` instead of returning `null`.

**Fix:** Updated `FFAvUtil_Phase03.cs`: `internal const int AvErrorEof = unchecked((int)0xDFB9B0BB);`

## Related (original report)

The original issue description was that test skip detection based on file existence could allow tests to run even when the bootstrapper path differs from the file-detection path.
After resolving the two root causes above, all 110 tests in `FrameFlow.Decoding.Tests` pass without crashes.
The skip detection concern remains theoretically valid but is not causing failures in the current environment.

## Verification

After both fixes: `dotnet test tests/FrameFlow.Decoding.Tests --nologo` → Passed: 110, Failed: 0.
