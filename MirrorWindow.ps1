#Requires -Version 5.1
<#
  MirrorWindow.ps1 - show the LG desktop on the Philips TV as a fullscreen window.

  Why a window instead of a real clone: Windows clone mode needs ONE mode shared
  by both displays, and the TV does not report 2560x1440, so a native clone
  forces everything down to 1920x1080. A window can show 1440p upscaled to the
  TV's native 4K, which is what we actually want.

  Usage:
      MirrorWindow.ps1
      MirrorWindow.ps1 -Fps 60
      MirrorWindow.ps1 -SourceName LG -TargetName Philips

  Keys: Esc closes the window. Start it with mirror-on.cmd, stop with mirror-off.cmd.
  ASCII-only (PS 5.1 + UTF-8 without BOM).
#>
param(
    [string]$SourceName = 'LG',
    [string]$TargetName = 'Philips',
    [int]$Fps = 30
)

$ErrorActionPreference = 'Stop'
if ($Fps -lt 1) { $Fps = 1 }
if ($Fps -gt 60) { $Fps = 60 }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Without DPI awareness the process sees 4K displays with scaling as smaller
# virtual screens (e.g. 3840x2160 @ 300% -> 1280x720), so the window would be
# placed in virtualised coordinates and miss most of the TV.
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class DpiFix { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
'@ -Language CSharp
[DpiFix]::SetProcessDPIAware() | Out-Null

$code = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class Ccd
{
    public const uint QDC_ONLY_ACTIVE = 2;
    public const uint GET_SOURCE_NAME = 1;
    public const uint GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] public struct RATIONAL { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)] public struct SRC { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    public struct TGT
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public RATIONAL refreshRate; public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] public struct PATH { public SRC sourceInfo; public TGT targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SOURCE_NAME
    {
        public uint type; public uint size; public LUID adapterId; public uint id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TARGET_NAME
    {
        public uint type; public uint size; public LUID adapterId; public uint id; public uint flags;
        public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendly;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint f, out uint np, out uint nm);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint f, ref uint np, [Out] PATH[] p, ref uint nm, IntPtr m, IntPtr id);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME n);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME n);

    // gdiName ("\\.\DISPLAY1") -> friendly monitor name ("Philips FTV")
    public static string[] Map()
    {
        uint np = 0, nm = 0;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE, out np, out nm) != 0) return new string[0];
        var p = new PATH[np];
        IntPtr modes = GlobalAlloc(0x40, (UIntPtr)(nm * 64)); // GPTR
        int rc;
        try { rc = QueryDisplayConfig(QDC_ONLY_ACTIVE, ref np, p, ref nm, modes, IntPtr.Zero); }
        finally { if (modes != IntPtr.Zero) GlobalFree(modes); }
        if (rc != 0) return new string[0];

        var list = new List<string>();
        for (int i = 0; i < np; i++)
        {
            var sn = new SOURCE_NAME();
            sn.type = GET_SOURCE_NAME;
            sn.size = (uint)Marshal.SizeOf(typeof(SOURCE_NAME));
            sn.adapterId = p[i].sourceInfo.adapterId;
            sn.id = p[i].sourceInfo.id;

            var tn = new TARGET_NAME();
            tn.type = GET_TARGET_NAME;
            tn.size = (uint)Marshal.SizeOf(typeof(TARGET_NAME));
            tn.adapterId = p[i].targetInfo.adapterId;
            tn.id = p[i].targetInfo.id;

            string gdi = DisplayConfigGetDeviceInfo(ref sn) == 0 ? sn.gdiDeviceName : "";
            string fr = DisplayConfigGetDeviceInfo(ref tn) == 0 ? tn.friendly : "";
            if (gdi.Length > 0) list.Add(gdi + "|" + fr);
        }
        return list.ToArray();
    }
}
'@

Add-Type -TypeDefinition $code -Language CSharp

$map = @{}
foreach ($pair in [Ccd]::Map()) {
    $parts = $pair -split '\|', 2
    if ($parts.Count -eq 2) { $map[$parts[0]] = $parts[1] }
}

$screens = [System.Windows.Forms.Screen]::AllScreens
Write-Host 'Displays:'
foreach ($s in $screens) {
    $label = $map[$s.DeviceName]
    if (-not $label) { $label = '(unknown)' }
    Write-Host ("  {0,-14} {1,5}x{2,-5} at {3},{4}  {5}" -f $s.DeviceName, $s.Bounds.Width, $s.Bounds.Height, $s.Bounds.X, $s.Bounds.Y, $label)
}

function Find-Screen([string]$pattern) {
    foreach ($s in $screens) {
        $label = $map[$s.DeviceName]
        if ($label -and $label -match $pattern) { return $s }
    }
    return $null
}

$source = Find-Screen $SourceName
$target = Find-Screen $TargetName

if (-not $source) { Write-Host ("ERROR: no display matching '{0}'" -f $SourceName) -ForegroundColor Red; exit 1 }
if (-not $target) { Write-Host ("ERROR: no display matching '{0}' (TV off?)" -f $TargetName) -ForegroundColor Red; exit 1 }
if ($source.DeviceName -eq $target.DeviceName) { Write-Host 'ERROR: source and target are the same display' -ForegroundColor Red; exit 1 }

Write-Host ("Mirroring {0} -> {1} @ {2} fps. Esc to stop." -f $source.DeviceName, $target.DeviceName, $Fps) -ForegroundColor Green

$pidFile = Join-Path $env:TEMP 'SoftSplitMirror.pid'
if (Test-Path $pidFile) {
    $oldId = 0
    [int]::TryParse((Get-Content $pidFile -Raw -ErrorAction SilentlyContinue), [ref]$oldId) | Out-Null
    if ($oldId -gt 0) {
        $old = Get-Process -Id $oldId -ErrorAction SilentlyContinue
        if ($old) {
            Write-Host ("Already running (pid {0}). Nothing to do." -f $oldId) -ForegroundColor Yellow
            exit 0
        }
    }
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
}
Set-Content -LiteralPath $pidFile -Value $PID -Encoding ASCII

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.Bounds = $target.Bounds
$form.TopMost = $true
$form.ShowInTaskbar = $false
$form.BackColor = [System.Drawing.Color]::Black
$form.KeyPreview = $true

$picture = New-Object System.Windows.Forms.PictureBox
$picture.Dock = 'Fill'
$picture.SizeMode = [System.Windows.Forms.PictureBoxSizeMode]::StretchImage
$picture.BackColor = [System.Drawing.Color]::Black
$form.Controls.Add($picture)

$form.Add_KeyDown({ if ($_.KeyCode -eq 'Escape') { $form.Close() } })

$srcRect = $source.Bounds
$script:bmp = $null
$script:gfx = $null

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = [int](1000 / $Fps)
$timer.Add_Tick({
    try {
        if ($null -eq $script:bmp) {
            $script:bmp = New-Object System.Drawing.Bitmap $srcRect.Width, $srcRect.Height
            $script:gfx = [System.Drawing.Graphics]::FromImage($script:bmp)
        }
        $script:gfx.CopyFromScreen($srcRect.X, $srcRect.Y, 0, 0, $script:bmp.Size, [System.Drawing.CopyPixelOperation]::SourceCopy)
        $picture.Image = $script:bmp
        $picture.Refresh()
    } catch { }
})
$timer.Start()

try {
    [System.Windows.Forms.Application]::Run($form)
} finally {
    $timer.Stop()
    $timer.Dispose()
    if ($script:gfx) { $script:gfx.Dispose() }
    if ($script:bmp) { $script:bmp.Dispose() }
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
}
