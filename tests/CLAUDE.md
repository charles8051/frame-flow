# Writing tests in FrameFlow

A test here has to pass or fail the same way on a fast machine and on a loaded CI runner. The build
already bans sleeps, wall-clock reads, stopwatches, the tick count and timers built without a
`TimeProvider` everywhere under `tests/` (RS0030, `tests/BannedSymbols.txt`). These are the rules it
cannot check. [ADR-0072](../docs/adr/ADR-0072-tests-do-not-depend-on-elapsed-time.md) has the
reasoning.

1. **Wait on a signal, not a duration.** When the code under test works on another thread, await
   something it completes: a `TaskCompletionSource` in a fake, a park signal, or a no-op command
   sent through the same queue. Drive time with `FakeTimeProvider`. Passing `TimeProvider.System`
   compiles, but it is still the real clock. A timeout (`WaitAsync`, `CancellationTokenSource(TimeSpan)`)
   may only bound a failure: the test has to stay correct if the timeout were ten times longer.

2. **Don't assert that nothing happened straight after an action that wakes a worker.** The worker
   has not run yet, so the assertion passes whether or not the code is right. Wait until the worker
   has reacted, through its next park or a no-op queued behind the action, then assert.

3. **Keep the exemptions for elapsed time.** `FrameFlow.Integration.Tests` is off the ban because
   some of its tests measure real durations. Don't move a test there to get past RS0030. A file-level
   `#pragma warning disable RS0030` is only for a file whose subject is timing itself.

4. **Show a regression test failing before claiming it catches the bug.** Put the bug back, run the
   test, and check that it fails for that reason. Say in the PR what was injected and what failed.
