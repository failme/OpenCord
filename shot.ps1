# Launch ClaudeScord (live, using $env:CLAUDESCORD_TOKEN) and PrintWindow it 1:1.
# Usage: .\shot.ps1 -Wait 12 -Out shot.png            (live session)
#        .\shot.ps1 -LaunchArgs "--demo"              (offline demo data)
#        .\shot.ps1 -Keys "^," -Out settings.png      (send keys first: ^ = Ctrl, + = Shift, % = Alt)
param([int]$Wait = 12, [string]$Out = "shot.png", [string]$LaunchArgs = "", [switch]$Keep,
      [string]$Keys = "")
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class Cap3 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public struct R { public int L, T, Rt, B; }
}
"@ -ErrorAction SilentlyContinue
[Cap3]::SetProcessDPIAware() | Out-Null
Get-Process ClaudeScord -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$exe = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows\ClaudeScord.exe"
if ($LaunchArgs) { $p = Start-Process -FilePath $exe -ArgumentList $LaunchArgs -PassThru }
else { $p = Start-Process -FilePath $exe -PassThru }
Start-Sleep -Seconds $Wait
$p.Refresh()
$h = $p.MainWindowHandle
if ($h -eq 0) { Write-Output "no window"; exit 1 }

if ($Keys) {
    Add-Type -AssemblyName System.Windows.Forms
    $fg = Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);' `
                   -Name FgWin2 -PassThru -ErrorAction SilentlyContinue
    if ($fg) { $fg::SetForegroundWindow($h) | Out-Null }
    Start-Sleep -Milliseconds 700
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Start-Sleep -Seconds 3
    # Settings and the dialogs are their own top-level Forms, so the shell's handle would capture
    # the window behind them. Whatever the keystroke brought up is the foreground window.
    $fgh = [Cap3]::GetForegroundWindow()
    if ($fgh -ne [IntPtr]::Zero) { $h = $fgh }
}
$r = New-Object Cap3+R
[Cap3]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Rt - $r.L; $ht = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[Cap3]::PrintWindow($h, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc)
$bmp.Save((Join-Path $PSScriptRoot $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
$p.Refresh()
$mb = [math]::Round($p.WorkingSet64 / 1MB, 1)
Write-Output ("saved " + $Out + "  window " + $w + "x" + $ht + "  RSS " + $mb + " MB")
if (-not $Keep) { if (-not $p.HasExited) { $p.Kill() } }
