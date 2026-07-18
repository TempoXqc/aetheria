using System.Buffers.Binary;
using System.Text;

namespace Aetheria.Shared.Protocol;

/// <summary>
/// A bounds-checked little-endian reader over a received datagram. A <c>ref struct</c> so it
/// can wrap a <see cref="ReadOnlySpan{T}"/> with no allocation. Every read validates there are
/// enough bytes remaining and throws <see cref="MalformedPacketException"/> otherwise — the
/// server must never trust the size or shape of bytes arriving from the network.
/// </summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public PacketReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public readonly int Remaining => _data.Length - _position;

    public byte ReadByte()
    {
        Require(1);
        return _data[_position++];
    }

    public bool ReadBool() => ReadByte() != 0;

    public int ReadInt()
    {
        Require(4);
        int value = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_position));
        _position += 4;
        return value;
    }

    public uint ReadUInt()
    {
        Require(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position));
        _position += 4;
        return value;
    }

    public long ReadLong()
    {
        Require(8);
        long value = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_position));
        _position += 8;
        return value;
    }

    public float ReadFloat()
    {
        Require(4);
#if NETSTANDARD2_1
        // ReadSingleLittleEndian is .NET 5+; reinterpret through the int bits instead.
        float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_position)));
#else
        float value = BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(_position));
#endif
        _position += 4;
        return value;
    }

    public string ReadString()
    {
        Require(2);
        ushort byteCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_position));
        _position += 2;
        Require(byteCount);
        string value = Encoding.UTF8.GetString(_data.Slice(_position, byteCount));
        _position += byteCount;
        return value;
    }

    private readonly void Require(int count)
    {
        if (Remaining < count)
        {
            throw new MalformedPacketException(
                $"Expected {count} more byte(s) but only {Remaining} remain.");
        }
    }
}

/// <summary>Thrown when an incoming packet is truncated or otherwise cannot be decoded.</summary>
public sealed class MalformedPacketException : Exception
{
    public MalformedPacketException(string message) : base(message)
    {
    }
}
