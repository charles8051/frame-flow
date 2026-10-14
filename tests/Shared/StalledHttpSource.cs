using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FrameFlow.Media;

namespace FrameFlow.Tests.Shared;

/// <summary>
/// A network source that stalls: an HTTP server on 127.0.0.1 that answers FFmpeg as far as a test
/// needs and then sends nothing more, so a demuxer call waits on it until it is interrupted (#709).
/// Nothing leaves the machine.
/// </summary>
/// <remarks>
/// <para>
/// The signals are the server's: it has read a request. Once it has, the client is inside a
/// native call waiting for the reply, and only an interrupt, or the server, ends that call.
/// Disposing the server closes every connection, which also ends a call that was never interrupted,
/// so a test that fails leaves no thread blocked in FFmpeg.
/// </para>
/// <para>
/// <see cref="NeverAnswering"/> reads each request and never replies: an open waits for the
/// reply's header. The others serve raw media of a declared length, with the demuxer named and
/// FFmpeg's <c>reconnect</c> option set. The first request gets the reply's header and the first
/// <c>openingBytes</c>, and then the connection closes early. The demuxer asks for the rest inside
/// its next read, and that second request is never answered. Every request after it is served in
/// full, so a seek after an interrupted read reopens the source.
/// </para>
/// <para>
/// <see cref="StallingAfterTheHeader"/> serves PCM, which opens and probes without reading past its
/// first bytes, so the read that stalls is a run's or a seek's. <see cref="StallingWhileProbed"/>
/// serves the first half of a real file: its probe finds the streams' parameters there and reads on,
/// to measure the frame rate or the duration, so the read that stalls is the probe's.
/// </para>
/// </remarks>
internal sealed class StalledHttpSource : IAsyncDisposable
{
    /// <summary>
    /// One packet of the PCM: 4,096 samples, which is how much the demuxer reads at a time at this
    /// rate. The open reads only the first ten bytes, its check for an ID3 tag.
    /// </summary>
    public const int PcmPacketBytes = 8192;

    /// <summary>The PCM's length: five seconds at 44.1 kHz, mono, 16-bit.</summary>
    public const int PcmLength = 44_100 * 2 * 5;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _closing = new();
    private readonly ConcurrentQueue<Socket> _connections = new();
    private readonly ConcurrentQueue<Task> _handlers = new();
    private readonly bool _answers;
    private readonly byte[] _content;
    private readonly int _openingBytes;
    private readonly string? _inputFormat;
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal) { ["reconnect"] = "1" };
    private readonly TaskCompletionSource _firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _accepting;
    private int _requests;
    private int _disposed;

    private StalledHttpSource(bool answers, byte[] content, int openingBytes, string? inputFormat)
    {
        _answers = answers;
        _content = content;
        _openingBytes = openingBytes;
        _inputFormat = inputFormat;
        _listener.Start();
        Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/stalled.raw");
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>A server that reads every request and never replies.</summary>
    public static StalledHttpSource NeverAnswering() =>
        new(answers: false, content: [], openingBytes: 0, inputFormat: null);

    /// <summary>
    /// A server whose PCM opens, and whose next read past <paramref name="openingBytes"/> stalls.
    /// One packet by default, so the read after the first packet has nothing buffered.
    /// </summary>
    public static StalledHttpSource StallingAfterTheHeader(int openingBytes = PcmPacketBytes) =>
        new(answers: true, new byte[PcmLength], openingBytes, "s16le"); // silence

    /// <summary>
    /// A server that sends the first half of <paramref name="media"/>, which the demuxer named
    /// <paramref name="inputFormat"/> opens, and whose probe then stalls.
    /// </summary>
    public static StalledHttpSource StallingWhileProbed(byte[] media, string inputFormat) =>
        new(answers: true, media, media.Length / 2, inputFormat);

    public Uri Uri { get; }

    /// <summary>The length of what the server serves, in bytes.</summary>
    public int Length => _content.Length;

    /// <summary>
    /// The source to open: the media with the demuxer named and its options set, or for a server
    /// that never answers, the bare URI.
    /// </summary>
    public MediaSource Source =>
        _answers
            ? MediaSource.FromUri(Uri) with
            {
                InputFormat = _inputFormat,
                DemuxerOptions = new Dictionary<string, string>(_options, StringComparer.Ordinal),
            }
            : MediaSource.FromUri(Uri);

    /// <summary>Completes once the first request has been read: the open waits for its reply.</summary>
    public Task FirstRequest => _firstRequest.Task;

    /// <summary>
    /// Completes once the request for the rest of the media has been read: the read that made it is
    /// waiting for a reply that never comes.
    /// </summary>
    public Task Reconnected => _reconnected.Task;

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var socket = await _listener.AcceptSocketAsync(_closing.Token).ConfigureAwait(false);
                _connections.Enqueue(socket);
                _handlers.Enqueue(Task.Run(() => ServeAsync(socket), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ServeAsync(Socket socket)
    {
        try
        {
            long from = await ReadRequestAsync(socket).ConfigureAwait(false);
            int request = Interlocked.Increment(ref _requests);

            if (!_answers)
            {
                _firstRequest.TrySetResult();
                return; // held open, unanswered, until the server is disposed
            }

            switch (request)
            {
                case 1:
                    _firstRequest.TrySetResult();
                    await ReplyAsync(socket, from, _openingBytes).ConfigureAwait(false);
                    socket.Shutdown(SocketShutdown.Both);
                    socket.Close();
                    return;
                case 2:
                    _reconnected.TrySetResult();
                    return; // held open, unanswered, until the server is disposed
                default:
                    await ReplyAsync(socket, from, Length - (int)from).ConfigureAwait(false);
                    socket.Shutdown(SocketShutdown.Both);
                    socket.Close();
                    return;
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Reads one request's header, and returns where its range starts.</summary>
    private async Task<long> ReadRequestAsync(Socket socket)
    {
        var received = new List<byte>();
        var buffer = new byte[1024];
        while (!EndsHeader(received))
        {
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None, _closing.Token).ConfigureAwait(false);
            if (read == 0)
                throw new SocketException((int)SocketError.ConnectionReset);
            received.AddRange(buffer.AsSpan(0, read));
        }

        foreach (var line in Encoding.ASCII.GetString([.. received]).Split("\r\n"))
        {
            const string range = "Range: bytes=";
            if (line.StartsWith(range, StringComparison.OrdinalIgnoreCase))
                return long.Parse(line[range.Length..].TrimEnd('-').Split('-')[0]);
        }

        return 0;
    }

    private static bool EndsHeader(List<byte> received) =>
        received.Count >= 4
        && received[^4] == '\r'
        && received[^3] == '\n'
        && received[^2] == '\r'
        && received[^1] == '\n';

    /// <summary>
    /// Replies with the range from <paramref name="from"/> to the end of the media, and sends the
    /// first <paramref name="count"/> bytes of it.
    /// </summary>
    private async Task ReplyAsync(Socket socket, long from, int count)
    {
        string header =
            "HTTP/1.1 206 Partial Content\r\n"
            + "Content-Type: application/octet-stream\r\n"
            + "Accept-Ranges: bytes\r\n"
            + $"Content-Range: bytes {from}-{Length - 1}/{Length}\r\n"
            + $"Content-Length: {Length - from}\r\n"
            + "\r\n";
        await socket.SendAsync(Encoding.ASCII.GetBytes(header), SocketFlags.None, _closing.Token).ConfigureAwait(false);
        await socket.SendAsync(_content.AsMemory((int)from, count), SocketFlags.None, _closing.Token).ConfigureAwait(false);
    }

    /// <summary>Closes every connection, which ends any call still waiting on one. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _closing.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();
        while (_connections.TryDequeue(out var socket))
        {
            try
            {
                socket.Close();
            }
            catch (ObjectDisposedException) { }
        }

        await _accepting.ConfigureAwait(false);
        await Task.WhenAll(_handlers).ConfigureAwait(false);
        _closing.Dispose();
    }
}
