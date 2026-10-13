using System.Text;
using BandBuzz.Band;

namespace BandBuzz;

/// <summary>Hardware-free checks: proto shape, crypto roundtrip, framing. No BLE, no toasts.</summary>
internal static class SelfTest
{
    public static int Run()
    {
        var failures = 0;
        void Check(bool ok, string name, string extra = "")
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(extra.Length == 0 ? "" : " " + extra)}");
            if (!ok)
                failures++;
        }

        // 1. Step1 wire shape: Command{08 01, 10 1A, 1A len{...}} (matches pc_test build_step1).
        var phone = new byte[16];
        for (var i = 0; i < 16; i++) phone[i] = (byte)i;
        var s1 = XiaomiProto.BuildStep1(phone);
        Check(s1 is [0x08, 0x01, 0x10, 0x1A, ..], "step1-prefix", BitConverter.ToString(s1, 0, Math.Min(4, s1.Length)));
        var (t, st) = XiaomiProto.ParseCommandType(s1);
        Check(t == 1 && st == 26, "step1-type", $"type={t} sub={st}");
        var cmd = XiaomiProto.Parse(s1);
        var auth = XiaomiProto.Find(cmd, 3)!.Raw;
        var pn = XiaomiProto.Find(XiaomiProto.Parse(auth), 30)!.Raw;
        var nonce = XiaomiProto.Find(XiaomiProto.Parse(pn), 1)!.Raw;
        Check(nonce.SequenceEqual(phone), "step1-nonce-roundtrip");

        // 2. Notif + dismiss shapes parse back.
        var notif = XiaomiProto.BuildNotif("Hi", "Body here", "com.google.chrome", "Chrome", 2001);
        var (nt, nst) = XiaomiProto.ParseCommandType(notif);
        Check(nt == 7 && nst == 0, "notif-type", $"type={nt} sub={nst}");
        var dis = XiaomiProto.BuildDismiss(2001, "com.google.chrome");
        var (dt, dst) = XiaomiProto.ParseCommandType(dis);
        Check(dt == 7 && dst == 1, "dismiss-type", $"type={dt} sub={dst}");

        // 3. Title truncation: <=120B and still valid UTF-8 (cut on a char boundary).
        var built = XiaomiProto.BuildNotif(new string('ü', 200), "b", "p", "a", 1);
        var n3raw = XiaomiProto.Find(XiaomiProto.Parse(XiaomiProto.Find(XiaomiProto.Parse(XiaomiProto.Find(XiaomiProto.Parse(built), 9)!.Raw), 3)!.Raw), 1)!.Raw;
        var titleBytes = XiaomiProto.Find(XiaomiProto.Parse(n3raw), 3)!.Raw;
        Check(titleBytes.Length <= 120, "truncate-limit", $"len={titleBytes.Length}");
        var validUtf8 = true;
        try { new UTF8Encoding(false, true).GetString(titleBytes); }
        catch { validUtf8 = false; }
        Check(validUtf8, "truncate-boundary");

        // 4. Crypto roundtrip (BCL AES-CCM, tag 4) + wrong-key rejection.
        var key = XiaomiCrypto.RandomNonce();
        var n4 = new byte[] { 1, 2, 3, 4 };
        var pt = "hello band"u8.ToArray();
        var ct = XiaomiCrypto.Encrypt(key, n4, 1, pt);
        Check(ct.Length == pt.Length + 4, "ccm-tag-len", $"len={ct.Length}");
        Check(XiaomiCrypto.Decrypt(key, n4, 1, ct).SequenceEqual(pt), "ccm-roundtrip");
        var bad = false;
        try { XiaomiCrypto.Decrypt(new byte[16], n4, 1, ct); }
        catch { bad = true; }
        Check(bad, "ccm-wrong-key-fails");

        // 5. Step2 keys: 64B, deterministic, nonce-sensitive (mirrors pc_test step2_keys).
        var secret = Convert.FromHexString("21c22a78e8526957f65b412ff8a64e27");
        var w = new byte[16]; w[0] = 9;
        var k1 = XiaomiCrypto.ComputeStep2Keys(secret, phone, w);
        var k2 = XiaomiCrypto.ComputeStep2Keys(secret, phone, w);
        Check(k1.Length == 64 && k1.SequenceEqual(k2), "hkdf-deterministic");
        var k3 = XiaomiCrypto.ComputeStep2Keys(secret, phone, new byte[16]);
        Check(!k1.SequenceEqual(k3), "hkdf-nonce-sensitive");

        // 6. Nonce12 layout: n4 + 00000000 + idx LE.
        var n12 = XiaomiCrypto.Nonce12([0xAA, 0xBB, 0xCC, 0xDD], 1);
        Check(n12.AsSpan().SequenceEqual(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0, 0, 0, 0, 1, 0, 0, 0 }), "nonce12-layout");

        // 7. Framing: single roundtrip + ack decode; chunked split/reassemble.
        var single = XiaomiFrame.EncodeSingle([1, 2, 3], true, 7);
        Check(single.Length == 6 + 3 && single[2] == 2 && single[3] == 1, "frame-single-header");
        var dec = XiaomiFrame.Decode(single);
        Check(dec is XiaomiFrame.Incoming.Single s && s.Encrypted && s.Payload.SequenceEqual(new byte[] { 7, 0, 1, 2, 3 }),
            "frame-single-roundtrip");
        Check(XiaomiFrame.Decode([0, 0, 3, 0]) is XiaomiFrame.Incoming.Ack a && a.Result == 0, "frame-ack");
        var bigPayload = Enumerable.Range(0, 600).Select(i => (byte)(i & 0xFF)).ToArray();
        var count = XiaomiFrame.ChunkCount(bigPayload.Length, 244);
        Check(count == 3, "frame-chunk-count", $"count={count}");
        Check(XiaomiFrame.ShouldWriteChunked(bigPayload.Length, true, 244), "frame-chunk-decision");
        Check(!XiaomiFrame.ShouldWriteChunked(100, true, 244), "frame-single-decision");
        var start = XiaomiFrame.EncodeChunkedStart(count, true);
        Check(XiaomiFrame.Decode(start) is XiaomiFrame.Incoming.ChunkedStart cs && cs.Count == count && cs.Encrypted,
            "frame-chunkstart-roundtrip");
        var rx = new XiaomiFrame.Reassembler();
        var css = (XiaomiFrame.Incoming.ChunkedStart)XiaomiFrame.Decode(start);
        rx.FeedStart(css);
        byte[]? full = null;
        var stride = 244 - 2;
        for (var i = 1; i <= count; i++)
        {
            var from = (i - 1) * stride;
            var slice = bigPayload[from..Math.Min(i * stride, bigPayload.Length)];
            var pkt = XiaomiFrame.EncodeChunk(i, slice);
            full = rx.Feed((XiaomiFrame.Incoming.Chunk)XiaomiFrame.Decode(pkt));
        }
        Check(full is not null && full.SequenceEqual(bigPayload), "frame-reassemble");

        // 8. App slot mapping.
        Check(Program.SlotFor("Google Chrome").Nid == 2001, "slot-chrome");
        Check(Program.SlotFor("Microsoft Teams").Nid == 2002, "slot-teams");

        // 9. Cross-check vs pc_test vectors (nonce 00..0F, key 21c2...; see README).
        Check(Convert.ToHexString(s1).ToLowerInvariant()
            == "0801101a1a15f201120a10000102030405060708090a0b0c0d0e0f", "vector-step1");
        Check(Convert.ToHexString(XiaomiProto.BuildDeviceInfo()).ToLowerInvariant()
            == "080015000008421a0757696e646f777320e0012a02454e", "vector-devinfo");
        Check(Convert.ToHexString(XiaomiCrypto.ComputeStep2Keys(secret, phone, w)).ToLowerInvariant()
            == "cdb55dd8e94f860517cd4cd9f361075f5fcb032d87b5aa92cb242e15bf960c7c63ce7bd5fc191ace5145d67d85350d930cd4cb3c53021b9c39a2b335efd73e53",
            "vector-hkdf");

        Console.WriteLine(failures == 0 ? "SELFTEST OK" : $"SELFTEST {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}
