$ErrorActionPreference = "Stop"
$exe = "C:\Users\Ahmet\AppData\Local\Programs\Inbrisk\inbrisk.exe"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = "mcp --log-level Warning"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$p = New-Object System.Diagnostics.Process
$p.StartInfo = $psi
$p.Start() | Out-Null

function Send($obj) {
  $json = $obj | ConvertTo-Json -Depth 20 -Compress
  $p.StandardInput.WriteLine($json)
  $p.StandardInput.Flush()
}

# read one line with timeout
function ReadLine($timeoutMs) {
  $t = $p.StandardOutput.ReadLineAsync()
  if (-not $t.Wait($timeoutMs)) { return $null }
  return $t.Result
}

Send @{ jsonrpc = "2.0"; id = 1; method = "initialize"; params = @{
  protocolVersion = "2025-06-18"
  capabilities = @{}
  clientInfo = @{ name = "dump-probe"; version = "1.0.0" }
} }

$init = ReadLine 30000
Write-Output "=== INITIALIZE RESPONSE ==="
if ($init) { Write-Output $init } else { Write-Output "(timeout)" }

Send @{ jsonrpc = "2.0"; method = "notifications/initialized"; params = @{} }

Send @{ jsonrpc = "2.0"; id = 2; method = "tools/list"; params = @{} }

$tools = ReadLine 60000
Write-Output ""
Write-Output "=== TOOLS/LIST RESPONSE ==="
if ($tools) {
  # write raw to file for verbatim dump
  Set-Content -Path "C:\Users\Ahmet\Documents\inbrisk\_raw_tools_list.json" -Value $tools -Encoding UTF8 -NoNewline
  $obj = $tools | ConvertFrom-Json
  Write-Output ("tool count = " + $obj.result.tools.Count)
  foreach ($t in $obj.result.tools) { Write-Output ("  - " + $t.name) }
} else { Write-Output "(timeout)" }

try { $p.StandardInput.Close() } catch {}
if (-not $p.WaitForExit(10000)) { $p.Kill() }