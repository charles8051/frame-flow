// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.MotionClip;

/// <summary>
/// Numpad-numbered 3×3 grid mask over the motion-detector's downsampled
/// frame. Cells are numbered like a numpad — bottom row 1-2-3, middle
/// row 4-5-6, top row 7-8-9 — which matches operator intuition when
/// looking at a screen.
/// </summary>
/// <remarks>
/// <para>
/// Used by <see cref="MotionDetector"/> to limit which pixels count
/// toward the changed-pixel ratio. Used by the Avalonia preview overlay
/// to render which cells are armed. Single source of truth for the
/// "what's the numpad layout" question so the detector and the overlay
/// can't drift.
/// </para>
/// <para>
/// <b>Ratio semantics.</b> When a mask is in effect, the detector
/// divides changed-pixel count by <see cref="ActivePixelCount"/>, not
/// the full frame. That keeps the user-facing
/// <c>--sensitivity</c> threshold consistent regardless of how many
/// sectors are armed — "0.8 means trigger when 2 % of the watched
/// area changes" is true whether the watched area is the whole frame
/// or a single sector.
/// </para>
/// </remarks>
public sealed class MotionSectorMask
{
    /// <summary>The grid is fixed at 3×3 for the v1 numpad design.</summary>
    public const int GridRows = 3;

    /// <summary>The grid is fixed at 3×3 for the v1 numpad design.</summary>
    public const int GridCols = 3;

    /// <summary>Total sector count (1 through 9 inclusive).</summary>
    public const int SectorCount = GridRows * GridCols;

    private readonly bool[] _pixelMask; // length = width * height
    private readonly bool[] _sectorArmed; // length = 10; index by numpad number
    private readonly int _width;
    private readonly int _height;

    /// <summary>
    /// Builds a mask sized <paramref name="width"/> × <paramref name="height"/>
    /// over the supplied armed-sector set. An empty or null sector set is
    /// treated as "all 9 armed" (the historic behaviour, equivalent to no
    /// masking at all).
    /// </summary>
    /// <param name="width">Pixel width of the downsampled buffer.</param>
    /// <param name="height">Pixel height of the downsampled buffer.</param>
    /// <param name="armedSectors">
    /// Numpad sector numbers (1-9) that count as "watched". Values outside
    /// 1-9 are silently dropped. Duplicates are deduped.
    /// </param>
    public MotionSectorMask(int width, int height, IEnumerable<int>? armedSectors)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _width = width;
        _height = height;

        _sectorArmed = new bool[SectorCount + 1]; // index 1..9

        var armed = armedSectors?.Where(n => n is >= 1 and <= SectorCount).ToHashSet();
        if (armed is null || armed.Count == 0)
        {
            // No filter requested → arm everything. ActivePixelCount == width * height.
            for (int i = 1; i <= SectorCount; i++)
                _sectorArmed[i] = true;
            ArmedSectors = Enumerable.Range(1, SectorCount).ToArray();
        }
        else
        {
            foreach (int n in armed)
                _sectorArmed[n] = true;
            ArmedSectors = armed.OrderBy(n => n).ToArray();
        }

        _pixelMask = new bool[width * height];
        int active = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * GridRows / height; // 0, 1, or 2
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                int col = x * GridCols / width; // 0, 1, or 2
                int sector = SectorNumberFor(row, col);
                if (_sectorArmed[sector])
                {
                    _pixelMask[rowBase + x] = true;
                    active++;
                }
            }
        }
        ActivePixelCount = active;
    }

    /// <summary>Sorted list of armed sector numbers (1-9).</summary>
    public IReadOnlyList<int> ArmedSectors { get; }

    /// <summary>
    /// Number of pixels in the mask that are armed. Used by
    /// <see cref="MotionDetector"/> as the ratio denominator so the
    /// changed-pixel ratio is a fraction of the watched area, not of
    /// the full frame.
    /// </summary>
    public int ActivePixelCount { get; }

    /// <summary>
    /// <see langword="true"/> if EVERY sector is armed (equivalent to
    /// no masking). Useful for the overlay to short-circuit rendering
    /// when there's nothing to show.
    /// </summary>
    public bool AllArmed => ArmedSectors.Count == SectorCount;

    /// <summary>Returns whether the given numpad cell is armed. Numbers outside 1-9 return false.</summary>
    public bool IsArmed(int numpadNumber) =>
        numpadNumber is >= 1 and <= SectorCount && _sectorArmed[numpadNumber];

    /// <summary>
    /// Direct read of the per-pixel mask. <see cref="MotionDetector"/> uses
    /// this in its inner loop. Length is <c>width × height</c>; index by
    /// <c>y * width + x</c>.
    /// </summary>
    public ReadOnlySpan<bool> PixelMask => _pixelMask;

    /// <summary>
    /// Numpad number for a (row, col) cell in the 3×3 grid. Layout:
    /// <code>
    ///   7 8 9    (row 0)
    ///   4 5 6    (row 1)
    ///   1 2 3    (row 2)
    /// </code>
    /// Operator intuition: number them like the numpad you're looking at,
    /// so "1" is bottom-left and "9" is top-right.
    /// </summary>
    public static int SectorNumberFor(int row, int col)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfNegative(col);
        if (row >= GridRows || col >= GridCols)
            throw new ArgumentOutOfRangeException(nameof(row), $"row={row} col={col} outside {GridRows}x{GridCols} grid");
        return (GridRows - 1 - row) * GridCols + col + 1;
    }

    /// <summary>
    /// Reverses <see cref="SectorNumberFor"/>: returns the (row, col) pair
    /// for a numpad number (1-9). Used by the overlay to position cells.
    /// </summary>
    public static (int row, int col) RowColFor(int numpadNumber)
    {
        if (numpadNumber is < 1 or > SectorCount)
            throw new ArgumentOutOfRangeException(nameof(numpadNumber), $"Expected 1..{SectorCount}, got {numpadNumber}");
        int zeroBased = numpadNumber - 1;
        int col = zeroBased % GridCols;
        int row = GridRows - 1 - (zeroBased / GridCols);
        return (row, col);
    }
}
