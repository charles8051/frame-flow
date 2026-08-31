namespace FrameFlow.Media.Tests;

public sealed class MediaSourceTests
{
    // -----------------------------------------------------------------------
    // FromFile — happy path
    // -----------------------------------------------------------------------

    [Fact]
    public void FromFile_SetsFilePath_ToFullPath()
    {
        // Use a relative path to exercise the full-path expansion.
        // Path.GetFullPath normalises it regardless of whether the file exists.
        var relativePath = "video.mp4";
        var expected = Path.GetFullPath(relativePath);

        var source = MediaSource.FromFile(relativePath);

        Assert.Equal(expected, source.FilePath);
    }

    [Fact]
    public void FromFile_SetsDisplayName_ToFileName()
    {
        var source = MediaSource.FromFile("some/path/clip.mkv");
        Assert.Equal("clip.mkv", source.DisplayName);
    }

    [Fact]
    public void FromFile_SetsIsSeekable_True()
    {
        var source = MediaSource.FromFile("video.mp4");
        Assert.True(source.IsSeekable);
    }

    [Fact]
    public void FromFile_SetsUri_ToFileUri()
    {
        var source = MediaSource.FromFile("video.mp4");
        Assert.NotNull(source.Uri);
        Assert.True(source.Uri!.IsFile);
    }

    [Fact]
    public void FromFile_Uri_PointsToSamePathAsFilePath()
    {
        var source = MediaSource.FromFile("video.mp4");
        Assert.Equal(source.FilePath, source.Uri!.LocalPath);
    }

    // -----------------------------------------------------------------------
    // FromUri — remote URI is not seekable
    // -----------------------------------------------------------------------

    [Fact]
    public void FromUri_RemoteUri_IsNotSeekable()
    {
        var uri = new Uri("https://example.com/stream.m3u8");
        var source = MediaSource.FromUri(uri);
        Assert.False(source.IsSeekable);
    }

    [Fact]
    public void FromUri_RemoteUri_SetsDisplayName_ToUriString()
    {
        var uri = new Uri("https://example.com/stream.m3u8");
        var source = MediaSource.FromUri(uri);
        Assert.Equal(uri.ToString(), source.DisplayName);
    }

    [Fact]
    public void FromUri_RemoteUri_StoresUri()
    {
        var uri = new Uri("rtsp://192.168.1.1/live");
        var source = MediaSource.FromUri(uri);
        Assert.Equal(uri, source.Uri);
    }

    [Fact]
    public void FromUri_RemoteUri_FilePathIsNull()
    {
        var uri = new Uri("https://cdn.example.com/video.mp4");
        var source = MediaSource.FromUri(uri);
        Assert.Null(source.FilePath);
    }

    // -----------------------------------------------------------------------
    // FromUri — file URI is seekable
    // -----------------------------------------------------------------------

    [Fact]
    public void FromUri_FileUri_IsSeekable()
    {
        var uri = new Uri(Path.GetFullPath("video.mp4"));
        var source = MediaSource.FromUri(uri);
        Assert.True(source.IsSeekable);
    }

    [Fact]
    public void FromUri_FileUri_SetsFilePath()
    {
        var fullPath = Path.GetFullPath("video.mp4");
        var uri = new Uri(fullPath);
        var source = MediaSource.FromUri(uri);
        Assert.Equal(uri.LocalPath, source.FilePath);
    }

    // -----------------------------------------------------------------------
    // Interface contract
    // -----------------------------------------------------------------------

    [Fact]
    public void ImplementsIMediaSource()
    {
        var source = MediaSource.FromFile("video.mp4");
        Assert.IsAssignableFrom<IMediaSource>(source);
    }

    [Fact]
    public void IMediaSource_DisplayName_MatchesProperty()
    {
        IMediaSource source = MediaSource.FromFile("clip.mp4");
        Assert.Equal("clip.mp4", source.DisplayName);
    }

    [Fact]
    public void IMediaSource_IsSeekable_MatchesProperty()
    {
        IMediaSource source = MediaSource.FromFile("clip.mp4");
        Assert.True(source.IsSeekable);
    }

    // -----------------------------------------------------------------------
    // Record constructor — direct instantiation
    // -----------------------------------------------------------------------

    [Fact]
    public void RecordConstructor_StoresAllProperties()
    {
        var uri = new Uri("https://example.com/test.mp4");
        var source = new MediaSource("My Video", uri, "/files/test.mp4", false);

        Assert.Equal("My Video", source.DisplayName);
        Assert.Equal(uri, source.Uri);
        Assert.Equal("/files/test.mp4", source.FilePath);
        Assert.False(source.IsSeekable);
    }

    [Fact]
    public void RecordConstructor_DefaultIsSeekable_IsTrue()
    {
        var source = new MediaSource("Test");
        Assert.True(source.IsSeekable);
    }

    [Fact]
    public void RecordConstructor_DefaultUri_IsNull()
    {
        var source = new MediaSource("Test");
        Assert.Null(source.Uri);
    }

    [Fact]
    public void RecordConstructor_DefaultFilePath_IsNull()
    {
        var source = new MediaSource("Test");
        Assert.Null(source.FilePath);
    }
}
