using System.Buffers.Binary;
using System.Text;

namespace BandBuzz.Band;

/// <summary>Minimal proto2 codec, auth + notification only (no protobuf dependency). Field numbers match BandMoot's xiaomi.proto.</summary>
internal static class XiaomiProto
{
    // ---- encoding ----

    private static void WriteVarint(List<byte> out_, ulong n)
    {
        while (n > 0x7F)
        {
            out_.Add((byte)((n & 0x7F) | 0x80));
            n >>= 7;
        }
        out_.Add((byte)n);
    }

    private static byte[] FieldVarint(int num, ulong val)
    {
        var b = new List<byte>();
        WriteVarint(b, ((ulong)num << 3) | 0);
        WriteVarint(b, val);
        return [.. b];
    }

    private static byte[] FieldBytes(int num, byte[] data)
    {
        var b = new List<byte>();
        WriteVarint(b, ((ulong)num << 3) | 2);
        WriteVarint(b, (ulong)data.Length);
        b.AddRange(data);
        return [.. b];
    }

    private static byte[] FieldF32(int num, float f)
    {
        var b = new List<byte>();
        WriteVarint(b, ((ulong)num << 3) | 5);
        var tmp = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(tmp, f);
        b.AddRange(tmp);
        return [.. b];
    }

    private static byte[] Cat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var out_ = new byte[total];
        var o = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, out_, o, p.Length);
            o += p.Length;
        }
        return out_;
    }

    public static byte[] BuildStep1(byte[] phoneNonce) => Cat(
        FieldVarint(1, XiaomiProtocol.AuthType),
        FieldVarint(2, XiaomiProtocol.CmdNonce),
        FieldBytes(3, FieldBytes(30, FieldBytes(1, phoneNonce))));

    public static byte[] BuildDeviceInfo(string phoneName = "Windows", float apiLevel = 34f, string region = "EN") => Cat(
        FieldVarint(1, 0),
        FieldF32(2, apiLevel),
        FieldBytes(3, Encoding.UTF8.GetBytes(phoneName)),
        FieldVarint(4, 224),
        FieldBytes(5, Encoding.UTF8.GetBytes(region)));

    public static byte[] BuildStep3(byte[] encNonces, byte[] encDevInfo) => Cat(
        FieldVarint(1, XiaomiProtocol.AuthType),
        FieldVarint(2, XiaomiProtocol.CmdAuth),
        FieldBytes(3, FieldBytes(32, Cat(
            FieldBytes(1, encNonces),
            FieldBytes(2, encDevInfo)))));

    /// <summary>Band card. Truncates like pc_test.</summary>
    public static byte[] BuildNotif(string title, string body, string package, string appName, int nid)
    {
        var ts = DateTime.Now.ToString("yyyyMMdd'T'HHmmss");
        var n3 = Cat(
            FieldBytes(1, Encoding.UTF8.GetBytes(package)),
            FieldBytes(2, Encoding.UTF8.GetBytes(appName)),
            FieldBytes(3, TruncateUtf8(title, 120)),
            FieldBytes(4, []),
            FieldBytes(5, TruncateUtf8(body, 512)),
            FieldBytes(6, Encoding.ASCII.GetBytes(ts)),
            FieldVarint(7, (ulong)nid));
        var notif = FieldBytes(3, FieldBytes(1, n3));
        return Cat(
            FieldVarint(1, XiaomiProtocol.NotifType),
            FieldVarint(2, XiaomiProtocol.CmdNotifSend),
            FieldBytes(9, notif));
    }

    public static byte[] BuildDismiss(int nid, string package)
    {
        var notifId = Cat(
            FieldVarint(1, (ulong)nid),
            FieldBytes(2, Encoding.UTF8.GetBytes(package)));
        var dismiss = FieldBytes(1, notifId);
        var notif = Cat(FieldBytes(4, dismiss));
        return Cat(
            FieldVarint(1, XiaomiProtocol.NotifType),
            FieldVarint(2, XiaomiProtocol.CmdNotifDismiss),
            FieldBytes(9, notif));
    }

    public static byte[] BuildScreenOn() => Cat(
        FieldVarint(1, XiaomiProtocol.NotifType),
        FieldVarint(2, XiaomiProtocol.CmdScreenOnSet),
        FieldBytes(9, FieldVarint(7, 1)));

    private static byte[] TruncateUtf8(string s, int maxBytes)
    {
        var b = Encoding.UTF8.GetBytes(s);
        if (b.Length <= maxBytes)
            return b;
        // Cut on a char boundary.
        var cut = maxBytes;
        while (cut > 0 && (b[cut] & 0xC0) == 0x80)
            cut--;
        return b[..cut];
    }

    // ---- decoding ----

    public sealed record Field(int Num, int Wire, byte[] Raw, ulong Varint);

    public static List<Field> Parse(byte[] buf)
    {
        var pos = 0;
        var out_ = new List<Field>();
        while (pos < buf.Length)
        {
            var (tag, p1) = ReadVarint(buf, pos);
            pos = p1;
            var num = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            switch (wire)
            {
                case 0:
                    var (v, p2) = ReadVarint(buf, pos);
                    pos = p2;
                    out_.Add(new Field(num, wire, [], v));
                    break;
                case 2:
                    var (ln, p3) = ReadVarint(buf, pos);
                    pos = p3;
                    if (pos + (int)ln > buf.Length)
                        throw new InvalidDataException("truncated length-delimited field");
                    var slice = buf[pos..(pos + (int)ln)];
                    pos += (int)ln;
                    out_.Add(new Field(num, wire, slice, 0));
                    break;
                case 5:
                    if (pos + 4 > buf.Length)
                        throw new InvalidDataException("truncated fixed32 field");
                    out_.Add(new Field(num, wire, buf[pos..(pos + 4)], 0));
                    pos += 4;
                    break;
                default:
                    throw new InvalidDataException($"unsupported wire {wire}");
            }
        }
        return out_;
    }

    private static (ulong Value, int Next) ReadVarint(byte[] buf, int pos)
    {
        ulong res = 0;
        var shift = 0;
        while (true)
        {
            if (pos >= buf.Length)
                throw new InvalidDataException("truncated varint");
            var b = buf[pos++];
            res |= (ulong)(b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
                return (res, pos);
            if (shift >= 64)
                throw new InvalidDataException("varint overflow");
        }
    }

    public static Field? Find(List<Field> fields, int num)
    {
        foreach (var f in fields)
            if (f.Num == num)
                return f;
        return null;
    }

    /// <summary>Command bytes -> (watchNonce, watchHmac); throws on shape mismatch.</summary>
    public static (byte[] Nonce, byte[] Hmac) ParseWatchNonce(byte[] commandBytes)
    {
        var cmd = Parse(commandBytes);
        var authRaw = Find(cmd, 3)?.Raw ?? throw new InvalidDataException("missing auth");
        var watchRaw = Find(Parse(authRaw), 31)?.Raw ?? throw new InvalidDataException("missing watchNonce");
        var w = Parse(watchRaw);
        var nonce = Find(w, 1)?.Raw ?? throw new InvalidDataException("missing nonce");
        var hmac = Find(w, 2)?.Raw ?? throw new InvalidDataException("missing hmac");
        return (nonce, hmac);
    }

    public static (int Type, int Subtype) ParseCommandType(byte[] commandBytes)
    {
        var cmd = Parse(commandBytes);
        return ((int)(Find(cmd, 1)?.Varint ?? 0), (int)(Find(cmd, 2)?.Varint ?? 0));
    }
}
