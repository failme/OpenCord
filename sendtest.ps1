# End-to-end send check: drive the real composer with keystrokes, then confirm over REST that the
# message actually landed. Verifies the whole path (TextBox -> Composer -> Session -> REST -> gateway
# echo) rather than just calling the API, which would prove nothing about the app.
param(
  [string]$Channel = "1033170201052725269",   # DM with failme
  [string]$Token   = $env:CLAUDESCORD_TOKEN
)
Add-Type -AssemblyName System.Windows.Forms
$body = "claudescord send test " + (Get-Random -Maximum 999999)

Get-Process ClaudeScord -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$dir = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows"
$prefs = Join-Path $dir "prefs.json"
$j = Get-Content $prefs -Raw | ConvertFrom-Json
$j.LastGuild = 0
$j.LastChannel = [uint64]$Channel
$j | ConvertTo-Json | Set-Content $prefs -Encoding utf8

$env:CLAUDESCORD_TOKEN = $Token
$p = Start-Process -FilePath (Join-Path $dir "ClaudeScord.exe") -PassThru
Start-Sleep -Seconds 15
$p.Refresh()
if ($p.MainWindowHandle -eq 0) { Write-Output "FAIL: no window"; exit 1 }

[void][System.Windows.Forms.SendKeys]  # ensure type is loaded
$sig = '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);'
$fg = Add-Type -MemberDefinition $sig -Name FgWin -PassThru
$fg::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 800

[System.Windows.Forms.SendKeys]::SendWait($body)
Start-Sleep -Milliseconds 600
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Seconds 4

$h = @{ Authorization = $Token }
$msgs = Invoke-RestMethod -Uri "https://discord.com/api/v9/channels/$Channel/messages?limit=5" -Headers $h
$hit = $msgs | Where-Object { $_.content -eq $body }
if ($hit) { Write-Output "PASS: sent and confirmed -> '$body'" }
else { Write-Output "FAIL: not found. newest was: '$($msgs[0].content)'" }

$p.Refresh()
Write-Output ("RSS " + [math]::Round($p.WorkingSet64 / 1MB, 1) + " MB")
if (-not $p.HasExited) { $p.Kill() }
