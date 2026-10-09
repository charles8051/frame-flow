using FrameFlow.Decoding.Core;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Which hardware candidates a decoder tries, and in what order. Pure: no FFmpeg, no device. The
/// platform orders and the ranking were not pinned by any test before they moved here.
/// </summary>
public sealed class DecoderChoiceTests
{
    private const int H264 = 27;
    private const int Mjpeg = KnownRefusals.MjpegCodecId;

    // Device types as FFmpeg numbers them. The values only have to differ from each other.
    private const int CudaType = 2;
    private const int D3D11Type = 4;
    private const int Dxva2Type = 10;
    private const int VulkanType = 11;
    private const int VaapiType = 3;

    private static HwAccelCandidate Config(HardwareDecodeBackendKind kind, int deviceType) =>
        new("h264", kind, deviceType, HwPixelFormat: 100 + deviceType);

    private static readonly HwAccelCandidate Cuda = Config(HardwareDecodeBackendKind.Cuda, CudaType);
    private static readonly HwAccelCandidate D3D11 = Config(HardwareDecodeBackendKind.D3D11Va, D3D11Type);
    private static readonly HwAccelCandidate Dxva2 = Config(HardwareDecodeBackendKind.Dxva2, Dxva2Type);
    private static readonly HwAccelCandidate Vulkan = Config(HardwareDecodeBackendKind.Vulkan, VulkanType);
    private static readonly HwAccelCandidate VaApi = Config(HardwareDecodeBackendKind.VaApi, VaapiType);

    private static readonly HardwareDecodeBackendKind[] None = [];

    private static IReadOnlyList<HardwareDecodeBackendKind> Kinds(DecoderChoiceDecision d) =>
        d.Hardware.Select(c => c.Kind).ToList();

    private static DecoderChoiceDecision Decide(
        IReadOnlyList<HwAccelCandidate> configs,
        HardwareDecodeMode mode = HardwareDecodeMode.Auto,
        IReadOnlyList<HardwareDecodeBackendKind>? preferred = null,
        OsFamily os = OsFamily.Windows,
        IReadOnlyCollection<HardwareDecodeBackendKind>? initialised = null,
        int? borrowedDeviceType = null,
        int codecId = H264,
        IReadOnlyList<KnownRefusal>? refusals = null,
        IReadOnlyList<string>? excluded = null,
        string codecName = "h264"
    ) =>
        DecoderChoice.Decide(
            codecId,
            codecName,
            "h264",
            configs,
            mode,
            preferred ?? None,
            DecoderChoice.PlatformDefault(os),
            initialised ?? configs.Select(c => c.Kind).ToHashSet(),
            borrowedDeviceType,
            refusals ?? KnownRefusals.Builtin,
            excluded ?? []
        );

    // ── The platform orders ──────────────────────────────────────────────────

    [Fact]
    public void WindowsPrefersTheDirectXBackendsBeforeCuda() =>
        Assert.Equal(
            [
                HardwareDecodeBackendKind.D3D11Va,
                HardwareDecodeBackendKind.D3D12Va,
                HardwareDecodeBackendKind.Dxva2,
                HardwareDecodeBackendKind.Cuda,
                HardwareDecodeBackendKind.Qsv,
            ],
            DecoderChoice.PlatformDefault(OsFamily.Windows)
        );

    [Fact]
    public void LinuxPrefersVaApiThenCuda() =>
        Assert.Equal(
            [
                HardwareDecodeBackendKind.VaApi,
                HardwareDecodeBackendKind.Cuda,
                HardwareDecodeBackendKind.Vdpau,
                HardwareDecodeBackendKind.Qsv,
                HardwareDecodeBackendKind.Vulkan,
                HardwareDecodeBackendKind.Drm,
            ],
            DecoderChoice.PlatformDefault(OsFamily.Linux)
        );

    [Fact]
    public void MacOsHasVideoToolbox() =>
        Assert.Equal(
            [HardwareDecodeBackendKind.VideoToolbox],
            DecoderChoice.PlatformDefault(OsFamily.MacOs)
        );

    [Fact]
    public void AnUnknownPlatformHasNoDefault() =>
        Assert.Empty(DecoderChoice.PlatformDefault(OsFamily.Other));

    // ── Candidates and their order ───────────────────────────────────────────

    [Fact]
    public void OnWindowsDirect3DComesBeforeCudaForTheSameCodec()
    {
        var decision = Decide([Cuda, D3D11, Dxva2, Vulkan]);

        Assert.Equal(
            [
                HardwareDecodeBackendKind.D3D11Va,
                HardwareDecodeBackendKind.Dxva2,
                HardwareDecodeBackendKind.Cuda,
                HardwareDecodeBackendKind.Vulkan,
            ],
            Kinds(decision)
        );
    }

    [Fact]
    public void ThePlatformOrderFollowsTheOperatingSystem()
    {
        var decision = Decide([Cuda, VaApi, Vulkan], os: OsFamily.Linux);

        Assert.Equal(
            [
                HardwareDecodeBackendKind.VaApi,
                HardwareDecodeBackendKind.Cuda,
                HardwareDecodeBackendKind.Vulkan,
            ],
            Kinds(decision)
        );
    }

    [Fact]
    public void APreferenceComesBeforeThePlatformDefaultInTheOrderGiven()
    {
        var decision = Decide(
            [D3D11, Dxva2, Cuda, Vulkan],
            preferred: [HardwareDecodeBackendKind.Vulkan, HardwareDecodeBackendKind.Cuda]
        );

        Assert.Equal(
            [
                HardwareDecodeBackendKind.Vulkan,
                HardwareDecodeBackendKind.Cuda,
                HardwareDecodeBackendKind.D3D11Va,
                HardwareDecodeBackendKind.Dxva2,
            ],
            Kinds(decision)
        );
    }

    [Fact]
    public void APreferredBackendTheDecoderHasNoConfigForIsIgnored()
    {
        var decision = Decide([D3D11], preferred: [HardwareDecodeBackendKind.Vulkan]);

        Assert.Equal([HardwareDecodeBackendKind.D3D11Va], Kinds(decision));
    }

    [Fact]
    public void ABackendInNeitherListComesLast()
    {
        var other = Config(HardwareDecodeBackendKind.OpenCl, 12);

        var decision = Decide([other, Cuda]);

        Assert.Equal(
            [HardwareDecodeBackendKind.Cuda, HardwareDecodeBackendKind.OpenCl],
            Kinds(decision)
        );
    }

    [Fact]
    public void BackendsOfOneRankKeepTheOrderTheDecoderListsThem()
    {
        var first = Config(HardwareDecodeBackendKind.OpenCl, 12);
        var second = Config(HardwareDecodeBackendKind.Other, 13);

        Assert.Equal([first, second], Decide([first, second]).Hardware);
        Assert.Equal([second, first], Decide([second, first]).Hardware);
    }

    [Fact]
    public void OnlyABackendWhoseDeviceInitialisedIsACandidate()
    {
        var decision = Decide(
            [D3D11, Cuda, Vulkan],
            initialised: [HardwareDecodeBackendKind.Cuda]
        );

        Assert.Equal([HardwareDecodeBackendKind.Cuda], Kinds(decision));
    }

    [Fact]
    public void NoInitialisedBackendLeavesNoCandidate()
    {
        var decision = Decide([D3D11, Cuda], initialised: []);

        Assert.Empty(decision.Hardware);
        Assert.Contains("no hardware backend", decision.Reason);
    }

    [Fact]
    public void ADecoderWithNoConfigsHasNoCandidate() =>
        Assert.Empty(Decide([]).Hardware);

    [Fact]
    public void TheDecoderOfACandidateIsKept() =>
        Assert.Equal("h264", Assert.Single(Decide([D3D11]).Hardware).Decoder);

    [Fact]
    public void RequiredOrdersLikeAuto()
    {
        var configs = new[] { Cuda, D3D11, Dxva2 };

        Assert.Equal(
            Kinds(Decide(configs, HardwareDecodeMode.Auto)),
            Kinds(Decide(configs, HardwareDecodeMode.Required))
        );
    }

    [Fact]
    public void DisabledHasNoCandidateAndSaysWhy()
    {
        var decision = Decide([D3D11, Cuda], HardwareDecodeMode.Disabled);

        Assert.Empty(decision.Hardware);
        Assert.Contains("disabled", decision.Reason);
    }

    [Fact]
    public void TheSoftwareDecoderIsPassedThrough()
    {
        var decision = DecoderChoice.Decide(
            H264,
            "h264",
            "libdav1d",
            [D3D11],
            HardwareDecodeMode.Auto,
            None,
            [],
            [HardwareDecodeBackendKind.D3D11Va],
            null,
            [],
            []
        );

        Assert.Equal("libdav1d", decision.SoftwareDecoder);
    }

    // ── A borrowed device ────────────────────────────────────────────────────

    [Fact]
    public void ABorrowedDeviceIsTheOnlyCandidateWhateverTheOrderAndWhetherOrNotItInitialised()
    {
        var decision = Decide(
            [D3D11, Cuda, Vulkan],
            preferred: [HardwareDecodeBackendKind.Vulkan],
            initialised: [],
            borrowedDeviceType: CudaType
        );

        Assert.Equal([HardwareDecodeBackendKind.Cuda], Kinds(decision));
    }

    [Fact]
    public void ABorrowedDeviceTheDecoderCannotUseHasNoCandidate()
    {
        var decision = Decide([D3D11, Vulkan], borrowedDeviceType: CudaType);

        Assert.Empty(decision.Hardware);
        Assert.Contains("borrowed", decision.Reason);
    }

    [Fact]
    public void ABorrowedDeviceIsNotRefused()
    {
        var mjpegOnCuda = Config(HardwareDecodeBackendKind.Cuda, CudaType);

        var decision = Decide([mjpegOnCuda], codecId: Mjpeg, borrowedDeviceType: CudaType);

        Assert.Equal([HardwareDecodeBackendKind.Cuda], Kinds(decision));
        Assert.Empty(decision.Refused);
    }

    // ── Known refusals ───────────────────────────────────────────────────────

    [Fact]
    public void AutoDropsABackendKnownToDecodeTheCodecWrongly()
    {
        var decision = Decide([Cuda], codecId: Mjpeg);

        Assert.Empty(decision.Hardware);
        var refusal = Assert.Single(decision.Refused);
        Assert.Equal(HardwareDecodeBackendKind.Cuda, refusal.Backend);
        Assert.Contains("skipped Cuda", decision.Reason);
    }

    [Fact]
    public void AutoKeepsTheOtherBackendsForTheSameCodec()
    {
        var decision = Decide([Cuda, Vulkan], codecId: Mjpeg);

        Assert.Equal([HardwareDecodeBackendKind.Vulkan], Kinds(decision));
    }

    [Fact]
    public void ARefusalIsForTheCodecItNames()
    {
        var decision = Decide([Cuda], codecId: H264);

        Assert.Equal([HardwareDecodeBackendKind.Cuda], Kinds(decision));
        Assert.Empty(decision.Refused);
    }

    [Fact]
    public void RequiredKeepsTheRefusedBackend()
    {
        var decision = Decide([Cuda], HardwareDecodeMode.Required, codecId: Mjpeg);

        Assert.Equal([HardwareDecodeBackendKind.Cuda], Kinds(decision));
        Assert.Empty(decision.Refused);
    }

    [Fact]
    public void APreferenceDoesNotOverrideARefusal()
    {
        var decision = Decide(
            [Cuda],
            preferred: [HardwareDecodeBackendKind.Cuda],
            codecId: Mjpeg
        );

        Assert.Empty(decision.Hardware);
    }

    [Fact]
    public void NoRefusalsDropNothing()
    {
        var decision = Decide([Cuda], codecId: Mjpeg, refusals: []);

        Assert.Equal([HardwareDecodeBackendKind.Cuda], Kinds(decision));
    }

    // ── Excluded codecs ──────────────────────────────────────────────────────

    [Fact]
    public void AnExcludedCodecHasNoHardwareCandidate()
    {
        var decision = Decide([D3D11, Cuda], excluded: ["h264"]);

        Assert.Empty(decision.Hardware);
        Assert.True(decision.Excluded);
        Assert.Contains("ExcludedCodecs", decision.Reason);
    }

    [Theory]
    [InlineData("H264")]
    [InlineData("h264")]
    public void ExclusionIgnoresCase(string listed)
    {
        var decision = Decide([D3D11], excluded: [listed]);

        Assert.True(decision.Excluded);
    }

    [Fact]
    public void AnotherCodecInTheListLeavesThisOneOnHardware()
    {
        var decision = Decide([D3D11], excluded: ["av1", "mjpeg"]);

        Assert.False(decision.Excluded);
        Assert.Equal([HardwareDecodeBackendKind.D3D11Va], Kinds(decision));
    }

    [Fact]
    public void ABorrowedDeviceDoesNotOverrideAnExclusion()
    {
        var decision = Decide([Cuda], borrowedDeviceType: CudaType, excluded: ["h264"]);

        Assert.Empty(decision.Hardware);
        Assert.True(decision.Excluded);
    }

    [Fact]
    public void RequiredAlsoSeesTheExclusion()
    {
        var decision = Decide([D3D11], HardwareDecodeMode.Required, excluded: ["h264"]);

        Assert.Empty(decision.Hardware);
        Assert.True(decision.Excluded);
    }

    [Fact]
    public void DisabledIsNotReportedAsExcluded()
    {
        var decision = Decide([D3D11], HardwareDecodeMode.Disabled, excluded: ["h264"]);

        Assert.False(decision.Excluded);
        Assert.Contains("disabled", decision.Reason);
    }

    // ── A hardware decoder other than the software one (#417) ─────────────────

    [Fact]
    public void AHardwareDecoderThatIsNotTheSoftwareOneIsNamedInTheReason()
    {
        var native = new HwAccelCandidate("av1", HardwareDecodeBackendKind.D3D11Va, D3D11Type, 101);

        var decision = DecoderChoice.Decide(
            225,
            "av1",
            "libdav1d",
            [native],
            HardwareDecodeMode.Auto,
            None,
            DecoderChoice.PlatformDefault(OsFamily.Windows),
            [HardwareDecodeBackendKind.D3D11Va],
            null,
            [],
            []
        );

        Assert.Equal("av1", Assert.Single(decision.Hardware).Decoder);
        Assert.Equal("libdav1d", decision.SoftwareDecoder);
        Assert.Contains("D3D11Va (av1)", decision.Reason);
    }

    [Fact]
    public void CandidatesFromTwoDecodersOrderByBackendThenByTheOrderListed()
    {
        var first = new HwAccelCandidate("av1", HardwareDecodeBackendKind.OpenCl, 12, 1);
        var second = new HwAccelCandidate("other", HardwareDecodeBackendKind.OpenCl, 12, 2);
        var cuda = new HwAccelCandidate("other", HardwareDecodeBackendKind.Cuda, CudaType, 3);

        var decision = Decide([first, second, cuda]);

        Assert.Equal([cuda, first, second], decision.Hardware);
    }

    // ── The reason, for a log ────────────────────────────────────────────────

    [Fact]
    public void TheReasonNamesTheOrderAndWhatItCameFrom()
    {
        Assert.Contains("platform default", Decide([D3D11, Cuda]).Reason);
        Assert.Contains(
            "preference first",
            Decide([D3D11, Cuda], preferred: [HardwareDecodeBackendKind.Cuda]).Reason
        );
    }
}
