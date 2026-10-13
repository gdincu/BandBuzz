using Windows.ApplicationModel;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace BandBuzz.Notifications;

public sealed record ToastItem(uint Id, string App, string Title, string Body);

/// <summary>
/// Windows toast listener: Chrome and Teams both post through the WinRT toast
/// pipeline, so this is the lightest reliable source. Needs package identity.
/// </summary>
internal sealed class NotificationWatcher : IDisposable
{
    private readonly IReadOnlyList<string> _filters;
    private readonly TimeSpan _dedupWindow;
    private readonly Action<string> _log;
    private readonly UserNotificationListener _listener = UserNotificationListener.Current;

    private readonly Dictionary<uint, ToastItem> _seen = new();
    private (string App, string Title, string Body, DateTimeOffset At)? _lastSent;
    private bool _subscribed;

    public event Action<ToastItem>? Added;

    public NotificationWatcher(IEnumerable<string> filters, TimeSpan dedupWindow, Action<string>? log = null)
    {
        _filters = filters.ToList();
        _dedupWindow = dedupWindow;
        _log = log ?? Console.WriteLine;
    }

    public static string? PackageName()
    {
        try { return Package.Current.Id.FullName; }
        catch { return null; }
    }

    public static bool HasPackageIdentity() => PackageName() is not null;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!HasPackageIdentity())
            throw new InvalidOperationException(
                "No package identity: you are running unpackaged (dotnet run / bin exe). " +
                "Windows only grants toast access to the installed identity package, " +
                "so this always reports Denied here. " +
                "Run install.ps1 from the repo root, then run the installed app. " +
                "BLE-only checks work unpackaged: BandBuzz --test ... / --selftest / --diag.");
        var access = await _listener.RequestAccessAsync().AsTask(ct);
        if (access != UserNotificationListenerAccessStatus.Allowed)
            throw new InvalidOperationException(
                $"Notification access: {access} - open Settings > Privacy > Notifications " +
                $"(Windows 10) or Privacy & security > Notifications (Windows 11), " +
                $"allow apps to access notifications, then re-run the installed app (publish\\app\\BandBuzz.exe).");

        // Snapshot current toasts as seen: only NEW arrivals get forwarded.
        foreach (var t in await ReadCurrentAsync(ct))
            _seen[t.Id] = t;

        if (!_subscribed)
        {
            _listener.NotificationChanged += OnChanged;
            _subscribed = true;
        }
        _log($"Listening for [{string.Join(", ", _filters)}] (dedup {_dedupWindow.TotalSeconds:0}s).");
    }

    private void OnChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        // Fire-and-forget: never block the WinRT callback.
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(CancellationToken.None); }
            catch (Exception ex) { _log($"refresh failed: {ex.Message}"); }
        });
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var current = await ReadCurrentAsync(ct);
        var ids = new HashSet<uint>();
        foreach (var t in current)
        {
            ids.Add(t.Id);
            if (_seen.ContainsKey(t.Id))
                continue;
            _seen[t.Id] = t;
            if (Matches(t) && !IsDup(t))
            {
                _lastSent = (t.App, t.Title, t.Body, DateTimeOffset.UtcNow);
                Added?.Invoke(t);
            }
        }
        foreach (var id in _seen.Keys.Where(k => !ids.Contains(k)).ToList())
            _seen.Remove(id);
    }

    /// <summary>Current toasts (unfiltered) - for --list tuning.</summary>
    public async Task<IReadOnlyList<ToastItem>> ListCurrentAsync(CancellationToken ct = default)
        => await ReadCurrentAsync(ct);

    private async Task<IReadOnlyList<ToastItem>> ReadCurrentAsync(CancellationToken ct)
    {
        var notifs = await _listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(ct);
        var out_ = new List<ToastItem>();
        foreach (var n in notifs)
        {
            var app = n.AppInfo?.DisplayInfo?.DisplayName ?? "(unknown)";
            var (title, body) = ExtractTexts(n);
            out_.Add(new ToastItem(n.Id, app, title, body));
        }
        return out_;
    }

    private static (string Title, string Body) ExtractTexts(UserNotification n)
    {
        var texts = new List<string>();
        try
        {
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding is not null)
                foreach (var el in binding.GetTextElements())
                    if (!string.IsNullOrWhiteSpace(el.Text))
                        texts.Add(el.Text.Trim());
        }
        catch { /* malformed toast - fall through */ }
        return (
            texts.Count > 0 ? texts[0] : "(no title)",
            texts.Count > 1 ? string.Join("  ·  ", texts.Skip(1)) : string.Empty);
    }

    private bool Matches(ToastItem t)
    {
        if (_filters.Count == 0)
            return true;
        return _filters.Any(f =>
            t.App.Contains(f, StringComparison.OrdinalIgnoreCase) ||
            t.Title.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsDup(ToastItem t)
    {
        if (_lastSent is not { } last)
            return false;
        return last.App == t.App && last.Title == t.Title && last.Body == t.Body
            && (DateTimeOffset.UtcNow - last.At) < _dedupWindow;
    }

    /// <summary>Non-throwing access probe for --diag (status or failure reason).</summary>
    public static async Task<string> ProbeAccessAsync()
    {
        if (!HasPackageIdentity())
            return "Unavailable (no package identity - unpackaged run)";
        try
        {
            var access = await UserNotificationListener.Current.RequestAccessAsync().AsTask();
            return access.ToString();
        }
        catch (Exception ex)
        {
            return $"Error 0x{ex.HResult:X8}: {ex.Message}";
        }
    }

    public string DescribeFilter(ToastItem t) => Matches(t) ? "match" : "filtered-out";

    public void Dispose()
    {
        if (_subscribed)
        {
            _listener.NotificationChanged -= OnChanged;
            _subscribed = false;
        }
    }
}
