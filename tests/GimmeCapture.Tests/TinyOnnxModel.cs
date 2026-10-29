using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GimmeCapture.Tests;

/// <summary>
/// The smallest real ONNX model: one Identity node that hands back a float tensor of any four dimensions. ONNX Runtime
/// builds and runs a session on it like any other, so code that loads and runs models can be tested without shipping
/// one. Written out field by field in the protobuf encoding ONNX files use.
/// </summary>
internal static class TinyOnnxModel
{
    public static void WriteIdentity(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Identity());
    }

    public static byte[] Identity()
    {
        byte[] shape = Join(Message(1, Text(2, "n")), Message(1, Text(2, "c")), Message(1, Text(2, "h")), Message(1, Text(2, "w")));
        byte[] tensorType = Join(Number(1, 1 /* FLOAT */), Message(2, shape));
        byte[] type = Message(1, tensorType);
        byte[] node = Join(Text(1, "x"), Text(2, "y"), Text(4, "Identity"));
        byte[] graph = Join(
            Message(1, node),
            Text(2, "identity"),
            Message(11, Join(Text(1, "x"), Message(2, type))),
            Message(12, Join(Text(1, "y"), Message(2, type))));
        return Join(
            Number(1, 7),                   // ir_version
            Message(8, Number(2, 13)),      // opset_import: the default domain at opset 13
            Message(7, graph));
    }

    private static byte[] Number(int field, long value) => Join(Varint((ulong)(field << 3)), Varint((ulong)value));

    private static byte[] Text(int field, string value) => Message(field, Encoding.UTF8.GetBytes(value));

    private static byte[] Message(int field, byte[] payload) =>
        Join(Varint((ulong)((field << 3) | 2)), Varint((ulong)payload.Length), payload);

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
        }
        while (value != 0);

        return bytes.ToArray();
    }

    private static byte[] Join(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (byte[] part in parts)
        {
            stream.Write(part);
        }

        return stream.ToArray();
    }
}
