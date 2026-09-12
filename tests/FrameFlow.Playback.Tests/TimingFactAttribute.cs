namespace FrameFlow.Playback.Tests;

/// <summary>
/// A fact that only runs when <c>FRAMEFLOW_TIMING_TESTS</c> is set to <c>1</c> or
/// <c>true</c>. For tests that measure real elapsed time.
/// </summary>
/// <remarks>
/// <para>
/// A test that measures wall-clock duration is answering a question about the machine
/// it runs on, not about the code. That is a legitimate question — it is how the
/// high-resolution timer work in ADR-0067 was validated at all — but it needs a quiet
/// machine to answer honestly, and a shared CI runner is not one. #148 is what happens
/// when the two are mixed: a correct tree fails because the runner descheduled a sleep.
/// </para>
/// <para>
/// So the measurement stays, and stops being a gate. Same shape as
/// <c>FRAMEFLOW_VISUAL_TESTS</c> in the integration suite, which gates tests that open
/// real windows for the same reason: the environment decides whether the question can
/// be asked, so the environment is what opts in.
/// </para>
/// </remarks>
internal sealed class TimingFactAttribute : FactAttribute
{
    public TimingFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable("FRAMEFLOW_TIMING_TESTS");
        if (!string.Equals(value, "1", StringComparison.Ordinal)
            && !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Timing measurements disabled. Set FRAMEFLOW_TIMING_TESTS=1 to enable.";
        }
    }
}
