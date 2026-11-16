#Requires -Version 5.1
<#
  MirrorDisplay.ps1 - one-click software mirroring of the LG (main) display
  onto the Philips TV, while the AOC stays an independent extended display.

  How it works: Windows' own display topology API (CCD / SetDisplayConfig).
  The TV target is pointed at the SAME source as the LG target, so both show
  one desktop. The AOC keeps its own source. No OBS, no extra software.

  Usage:
      MirrorDisplay.ps1 -Action on       mirror LG -> TV
      MirrorDisplay.ps1 -Action off      back to normal extend
      MirrorDisplay.ps1 -Action status   show current topology

  ASCII-only on purpose (PS 5.1 + UTF-8 without BOM).
#>
param(
    [ValidateSet('on', 'off', 'status')]
    [string]$Action = 'status'
)

$ErrorActionPreference = 'Stop'

$code = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class Mirror
{
    public const uint QDC_ONLY_ACTIVE_PATHS = 2;
    public const uint SDC_TOPOLOGY_EXTEND   = 4;
    public const uint SDC_APPLY             = 0x80;
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    public const uint SDC_ALLOW_CHANGES     = 0x400;
    public const uint INVALID_MODE_IDX      = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public RATIONAL refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct REGION2D { public uint cx; public uint cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public RATIONAL hSyncFreq;
        public RATIONAL vSyncFreq;
        public REGION2D activeSize;
        public REGION2D totalSize;
        public uint videoStandard;
        public uint scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TARGET_MODE { public VIDEO_SIGNAL_INFO targetVideoSignalInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SOURCE_MODE { public uint width; public uint height; public uint pixelFormat; public POINTL position; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DESKTOP_IMAGE_INFO { public POINTL PathSourceSize; public RECT DesktopImageRegion; public RECT DesktopImageClip; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public TARGET_MODE targetMode;
        [FieldOffset(16)] public SOURCE_MODE sourceMode;
        [FieldOffset(16)] public DESKTOP_IMAGE_INFO desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TARGET_DEVICE_NAME
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] PATH_INFO[] paths,
        ref uint numMode, [Out] MODE_INFO[] modes, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPath, [In] PATH_INFO[] paths,
        uint numMode, [In] MODE_INFO[] modes, uint flags);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME deviceName);

    static string TargetName(PATH_INFO p)
    {
        var t = new TARGET_DEVICE_NAME();
        t.type = 2;
        t.size = (uint)Marshal.SizeOf(typeof(TARGET_DEVICE_NAME));
        t.adapterId = p.targetInfo.adapterId;
        t.id = p.targetInfo.id;
        if (DisplayConfigGetDeviceInfo(ref t) == 0) return t.monitorFriendlyDeviceName;
        return "?";
    }

    static string SourceModeText(MODE_INFO[] modes, uint idx)
    {
        if (idx == INVALID_MODE_IDX || idx >= modes.Length) return "(none)";
        var m = modes[idx];
        if (m.infoType == 1) return m.sourceMode.width + "x" + m.sourceMode.height;
        return "(target mode)";
    }

    static string TargetModeText(MODE_INFO[] modes, uint idx)
    {
        if (idx == INVALID_MODE_IDX || idx >= modes.Length) return "(none)";
        var m = modes[idx];
        if (m.infoType == 2)
            return m.targetMode.targetVideoSignalInfo.activeSize.cx + "x" + m.targetMode.targetVideoSignalInfo.activeSize.cy;
        return "(source mode)";
    }

    public static string[] Query()
    {
        uint numPath = 0, numMode = 0;
        int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPath, out numMode);
        if (err != 0) return new string[] { "ERROR GetDisplayConfigBufferSizes=" + err };

        var paths = new PATH_INFO[numPath];
        var modes = new MODE_INFO[numMode];
        err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPath, paths, ref numMode, modes, IntPtr.Zero);
        if (err != 0) return new string[] { "ERROR QueryDisplayConfig=" + err };

        var list = new List<string>();
        var sources = new Dictionary<uint, int>();
        for (int i = 0; i < numPath; i++)
        {
            uint sid = paths[i].sourceInfo.id;
            if (sources.ContainsKey(sid)) sources[sid]++; else sources[sid] = 1;
        }

        for (int i = 0; i < numPath; i++)
        {
            string name = TargetName(paths[i]);
            string src = SourceModeText(modes, paths[i].sourceInfo.modeInfoIdx);
            string tgt = TargetModeText(modes, paths[i].targetInfo.modeInfoIdx);
            string cloned = sources[paths[i].sourceInfo.id] > 1 ? "  [CLONED with another display]" : "";
            list.Add(string.Format("{0,-18} source={1} {2}  target={3}{4}",
                name, paths[i].sourceInfo.id, src, tgt, cloned));
        }
        return list.ToArray();
    }

    // Point the TV target at the LG source => duplicate. AOC keeps its own source.
    public static string SetClone()
    {
        uint numPath = 0, numMode = 0;
        int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPath, out numMode);
        if (err != 0) return "ERROR buffer sizes=" + err;

        var paths = new PATH_INFO[numPath];
        var modes = new MODE_INFO[numMode];
        err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPath, paths, ref numMode, modes, IntPtr.Zero);
        if (err != 0) return "ERROR query=" + err;

        int lg = -1, tv = -1;
        for (int i = 0; i < numPath; i++)
        {
            string n = TargetName(paths[i]);
            if (n.IndexOf("Philips", StringComparison.OrdinalIgnoreCase) >= 0) tv = i;
            else if (n.IndexOf("LG", StringComparison.OrdinalIgnoreCase) >= 0) lg = i;
        }

        if (lg < 0) return "ERROR: main LG display not found";
        if (tv < 0) return "ERROR: Philips TV not found (is it powered on and connected?)";
        if (lg == tv) return "ERROR: same display matched twice";

        PATH_SOURCE_INFO lgSource = paths[lg].sourceInfo;
        paths[tv].sourceInfo = lgSource;

        err = SetDisplayConfig(numPath, paths, numMode, modes,
            SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_APPLY);
        if (err != 0) return "ERROR SetDisplayConfig=" + err + " (0x" + err.ToString("X") + ")";
        return "OK";
    }

    public static string SetExtend()
    {
        int err = SetDisplayConfig(0, null, 0, null, SDC_TOPOLOGY_EXTEND | SDC_APPLY);
        if (err != 0) return "ERROR SetDisplayConfig(extend)=" + err + " (0x" + err.ToString("X") + ")";
        return "OK";
    }
}
'@

Add-Type -TypeDefinition $code -Language CSharp

switch ($Action) {
    'status' {
        [Mirror]::Query() | ForEach-Object { Write-Host $_ }
    }
    'on' {
        $r = [Mirror]::SetClone()
        if ($r -ne 'OK') { Write-Host $r -ForegroundColor Red; exit 1 }
        Start-Sleep -Milliseconds 700
        Write-Host 'Mode: mirror LG -> TV' -ForegroundColor Green
        [Mirror]::Query() | ForEach-Object { Write-Host ('  ' + $_) }
    }
    'off' {
        $r = [Mirror]::SetExtend()
        if ($r -ne 'OK') { Write-Host $r -ForegroundColor Red; exit 1 }
        Start-Sleep -Milliseconds 700
        Write-Host 'Mode: extend (normal)' -ForegroundColor Green
        [Mirror]::Query() | ForEach-Object { Write-Host ('  ' + $_) }
    }
}
