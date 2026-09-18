#!/usr/bin/env bash
# Parallel test runner. Spawns one `dotnet test` process per test
# project via xargs -P, leaning on three layers of parallelism:
#
#   1. Per-project (this script): one OS process per test assembly,
#      up to 8 concurrent. Lets short projects (Video, Avalonia, Media)
#      finish in parallel with the long Integration suite instead of
#      stacking after it sequentially.
#   2. Per-class within each project (xUnit default): test classes
#      run in parallel up to Environment.ProcessorCount. The
#      integration tests now opt in by using IClassFixture rather than
#      a shared [Collection], so 16 test classes parallelize instead
#      of serializing through one collection gate.
#   3. Per-method within each class (xUnit default): serial, kept that
#      way because per-method parallelism would need v3 + audit of
#      every fixture for in-class mutation.
#
# Baseline (sequential `dotnet test`):  ~2m 38s
# This script:                           ~25s
#
# Requires a prior `dotnet build` since each invocation uses
# --no-build --no-restore to skip the per-process MSBuild overhead.

set -euo pipefail

cd "$(dirname "$0")/.."

if [ "${1:-}" = "--build" ]; then
  echo "==> dotnet build FrameFlow.slnx"
  dotnet build FrameFlow.slnx -nologo -clp:NoSummary -v:q
fi

# Discover test projects via the slnx → /tests/ convention. A support library
# under tests/ sets <IsTestProject>false</IsTestProject>; `dotnet test` runs
# nothing for it and prints no summary, so the worker would flag it NOSUMMARY.
#
# The property is read from MSBuild evaluation, not grepped from the csproj, so
# a commented-out or conditional element counts only when MSBuild applies it.
# Only an evaluated false is excluded. A project that evaluates to anything
# else, or fails to evaluate, stays in the run and fails loudly if it cannot
# start, instead of dropping out unnoticed.
#
# Evaluation uses the framework the worker passes to `dotnet test -f`, which sets
# the same TargetFramework global property, so a property conditioned on it
# reads the same in discovery and in the run.
framework=net10.0
projects=()
while read -r is_test csproj; do
  if [ "$is_test" = "false" ]; then
    echo "==> skipping ${csproj} (IsTestProject=false)"
  else
    projects+=( "$csproj" )
  fi
done < <(
  printf '%s\n' tests/*/*.csproj \
    | xargs -P 8 -I{} bash -c '
        value=$(dotnet msbuild "$1" -getProperty:IsTestProject -p:TargetFramework="$2" -nologo 2>/dev/null \
          | tail -1 | tr -d "[:space:]" | tr "[:upper:]" "[:lower:]")
        printf "%s %s\n" "${value:-unset}" "$1"
      ' _ {} "$framework" \
    | sort -k2
)

echo "==> running ${#projects[@]} test assemblies in parallel"
start=$(date +%s)

# Run each project, tail to the summary line, sort for stable output.
# Each worker prints the assembly's summary line, or a synthetic NOSUMMARY line
# when dotnet test exits non-zero without one. Without that, a crashed or
# aborted run ("Test Run Aborted.", a process that cannot start) contributes no
# "Failed: N" field, and the aggregation below would score it as zero failures
# and exit 0. `tail` masks the exit status, so it is captured explicitly rather
# than inferred from the pipeline.
# Where a failing assembly's full output is kept. Only summary lines reach stdout,
# so without this a red run says how many failed and never which - see #284, where
# that gap is why an intermittent failure went months without a name. A green run
# leaves nothing behind.
#
# A red run's logs are kept deliberately, so they outlive the run that made them,
# and an interrupted run is cleaned by the trap. Yesterday's are worth neither:
# prune them here rather than growing /tmp one failure at a time.
find "${TMPDIR:-/tmp}" -maxdepth 1 -name "frameflow-tests.*" -type d -mtime +1 \
  -exec rm -rf {} + 2>/dev/null || true

logdir=$(mktemp -d "${TMPDIR:-/tmp}/frameflow-tests.XXXXXX")
export logdir
trap 'rm -rf "$logdir"' INT TERM

results=$(
  printf '%s\n' "${projects[@]}" \
    | xargs -P 8 -I{} bash -c '
        out=$(dotnet test "$1" -f "$2" --no-build --no-restore --nologo --verbosity quiet 2>&1)
        rc=$?
        line=$(printf "%s\n" "$out" | tail -1)

        # Keep the whole output unless the worker was clean. The [FAIL] lines and the
        # assertion messages sit above the summary and are otherwise discarded. The
        # condition is not-known-good rather than failed, so an assembly that printed
        # no summary at all - crashed, aborted, could not start - keeps its output
        # too, which is the case with the least to go on otherwise.
        #
        # Named for the project directory rather than the csproj: two projects can
        # share a file name, and a collision would silently drop one of the reports.
        if [ "$rc" -ne 0 ] \
           || ! printf "%s" "$line" | grep -qE "Failed:[[:space:]]+0([^0-9]|$)"; then
          slug=$(dirname "$1" | sed "s#^tests/##" | tr "\\/" "__")
          printf "%s\n" "$out" > "$logdir/$slug.log"
        fi
        if printf "%s" "$line" | grep -qE "Failed:[[:space:]]+[0-9]+"; then
          printf "%s\n" "$line"
        else
          printf "NOSUMMARY (exit %s) - %s\n" "$rc" "$1"
          printf "%s\n" "$out" | tail -15 >&2
        fi
        # A summary is not proof of success. dotnet test can print "Failed: 0"
        # and still exit non-zero — a host crash during shutdown, a collector
        # that could not write, an MSBuild error after the run. Flag only that
        # case: a non-zero exit the summary already explains needs no marker,
        # because the Failed: count carries it.
        if [ "$rc" -ne 0 ] \
           && printf "%s" "$line" | grep -qE "Failed:[[:space:]]+0([^0-9]|$)"; then
          printf "UNEXPLAINEDEXIT (exit %s) - %s\n" "$rc" "$1"
          printf "%s\n" "$out" | tail -15 >&2
        fi
        exit 0
      ' _ {} "$framework" \
    | sort
)
printf '%s\n' "$results"

# Roll the per-assembly lines up into one line. A per-assembly "Passed!" is
# printed even when most of that assembly skipped, so a suite with no FFmpeg
# runtime and no corpus reads identically to a fully covered one. The skip
# total is what distinguishes them, and it belongs in the summary rather than
# spread across nineteen lines nobody adds up.
sum_field() {
  local total=0 n
  while read -r n; do total=$(( total + n )); done < <(
    printf '%s\n' "$results" | grep -oE "$1:[[:space:]]+[0-9]+" | grep -oE '[0-9]+'
  )
  echo "$total"
}
failed=$(sum_field Failed)
passed=$(sum_field Passed)
skipped=$(sum_field Skipped)

# `failed` stays the true count of failed tests, so the summary line does not
# inflate it. Anomalies gate the exit separately: an assembly that printed no
# summary, or exited non-zero while claiming zero failures, did not succeed even
# though it contributed no failure count. The two markers are mutually
# exclusive, so an assembly is counted at most once.
nosummary=$(printf '%s\n' "$results" | grep -c '^NOSUMMARY' || true)
unexplained=$(printf '%s\n' "$results" | grep -c '^UNEXPLAINEDEXIT' || true)
anomalies=$(( nosummary + unexplained ))

elapsed=$(( $(date +%s) - start ))
echo "==> ${passed} passed, ${failed} failed, ${skipped} skipped in ${elapsed}s"

# Name the failures. A count alone cannot be acted on, and a rerun that goes green
# takes the evidence with it.
if [ "$failed" -gt 0 ] || [ "$anomalies" -gt 0 ]; then
  echo
  for log in "$logdir"/*.log; do
    [ -e "$log" ] || continue
    echo "--- $(basename "$log" .log)"
    grep -E "\[FAIL\]" "$log" | sed "s/^\[xUnit\.net [0-9:.]*\] *//; s/^ */    /" || true
  done
  echo
  echo "    Full output: $logdir"
else
  rm -rf "$logdir"
fi

if [ "$nosummary" -gt 0 ]; then
  echo "    ${nosummary} assembl(y|ies) produced no summary line — crashed, aborted, or"
  echo "    failed to start. Their stderr tail is above."
fi

if [ "$unexplained" -gt 0 ]; then
  echo "    ${unexplained} assembl(y|ies) exited non-zero while reporting zero failed"
  echo "    tests. Something failed outside the tests themselves; stderr tail above."
fi

if [ "$skipped" -gt 0 ]; then
  echo "    Skips are gated on the FFmpeg runtime (scripts/fetch-ffmpeg.cs) and"
  echo "    the generated corpus (scripts/generate-test-corpus.cs). A high skip"
  echo "    count means the suite covers less than the per-assembly lines suggest."
fi

[ "$failed" -eq 0 ] && [ "$anomalies" -eq 0 ]
