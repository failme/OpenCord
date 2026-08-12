# Click at window-relative coordinates, then report whether the app survived and what crash.log says.
# Usage: .\click.ps1 -X 1503 -Y 165 -Out after-click.png
param([int]$X, [int]$Y, [int]$Wait = 14, [string]$Out = "", [int]$Settle = 3)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class Clk {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public struct R { public int L, T, Rt, B; }
  public const uint DOWN = 0x0002, UP = 0x0004;
}
"@ -ErrorAction SilentlyContinue
[Clk]::SetProcessDPIAware() | Out-Null

$dir = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows"
$log = Join-Path $dir "crash.log"
if (Test-Path $log) { Remove-Item $log -Force }

Get-Process ClaudeScord -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$p = Start-Process -FilePath (Join-Path $dir "ClaudeScord.exe") -PassThru
Start-Sleep -Seconds $Wait
$p.Refresh()
if ($p.MainWindowHandle -eq 0) { Write-Output "no window"; exit 1 }
[Clk]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 600

$r = New-Object Clk+R
[Clk]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
[Clk]::SetCursorPos($r.L + $X, $r.T + $Y) | Out-Null
Start-Sleep -Milliseconds 400
[Clk]::mouse_event([Clk]::DOWN, 0, 0, 0, [IntPtr]::Zero)
[Clk]::mouse_event([Clk]::UP, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Seconds $Settle

$p.Refresh()
if ($p.HasExited) { Write-Output "PROCESS DIED" }
else {
  Write-Output ("alive, RSS " + [math]::Round($p.WorkingSet64 / 1MB, 1) + " MB")
  if ($Out) {
    $h = [Clk]::GetForegroundWindow()
    if ($h -eq [IntPtr]::Zero) { $h = $p.MainWindowHandle }
    $r2 = New-Object Clk+R
    [Clk]::GetWindowRect($h, [ref]$r2) | Out-Null
    $w = $r2.Rt - $r2.L; $ht = $r2.B - $r2.T
    if ($w -gt 0 -and $ht -gt 0) {
      $bmp = New-Object System.Drawing.Bitmap $w, $ht
      $g = [System.Drawing.Graphics]::FromImage($bmp)
      $hdc = $g.GetHdc(); [Clk]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
      $bmp.Save((Join-Path $PSScriptRoot $Out), [System.Drawing.Imaging.ImageFormat]::Png)
      $g.Dispose(); $bmp.Dispose()
      Write-Output "saved $Out ($w x $ht)"
    }
  }
}
if (Test-Path $log) { Write-Output "--- crash.log ---"; Get-Content $log | Select-Object -First 25 }
else { Write-Output "(no crash.log)" }
if (-not $p.HasExited) { $p.Kill() }
