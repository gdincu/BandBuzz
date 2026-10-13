using System.Threading.Channels;
using BandBuzz.Band;
using BandBuzz.Notifications;

namespace BandBuzz;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            PrintHelp();
            return 0;
        }
        if (args.Contains("--selftest"))
            return SelfTest.Run();

        var cfg = Config.Load();
        ApplyOverrides(cfg, args);

        if (args.Contains("--diag"))
            return await DiagAsync(cfg);

        if (!XiaomiProtocol.IsValidAuthKey(cfg.AuthKey))
        {
            Console.WriteLine("Key must be 32 hex chars (or 0x+32).");
            return 2;
        }
        try { XiaomiProtocol.ParseMac(cfg.Mac); }
        catch (ArgumentException)
        {
            Console.WriteLine("Use AA:BB:CC:DD:EE:FF.");
            return 2;
        }

        if (args.Contains("--list"))
            return await ListAsync(cfg);
        if (args.Contains("--test"))
            return await TestSendAsync(cfg, args);

        return await WatchAsync(cfg);
    }

    private static void ApplyOverrides(Config cfg, string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mac" when i + 1 < args.Length:
                    cfg.Mac = args[++i];
                    break;
                case "--key" when i + 1 < args.Length:
                    cfg.AuthKey = args[++i];
                    break;
                case "--filter" when i + 1 < args.Length:
                    cfg.Filters = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    break;
            }
        }
    }

    // ---- modes ----

    private static async Task<int> DiagAsync(Config cfg)
    {
        Console.WriteLine($"OS: {Environment.OSVersion.Version} ({(OperatingSystem.IsWindowsVersionAtLeast(11) ? "Win11+" : "Win10")})");
        Console.WriteLine($"Package identity: {(NotificationWatcher.HasPackageIdentity() ? NotificationWatcher.PackageName() : "(none - unpackaged run)")}");
        Console.WriteLine($"Toast access: {await NotificationWatcher.ProbeAccessAsync()}");
        try
        {
            var adapter = await Windows.Devices.Bluetooth.BluetoothAdapter.GetDefaultAsync().AsTask();
            Console.WriteLine($"Bluetooth radio: {(adapter is null ? "none found" : $"present, LE={adapter.IsLowEnergySupported}")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Bluetooth radio: query failed ({ex.Message})");
        }
        var key = cfg.AuthKey.Trim();
        var masked = key.Length <= 6 ? "***" : $"{key[..4]}...{key[^2..]} ({key.Length} chars)";
        Console.WriteLine($"Config: MAC={cfg.Mac} Key={masked} Filters=[{string.Join(",", cfg.Filters)}] Dedup={cfg.DedupSeconds}s MaxWrite={cfg.MaxWrite}");
        Console.WriteLine(NotificationWatcher.HasPackageIdentity()
            ? "Toast path ready if access is Allowed."
            : "Next step: run install.ps1 from the repo root (one command), then run the installed app. BLE-only: --test / --selftest.");
        return 0;
    }

    private static async Task<int> ListAsync(Config cfg)
    {
        using var watcher = new NotificationWatcher(cfg.Filters, TimeSpan.FromSeconds(cfg.DedupSeconds));
        try
        {
            await watcher.StartAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
        var items = await watcher.ListCurrentAsync();
        if (items.Count == 0)
            Console.WriteLine("No current toasts. (Post a Chrome/Teams notification and retry.)");
        foreach (var t in items)
            Console.WriteLine($"[{watcher.DescribeFilter(t)}] {t.App} | {t.Title} | {t.Body}");
        return 0;
    }

    private static async Task<int> TestSendAsync(Config cfg, string[] args)
    {
        var rest = args.SkipWhile(a => a != "--test").Skip(1).ToArray();
        var title = rest.ElementAtOrDefault(0) ?? "Test";
        var body = rest.ElementAtOrDefault(1) ?? "Hello from PC";
        var app = rest.ElementAtOrDefault(2) ?? "Chrome";
        var (nid, package, appName) = SlotFor(app);

        await using var band = new BandClient(cfg.Mac, cfg.AuthKey, Console.WriteLine) { MaxWrite = cfg.MaxWrite };
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await band.ConnectAsync(cts.Token);
            await band.SendNotifAsync(title, body, package, appName, nid, cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            return 1;
        }
        Console.WriteLine("Done - check the band screen. Card stays until overwritten/dismissed.");
        return 0;
    }

    private static async Task<int> WatchAsync(Config cfg)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var ct = cts.Token;

        await using var band = new BandClient(cfg.Mac, cfg.AuthKey, Log) { MaxWrite = cfg.MaxWrite };
        using var watcher = new NotificationWatcher(cfg.Filters, TimeSpan.FromSeconds(cfg.DedupSeconds), Log);

        var queue = Channel.CreateBounded<ToastItem>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        watcher.Added += t => queue.Writer.TryWrite(t);

        try
        {
            await watcher.StartAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"BandBuzz watching. MAC={cfg.Mac} Ctrl+C to stop.");
        _ = Task.Run(() => SendLoopAsync(band, queue.Reader, cfg, ct), ct);

        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Stopping.");
        return 0;
    }

    private static async Task SendLoopAsync(BandClient band, ChannelReader<ToastItem> reader, Config cfg, CancellationToken ct)
    {
        await foreach (var t in reader.ReadAllAsync(ct))
        {
            try
            {
                if (!band.IsReady)
                    await ConnectWithRetryAsync(band, cfg, ct);
                var (nid, package, appName) = SlotFor(t.App);
                var title = string.IsNullOrWhiteSpace(t.Title) ? t.App : t.Title;
                await band.SendNotifAsync($"{appName}: {title}", t.Body, package, appName, nid, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log($"send failed ({ex.Message}), will reconnect on next item");
                band.Disconnect();
                try { await ConnectWithRetryAsync(band, cfg, ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex2) { Log($"reconnect failed: {ex2.Message}"); }
            }
        }
    }

    private static async Task ConnectWithRetryAsync(BandClient band, Config cfg, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await band.ConnectAsync(ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log($"connect failed ({ex.Message}), retry in {cfg.ReconnectSeconds}s");
                await Task.Delay(TimeSpan.FromSeconds(cfg.ReconnectSeconds), ct);
            }
        }
    }

    /// <summary>Stable per-app card slot so a new toast overwrites the old card.</summary>
    internal static (int Nid, string Package, string AppName) SlotFor(string app)
    {
        if (app.Contains("chrome", StringComparison.OrdinalIgnoreCase))
            return (2001, "com.google.chrome", "Chrome");
        if (app.Contains("teams", StringComparison.OrdinalIgnoreCase))
            return (2002, "com.microsoft.teams", "Teams");
        var slot = 2000 + Math.Abs(app.GetHashCode(StringComparison.OrdinalIgnoreCase)) % 900;
        return (slot, "com.bandbuzz.bridge", app.Trim());
    }

    private static void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

    private static void PrintHelp()
    {
        Console.WriteLine("""
            BandBuzz - Chrome/Teams toasts -> Xiaomi Smart Band 8 Active (BLE).
            Usage:
              BandBuzz                        watch + forward (needs the installed app - install.ps1)
              BandBuzz --list                 dump current toasts (tune --filter)
              BandBuzz --test [title] [body] [app]   BLE-only send, no toast access needed
              BandBuzz --selftest             hardware-free crypto/framing checks
              BandBuzz --diag                 environment + access troubleshooter
            Options:
              --mac AA:BB:CC:DD:EE:FF   --key <32hex>   --filter Chrome,Teams
            Config: appsettings.json next to the exe (same keys).
            Packaging: toast access needs the identity package - run install.ps1 once (see README).
            """);
    }
}
