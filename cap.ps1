# DPI-aware capture: PrintWindow the app window into a 1:1 bitmap.
param([int]$Wait = 5, [string]$Out = "shot2.png", [string]$LaunchArgs = "--demo")
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class Cap2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public struct R { public int L, T, Rt, B; }
}
"@
[Cap2]::SetProcessDPIAware() | Out-Null
Get-Process ClaudeScord -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
$p = Start-Process -FilePath "bin\Debug\net8.0-windows\ClaudeScord.exe" -ArgumentList $LaunchArgs -PassThru
Start-Sleep -Seconds $Wait
$p.Refresh()
$h = $p.MainWindowHandle
$r = New-Object Cap2+R
[Cap2]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Rt - $r.L; $ht = $r.B - $r.T
Write-Output ("window: " + $w + "x" + $ht)
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[Cap2]::PrintWindow($h, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc)
$bmp.Save((Join-Path $PWD $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("saved " + $Out)
if (-not $p.HasExited) { $p.Kill() }
