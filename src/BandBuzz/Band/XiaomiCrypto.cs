using System.Security.Cryptography;

namespace BandBuzz.Band;

/// <summary>Xiaomi handshake crypto (BCL HMACSHA256 + AES-CCM only). Index 0 is reserved for auth.</summary>
internal static class XiaomiCrypto
{
    public sealed record SessionKeys(
        byte[] EncryptionKey,
        byte[] DecryptionKey,
        byte[] EncryptionNonce,
        byte[] DecryptionNonce);

    public static byte[] RandomNonce()
    {
        var b = new byte[16];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    /// <summary>HKDF-like 64-byte expansion with label "miwear-auth".</summary>
    public static byte[] ComputeStep2Keys(byte[] secret, byte[] phoneNonce, byte[] watchNonce)
    {
        using var mac = new HMACSHA256([.. phoneNonce, .. watchNonce]);
        var hk = mac.ComputeHash(secret);

        var output = new byte[64];
        var tmp = Array.Empty<byte>();
        var label = "miwear-auth"u8.ToArray();
        byte counter = 1;
        var i = 0;
        using var mac2 = new HMACSHA256(hk);
        while (i < output.Length)
        {
            mac2.Initialize();
            mac2.TransformBlock(tmp, 0, tmp.Length, null, 0);
            mac2.TransformBlock(label, 0, label.Length, null, 0);
            mac2.TransformFinalBlock([counter], 0, 1);
            tmp = mac2.Hash!;
            foreach (var b in tmp)
            {
                if (i >= output.Length)
                    break;
                output[i++] = b;
            }
            counter++;
        }
        return output;
    }

    public static byte[] Hmac(byte[] key, byte[] input)
    {
        using var mac = new HMACSHA256(key);
        return mac.ComputeHash(input);
    }

    public static byte[] Nonce12(byte[] nonce4, int index)
    {
        var n = new byte[12];
        Buffer.BlockCopy(nonce4, 0, n, 0, 4);
        // bytes 4..8 stay zero; index little-endian at 8..12
        BitConverter.TryWriteBytes(n.AsSpan(8), index);
        return n;
    }

    /// <summary>AES-CCM encrypt, 4-byte tag appended.</summary>
    public static byte[] Encrypt(byte[] key, byte[] nonce4, int index, byte[] plaintext)
    {
        var nonce = Nonce12(nonce4, index);
        var ct = new byte[plaintext.Length];
        var tag = new byte[4];
        using var ccm = new AesCcm(key);
        ccm.Encrypt(nonce, plaintext, ct, tag);
        return [.. ct, .. tag];
    }

    public static byte[] Decrypt(byte[] key, byte[] nonce4, int index, byte[] payload)
    {
        if (payload.Length < 4)
            throw new CryptographicException("ciphertext too short");
        var nonce = Nonce12(nonce4, index);
        var ct = payload[..^4];
        var tag = payload[^4..];
        var pt = new byte[ct.Length];
        using var ccm = new AesCcm(key);
        ccm.Decrypt(nonce, ct, tag, pt);
        return pt;
    }

    /// <summary>Verifies the band HMAC; returns keys + step-3 reply, or null on mismatch (wrong auth key).</summary>
    public static (SessionKeys Keys, byte[] Step3)? HandleWatchNonce(
        byte[] secret, byte[] phoneNonce, byte[] watchNonce, byte[] watchHmac)
    {
        var k64 = ComputeStep2Keys(secret, phoneNonce, watchNonce);
        var keys = new SessionKeys(
            EncryptionKey: k64[16..32],
            DecryptionKey: k64[0..16],
            EncryptionNonce: k64[36..40],
            DecryptionNonce: k64[32..36]);

        var calc = Hmac(keys.DecryptionKey, [.. watchNonce, .. phoneNonce]);
        if (!CryptographicOperations.FixedTimeEquals(calc, watchHmac))
            return null;

        var encNonces = Hmac(keys.EncryptionKey, [.. phoneNonce, .. watchNonce]);
        var encDev = Encrypt(keys.EncryptionKey, keys.EncryptionNonce, 0,
            XiaomiProto.BuildDeviceInfo());
        var step3 = XiaomiProto.BuildStep3(encNonces, encDev);
        return (keys, step3);
    }
}
