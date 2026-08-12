# Click at window-relative coords, then list every top-level window the process owns, so we can tell
# "the popup never opened" apart from "the popup opened and was fine".
param([int]$X = -1, [int]$Y = -1, [int]$Wait = 14, [int]$Settle = 4, [int]$X2 = -1, [int]$Y2 = -1,
      [string]$Out = "", [string]$Keys = "")
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class Pr {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  public struct R { public int L, T, Rt, B; }
  public const uint DOWN = 0x0002, UP = 0x0004;
  public static List<string> Windows(uint want) {
    var outp = new List<string>();
    EnumWindows((h, p) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != want || !IsWindowVisible(h)) return true;
      var t = new StringBuilder(200); GetWindowText(h, t, 200);
      var c = new StringBuilder(200); GetClassName(h, c, 200);
      R r; GetWindowRect(h, out r);
      outp.Add(c + " | '" + t + "' | " + (r.Rt-r.L) + "x" + (r.B-r.T) + " @ " + r.L + "," + r.T);
      return true;
    }, IntPtr.Zero);
    return outp;
  }
  // The popup itself, not the shell and not its drop shadow: the WinForms window that isn't main.
  public static IntPtr Popup(uint want, IntPtr main) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, p) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != want || !IsWindowVisible(h) || h == main) return true;
      var c = new StringBuilder(200); GetClassName(h, c, 200);
      if (c.ToString().StartsWith("WindowsForms")) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@ -ErrorAction SilentlyContinue
[Pr]::SetProcessDPIAware() | Out-Null

$dir = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows"
$log = Join-Path $dir "crash.log"
if (Test-Path $log) { Remove-Item $log -Force }
Get-Process ClaudeScord -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$dbg = Join-Path $dir "debug.log"
if (Test-Path $dbg) { Remove-Item $dbg -Force }
$p = Start-Process -FilePath (Join-Path $dir "ClaudeScord.exe") -ArgumentList "--log" -PassThru
Start-Sleep -Seconds $Wait
$p.Refresh()
[Pr]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 600

Write-Output "--- windows BEFORE click ---"
[Pr]::Windows([uint32]$p.Id) | ForEach-Object { "  $_" }

$r = New-Object Pr+R
[Pr]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null

# SetForegroundWindow from another process is unreliable (Windows' foreground lock silently refuses
# it), and a click on a window that isn't foreground gets spent activating it. Both made runs look
# like "the shortcut did nothing" at random. One real click on dead space activates reliably.
#
# Dead space, specifically: the member list below the last row is a no-op in its hit test. The title
# bar is not usable for this — it forwards the click as WM_NCLBUTTONDOWN/HTCAPTION, which drops
# Windows into its modal window-drag loop and eats every keystroke that follows.
$ok = $false
for ($try = 0; $try -lt 5 -and -not $ok; $try++) {
    [Pr]::SetCursorPos($r.L + [int](($r.Rt - $r.L) * 0.86), $r.T + [int](($r.B - $r.T) * 0.84)) | Out-Null
    [Pr]::mouse_event([Pr]::DOWN, 0, 0, 0, [IntPtr]::Zero)
    [Pr]::mouse_event([Pr]::UP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    $ok = ([Pr]::GetForegroundWindow() -eq $p.MainWindowHandle)
}
if (-not $ok) {
    $fgh = [Pr]::GetForegroundWindow()
    $c = New-Object System.Text.StringBuilder 200
    [Pr]::GetClassName($fgh, $c, 200) | Out-Null
    $t = New-Object System.Text.StringBuilder 200
    [Pr]::GetWindowText($fgh, $t, 200) | Out-Null
    Write-Output ("WARNING: app never took foreground. fg=" + $fgh + " class=" + $c + " title='" + $t + "' main=" + $p.MainWindowHandle)
}
if ($Keys) {
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
} elseif ($X -ge 0) {
    [Pr]::SetCursorPos($r.L + $X, $r.T + $Y) | Out-Null
    Start-Sleep -Milliseconds 400
    [Pr]::mouse_event([Pr]::DOWN, 0, 0, 0, [IntPtr]::Zero)
    [Pr]::mouse_event([Pr]::UP, 0, 0, 0, [IntPtr]::Zero)
}
Start-Sleep -Seconds $Settle

$p.Refresh()
if ($p.HasExited) { Write-Output "PROCESS DIED (first click)" }
else {
  Write-Output "--- windows AFTER click ---"
  [Pr]::Windows([uint32]$p.Id) | ForEach-Object { "  $_" }

  # Second click, in *screen* coords, for hitting whatever the first click opened.
  if ($X2 -ge 0) {
    [Pr]::SetCursorPos($X2, $Y2) | Out-Null
    Start-Sleep -Milliseconds 500
    [Pr]::mouse_event([Pr]::DOWN, 0, 0, 0, [IntPtr]::Zero)
    [Pr]::mouse_event([Pr]::UP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Seconds $Settle
    $p.Refresh()
    if ($p.HasExited) { Write-Output "PROCESS DIED (second click)" }
    else { Write-Output "--- alive after second click ---" }
  }
}
if ($Out -and -not $p.HasExited) {
  $ph = [Pr]::Popup([uint32]$p.Id, $p.MainWindowHandle)
  if ($ph -ne [IntPtr]::Zero) {
    $pr = New-Object Pr+R
    [Pr]::GetWindowRect($ph, [ref]$pr) | Out-Null
    $w = $pr.Rt - $pr.L; $ht = $pr.B - $pr.T
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc(); [Pr]::PrintWindow($ph, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save((Join-Path $PSScriptRoot $Out), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "saved popup -> $Out ($w x $ht)"
  } else { Write-Output "no popup window to capture" }
}
if (Test-Path $log) { Write-Output "--- crash.log ---"; Get-Content $log | Select-Object -First 30 }
else { Write-Output "(no crash.log)" }
if (-not $p.HasExited) { $p.Kill() }
