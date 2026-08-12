# Focus + synthetic input, shared by the test harnesses.
#
# SetForegroundWindow on its own is refused when another process owns the foreground — which on this
# machine means the clicks land in whatever browser window happens to be on top. Everything here is
# guarded: Focus verifies it actually won the foreground and returns $false if it did not, and no
# harness may click until it has.

Add-Type @"
using System; using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  public struct R { public int L, T, Rt, B; }
  public const uint DOWN = 0x0002, UP = 0x0004, RDOWN = 0x0008, RUP = 0x0010, WHEEL = 0x0800;

  // Windows only lets a process take the foreground if it owns the last input event or is already
  // attached to the foreground thread's input queue. Tapping ALT satisfies the first rule for our
  // own process; attaching covers the rest.
  public static bool Focus(IntPtr h) {
    for (int i = 0; i < 6; i++) {
      ShowWindow(h, 9);                                   // SW_RESTORE
      keybd_event(0x12, 0, 0, IntPtr.Zero);               // ALT down
      keybd_event(0x12, 0, 2, IntPtr.Zero);               // ALT up
      uint us = GetCurrentThreadId(), them = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
      if (them != us) AttachThreadInput(us, them, true);
      BringWindowToTop(h); SetForegroundWindow(h);
      if (them != us) AttachThreadInput(us, them, false);
      System.Threading.Thread.Sleep(250);
      if (GetForegroundWindow() == h) return true;
    }
    return false;
  }
}
"@ -ErrorAction SilentlyContinue
[Win]::SetProcessDPIAware() | Out-Null

# Window-relative helpers. $script:Origin is set by Grab.
function Grab($hwnd) {
    $r = New-Object Win+R
    [Win]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $script:Origin = $r
    @{ W = $r.Rt - $r.L; H = $r.B - $r.T }
}
function ClickAt($x, $y, [switch]$Right) {
    [Win]::SetCursorPos($script:Origin.L + $x, $script:Origin.T + $y) | Out-Null
    Start-Sleep -Milliseconds 220
    if ($Right) { [Win]::mouse_event([Win]::RDOWN, 0,0,0,[IntPtr]::Zero); [Win]::mouse_event([Win]::RUP, 0,0,0,[IntPtr]::Zero) }
    else        { [Win]::mouse_event([Win]::DOWN,  0,0,0,[IntPtr]::Zero); [Win]::mouse_event([Win]::UP,  0,0,0,[IntPtr]::Zero) }
}
function MoveTo($x, $y) { [Win]::SetCursorPos($script:Origin.L + $x, $script:Origin.T + $y) | Out-Null }
# $clicks MUST be typed: an untyped "-5" compares as a *string* against 0, which is false, so
# every call silently scrolled one direction. Negative = up (dwData +120), matching Windows.
function Wheel($x, $y, [int]$clicks, [int]$ms = 500) {
    [Win]::SetCursorPos($script:Origin.L + $x, $script:Origin.T + $y) | Out-Null
    $dir = if ($clicks -lt 0) { 120 } else { -120 }
    for ($i = 0; $i -lt [math]::Abs($clicks); $i++) {
        [Win]::mouse_event([Win]::WHEEL, 0, 0, $dir, [IntPtr]::Zero)
        Start-Sleep -Milliseconds $ms
    }
}
# Popups here are ToolStripDropDowns — separate top-level windows, so PrintWindow on the main window
# captures the app with a hole where the picker is. Anything involving a popup has to come off the
# screen instead.
function ScreenSnap($hwnd, $path) {
    Add-Type -AssemblyName System.Drawing
    $r = New-Object Win+R
    [Win]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Rt - $r.L; $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { return $null }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "$w x $h"
}
function Snap($hwnd, $path) {
    Add-Type -AssemblyName System.Drawing
    $r = New-Object Win+R
    [Win]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Rt - $r.L; $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { return $null }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc(); [Win]::PrintWindow($hwnd, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "$w x $h"
}
