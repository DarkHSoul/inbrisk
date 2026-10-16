#requires -Version 5.1
<#
.SYNOPSIS
  Inbrisk test paketini guvenli calistirir.

.DESCRIPTION
  GUI otomasyonu testleri sorunlari:
    1) Iptal edilen/asan bir kosudan sonra test hostu (.NET Host) ve
       Inbrisk.TestApp surecleri ortada kaliyor. TestApp penceresi
       ekranda duruyor, inbrisk "takilmis" gibi gorunuyor.
    2) Oksuz host surecleri inbrisk-mcp.dll'i kilitliyor; sonraki
       derleme MSB3027/MSB3021 ile basarisiz oluyor. Arz zinciri:
       iptal -> oksuz host -> kilitli DLL -> derleme hatasi -> daha
       fazla oksuz.
    3) Birden fazla test ayni anda odaga/tiklamaya calisirsa
       birbirlerini bozarlar; testler asilir.

  Bu betik:
    - onceki kosulardan kalan surecleri temizler (yalnizca bu deponun
      test cikti dizinlerine ait olanlari; KULLANICI sureclerine dokunmaz)
    - testleri TEK ISCE calistirir
    - asili kalan testi tespit eder ve oldurur (blame-hang)

.PARAMETER Filter
  xUnit filtresi, orn. 'FullyQualifiedName~ActionTests'

.PARAMETER HangTimeoutSec
  Bir test kac saniye sonra 'asili' sayilir. Varsayilan 120.

.EXAMPLE
  .\run_tests.ps1
.EXAMPLE
  .\run_tests.ps1 -Filter 'FullyQualifiedName~Inbrisk.Tests.ActionTests'
#>
[CmdletBinding()]
param(
    [string]   $Filter,
    [int]      $HangTimeoutSec = 120,
    [int]      $CtsTimeoutMin  = 90
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $repo 'inbrisk.sln'))) {
    $repo = 'C:\Users\Ahmet\Documents\inbrisk'
}
Push-Location $repo
try {
    # --- 1) bu depoya ait oksuz test sureclerini temizle ---------------------
    Write-Host '[1/3] Onceki kosulardan kalan surecler temizleniyor...' -ForegroundColor Cyan
    $pattern = 'Inbrisk\.Tests|Inbrisk\.TestApp|vstest\.console|testhost'
    $stale = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
             Where-Object { $_.CommandLine -match $pattern }
    if ($stale) {
        foreach ($p in $stale) {
            $tag = ($p.CommandLine -replace '\s+', ' ')
            if ($tag.Length -gt 70) { $tag = $tag.Substring(0, 70) }
            Write-Host "      durduruluyor PID $($p.ProcessId): $tag" -ForegroundColor DarkGray
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Seconds 2
    } else {
        Write-Host '      oksuz surec yok' -ForegroundColor DarkGray
    }

    # --- 2) temiz derleme ----------------------------------------------------
    Write-Host '[2/3] Derleniyor...' -ForegroundColor Cyan
    $build = dotnet build --nologo 2>&1
    $err = $build | Select-String ': error '
    if ($err) {
        Write-Host '      DERLEME HATASI:' -ForegroundColor Red
        $err | Select-Object -First 5 | ForEach-Object {
            $t = ($_ -replace '\s+', ' ')
            if ($t.Length -gt 120) { $t = $t.Substring(0, 120) }
            Write-Host "      $t" -ForegroundColor Red
        }
        exit 1
    }
    Write-Host '      derleme tamam' -ForegroundColor DarkGray

    # --- 3) testleri calistir ------------------------------------------------
    Write-Host '[3/3] Testler calisiyor (tek isce, asili tespiti aktif)...' -ForegroundColor Cyan
    $args = @('test', '--nologo', '--blame-hang',
              "--blame-hang-timeout", "${HangTimeoutSec}s",
              '--results-directory', "$repo\out\testrun")
    if ($Filter) { $args += @('--filter', $Filter) }

    $p = Start-Process -FilePath 'dotnet' -ArgumentList $args `
         -NoNewWindow -PassThru -RedirectStandardOutput "$repo\out\test.out" `
         -RedirectStandardError "$repo\out\test.err"

    $finished = $p.WaitForExit($CtsTimeoutMin * 60 * 1000)
    if (-not $finished) {
        Write-Host "      ZAMAN ASIMI: $CtsTimeoutMin dakika doldu, surec durduruluyor" -ForegroundColor Red
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        $stale = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
                 Where-Object { $_.CommandLine -match $pattern }
        $stale | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        exit 124
    }

    $log = "$repo\out\test.out"
    if (Test-Path $log) {
        $lines = Get-Content $log -Encoding UTF8
        $lines | Select-String 'Passed!|Failed!|Total tests|\bPassed:|\bFailed:|\bSkipped:' |
            Select-Object -First 4 | ForEach-Object {
                $t = ($_ -replace "`e\[[0-9;]*m", '') -replace '\s+', ' '
                Write-Host "      $t" -ForegroundColor Cyan
            }
        Write-Host ''
        Write-Host '--- basarisiz testler ---' -ForegroundColor Yellow
        $failed = $lines | Select-String '^\s*Failed\s+\S'
        if ($failed) {
            $failed | Select-Object -First 25 | ForEach-Object {
                $t = ($_ -replace "`e\[[0-9;]*m", '') -replace '\s+', ' '
                if ($t.Length -gt 100) { $t = $t.Substring(0, 100) }
                Write-Host "  $t" -ForegroundColor Yellow
            }
        } else {
            Write-Host '  yok' -ForegroundColor DarkGray
        }
        Write-Host ''
        Write-Host "kayit: $log" -ForegroundColor DarkGray
    }
    exit $p.ExitCode
}
finally {
    Pop-Location
}
