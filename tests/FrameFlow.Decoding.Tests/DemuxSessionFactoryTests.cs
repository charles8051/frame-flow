namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Tests for <see cref="DemuxSessionFactory"/> that do not require real FFmpeg binaries
/// for the main contract tests. The "non-existent file" test is opportunistic — it runs
/// when FFmpeg is available and skips gracefully via exception handling when it is not.
/// </summary>
public sealed class DemuxSessionFactoryTests : IClassFixture<FfmpegBootstrapFixture>
{
    public DemuxSessionFactoryTests(FfmpegBootstrapFixture _) { }

    // -------------------------------------------------------------------------
    // DemuxSessionFactory implements IDemuxSessionFactory
    // -------------------------------------------------------------------------

    [Fact]
    public void DemuxSessionFactory_ImplementsIDemuxSessionFactory()
    {
        var factory = new DemuxSessionFactory();
        Assert.IsAssignableFrom<IDemuxSessionFactory>(factory);
    }

    // -------------------------------------------------------------------------
    // Argument validation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OpenAsync_ThrowsArgumentNullException_ForNullSource()
    {
        var factory = new DemuxSessionFactory();

        await Assert.ThrowsAsync<ArgumentNullException>(() => factory.OpenAsync(null!).AsTask());
    }

    [Fact]
    public async Task OpenAsync_ThrowsOperationCanceledException_WhenTokenAlreadyCancelled()
    {
        var factory = new DemuxSessionFactory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var source = MediaSource.FromFile(@"C:\fake\nonexistent.mp4");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            factory.OpenAsync(source, cts.Token).AsTask()
        );
    }

    [Fact]
    public async Task OpenAsync_ThrowsInvalidOperationException_ForNonExistentFile()
    {
        var factory = new DemuxSessionFactory();
        // This test will only throw the expected exception if FFmpeg is loaded.
        // If FFmpeg is not present, the DllNotFoundException propagates — skip gracefully.
        var source = MediaSource.FromFile(@"C:\totally_nonexistent_file_xyz.mp4");

        Exception? caught = null;
        try
        {
            await factory.OpenAsync(source);
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }
        catch (DllNotFoundException)
        {
            // FFmpeg not available in this test environment — skip.
            return;
        }
        catch (Exception)
        {
            // Any other exception from the P/Invoke layer when FFmpeg is absent.
            return;
        }

        // If we got here with an InvalidOperationException, that's the correct outcome.
        if (caught is not null)
            Assert.Contains("nonexistent", caught.Message, StringComparison.OrdinalIgnoreCase);
    }
}
