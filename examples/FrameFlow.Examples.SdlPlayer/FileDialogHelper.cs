using System.Runtime.InteropServices;

namespace FrameFlow.Examples.SdlPlayer;

/// <summary>
/// Cross-platform file open dialog. Uses the Windows common file dialog on
/// Windows and falls back to console input on other platforms.
/// </summary>
internal static class FileDialogHelper
{
    /// <summary>
    /// Shows a file open dialog and returns the selected path, or <see langword="null"/>
    /// if the user cancelled.
    /// </summary>
    public static string? ShowOpenFileDialog()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WindowsFileDialog.ShowOpen();

        Console.Write("Enter media file path: ");
        var line = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    /// <summary>
    /// Win32 <c>GetOpenFileNameW</c> wrapper for picking media files.
    /// </summary>
    private static class WindowsFileDialog
    {
        private const int MaxPath = 260;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_NOCHANGEDIR = 0x00000008;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAME
        {
            public int lStructSize;
            public nint hwndOwner;
            public nint hInstance;
            public string lpstrFilter;
            public nint lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public nint lpstrFile;
            public int nMaxFile;
            public nint lpstrFileTitle;
            public int nMaxFileTitle;
            public string? lpstrInitialDir;
            public string? lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string? lpstrDefExt;
            public nint lCustData;
            public nint lpfnHook;
            public string? lpTemplateName;
            public nint pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetOpenFileNameW(ref OPENFILENAME lpofn);

        public static string? ShowOpen()
        {
            var buffer = new char[MaxPath];
            buffer[0] = '\0';
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var ofn = new OPENFILENAME
                {
                    lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                    lpstrFilter =
                        "Media Files\0*.mp4;*.mkv;*.webm;*.avi;*.mov;*.flv;*.m4a;*.mp3;*.ogg;*.flac;*.wav\0"
                        + "Video Files\0*.mp4;*.mkv;*.webm;*.avi;*.mov;*.flv\0"
                        + "Audio Files\0*.m4a;*.mp3;*.ogg;*.flac;*.wav\0"
                        + "All Files\0*.*\0\0",
                    nFilterIndex = 1,
                    lpstrFile = handle.AddrOfPinnedObject(),
                    nMaxFile = MaxPath,
                    lpstrTitle = "Open Media File",
                    Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
                };
                if (!GetOpenFileNameW(ref ofn))
                    return null;

                return new string(buffer, 0, Array.IndexOf(buffer, '\0'));
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
