namespace BandBuzz.Band;

/// <summary>Xiaomi BLE identity for Mi Band 8 Active (service FE95).</summary>
internal static class XiaomiProtocol
{
    public static readonly Guid ServiceFe95 = Guid.Parse("0000fe95-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CharCommandRead = Guid.Parse("00000051-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CharCommandWrite = Guid.Parse("00000052-0000-1000-8000-00805f9b34fb");

    public const int AuthType = 1;
    public const int CmdNonce = 26;
    public const int CmdAuth = 27;

    public const int NotifType = 7;
    public const int CmdNotifSend = 0;
    public const int CmdNotifDismiss = 1;
    public const int CmdScreenOnSet = 7;

    /// <summary>32 hex chars, optionally 0x-prefixed.</summary>
    public static bool IsValidAuthKey(string key)
    {
        var k = key.Trim();
        if (k.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            k = k[2..];
        return k.Length == 32 && k.All(c => Uri.IsHexDigit(c));
    }

    public static byte[] ParseAuthKey(string key)
    {
        var k = key.Trim();
        if (k.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            k = k[2..];
        if (k.Length != 32 || !k.All(Uri.IsHexDigit))
            throw new ArgumentException("Auth key must be 32 hex chars (or 0x+32).", nameof(key));
        return Convert.FromHexString(k);
    }

    /// <summary>AA:BB:CC:DD:EE:FF (case-insensitive, ':' or '-' separated) to ulong for WinRT.</summary>
    public static ulong ParseMac(string mac)
    {
        var hex = mac.Trim().Replace(":", "").Replace("-", "");
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit))
            throw new ArgumentException("Use AA:BB:CC:DD:EE:FF.", nameof(mac));
        return Convert.ToUInt64(hex, 16);
    }
}
