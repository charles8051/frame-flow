using System.Reflection;
using System.Reflection.Emit;
using FrameFlow.Inference.Core;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// The pixel and tensor kernels compile optimized on their first call, so a video operator's first
/// frames do not run their loops at tier 0 (#546). Read from the methods' metadata and IL: what the
/// JIT does with the attributes is the runtime's contract, not something a test can time.
/// </summary>
public sealed class KernelCompilationTests
{
    private static readonly Type[] Kernels =
    [
        typeof(TensorToImageKernel),
        typeof(ImageToTensorKernel),
        typeof(HalfToFloatKernel),
    ];

    private const MethodImplAttributes OptimizedOrInlined =
        MethodImplAttributes.AggressiveOptimization | MethodImplAttributes.AggressiveInlining;

    public static TheoryData<string> LoopMethods() => new(LoopMethodNames());

    private static IEnumerable<string> LoopMethodNames() =>
        Kernels.SelectMany(DeclaredMethods).Where(HasLoop).Select(m => $"{m.DeclaringType!.Name}.{m.Name}").Distinct();

    /// <summary>
    /// A method with a loop either compiles optimized itself or is inlined into a caller that does.
    /// A new loop without either attribute starts at tier 0 and fails here.
    /// </summary>
    [Theory]
    [MemberData(nameof(LoopMethods))]
    public void AMethodWithALoop_CompilesOptimized_OrIsInlined(string name)
    {
        var flags = Find(name).Where(HasLoop).Select(m => m.MethodImplementationFlags & OptimizedOrInlined).ToList();
        Assert.All(flags, f => Assert.NotEqual(default, f));
    }

    /// <summary>
    /// The scan finds the loops the kernels are known to have, so an empty or short list above is
    /// not a pass by default.
    /// </summary>
    [Fact]
    public void TheLoopScan_FindsTheKnownLoops()
    {
        var found = LoopMethodNames().ToHashSet();

        Assert.Contains("TensorToImageKernel.Run", found);
        Assert.Contains("TensorToImageKernel.ConvertRow", found);
        Assert.Contains("ImageToTensorKernel.Run", found);
        Assert.Contains("ImageToTensorKernel.BilinearRow", found);
        Assert.Contains("ImageToTensorKernel.ConvertBilinear", found);
        Assert.Contains("ImageToTensorKernel.ToHalves", found);
        Assert.Contains("HalfToFloatKernel.ToFloats", found);
    }

    /// <summary>
    /// Vector helpers called per element that the JIT declined to inline on its own. Without the
    /// attribute each compiled separately and started at tier 0.
    /// </summary>
    [Theory]
    [InlineData("ImageToTensorKernel.HalfBits")]
    [InlineData("HalfToFloatKernel.FloatBits")]
    [InlineData("ImageToTensorKernel.LinearIndex")]
    public void APerElementHelper_IsInlined(string name)
    {
        var method = Assert.Single(Find(name));
        Assert.True(
            method.MethodImplementationFlags.HasFlag(MethodImplAttributes.AggressiveInlining),
            $"{name} is not AggressiveInlining.");
    }

    private static IEnumerable<MethodInfo> DeclaredMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);

    private static IEnumerable<MethodInfo> Find(string name)
    {
        var parts = name.Split('.');
        var type = Kernels.Single(t => t.Name == parts[0]);
        var methods = DeclaredMethods(type).Where(m => m.Name == parts[1]).ToList();
        Assert.NotEmpty(methods);
        return methods;
    }

    /// <summary>Whether the method's IL branches backwards, which is how C# compiles a loop.</summary>
    private static bool HasLoop(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            return false;

        int i = 0;
        while (i < il.Length)
        {
            int start = i;
            OpCode op = il[i] == 0xFE ? TwoByte[il[i + 1]] : OneByte[il[i]];
            i += op.Size;
            switch (op.OperandType)
            {
                case OperandType.ShortInlineBrTarget:
                    if (i + 1 + (sbyte)il[i] <= start)
                        return true;
                    i += 1;
                    break;
                case OperandType.InlineBrTarget:
                    if (i + 4 + BitConverter.ToInt32(il, i) <= start)
                        return true;
                    i += 4;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + 4 * BitConverter.ToInt32(il, i);
                    break;
                default:
                    i += OperandSize(op.OperandType);
                    break;
            }
        }

        return false;
    }

    private static int OperandSize(OperandType type) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 4,
    };

    private static readonly OpCode[] OneByte = OpCodeTable(twoByte: false);

    private static readonly OpCode[] TwoByte = OpCodeTable(twoByte: true);

    private static OpCode[] OpCodeTable(bool twoByte)
    {
        var table = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)field.GetValue(null)!;
            if ((op.Size == 2) == twoByte)
                table[(ushort)op.Value & 0xFF] = op;
        }

        return table;
    }
}
