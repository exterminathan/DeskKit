param([Parameter(Position=0)][string]$Cmd = 'status', [Parameter(Position=1)][string]$Arg)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Ddc {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct PHYSICAL_MONITOR { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string desc; }
  delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rc, IntPtr data);
  [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
  [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
  [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] arr);
  [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint cur, out uint max);
  [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr h, byte code, uint val);
  public static List<PHYSICAL_MONITOR> All() {
    var list = new List<PHYSICAL_MONITOR>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, d, r, p) => {
      uint n; if (GetNumberOfPhysicalMonitorsFromHMONITOR(m, out n) && n > 0) {
        var a = new PHYSICAL_MONITOR[n]; if (GetPhysicalMonitorsFromHMONITOR(m, n, a)) list.AddRange(a);
      } return true; }, IntPtr.Zero);
    return list;
  }
  [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(int x, int y, uint flags);
  // Physical monitors of the primary display first, then the rest.
  public static List<PHYSICAL_MONITOR> Ordered() {
    var all = All();
    var res = new List<PHYSICAL_MONITOR>();
    IntPtr pm = MonitorFromPoint(0, 0, 1);
    uint n;
    if (pm != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(pm, out n) && n > 0) {
      var a = new PHYSICAL_MONITOR[n];
      if (GetPhysicalMonitorsFromHMONITOR(pm, n, a)) res.AddRange(a);
    }
    foreach (var m in all) { bool dup = false; foreach (var r in res) if (r.h == m.h) dup = true; if (!dup) res.Add(m); }
    return res;
  }
  public static string Get(IntPtr h, byte c) {
    uint t, cur, max; if (GetVCPFeatureAndVCPFeatureReply(h, c, out t, out cur, out max)) return cur + "/" + max; return "ERR " + Marshal.GetLastWin32Error();
  }
  public static bool Set(IntPtr h, byte c, uint v) { return SetVCPFeature(h, c, v); }
}
'@
# Control a monitor over DDC/CI (Eye Saver module, DeskKit).
#   monitor.ps1 status | on | standby | brightness <0-100> | contrast <0-100>
#               eyesaver on|off|toggle   (bare "toggle" also works)
# Settings: %APPDATA%\DeskKit\eyesaver\settings.json (written with defaults on first run):
#   monitor_match : text to find in the monitor description; "" or no match = primary monitor
#   profiles      : "on" and "off", each a map of VCP code (hex string) to value
#   detect_codes  : VCP codes used to decide "eyesaver on" (code, max value that means on)
# Exit codes: 0 ok, 2 bad arguments, 5 no DDC/CI monitor, 1 other error.
# The monitor may answer only after its OSD has been opened once (firmware quirk).
function Fail($code, $msg) {
  [Console]::Error.WriteLine("ERROR: $msg")
  if (-not $env:DESKKIT_SHELL) {
    try { Add-Type -AssemblyName System.Windows.Forms; [void][System.Windows.Forms.MessageBox]::Show($msg, 'Eye Saver') } catch {}
  }
  exit $code
}
$DataDir = if ($env:DESKKIT_DATA_DIR) { $env:DESKKIT_DATA_DIR } else { Join-Path $env:APPDATA 'DeskKit\eyesaver' }
$SettingsFile = Join-Path $DataDir 'settings.json'
$Default = [ordered]@{
  monitor_match = 'S34J55'
  detect_code   = '0x1A'
  detect_on_max = 19
  profiles      = [ordered]@{
    on  = [ordered]@{ '0x10' = 32; '0x12' = 30; '0x16' = 46; '0x18' = 18; '0x1A' = 1 }
    off = [ordered]@{ '0x10' = 57; '0x12' = 76; '0x16' = 50; '0x18' = 50; '0x1A' = 50 }
  }
}
try {
  if (-not (Test-Path $SettingsFile)) {
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    [IO.File]::WriteAllText($SettingsFile, ($Default | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
  }
  $Cfg = Get-Content $SettingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
} catch { Fail 1 "Could not read settings file: $SettingsFile ($($_.Exception.Message))" }

function Code($v) { $t = ([string]$v).Trim() -replace '^0[xX]', ''; [Convert]::ToByte($t, 16) }
$validCmds = 'status','on','standby','brightness','contrast','eyesaver','toggle'
if ($Cmd -notin $validCmds) { Fail 2 "Unknown command '$Cmd'. Use: status, on, standby, brightness <0-100>, contrast <0-100>, eyesaver on|off|toggle." }
if ($Cmd -eq 'toggle') { $Cmd = 'eyesaver'; $Arg = 'toggle' }
if ($Cmd -in 'brightness','contrast') {
  $n = 0
  if (-not [int]::TryParse($Arg, [ref]$n) -or $n -lt 0 -or $n -gt 100) { Fail 2 "$Cmd needs a number from 0 to 100." }
}
if ($Cmd -eq 'eyesaver' -and $Arg -notin 'on','off','toggle') { Fail 2 'Use: eyesaver on|off|toggle' }

$all = @([Ddc]::Ordered())
if ($all.Count -eq 0) { Fail 5 'No DDC/CI monitor found. Check the cable and that DDC/CI is on in the monitor menu.' }
$m = $null
$match = [string]$Cfg.monitor_match
if ($match) { $m = $all | Where-Object { $_.desc -like "*$match*" } | Select-Object -First 1 }
if (-not $m) { $m = $all[0] }   # fall back to the primary monitor

function Send($code, $val) {
  for ($i = 0; $i -lt 3; $i++) { if ([Ddc]::Set($m.h, [byte]$code, [uint32]$val)) { Start-Sleep -Milliseconds 150; return } ; Start-Sleep -Milliseconds 300 }
  Fail 5 ("DDC/CI write failed for 0x{0:X2}. Open the monitor menu once and try again." -f $code)
}
switch ($Cmd) {
  'status'     {
    "Monitor: $($m.desc)"
    $codes = @($Cfg.profiles.on.PSObject.Properties.Name | ForEach-Object { Code $_ }) + 0xD6 | Select-Object -Unique
    foreach ($c in $codes) { "0x{0:X2} = {1}" -f $c, [Ddc]::Get($m.h, [byte]$c); Start-Sleep -Milliseconds 200 }
  }
  'on'         { Send 0xD6 1 }
  'standby'    { Send 0xD6 4 }
  'brightness' { Send 0x10 ([int]$Arg) }
  'contrast'   { Send 0x12 ([int]$Arg) }
  'eyesaver'   {
    if ($Arg -eq 'toggle') {
      $cur = [Ddc]::Get($m.h, (Code $Cfg.detect_code))
      if ($cur -like 'ERR*') { Fail 5 'The monitor did not answer over DDC/CI. Open the monitor menu once and try again.' }
      $v = [int]($cur -split '/')[0]
      $Arg = if ($v -le [int]$Cfg.detect_on_max) { 'off' } else { 'on' }
    }
    $p = $Cfg.profiles.$Arg
    if (-not $p) { Fail 1 "Profile '$Arg' is missing in settings.json." }
    foreach ($prop in $p.PSObject.Properties) { Send (Code $prop.Name) ([int]$prop.Value) }
  }
}
exit 0
