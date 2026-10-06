Get-CimInstance Win32_Process | Where-Object {
    ($_.Name -eq 'powershell.exe' -or $_.Name -eq 'python.exe') -and
    $_.CommandLine -match 'pop_modal'
} | ForEach-Object {
    Write-Output ("killing " + $_.Name + " " + $_.ProcessId)
    Stop-Process -Id $_.ProcessId -Force
}
Write-Output "done"
