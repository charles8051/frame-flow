using System.Text;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// One-node ONNX models, encoded here so the tests need no model file. Each is an opset-13 graph
/// from a float input <c>x</c> to a float output <c>y</c> of the same shape.
/// </summary>
internal static class OnnxModel
{
    /// <summary><c>y = -x</c>: every output is exact, so a wrong or stale input shows at once.</summary>
    public static byte[] Negate(params long[] shape) => SingleNode("Neg", shape);

    private static byte[] SingleNode(string opType, long[] shape)
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

    private static byte[] ValueInfo(string name, long[] shape)
    {
        // ValueInfoProto { name, type: TypeProto { tensor_type { elem_type FLOAT, shape } } }.
        var dims = Message(w =>
        {
            foreach (long dim in shape)
                w.Bytes(1, Message(d => d.Varint(1, (ulong)dim)));
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
