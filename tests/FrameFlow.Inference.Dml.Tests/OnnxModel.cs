using System.Text;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// Small ONNX models, encoded here so the tests need no model file. Each is an opset-13 graph on an
/// input <c>x</c>, a float one unless it says otherwise.
/// </summary>
internal static class OnnxModel
{
    // TensorProto.DataType values.
    public const ulong Float = 1;
    public const ulong Int64 = 7;
    public const ulong String = 8;
    public const ulong Float16 = 10;

    /// <summary><c>y = -x</c>: every output is exact, so a wrong or stale input shows at once.</summary>
    public static byte[] Negate(params long[] shape) => SingleNode("Neg", Array.ConvertAll(shape, d => new Dim(d)));

    /// <summary><see cref="Negate(long[])"/> with named, free dimensions where the shape says so.</summary>
    public static byte[] Negate(params Dim[] shape) => SingleNode("Neg", shape);

    /// <summary>
    /// Three outputs of <c>x</c>: <c>indices = NonZero(x)</c>, int64 <c>[rank of x, count]</c> where
    /// count is the number of non-zero elements, so its shape depends on <c>x</c>'s values;
    /// <c>y = -x</c>, float at <c>x</c>'s shape; and <c>size = Size(x)</c>, an int64 scalar.
    /// </summary>
    public static byte[] DataDependent(params long[] shape)
    {
        var dims = Array.ConvertAll(shape, d => new Dim(d));
        var graph = Message(w => w
            .Bytes(1, Node("NonZero", "x", "indices"))
            .Bytes(1, Node("Neg", "x", "y"))
            .Bytes(1, Node("Size", "x", "size"))
            .String(2, "g")
            .Bytes(11, ValueInfo("x", dims))
            .Bytes(12, ValueInfo("indices", [shape.Length, "count"], Int64))
            .Bytes(12, ValueInfo("y", dims))
            .Bytes(12, ValueInfo("size", [], Int64)));
        return Model(graph);
    }

    /// <summary><c>y = Cast(x)</c>: <c>x</c> of element type <paramref name="from"/>, <c>y</c> of <paramref name="to"/>.</summary>
    public static byte[] Cast(ulong from, ulong to, params long[] shape)
    {
        var dims = Array.ConvertAll(shape, d => new Dim(d));
        var graph = Message(w => w
            .Bytes(1, Node("Cast", ["x"], "y", IntAttribute("to", to)))
            .String(2, "g")
            .Bytes(11, ValueInfo("x", dims, from))
            .Bytes(12, ValueInfo("y", dims, to)));
        return Model(graph);
    }

    /// <summary>
    /// Outputs that are constants plus a multiple of <c>x</c>'s mean: each is
    /// <c>Constant + Scale * mean(x)</c>, computed in floats. An fp16 input is cast to floats first,
    /// and an fp16 output cast from them last, as a model with fp16 inputs and outputs and fp32
    /// inside is.
    /// </summary>
    public static byte[] ConstantPlusMean(ulong inputType, long[] inputShape, params MeanOutput[] outputs)
    {
        var graph = Message(w =>
        {
            string x = "x";
            if (inputType == Float16)
            {
                w.Bytes(1, Node("Cast", ["x"], "x_float", IntAttribute("to", Float)));
                x = "x_float";
            }

            w.Bytes(1, Node("ReduceMean", [x], "mean", IntAttribute("keepdims", 0)));
            foreach (var output in outputs)
            {
                string sum = output.Type == Float16 ? output.Name + "_float" : output.Name;
                w.Bytes(1, Node("Mul", [output.Name + "_scale", "mean"], output.Name + "_scaled"));
                w.Bytes(1, Node("Add", [output.Name + "_constant", output.Name + "_scaled"], sum));
                if (output.Type == Float16)
                    w.Bytes(1, Node("Cast", [sum], output.Name, IntAttribute("to", Float16)));
            }

            w.String(2, "g");
            foreach (var output in outputs)
            {
                w.Bytes(5, FloatTensor(output.Name + "_constant", output.Shape, output.Constant));
                w.Bytes(5, FloatTensor(output.Name + "_scale", output.Shape, output.Scale));
            }

            w.Bytes(11, ValueInfo("x", Array.ConvertAll(inputShape, d => new Dim(d)), inputType));
            foreach (var output in outputs)
                w.Bytes(12, ValueInfo(output.Name, Array.ConvertAll(output.Shape, d => new Dim(d)), output.Type));
        });
        return Model(graph);
    }

    /// <summary>
    /// One output of <see cref="ConstantPlusMean"/>: <paramref name="Constant"/> plus
    /// <paramref name="Scale"/> times the input's mean, elementwise, of element type
    /// <paramref name="Type"/>.
    /// </summary>
    public sealed record MeanOutput(string Name, ulong Type, long[] Shape, float[] Constant, float[] Scale);

    /// <summary>One dimension of a model's shape: a fixed size, or a name the model leaves free.</summary>
    public readonly record struct Dim(long Value, string? Name = null)
    {
        public static implicit operator Dim(long value) => new(value);

        public static implicit operator Dim(string name) => new(0, name);
    }

    private static byte[] SingleNode(string opType, Dim[] shape)
    {
        var graph = Message(w => w
            .Bytes(1, Node(opType, "x", "y"))
            .String(2, "g")
            .Bytes(11, ValueInfo("x", shape))
            .Bytes(12, ValueInfo("y", shape)));
        return Model(graph);
    }

    private static byte[] Model(byte[] graph)
    {
        // ModelProto: ir_version 8, graph, opset_import { version 13 }.
        var opset = Message(w => w.Varint(2, 13));
        return Message(w => w.Varint(1, 8).String(2, "frameflow-tests").Bytes(7, graph).Bytes(8, opset));
    }

    // NodeProto { input, output, op_type }.
    private static byte[] Node(string opType, string input, string output) =>
        Message(w => w.String(1, input).String(2, output).String(4, opType));

    // NodeProto { input..., output, op_type, attribute... }.
    private static byte[] Node(string opType, string[] inputs, string output, params byte[][] attributes) =>
        Message(w =>
        {
            foreach (var input in inputs)
                w.String(1, input);
            w.String(2, output).String(4, opType);
            foreach (var attribute in attributes)
                w.Bytes(5, attribute);
        });

    // AttributeProto { name, i, type: INT (2) }.
    private static byte[] IntAttribute(string name, ulong value) =>
        Message(w => w.String(1, name).Varint(3, value).Varint(20, 2));

    // TensorProto { dims..., data_type: FLOAT, name, raw_data }, the floats little-endian.
    private static byte[] FloatTensor(string name, long[] shape, float[] values) =>
        Message(w =>
        {
            foreach (long dim in shape)
                w.Varint(1, (ulong)dim);
            w.Varint(2, Float).String(8, name).Bytes(9, System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray());
        });

    private static byte[] ValueInfo(string name, Dim[] shape, ulong elementType = Float)
    {
        // ValueInfoProto { name, type: TypeProto { tensor_type { elem_type, shape } } }. A
        // dimension is dim_value (1) or dim_param (2); a shape with none is a scalar's.
        var dims = Message(w =>
        {
            foreach (var dim in shape)
            {
                w.Bytes(1, dim.Name is null
                    ? Message(d => d.Varint(1, (ulong)dim.Value))
                    : Message(d => d.String(2, dim.Name)));
            }
        });
        var tensorType = Message(w => w.Varint(1, elementType).Bytes(2, dims));
        var type = Message(w => w.Bytes(1, tensorType));
        return Message(w => w.String(1, name).Bytes(2, type));
    }

    private static byte[] Message(Action<ProtoWriter> write)
    {
        var writer = new ProtoWriter();
        write(writer);
        return writer.ToArray();
    }

    /// <summary>The two protobuf wire types a model needs: varints and length-delimited fields.</summary>
    private sealed class ProtoWriter
    {
        private readonly MemoryStream _stream = new();

        public ProtoWriter Varint(int field, ulong value)
        {
            Raw(((ulong)field << 3) | 0);
            Raw(value);
            return this;
        }

        public ProtoWriter Bytes(int field, byte[] value)
        {
            Raw(((ulong)field << 3) | 2);
            Raw((ulong)value.Length);
            _stream.Write(value);
            return this;
        }

        public ProtoWriter String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public byte[] ToArray() => _stream.ToArray();

        private void Raw(ulong value)
        {
            while (value >= 0x80)
            {
                _stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            _stream.WriteByte((byte)value);
        }
    }
}
