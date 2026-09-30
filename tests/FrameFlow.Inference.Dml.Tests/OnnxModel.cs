using System.Text;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// Small ONNX models, encoded here so the tests need no model file. Each is an opset-13 graph on a
/// float input <c>x</c>.
/// </summary>
internal static class OnnxModel
{
    // TensorProto.DataType values.
    private const ulong Float = 1;
    private const ulong Int64 = 7;

    /// <summary><c>y = -x</c>: every output is exact, so a wrong or stale input shows at once.</summary>
    public static byte[] Negate(params long[] shape) => SingleNode("Neg", Array.ConvertAll(shape, d => new Dim(d)));

    /// <summary><see cref="Negate(long[])"/> with named, free dimensions where the shape says so.</summary>
    public static byte[] Negate(params Dim[] shape) => SingleNode("Neg", shape);

    /// <summary><c>y = op(x)</c>, for an operator with one input, one output and no attributes set.</summary>
    public static byte[] Unary(string opType, params long[] shape) =>
        SingleNode(opType, Array.ConvertAll(shape, d => new Dim(d)));

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
