// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FrameFlow.Avalonia.Windows.Tests")]

// The view's setup and logger tests run in FrameFlow.Avalonia.Tests, which has a headless Avalonia app.
[assembly: InternalsVisibleTo("FrameFlow.Avalonia.Tests")]
