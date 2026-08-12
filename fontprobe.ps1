Add-Type -AssemblyName System.Drawing
$g = [System.Drawing.Graphics]::FromImage((New-Object System.Drawing.Bitmap(10,10)))
foreach ($f in [System.Drawing.FontFamily]::Families) {
  if ($f.Name -like "Nunito*") { Write-Output ("family: '" + $f.Name + "'") }
}
Write-Output "----"
function M($name, $size, $label) {
  try {
    $f = New-Object System.Drawing.Font($name, $size)
    $w = $g.MeasureString("Discord", $f).Width
    $h = $g.MeasureString("Ag", $f).Height
    Write-Output ("{0}: w='Discord'={1:N2} h='Ag'={2:N2}" -f $label, $w, $h)
    $f.Dispose()
  } catch { Write-Output ("{0}: FAIL" -f $label) }
}
M "Nunito" 12.0 "Nunito 12"
M "Nunito" 9.0 "Nunito 9"
M "Nunito" 18.0 "Nunito 18"
M "Nunito SemiBold" 12.0 "Nunito SemiBold 12"
M "Nunito SemiBold" 18.0 "Nunito SemiBold 18"
M "Nunito SemiBold" 10.5 "Nunito SemiBold 10.5"
M "Nunito" 24.0 "Nunito 24"
M "Nunito" 15.0 "Nunito 15"
M "Segoe UI" 12.0 "Segoe UI 12 (ref)"
M "Segoe UI" 9.0 "Segoe UI 9 (ref)"
