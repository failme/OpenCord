# Click one window-relative point and screen-capture the result, popups included.
# Usage: .\clickshot.ps1 -X 1563 -Y 34 -Out shot-inbox.png
param([int]$X, [int]$Y, [int]$Wait = 20, [string]$Out = "shot-click.png", [int]$Settle = 4,
      [int]$X2 = -1, [int]$Y2 = -1, [switch]$Keep, [switch]$Right, [switch]$Right2)

. (Join-Path $PSScriptRoot "winput.ps1")

$dir = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows"
Get-Process ClaudeScord -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Remove-Item (Join-Path $dir "crash.log") -ErrorAction SilentlyContinue
$p = Start-Process -FilePath (Join-Path $dir "ClaudeScord.exe") -PassThru
Start-Sleep -Seconds $Wait
$p.Refresh()
if ($p.MainWindowHandle -eq 0) { "no window"; exit 1 }
if (-not [Win]::Focus($p.MainWindowHandle)) { "could not focus the app window"; $p.Kill(); exit 1 }
$g = Grab $p.MainWindowHandle
"window $($g.W) x $($g.H)"

if ($Right) { ClickAt $X $Y -Right } else { ClickAt $X $Y }
Start-Sleep -Seconds $Settle
if ($X2 -ge 0) {
    if ($Right2) { ClickAt $X2 $Y2 -Right } else { ClickAt $X2 $Y2 }
    Start-Sleep -Seconds $Settle
}
ScreenSnap $p.MainWindowHandle (Join-Path $PSScriptRoot $Out)

$p.Refresh()
if ($p.HasExited) { "PROCESS DIED" } else { "alive, RSS {0} MB" -f [math]::Round($p.WorkingSet64/1MB,1) }
$log = Join-Path $dir "crash.log"
if (Test-Path $log) { "--- crash.log ---"; Get-Content $log | Select-Object -First 14 } else { "(no crash.log)" }
if (-not $Keep -and -not $p.HasExited) { $p.Kill() }
