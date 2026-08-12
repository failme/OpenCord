# Open and dismiss the message context menu repeatedly.
#
# Worth its own harness: the menu strip is now reused rather than rebuilt, and the failure mode of
# getting that wrong is an ObjectDisposedException thrown from ToolStripManager's message filter on
# the *next* click anywhere in the app — so it only shows up on the second or third open.
param([int]$Wait = 20, [int]$Rounds = 4, [switch]$Keep)

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

for ($i = 0; $i -lt $Rounds; $i++) {
    $y = [int]($g.H * (0.30 + 0.04 * $i))
    ClickAt ([int]($g.W * 0.45)) $y -Right
    Start-Sleep -Milliseconds 1500
    if ($i -eq 0) { ScreenSnap $p.MainWindowHandle (Join-Path $PSScriptRoot "shot-menu.png") | Out-Null }
    ClickAt ([int]($g.W * 0.30)) ([int]($g.H * 0.72))     # dismiss
    Start-Sleep -Milliseconds 900
    $p.Refresh()
    if ($p.HasExited) { "DIED after round $($i + 1)"; break }
}

$p.Refresh()
if ($p.HasExited) { "PROCESS DIED" }
else { "alive after $Rounds right-click rounds, RSS {0} MB" -f [math]::Round($p.WorkingSet64/1MB,1) }
$log = Join-Path $dir "crash.log"
if (Test-Path $log) { "--- crash.log ---"; Get-Content $log | Select-Object -First 14 } else { "(no crash.log)" }
if (-not $Keep -and -not $p.HasExited) { $p.Kill() }
