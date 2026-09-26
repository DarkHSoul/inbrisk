$closed = 0
Get-Process notepad -ErrorAction SilentlyContinue | ForEach-Object {
  if ($_.MainWindowTitle -like "*bench-doc*") {
    $_.CloseMainWindow() | Out-Null
    $script:closed++
  }
}
Write-Output ("closed bench notepads: " + $closed)
