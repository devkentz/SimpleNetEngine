using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace SimpleNetEngine.Protocol.Memory;

public class ArrayPoolBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int MinimumBufferSize = 32767; // use 32k buffer
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(MinimumBufferSize);
    private int _writeIndex;
    private int _readIndex;

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(_readIndex, WrittenCount);
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(_readIndex, WrittenCount);
    public int WrittenCount => _writeIndex - _readIndex;
    public int Capacity => _buffer.Length;
    public int FreeCapacity => Capacity - _writeIndex;

    public void Advance(int count)
    {
        if (count < 0) throw new ArgumentException("Count must be non-negative", nameof(count));
        if (count > FreeCapacity) throw new ArgumentException("Cannot advance past end of buffer", nameof(count));

        _writeIndex += count;
    }

    public void ReadAdvance(int count)
    {
        if (count < 0) throw new ArgumentException("Count must be non-negative", nameof(count));
        if (count > WrittenCount) throw new ArgumentException("Cannot read past written data", nameof(count));

        _readIndex += count;
        CompactIfNeeded();
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        CheckAndResizeBuffer(sizeHint);
        return _buffer.AsMemory(_writeIndex);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        CheckAndResizeBuffer(sizeHint);
        return _buffer.AsSpan(_writeIndex);
    }


    public ReadOnlySpan<byte> GetReadSpan(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);

        if (_readIndex + size > _writeIndex)
            throw new IndexOutOfRangeException("Not enough data to read.");

        return _buffer.AsSpan(_readIndex, size);
    }

    private void CheckAndResizeBuffer(int sizeHint)
    {
        if (_buffer == null)
            throw new ObjectDisposedException(nameof(ArrayPoolBufferWriter));

        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint));

        int actualSizeHint = sizeHint == 0 ? MinimumBufferSize : sizeHint;

        if (FreeCapacity >= actualSizeHint)
            return;

        if (_readIndex > 0)
        {
            CompactBuffer();
            if (FreeCapacity >= actualSizeHint)
                return;
        }

        int requiredSize = _writeIndex + actualSizeHint;
        int growTo = Math.Max(_buffer.Length * 2, requiredSize);

        byte[] newBuffer = ArrayPool<byte>.Shared.Rent(growTo);
        int bytesToCopy = _writeIndex - _readIndex;

        if (bytesToCopy > 0)
            _buffer.AsSpan(_readIndex, bytesToCopy).CopyTo(newBuffer);

        ArrayPool<byte>.Shared.Return(_buffer);

        _buffer = newBuffer;
        _writeIndex = bytesToCopy;
        _readIndex = 0;
    }

    private void CompactIfNeeded()
    {
        // Compact if more than 50% of the buffer has been read
        if (_readIndex > Capacity / 2)
        {
            CompactBuffer();
        }
    }

    private void CompactBuffer()
    {
        if (_readIndex <= 0) return;

        if (WrittenCount > 0)
        {
            Buffer.BlockCopy(_buffer, _readIndex, _buffer, 0, WrittenCount);
        }

        _writeIndex = WrittenCount;
        _readIndex = 0;
    }

    public void Clear()
    {
        _writeIndex = 0;
        _readIndex = 0;
    }

    public void Dispose()
    {
        if (_buffer != null! && _buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = null!;
            _writeIndex = 0;
            _readIndex = 0;
        }

    }

    public (int ReadIndex, int WriteIndex, int Capacity) GetBufferState()
    {
        return (_readIndex, _writeIndex, Capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt16BigEndian(short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(GetSpan(sizeof(short)), value);
        _writeIndex += sizeof(short);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt16BigEndian(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(GetSpan(sizeof(ushort)), value);
        _writeIndex += sizeof(ushort);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt32BigEndian(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(GetSpan(sizeof(int)), value);
        _writeIndex += sizeof(int);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32BigEndian(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(GetSpan(sizeof(uint)), value);
        _writeIndex += sizeof(uint);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt64BigEndian(long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(GetSpan(sizeof(long)), value);
        _writeIndex += sizeof(long);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt64BigEndian(ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(GetSpan(sizeof(ulong)), value);
        _writeIndex += sizeof(ulong);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSingleBigEndian(float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(GetSpan(sizeof(float)), value);
        _writeIndex += sizeof(float);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteDoubleBigEndian(double value)
    {
        BinaryPrimitives.WriteDoubleBigEndian(GetSpan(sizeof(double)), value);
        _writeIndex += sizeof(double);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt16LittleEndian(short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(GetSpan(sizeof(short)), value);
        _writeIndex += sizeof(short);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt16LittleEndian(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(GetSpan(sizeof(ushort)), value);
        _writeIndex += sizeof(ushort);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt32LittleEndian(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(GetSpan(sizeof(int)), value);
        _writeIndex += sizeof(int);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32LittleEndian(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(GetSpan(sizeof(uint)), value);
        _writeIndex += sizeof(uint);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt64LittleEndian(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(GetSpan(sizeof(long)), value);
        _writeIndex += sizeof(long);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt64LittleEndian(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(GetSpan(sizeof(ulong)), value);
        _writeIndex += sizeof(ulong);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSingleLittleEndian(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(GetSpan(sizeof(float)), value);
        _writeIndex += sizeof(float);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteDoubleLittleEndian(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(GetSpan(sizeof(double)), value);
        _writeIndex += sizeof(double);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        GetSpan(1)[0] = value;
        _writeIndex += 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBoolean(bool value)
    {
        GetSpan(1)[0] = value ? (byte) 1 : (byte) 0;
        _writeIndex += 1;
    }


    const int MaxUtf8BytesPerChar = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUtf8WithBytePrefix(string text)
    {
        var maxBytes = text.Length * MaxUtf8BytesPerChar;
        var span = GetSpan(maxBytes + 1);
        var byteCount = Encoding.UTF8.GetBytes(text, span[1..]); //span[0]은 length
        if (byteCount > byte.MaxValue)
            throw new FormatException("UTF-8 string length too large for byte-prefixed encoding.");

        span[0] = (byte) byteCount;
        Advance(byteCount + 1);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string ReadUtf8WithBytePrefix()
    {
        var length = ReadByte();
        var span = GetReadSpan(length);
        ReadAdvance(length);
        return Encoding.UTF8.GetString(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadInt16LittleEndian()
    {
        short value = BinaryPrimitives.ReadInt16LittleEndian(GetReadSpan(sizeof(short)));
        _readIndex += sizeof(short);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16LittleEndian()
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(GetReadSpan(sizeof(ushort)));
        _readIndex += sizeof(ushort);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32LittleEndian()
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(GetReadSpan(sizeof(int)));
        _readIndex += sizeof(int);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32LittleEndian()
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(GetReadSpan(sizeof(uint)));
        _readIndex += sizeof(uint);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64LittleEndian()
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(GetReadSpan(sizeof(long)));
        _readIndex += sizeof(long);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64LittleEndian()
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(GetReadSpan(sizeof(ulong)));
        _readIndex += sizeof(ulong);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadSingleLittleEndian()
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(GetReadSpan(sizeof(float)));
        _readIndex += sizeof(float);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadDoubleLittleEndian()
    {
        double value = BinaryPrimitives.ReadDoubleLittleEndian(GetReadSpan(sizeof(double)));
        _readIndex += sizeof(double);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadInt16BigEndian()
    {
        short value = BinaryPrimitives.ReadInt16BigEndian(GetReadSpan(sizeof(short)));
        _readIndex += sizeof(short);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16BigEndian()
    {
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(GetReadSpan(sizeof(ushort)));
        _readIndex += sizeof(ushort);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32BigEndian()
    {
        int value = BinaryPrimitives.ReadInt32BigEndian(GetReadSpan(sizeof(int)));
        _readIndex += sizeof(int);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32BigEndian()
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(GetReadSpan(sizeof(uint)));
        _readIndex += sizeof(uint);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64BigEndian()
    {
        long value = BinaryPrimitives.ReadInt64BigEndian(GetReadSpan(sizeof(long)));
        _readIndex += sizeof(long);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64BigEndian()
    {
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(GetReadSpan(sizeof(ulong)));
        _readIndex += sizeof(ulong);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadSingleBigEndian()
    {
        float value = BinaryPrimitives.ReadSingleBigEndian(GetReadSpan(sizeof(float)));
        _readIndex += sizeof(float);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadDoubleBigEndian()
    {
        double value = BinaryPrimitives.ReadDoubleBigEndian(GetReadSpan(sizeof(double)));
        _readIndex += sizeof(double);
        return value;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        var value = GetReadSpan(1);
        _readIndex += sizeof(byte);
        return value[0];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBoolean()
    {
        return ReadByte() == 1;
    }

    public void CopyTo(Span<byte> payload)
    {
        if (payload.Length < WrittenCount)
            throw new ArgumentException("Target array is smaller than the written data length.");

        WrittenSpan.CopyTo(payload);
    }
}