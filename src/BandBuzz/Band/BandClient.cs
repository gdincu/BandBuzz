using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BandBuzz.Band;

/// <summary>Xiaomi BLE link for Mi Band 8 Active: connect -> auth -> READY -> encrypted sends. Owns ONE connection.</summary>
internal sealed class BandClient : IAsyncDisposable
{
    private readonly string _mac;
    private readonly byte[] _secret;
    private readonly Action<string> _log;

    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _read;
    private GattCharacteristic? _write;

    private XiaomiCrypto.SessionKeys? _keys;
    private int _encIndex = 1;

    private readonly XiaomiFrame.Reassembler _reassembler = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Signalled from the BLE notify thread; consumed by the auth/send flow.
    private TaskCompletionSource<int>? _ackTcs;
    private TaskCompletionSource<bool>? _chunkStartTcs;
    private TaskCompletionSource<bool>? _chunkEndTcs;
    private TaskCompletionSource<(bool Encrypted, byte[] Payload)>? _cmdTcs;
    private int[]? _pendingResend;

    public bool IsReady { get; private set; }

    // 247-3 ATT overhead, same as the tested pc_test value.
    public int MaxWrite { get; set; } = 244;

    public BandClient(string mac, string authKeyHex, Action<string>? log = null)
    {
        _mac = mac;
        _secret = XiaomiProtocol.ParseAuthKey(authKeyHex);
        _log = log ?? Console.WriteLine;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        DisposeGatt();
        IsReady = false;
        _keys = null;
        _encIndex = 1;

        var addr = XiaomiProtocol.ParseMac(_mac);
        _log($"Connecting to {_mac} ... (band takes 1 connection - close phone app first)");
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr).AsTask(ct);
        if (device == null)
            throw new InvalidOperationException($"BLE device not found: {_mac}");
        _device = device;

        var svcResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);
        if (svcResult.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"GATT service discovery failed: {svcResult.Status}");
        var svc = svcResult.Services.FirstOrDefault(s => s.Uuid == XiaomiProtocol.ServiceFe95)
            ?? throw new InvalidOperationException("FE95 service not found");
        _service = svc; // rooted for the session: dropping it can stall notifies on some stacks.

        var charResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct);
        if (charResult.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Characteristic discovery failed: {charResult.Status}");
        _read = charResult.Characteristics.FirstOrDefault(c => c.Uuid == XiaomiProtocol.CharCommandRead)
            ?? throw new InvalidOperationException("0x51 characteristic missing");
        _write = charResult.Characteristics.FirstOrDefault(c => c.Uuid == XiaomiProtocol.CharCommandWrite)
            ?? throw new InvalidOperationException("0x52 characteristic missing");

        // Subscribe BEFORE any write, on both chars like the Android app + pc_test.
        await EnableNotifyAsync(_read, ct);
        try { await EnableNotifyAsync(_write, ct); } catch { /* some stacks refuse 0x52 notify */ }
        _log("Connected, notifications on.");

        await RunAuthAsync(ct);
    }

    private async Task EnableNotifyAsync(GattCharacteristic c, CancellationToken ct)
    {
        c.ValueChanged += OnValueChanged;
        var cc = await c.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
        if (cc != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"notify enable failed for {c.Uuid}: {cc}");
    }

    private async Task RunAuthAsync(CancellationToken ct)
    {
        var phoneNonce = XiaomiCrypto.RandomNonce();
        _log("Auth step 1: sending phone nonce...");
        // Arm the reply waiter BEFORE writing: a fast band reply arriving
        // between the ack and a later-created waiter would otherwise be dropped.
        _cmdTcs = new TaskCompletionSource<(bool, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        await RawWriteAsync(
            XiaomiFrame.EncodeSingle(XiaomiProto.BuildStep1(phoneNonce), false, 0),
            "auth step 1", ct);

        var (enc, payload) = await AwaitOrThrowAsync(_cmdTcs.Task, TimeSpan.FromSeconds(15), "timed out waiting for band reply", ct);
        var (watchNonce, watchHmac) = XiaomiProto.ParseWatchNonce(payload);
        _log($"Auth step 2: watch nonce {watchNonce.Length}B received, verifying...");

        var handled = XiaomiCrypto.HandleWatchNonce(_secret, phoneNonce, watchNonce, watchHmac);
        if (handled is null)
            throw new InvalidOperationException("Auth FAILED: HMAC mismatch - authKey is wrong for this band.");
        var keys = handled.Value.Keys;
        _keys = keys;

        _cmdTcs = new TaskCompletionSource<(bool, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        await RawWriteAsync(
            XiaomiFrame.EncodeSingle(handled.Value.Step3, false, 0),
            "auth step 3", ct);

        var (enc2, payload2) = await AwaitOrThrowAsync(_cmdTcs.Task, TimeSpan.FromSeconds(15), "timed out waiting for band reply", ct);
        if (enc2)
            payload2 = XiaomiCrypto.Decrypt(keys.DecryptionKey, keys.DecryptionNonce, 0, payload2);
        var (type, sub) = XiaomiProto.ParseCommandType(payload2);
        if (type != XiaomiProtocol.AuthType || sub != XiaomiProtocol.CmdAuth)
            _log($"Unexpected auth confirm (type={type} sub={sub}), continuing anyway.");

        IsReady = true;
        _log("Authenticated, link READY.");
        try
        {
            await SendCommandAsync(XiaomiProto.BuildScreenOn(), "screen-on-on-notifications", ct);
        }
        catch (Exception ex)
        {
            _log($"screen-on-on-notifications failed: {ex.Message}");
        }
    }

    public async Task SendNotifAsync(string title, string body, string package, string appName, int nid,
        CancellationToken ct = default)
    {
        if (!IsReady || _keys is null)
            throw new InvalidOperationException("link not READY");
        var plain = XiaomiProto.BuildNotif(title, body, package, appName, nid);
        await SendCommandAsync(plain, $"send '{title}'", ct);
        _log($"Sent: {title}");
    }

    // ---- command write: RawWriteAsync serializes individual writes internally ----

    private async Task SendCommandAsync(byte[] plain, string task, CancellationToken ct)
    {
        var ready = IsReady && _keys is not null;
        var bytes = ready
            ? XiaomiCrypto.Encrypt(_keys!.EncryptionKey, _keys.EncryptionNonce, _encIndex, plain)
            : plain;

        if (!XiaomiFrame.ShouldWriteChunked(bytes.Length, ready, MaxWrite))
        {
            var packet = XiaomiFrame.EncodeSingle(bytes, ready, ready ? _encIndex : 0);
            if (ready)
                _encIndex++;
            await RawWriteAsync(packet, task, ct);
            return;
        }

        var payload = ready ? XiaomiFrame.PrependEncIndex(bytes, _encIndex) : bytes;
        if (ready)
            _encIndex++;
        var count = XiaomiFrame.ChunkCount(payload.Length, MaxWrite);

        _chunkStartTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResend = null;
        await WriteRawAsync(XiaomiFrame.EncodeChunkedStart(count, ready), ct);
        await AwaitOrThrowAsync(_chunkStartTcs.Task, TimeSpan.FromSeconds(5), $"{task}: no chunk-start ack", ct);
        _log($"{task}: chunked-start acked ({count} chunks)");

        var indices = _pendingResend ?? Enumerable.Range(1, count).ToArray();
        _chunkEndTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stride = MaxWrite - 2;
        foreach (var idx in indices)
        {
            var from = (idx - 1) * stride;
            var to = Math.Min(idx * stride, payload.Length);
            await WriteRawAsync(XiaomiFrame.EncodeChunk(idx, payload[from..to]), ct);
        }
        await AwaitOrThrowAsync(_chunkEndTcs.Task, TimeSpan.FromSeconds(8), $"{task}: no chunk-end ack", ct);
        _log($"{task}: chunks acked");
    }

    /// <summary>
    /// Write + wait for band ack(0). The lock is not reentrant: it is only
    /// ever held inside this method, never across calls.
    /// </summary>
    private async Task RawWriteAsync(byte[] packet, string task, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            _ackTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WriteRawAsync(packet, ct);
            var res = await AwaitOrThrowAsync(_ackTcs.Task, TimeSpan.FromSeconds(5), $"{task}: no ack", ct);
            if (res != 0)
                throw new InvalidOperationException($"{task}: band nack result={res}");
            _log($"{task}: ack ok");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteRawAsync(byte[] packet, CancellationToken ct)
    {
        if (_write is null)
            throw new InvalidOperationException("no write char");
        var buf = packet.AsBuffer();
        // Prefer WriteWithoutResponse (fast); fall back to WithResponse like pc_test.
        var r = await _write.WriteValueWithResultAsync(buf, GattWriteOption.WriteWithoutResponse).AsTask(ct);
        if (r is null || r.Status != GattCommunicationStatus.Success)
        {
            r = await _write.WriteValueWithResultAsync(buf).AsTask(ct);
            if (r is null || r.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"ATT write failed: {r?.Status} protoErr={(int)(r?.ProtocolError ?? 0)}");
        }
    }

    private static async Task<T> AwaitOrThrowAsync<T>(Task<T> t, TimeSpan timeout, string msg, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await t.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(msg);
        }
    }

    // ---- inbound (BLE notify thread: decode fast, never block) ----

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        using var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var data = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(data);
        var msg = XiaomiFrame.Decode(data);
        SignalAcks(msg);
        switch (msg)
        {
            case XiaomiFrame.Incoming.Single s:
                // Protocol requires acking every incoming single, else the band stalls.
                _ = WriteFireAndForgetAsync(XiaomiFrame.PayloadAck);
                byte[] payload = s.Payload;
                if (s.Encrypted && _keys is not null)
                {
                    try { payload = XiaomiCrypto.Decrypt(_keys.DecryptionKey, _keys.DecryptionNonce, 0, payload); }
                    catch { /* fall through with raw */ }
                }
                _cmdTcs?.TrySetResult((s.Encrypted, payload));
                break;
            case XiaomiFrame.Incoming.ChunkedStart cs:
                _ = WriteFireAndForgetAsync(XiaomiFrame.ChunkStartAck);
                _reassembler.FeedStart(cs);
                break;
            case XiaomiFrame.Incoming.Chunk c:
                var full = _reassembler.Feed(c);
                if (full is not null)
                {
                    _ = WriteFireAndForgetAsync(XiaomiFrame.ChunkEndAck);
                    _cmdTcs?.TrySetResult((true, full));
                }
                break;
        }
    }

    private void SignalAcks(XiaomiFrame.Incoming msg)
    {
        switch (msg)
        {
            case XiaomiFrame.Incoming.Ack a:
                _ackTcs?.TrySetResult(a.Result);
                break;
            case XiaomiFrame.Incoming.ChunkAckStart:
                _chunkStartTcs?.TrySetResult(true);
                break;
            case XiaomiFrame.Incoming.ChunkAckEnd:
                _chunkEndTcs?.TrySetResult(true);
                break;
            case XiaomiFrame.Incoming.ChunkNack:
                _chunkEndTcs?.TrySetResult(true);
                _ackTcs?.TrySetResult(-1);
                break;
            case XiaomiFrame.Incoming.ChunkResend r:
                _pendingResend = r.Indices;
                _chunkStartTcs?.TrySetResult(true);
                break;
        }
    }

    private async Task WriteFireAndForgetAsync(byte[] packet)
    {
        try
        {
            if (_write is null)
                return;
            await _write.WriteValueAsync(packet.AsBuffer()).AsTask();
        }
        catch { /* best effort on the notify thread */ }
    }

    private void DisposeGatt()
    {
        if (_read is not null)
            _read.ValueChanged -= OnValueChanged;
        if (_write is not null)
            _write.ValueChanged -= OnValueChanged;
        _read = null;
        _write = null;
        _service = null;
        _device?.Dispose();
        _device = null;
        IsReady = false;
    }

    public void Disconnect() => DisposeGatt();

    public ValueTask DisposeAsync()
    {
        DisposeGatt();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
