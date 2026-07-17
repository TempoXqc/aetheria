using System.Buffers.Binary;
using System.Text;

namespace Aetheria.Shared.Protocol;

/// <summary>
/// A minimal growable little-endian binary writer for building outgoing packets.
/// Zero-allocation-friendly for typical packet sizes: starts on a pooled-ish buffer and
/// only grows when needed. Little-endian is chosen explicitly so the wire format is
/// identical regardless of host architecture.
/// </summary>
public sealed class PacketWriter
{
    private byte[] _buffer;
    private int _length;

    public PacketWriter(int capacity = 256)
    {
        _buffer = new byte[System.Math.Max(capacity, 16)];
        _length = 0;
    }

    /// <summary>Number of bytes written so far.</summary>
    public int Length => _length;

    /// <summary>The bytes written so far, without copying.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    /// <summary>Reset the writer so the underlying buffer can be reused for a new packet.</summary>
    public void Reset() => _length = 0;

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteInt(int value)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void WriteUInt(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void WriteLong(long value)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    public void WriteFloat(float value)
    {
        Ensure(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    /// <summary>Write a UTF-8 string prefixed with a 16-bit byte-length (max 65535 bytes).</summary>
    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
        {
            throw new ArgumentException($"String is too long to serialize ({byteCount} bytes).", nameof(value));
        }

        Ensure(2 + byteCount);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), (ushort)byteCount);
        _length += 2;
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
        _length += byteCount;
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    private void Ensure(int extra)
    {
        int required = _length + extra;
        if (required <= _buffer.Length)
        {
            return;
        }

        int newSize = _buffer.Length * 2;
        while (newSize < required)
        {
            newSize *= 2;
        }

        Array.Resize(ref _buffer, newSize);
    }
}
