using System.Text;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// One-node ONNX models, encoded here so the tests need no model file. Each is an opset-13 graph
/// from a float input <c>x</c> to a float output <c>y</c> of the same shape.
/// </summary>
internal static class OnnxModel
{
    /// <summary><c>y = -x</c>: every output is exact, so a wrong or stale input shows at once.</summary>
    public static byte[] Negate(params long[] shape) => SingleNode("Neg", Array.ConvertAll(shape, d => new Dim(d)));

    /// <summary><see cref="Negate(long[])"/> with named, free dimensions where the shape says so.</summary>
    public static byte[] Negate(params Dim[] shape) => SingleNode("Neg", shape);

    /// <summary>One dimension of a model's shape: a fixed size, or a name the model leaves free.</summary>
    public readonly record struct Dim(long Value, string? Name = null)
    {
        public static implicit operator Dim(long value) => new(value);

        public static implicit operator Dim(string name) => new(0, name);
    }

    private static byte[] SingleNode(string opType, Dim[] shape)
    {
        // ModelProto: ir_version 8, graph, opset_import { version 13 }.
        var node = Message(w => w.String(1, "x").String(2, "y").String(4, opType));
        var graph = Message(w => w
            .Bytes(1, node)
            .String(2, "g")
            .Bytes(11, ValueInfo("x", shape))
            .Bytes(12, ValueInfo("y", shape)));
        var opset = Message(w => w.Varint(2, 13));
        return Message(w => w.Varint(1, 8).String(2, "frameflow-tests").Bytes(7, graph).Bytes(8, opset));
    }

    private static byte[] ValueInfo(string name, Dim[] shape)
    {
        // ValueInfoProto { name, type: TypeProto { tensor_type { elem_type FLOAT, shape } } }. A
        // dimension is dim_value (1) or dim_param (2).
        var dims = Message(w =>
        {
            foreach (var dim in shape)
            {
                w.Bytes(1, dim.Name is null
                    ? Message(d => d.Varint(1, (ulong)dim.Value))
                    : Message(d => d.String(2, dim.Name)));
            }
        });
        var tensorType = Message(w => w.Varint(1, 1).Bytes(2, dims));
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
