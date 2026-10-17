namespace FrameFlow.Playback.Tests;

/// <summary>
/// Reads the pacer's park generation at a point where it cannot be stale (#778).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ClockSelectVideoSink.WaitForParkAfterAsync"/> waits for a park after the generation
/// it is given, so that generation has to be read while the loop is parked. Read while the loop
/// is still on its way to a park, it names the park before, and the wait returns on the park the
/// loop was heading for rather than the one the next action causes. The loop is still marked
/// parked for a moment after an action wakes it, so the wait can return before the loop has even
/// seen the action.
/// </para>
/// <para>
/// The loop is on its way to a park straight after anything it was seen to do: a frame reaching
/// the sink, a callback it raised, the first park after construction. A read at those points goes
/// through <see cref="ParkedGenerationAsync"/>. It is not enough after an action the loop has not
/// yet been seen to react to, such as a pause: the loop may still be marked parked in the park
/// that action is ending. Wait for the park after that action instead.
/// </para>
/// </remarks>
internal static class PacerParks
{
    /// <summary>
    /// Waits until the delivery loop is parked and returns that park's generation.
    /// </summary>
    /// <remarks>
    /// The park it returns on comes after whatever the caller last saw the loop do, because the
    /// loop does nothing visible from inside a park. Nothing wakes it between the park and the
    /// read, so the generation is that park's.
    /// </remarks>
    public static async Task<long> ParkedGenerationAsync(this ClockSelectVideoSink pacer)
    {
        await pacer.WaitForParkAfterAsync(long.MinValue);
        return pacer.ParkGeneration;
    }
}
