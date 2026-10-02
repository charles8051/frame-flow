// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Avalonia.Windows;

/// <summary>Whether the process runs in a remote desktop session.</summary>
internal static partial class RemoteSession
{
    private const int SmRemoteSession = 0x1000;

    /// <summary>
    /// True in a Remote Desktop session. Read each time: a session can be connected to remotely,
    /// or taken back locally, while the process runs.
    /// </summary>
    public static bool IsActive => GetSystemMetrics(SmRemoteSession) != 0;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
