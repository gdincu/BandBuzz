# BandBuzz

Lightweight background tool to push **Chrome and Teams notifications on a Xiaomi Smart Band 8 Active** band over Bluetooth.

## What you need

- A Windows 10 or 11 PC (build 19041 or newer) with Bluetooth turned on
- Your band, unpaired from any phone app (the band talks to one device at a time)
- .NET 9 SDK and the Windows SDK (only needed once, for setup)

## Setup (about 2 minutes)

**1. Install** — open PowerShell in the BandBuzz folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

This publishes the app, gives it the Windows identity it needs to read
notifications, and checks everything works. It runs as a normal user, except
for one step: Windows requires the setup certificate to be trusted
machine-wide, so if the script asks, just re-run the same command once in an
**Administrator** PowerShell — it picks up where it left off.

**2. Send a test to the band** (close the phone app first, if you use one):

```powershell
.\publish\app\BandBuzz.exe --test "Hello" "It works"
```

Look at your band — the card should appear within seconds.

**3. Start watching:**

```powershell
.\publish\app\BandBuzz.exe
```

That's it. New Chrome and Teams notifications now appear on the band.
Press Ctrl+C to stop.

## Everyday use

| What | Command |
|---|---|
| Watch and forward (the normal mode) | `.\publish\app\BandBuzz.exe` |
| See what Windows is showing right now | `.\publish\app\BandBuzz.exe --list` |
| Send a one-off test card | `.\publish\app\BandBuzz.exe --test "Title" "Body"` |
| Check everything is healthy | `.\publish\app\BandBuzz.exe --diag` |

A few things to know:

- **One card per app.** A new Chrome notification replaces the old Chrome
  card (same for Teams), so the band never fills up. Identical repeats
  within a few seconds are ignored.
- **It reconnects by itself.** If Bluetooth drops, BandBuzz keeps retrying
  quietly until the band is back.
- **One Bluetooth connection.** If the band won't connect, make sure no phone
  app is still holding onto it, then try again.

## Settings

Your band's MAC address and Bluetooth key come pre-filled. If you ever need
to change them (or which apps are forwarded), edit `src\BandBuzz\appsettings.json`
— or override on the command line:

```powershell
.\publish\app\BandBuzz.exe --mac AA:BB:CC:DD:EE:FF --key <32-hex-chars> --filter Chrome,Teams
```

You only need to re-run `install.ps1` after updating the app itself;
changing settings needs no reinstall.

## Release build (single file, no .NET prereq)

The default install above is framework-dependent (`publish\app` + the
25MB `Microsoft.Windows.SDK.NET.dll`, needs the .NET 9 runtime). For a
self-contained one-file build instead:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -Release
```

This puts a trimmed single `BandBuzz.exe` (~14MB, no separate SDK dll,
no .NET install needed) in `publish\release` and registers that folder
instead. Only one mode can be registered at a time — re-running with or
without `-Release` switches between them. Everyday commands are the same,
just with the other folder (`.\publish\release\BandBuzz.exe --diag`, …).

## Troubleshooting

**"No package identity" or notification access "Denied"**
You ran the app before installing (or from the wrong folder). Run
`install.ps1`, then use `publish\app\BandBuzz.exe`. If `--diag` shows an
identity but access is still Denied, allow it in Settings → Privacy →
Notifications (Windows 10) or Privacy & security → Notifications
(Windows 11).

**"Auth FAILED: HMAC mismatch"**
The Bluetooth key doesn't match this band. Double-check the key in
`appsettings.json`.

**Nothing connects / device not found**
BandBuzz scans for the band's Bluetooth signal first (up to ~20s), so give
the first attempt a moment. The band also only talks to one device at a
time — close the phone app (or anything else connected to the band) and
make sure the MAC address is right.

**Start here for anything else:** `.\publish\app\BandBuzz.exe --diag`
prints identity, notification access, Bluetooth radio, and settings in one
shot — include it if you ask for help.

## Uninstall

```powershell
Get-AppxPackage -Name BandBuzz.Bridge2 | Remove-AppxPackage
```

Then delete the folder if you like. (Re-running `install.ps1` later is safe.)

## Licence

AGPL-3.0-or-later — see `LICENSE` and `NOTICE.md`.
