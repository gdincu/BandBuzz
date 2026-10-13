<#
.SYNOPSIS
  One-command install for BandBuzz: publish -> sparse identity package -> register -> verify.
  No admin, no Visual Studio packaging, no Store. Safe to re-run (idempotent).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The Appx cmdlets only exist in Windows PowerShell 5.1, not PowerShell 7+.
if (-not (Get-Command Add-AppxPackage -ErrorAction SilentlyContinue)) {
  Write-Output "Re-launching under Windows PowerShell 5.1 for Appx cmdlets..."
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
  exit $LASTEXITCODE
}

$Name = 'BandBuzz.Bridge2'
$Publisher = 'CN=BandBuzzDev'
$Version = '1.0.0.0'
$AppId = 'App'
$Repo = Split-Path -Parent $PSCommandPath
$Proj = Join-Path $Repo 'src\BandBuzz\BandBuzz.csproj'
$Pub = Join-Path $Repo 'publish\app'
$PkgDir = Join-Path $Repo 'publish\pkg'
$Msix = Join-Path $Repo 'publish\BandBuzz.identity.msix'

function Fail($msg) { Write-Output "INSTALL FAILED: $msg"; exit 1 }

# ---- 0. prereqs ----
$os = [Environment]::OSVersion.Version
if ($os.Major -lt 10 -or ($os.Major -eq 10 -and $os.Build -lt 19041)) {
  Fail "Windows 10 build 19041+ required (this machine: $os)."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail "dotnet SDK not found (need .NET 9 SDK)." }
$sdkTools = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe' -ErrorAction SilentlyContinue |
  Sort-Object FullName -Descending | Select-Object -First 1
if (-not $sdkTools) { Fail "makeappx.exe not found - install the Windows SDK." }
$makeappx = $sdkTools.FullName
$signtool = Join-Path (Split-Path -Parent $makeappx) 'signtool.exe'
if (-not (Test-Path $signtool)) { Fail "signtool.exe not found next to $makeappx." }
Write-Output "Tools OK ($makeappx)."

# ---- 1. identity strings must match in both manifests ----
# (Version lives only in the package manifest; the fusion <msix> has none.)
$pkgXml = Get-Content (Join-Path $Repo 'src\BandBuzz\Package.appxmanifest') -Raw
$appXml = Get-Content (Join-Path $Repo 'src\BandBuzz\app.manifest') -Raw
foreach ($s in @($Name, $Publisher, $AppId)) {
  if ($pkgXml -notlike "*$s*" -or $appXml -notlike "*$s*") {
    Fail "identity mismatch: '$s' must appear in Package.appxmanifest AND app.manifest."
  }
}
if ($pkgXml -notlike "*$Version*") { Fail "version '$Version' missing from Package.appxmanifest." }

# ---- 2. publish ----
Write-Output "Publishing Release to $Pub ..."
dotnet publish $Proj -c Release -o $Pub
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed." }

# ---- 3. visual assets (resolved from the external location at runtime) ----
# Remove any stale dev-flow manifest beside the exe (unused by this flow).
$stale = Join-Path $Pub 'AppxManifest.xml'
if (Test-Path $stale) { Remove-Item $stale -Force }
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $Pub 'Assets'
if (-not (Test-Path $assets)) { New-Item -ItemType Directory $assets | Out-Null }
foreach ($n in @('StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png')) {
  $p = Join-Path $assets $n
  if (-not (Test-Path $p)) {
    $bmp = New-Object System.Drawing.Bitmap 1, 1
    $bmp.SetPixel(0, 0, [System.Drawing.Color]::White)
    $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
  }
}

# ---- 4. build identity-only msix (manifest only; /nv skips file validation) ----
if (-not (Test-Path $PkgDir)) { New-Item -ItemType Directory $PkgDir | Out-Null }
Copy-Item (Join-Path $Repo 'src\BandBuzz\Package.appxmanifest') (Join-Path $PkgDir 'AppxManifest.xml') -Force
Write-Output "Packing identity msix ..."
& $makeappx pack /o /d $PkgDir /nv /p $Msix
if ($LASTEXITCODE -ne 0) { Fail "makeappx failed." }

# ---- 5. dev cert (reuse if present) + trust ----
$cert = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
  Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey } |
  Select-Object -First 1
if (-not $cert) {
  Write-Output "Creating self-signed code-signing cert ($Publisher) ..."
  $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher `
    -KeyUsage DigitalSignature -FriendlyName 'BandBuzzDev (BandBuzz local install)' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
    -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5)
}
$trusted = Get-ChildItem Cert:\CurrentUser\TrustedPeople -ErrorAction SilentlyContinue |
  Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
$cerPub = Join-Path $Repo 'publish\BandBuzzDev.cer'
Export-Certificate -Cert $cert -FilePath $cerPub | Out-Null
if (-not $trusted) {
  Write-Output "Trusting cert (CurrentUser\TrustedPeople, no admin needed) ..."
  Import-Certificate -FilePath $cerPub -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
}

# ---- 6. sign (temp pfx, deleted afterwards) ----
$plain = [Guid]::NewGuid().ToString('N')
$pfx = Join-Path $env:TEMP 'BandBuzzDev.pfx'
$sec = ConvertTo-SecureString $plain -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $sec | Out-Null
& $signtool sign /fd SHA256 /f $pfx /p $plain $Msix | Out-Null
Remove-Item $pfx -Force
if ($LASTEXITCODE -ne 0) { Fail "signtool failed." }

# ---- 7. register against the publish folder (per-user, no admin) ----
function Test-Admin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Register-Identity {
  Add-AppxPackage -Path $Msix -ExternalLocation (Resolve-Path $Pub).Path -ErrorAction Stop
}

Write-Output "Registering identity package (external location: $Pub) ..."
try {
  Register-Identity
} catch {
  # NOTE: Add-AppxPackage wraps failures in a generic exception - the real
  # deployment HRESULT is only in the message text, so match on that.
  $msg = $_.Exception.Message
  if ($msg -like '*0x80073CFB*') {
    Write-Output "Same version already registered - re-registering ..."
    foreach ($p in Get-AppxPackage -Name $Name) { Remove-AppxPackage -Package $p.PackageFullName }
    Register-Identity
  } elseif ($msg -like '*0x800B0109*') {
    # Package signatures are honored from LocalMachine stores only (the
    # deployment service runs as SYSTEM), so one elevated run is required.
    if (-not (Test-Admin)) {
      Fail ("cert not trusted machine-wide. One-time admin step: right-click PowerShell -> " +
        "Run as administrator, then re-run install.ps1 (it resumes where it left off). " +
        "Everything after that runs unelevated.")
    }
    Write-Output "Trusting cert machine-wide (one time, elevated) ..."
    Import-Certificate -FilePath $cerPub -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    Register-Identity
  } else {
    Fail $_.Exception.Message
  }
}
Write-Output "Registered: $((Get-AppxPackage -Name $Name | Select-Object -First 1).PackageFullName)"

# ---- 7b. toast consent (per-user, no admin): same as the Settings master toggle ----
$consent = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\userNotificationListener'
$cur = (Get-ItemProperty $consent -Name Value -ErrorAction SilentlyContinue).Value
if ($cur -ne 'Allow') {
  Write-Output "Enabling notification-listener consent (same as Settings > Privacy > Notifications) ..."
  if (-not (Test-Path $consent)) { New-Item $consent -Force | Out-Null }
  Set-ItemProperty $consent -Name Value -Value 'Allow'
} else {
  Write-Output "Notification-listener consent already Allowed."
}

# ---- 8. verify: the installed exe must report identity ----
Write-Output "Verifying ..."
$diag = & (Join-Path $Pub 'BandBuzz.exe') --diag
$diag | ForEach-Object { Write-Output ("  " + $_) }
$diagText = $diag -join "`n"
if ($diagText -notlike "*Package identity: $Name*") { Fail "exe still reports no identity - see Troubleshooting in README." }

Write-Output ""
Write-Output "INSTALL OK. Run it with:"
Write-Output "  $Pub\BandBuzz.exe            (watch Chrome/Teams -> band)"
Write-Output "  $Pub\BandBuzz.exe --list     (show current toasts)"
if ($diagText -notlike "*Toast access: Allowed*") {
  Write-Output ""
  Write-Output "ONE REMAINING STEP - toast consent (a Windows privacy prompt, not an install issue):"
  Write-Output "  Open Settings > Privacy > Notifications (Windows 10)"
  Write-Output "  or Privacy & security > Notifications (Windows 11),"
  Write-Output "  allow apps to access notifications, then re-run:"
  Write-Output "  $Pub\BandBuzz.exe --diag    (Toast access should read Allowed)"
}
Write-Output "Tip: re-run install.ps1 after 'git pull' or manifest changes; plain rebuilds need nothing."
Write-Output "Uninstall: Get-AppxPackage -Name $Name | Remove-AppxPackage  (then delete publish\ if wanted)"
