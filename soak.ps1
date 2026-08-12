# Does the memory come back? Drives a picker for real, then watches whether closing it gives the RAM
# back — which is the actual complaint, not the peak.
#
#   .\soak.ps1                 gif picker
#   .\soak.ps1 -Which sticker  sticker picker
#   .\soak.ps1 -Which emoji    emoji picker
param([int]$Wait = 16, [int]$After = 60, [string]$Which = "gif", [switch]$Keep, [switch]$Shots)

. (Join-Path $PSScriptRoot "winput.ps1")

Get-Process ClaudeScord -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$dir = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows"
if (Test-Path (Join-Path $dir "crash.log")) { Remove-Item (Join-Path $dir "crash.log") -Force }
$p = Start-Process -FilePath (Join-Path $dir "ClaudeScord.exe") -ArgumentList "--log" -PassThru
Start-Sleep -Seconds $Wait
$p.Refresh()
if ($p.MainWindowHandle -eq 0) { "no window"; exit 1 }
if (-not [Win]::Focus($p.MainWindowHandle)) { "could not focus the app window - refusing to click"; $p.Kill(); exit 1 }

$g = Grab $p.MainWindowHandle
$w = $g.W; $h = $g.H

function Sample($label) {
    $p.Refresh()
    if ($p.HasExited) { "$label : PROCESS DIED"; exit 1 }
    "{0,-22} ws {1,6} MB   private {2,6} MB   threads {3,4}   handles {4,5}" -f $label,
        [math]::Round($p.WorkingSet64/1MB,1), [math]::Round($p.PrivateMemorySize64/1MB,1),
        $p.Threads.Count, $p.HandleCount
}

# Composer trailing buttons, right to left: emoji, sticker, gif.
#
# Physical pixels at 150% scale, derived from the design constants rather than eyeballed: the well's
# right edge is inboard of the member list (M.MembersWidth), buttons are M.HeaderBtn boxes at
# M.HeaderBtnPitch, inset 12. Re-derive these if the composer geometry moves again — stale
# coordinates click on nothing and the run looks like "memory never spiked".
$by = $h - 76
$bx = switch ($Which) { "emoji" { $w - 462 } "sticker" { $w - 522 } default { $w - 582 } }

Sample "idle (baseline)"
ClickAt $bx $by
Start-Sleep -Seconds 7
Sample "picker open"
if ($Shots) { ScreenSnap $p.MainWindowHandle (Join-Path $PSScriptRoot "soak-open.png") }
Wheel ($w - 300) ($h - 320) 8 700
Start-Sleep -Seconds 5
Sample "after scrolling"
Wheel ($w - 300) ($h - 320) 8 700
Start-Sleep -Seconds 5
Sample "after more scrolling"
if ($Shots) { ScreenSnap $p.MainWindowHandle (Join-Path $PSScriptRoot "soak-scrolled.png") }

# Click far away in the message list to dismiss the popup.
ClickAt ([int]($w * 0.45)) ([int]($h * 0.4))
Start-Sleep -Seconds 2
Sample "picker closed"

for ($t = 10; $t -le $After; $t += 10) {
    Start-Sleep -Seconds 10
    Sample "+${t}s idle"
}

$log = Join-Path $dir "crash.log"
if (Test-Path $log) { "--- crash.log ---"; Get-Content $log | Select-Object -First 20 }
Get-Content (Join-Path $dir "debug.log") -ErrorAction SilentlyContinue | Select-String "media" | Select-Object -Last 22
if (-not $Keep -and -not $p.HasExited) { $p.Kill() }
