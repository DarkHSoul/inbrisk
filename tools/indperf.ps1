param([int]$Ms = 3000, [string]$Label = "sample")
$p = Get-Process inbrisk -ErrorAction Stop
$c1 = $p.CPU
Start-Sleep -Milliseconds $Ms
$p.Refresh()
$c2 = $p.CPU
$ws = [math]::Round($p.WorkingSet64 / 1MB, 1)
$cpuPct = [math]::Round(($c2 - $c1) / ($Ms / 1000) / [Environment]::ProcessorCount * 100, 2)
Write-Output "$Label cpu=$cpuPct% ws=${ws}MB threads=$($p.Threads.Count)"
