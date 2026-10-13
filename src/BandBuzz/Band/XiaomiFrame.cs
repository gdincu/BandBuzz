using System.Buffers.Binary;

namespace BandBuzz.Band;

/// <summary>Xiaomi BLE framing, little-endian. Single: 00 00 02, flag 1/2, u16 index if encrypted. Chunked: 00 00 00, flag, u16 count; end-ack 00 00 01 00.</summary>
internal static class XiaomiFrame
{
    public static readonly byte[] PayloadAck = [0, 0, 3, 0];
    public static readonly byte[] ChunkStartAck = [0, 0, 1, 1];
    public static readonly byte[] ChunkEndAck = [0, 0, 1, 0];

    public static bool ShouldWriteChunked(int payloadSize, bool encrypted, int maxWrite)
    {
        var header = encrypted ? 6 : 4;
        return payloadSize + header > maxWrite;
    }

    public static byte[] EncodeSingle(byte[] payload, bool encrypted, int encIndex)
    {
        var header = encrypted ? 6 : 4;
        var out_ = new byte[header + payload.Length];
        out_[2] = 2;
        out_[3] = (byte)(encrypted ? 1 : 2);
        if (encrypted)
            BinaryPrimitives.WriteInt16LittleEndian(out_.AsSpan(4), (short)encIndex);
        Buffer.BlockCopy(payload, 0, out_, header, payload.Length);
        return out_;
    }

    public static byte[] EncodeChunkedStart(int numChunks, bool encrypted)
    {
        var out_ = new byte[6];
        out_[2] = 0;
        out_[3] = (byte)(encrypted ? 1 : 0);
        BinaryPrimitives.WriteInt16LittleEndian(out_.AsSpan(4), (short)numChunks);
        return out_;
    }

    public static byte[] EncodeChunk(int oneBasedIndex, byte[] bytes)
    {
        var out_ = new byte[2 + bytes.Length];
        BinaryPrimitives.WriteInt16LittleEndian(out_.AsSpan(0), (short)oneBasedIndex);
        Buffer.BlockCopy(bytes, 0, out_, 2, bytes.Length);
        return out_;
    }

    /// <summary>Prepends the running encrypted index for chunked sends.</summary>
    public static byte[] PrependEncIndex(byte[] payload, int encIndex)
    {
        var out_ = new byte[2 + payload.Length];
        BinaryPrimitives.WriteInt16LittleEndian(out_.AsSpan(0), (short)encIndex);
        Buffer.BlockCopy(payload, 0, out_, 2, payload.Length);
        return out_;
    }

    public static int ChunkCount(int payloadSize, int maxWrite) =>
        Math.Max(1, (int)Math.Ceiling(payloadSize / (double)(maxWrite - 2)));

    public abstract record Incoming
    {
        public sealed record ChunkedStart(bool Encrypted, int Count) : Incoming;
        public sealed record Chunk(int Index, byte[] Bytes) : Incoming;
        public sealed record Single(bool Encrypted, byte[] Payload) : Incoming;
        public sealed record Ack(int Result) : Incoming;
        public sealed record ChunkAckStart : Incoming;
        public sealed record ChunkAckEnd : Incoming;
        public sealed record ChunkNack : Incoming;
        public sealed record ChunkResend(int[] Indices) : Incoming;
        public sealed record Unknown(byte[] Raw) : Incoming;
    }

    public static Incoming Decode(byte[] value)
    {
        if (value.Length < 2)
            return new Incoming.Unknown(value);
        var chunk = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0));
        if (chunk != 0)
            return new Incoming.Chunk(chunk, value[2..]);
        if (value.Length < 3)
            return new Incoming.Unknown(value);
        switch (value[2])
        {
            case 0:
                if (value.Length < 6)
                    return new Incoming.Unknown(value);
                return new Incoming.ChunkedStart(value[3] == 1,
                    BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(4)));
            case 1:
                if (value.Length < 4)
                    return new Incoming.Unknown(value);
                return value[3] switch
                {
                    0 => new Incoming.ChunkAckEnd(),
                    1 => new Incoming.ChunkAckStart(),
                    2 => new Incoming.ChunkNack(),
                    5 => DecodeResend(value[4..]),
                    _ => new Incoming.Unknown(value),
                };
            case 2:
                if (value.Length < 4)
                    return new Incoming.Unknown(value);
                return new Incoming.Single(value[3] == 1, value[4..]);
            case 3:
                return new Incoming.Ack(value.Length > 3 ? value[3] : -1);
            default:
                return new Incoming.Unknown(value);
        }
    }

    private static Incoming DecodeResend(byte[] rest)
    {
        var idx = new int[rest.Length / 2];
        for (var i = 0; i < idx.Length; i++)
            idx[i] = BinaryPrimitives.ReadInt16LittleEndian(rest.AsSpan(i * 2));
        return new Incoming.ChunkResend(idx);
    }

    /// <summary>Reassembles band-to-PC chunked transfers.</summary>
    public sealed class Reassembler
    {
        private int _expected;
        private readonly SortedDictionary<int, byte[]> _chunks = new();

        public void FeedStart(Incoming.ChunkedStart start)
        {
            _expected = start.Count;
            _chunks.Clear();
        }

        public byte[]? Feed(Incoming.Chunk chunk)
        {
            if (_expected <= 0 || chunk.Index > _expected)
                return null;
            _chunks[chunk.Index] = chunk.Bytes;
            if (_chunks.Count != _expected)
                return null;
            using var ms = new MemoryStream();
            for (var i = 1; i <= _expected; i++)
            {
                if (!_chunks.TryGetValue(i, out var b))
                    return null;
                ms.Write(b, 0, b.Length);
            }
            _expected = 0;
            _chunks.Clear();
            return ms.ToArray();
        }
    }
}
