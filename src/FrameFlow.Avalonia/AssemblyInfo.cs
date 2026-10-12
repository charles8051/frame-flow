// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FrameFlow.Avalonia.Tests")]

// The zero-copy presenter builds its sink with the same re-pointable logger as the CPU view
// (SwitchableLogger), so a logger factory given after the sink exists reaches it there too.
[assembly: InternalsVisibleTo("FrameFlow.Avalonia.Windows")]
