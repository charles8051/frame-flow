// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.Core;

/// <summary>Which pending result belongs to the frame on screen. Pure.</summary>
internal static class PresentedMatch
{
    /// <summary>
    /// The index of the latest timestamp at or before <paramref name="presented"/> in
    /// <paramref name="ascending"/>, or -1 when every one is after it. A branch drops frames while a
    /// run is in progress, so most frames have no result of their own and show the one before.
    /// </summary>
    public static int LatestAtOrBefore(IReadOnlyList<TimeSpan> ascending, TimeSpan presented)
    {
        ArgumentNullException.ThrowIfNull(ascending);

        int low = 0, high = ascending.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (ascending[mid] <= presented)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }
}
