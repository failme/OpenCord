# Sample the app's memory over time. Prints working set, private bytes and the .NET managed heap
# separately, because "the app uses 400MB" has very different causes depending on which one grew.
param([int]$Seconds = 90, [int]$Every = 10)

Get-Process ClaudeScord -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$dir = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows"
$p = Start-Process -FilePath (Join-Path $dir "ClaudeScord.exe") -PassThru

# Managed-heap counter comes from the .NET perf counters exposed via the CLR ETW-free path:
# dotnet-counters is not installed here, so read GC heap through the process's own GC via a probe
# is not possible externally — instead report WorkingSet / Private / GDI+USER handles, which is
# enough to tell "managed leak" from "native/Skia" from "handle leak".
Add-Type @"
using System; using System.Runtime.InteropServices;
public class H {
  [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr h, uint flags);
}
"@ -ErrorAction SilentlyContinue

"{0,6}  {1,10}  {2,10}  {3,7}  {4,7}" -f "t(s)", "WorkSet MB", "Private MB", "GDI", "USER"
$t = 0
while ($t -le $Seconds) {
    Start-Sleep -Seconds $Every
    $t += $Every
    $p.Refresh()
    if ($p.HasExited) { "process exited"; break }
    $gdi = [H]::GetGuiResources($p.Handle, 0)
    $usr = [H]::GetGuiResources($p.Handle, 1)
    "{0,6}  {1,10}  {2,10}  {3,7}  {4,7}" -f $t,
        [math]::Round($p.WorkingSet64/1MB,1),
        [math]::Round($p.PrivateMemorySize64/1MB,1), $gdi, $usr
}
if (-not $p.HasExited) { $p.Kill() }
