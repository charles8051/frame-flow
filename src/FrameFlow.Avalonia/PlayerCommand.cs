// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia.Logging;
using FrameFlow.Media;

namespace FrameFlow.Avalonia;

/// <summary>
/// Runs an <see cref="Player.IMediaPlayer"/> transport command from a chrome
/// control and reports what happened.
/// </summary>
/// <remarks>
/// <para>
/// The chrome has no error surface of its own — a transport bar shows the
/// state the player reports, and a refused command simply leaves the buttons
/// where the state observable put them. That is the right visible outcome, but
/// it used to be the <i>only</i> outcome: every call site wrapped the player in
/// a bare <c>catch { }</c>, which discarded the refusal and any bug in the
/// chrome along with it.
/// </para>
/// <para>
/// Since ADR-0069 a refusal comes back as a <see cref="Result"/> instead of an
/// exception, so the two can be told apart. A refusal logs at
/// <see cref="LogEventLevel.Warning"/>; anything that actually throws logs at
/// <see cref="LogEventLevel.Error"/>. Both go through Avalonia's own logger,
/// which is what a control with no injected <c>ILogger</c> has, and which every
/// host in this repository already enables with <c>.LogToTrace()</c>.
/// </para>
/// <para>
/// The catch stays because these run from <c>async void</c> handlers, where an
/// escaping exception takes the process down rather than reaching a caller.
/// </para>
/// </remarks>
internal static class PlayerCommand
{
    /// <summary>
    /// Start <paramref name="command"/> and report its outcome. Returns
    /// immediately; the control keeps handling input while it runs.
    /// </summary>
    /// <param name="source">The control issuing the command, for the log entry.</param>
    /// <param name="operation">The command's name, for the log entry.</param>
    /// <param name="command">The command to run.</param>
    internal static async void FireAndForget(
        object source,
        string operation,
        Func<Task<Result>> command
    )
    {
        try
        {
            Report(source, operation, await command().ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            Logger
                .TryGet(LogEventLevel.Error, LogArea.Control)
                ?.Log(source, "FrameFlow: {Operation} threw. {Exception}", operation, ex);
        }
    }

    /// <summary>Log <paramref name="result"/> if it is a refusal.</summary>
    /// <param name="source">The control that issued the command, for the log entry.</param>
    /// <param name="operation">The command's name, for the log entry.</param>
    /// <param name="result">The command's outcome.</param>
    internal static void Report(object source, string operation, Result result)
    {
        if (result.IsSuccess)
            return;

        Logger
            .TryGet(LogEventLevel.Warning, LogArea.Control)
            ?.Log(
                source,
                "FrameFlow: {Operation} refused. {Category}: {Message}",
                operation,
                result.Error.Category,
                result.Error.Message
            );
    }
}
