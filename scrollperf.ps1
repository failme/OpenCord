# Frame times while scrolling the message list, which is the surface that has to feel native.
#
# Scrolls continuously with real wheel events so the reporting windows are all "busy" windows —
# a pause inside a window makes the fps figure meaningless. p95 and max are the numbers that matter;
# an average hides the hitch you can see.
param([int]$Wait = 20, [int]$Clicks = 30, [switch]$Keep)

. (Join-Path $PSScriptRoot "winput.ps1")

Get-Process ClaudeScord -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$dir = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows"
Remove-Item (Join-Path $dir "debug.log") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dir "crash.log") -ErrorAction SilentlyContinue
$p = Start-Process -FilePath (Join-Path $dir "ClaudeScord.exe") -ArgumentList "--log" -PassThru
Start-Sleep -Seconds $Wait
$p.Refresh()
if ($p.MainWindowHandle -eq 0) { "no window"; exit 1 }
if (-not [Win]::Focus($p.MainWindowHandle)) { "could not focus the app window"; $p.Kill(); exit 1 }
$g = Grab $p.MainWindowHandle

# Middle of the message list. Alternate direction without pausing, so every reporting window is busy.
$mx = [int]($g.W * 0.5); $my = [int]($g.H * 0.45)
for ($pass = 0; $pass -lt 3; $pass++) {
    Wheel $mx $my (-$Clicks) 90
    Wheel $mx $my $Clicks 90
}
Start-Sleep -Seconds 3

$p.Refresh()
"RSS {0} MB   private {1} MB   threads {2}" -f [math]::Round($p.WorkingSet64/1MB,1),
    [math]::Round($p.PrivateMemorySize64/1MB,1), $p.Threads.Count
Get-Content (Join-Path $dir "debug.log") -ErrorAction SilentlyContinue | Select-String "perf"
$log = Join-Path $dir "crash.log"
if (Test-Path $log) { "--- crash.log ---"; Get-Content $log | Select-Object -First 20 }
if (-not $Keep -and -not $p.HasExited) { $p.Kill() }
