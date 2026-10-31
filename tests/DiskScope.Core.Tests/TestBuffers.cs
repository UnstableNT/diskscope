using System.Buffers.Binary;
using System.Text;

namespace DiskScope.Core.Tests;

/// <summary>Helpers to build synthetic device answers for parser tests.</summary>
internal static class TestBuffers
{
    public static void SetWord(byte[] data, int word, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(word * 2), value);

    /// <summary>Writes an ATA string: two characters per word, first character in the high byte, space padded.</summary>
    public static void SetAtaString(byte[] data, int firstWord, int lastWord, string text)
    {
        var padded = text.PadRight((lastWord - firstWord + 1) * 2);
        for (var i = 0; i <= lastWord - firstWord; i++)
        {
            SetWord(data, firstWord + i, (ushort)((padded[i * 2] << 8) | padded[(i * 2) + 1]));
        }
    }

    public static void SetAscii(byte[] data, int offset, int length, string text) =>
        Encoding.ASCII.GetBytes(text.PadRight(length)).CopyTo(data, offset);
}
