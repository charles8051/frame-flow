// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.Dml.Core;

/// <summary>
/// The GPU waits a run makes before DirectML reads its device inputs: one per ready fence, at the
/// highest value any input needs from it. An input whose fence is zero is already written. Pure.
/// </summary>
internal static class ReadyWaits
{
    public static IReadOnlyList<(nint Fence, ulong Value)> For(IEnumerable<DeviceTensor> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var waits = new Dictionary<nint, ulong>();
        foreach (var tensor in inputs)
        {
            if (tensor.ReadyFence == 0)
                continue;
            waits[tensor.ReadyFence] = waits.TryGetValue(tensor.ReadyFence, out ulong value)
                ? Math.Max(value, tensor.ReadyValue)
                : tensor.ReadyValue;
        }

        return [.. waits.Select(pair => (pair.Key, pair.Value))];
    }
}
