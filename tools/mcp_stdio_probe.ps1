param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [Parameter(Mandatory=$true)][string]$WorkDir,
  [Parameter(Mandatory=$true)][string]$Request,
  [int]$WaitSeconds = 20
)
$ErrorActionPreference = 'Continue'
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.WorkingDirectory = $WorkDir
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$p = [System.Diagnostics.Process]::Start($psi)

$sb = New-Object System.Text.StringBuilder
$eb = New-Object System.Text.StringBuilder
$outDone = $p.StandardOutput.ReadToEndAsync()
$errDone = $p.StandardError.ReadToEndAsync()

foreach ($line in Get-Content -LiteralPath $Request) {
  $p.StandardInput.WriteLine($line)
}
$p.StandardInput.Flush()

$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline -and -not $p.HasExited -and -not $outDone.IsCompleted) { Start-Sleep -Milliseconds 250 }
Start-Sleep -Milliseconds 500
if (-not $p.HasExited) { $p.Kill() }
$p.WaitForExit(5000) | Out-Null

$out = $outDone.Result
$err = $errDone.Result
Write-Output "EXITED=$($p.HasExited) CODE=$($p.ExitCode)"
Write-Output "--- STDOUT ($($out.Length) chars) ---"
Write-Output $out
Write-Output "--- STDERR tail ---"
Write-Output ($err -split "`n" | Select-Object -Last 25 | Out-String)
