#!/usr/bin/env pwsh
# ─────────────────────────────────────────────────────────────────────────────
# Bricks4Agent — 測報(業界標準 HTML 程式碼覆蓋率報告)
#
# 一鍵產生:Coverlet 收覆蓋率 → ReportGenerator 出 HTML。組員只要跑這支即可,
# 不需手動裝工具(ReportGenerator 由 .config/dotnet-tools.json 鎖版本、自動還原)。
#
# 用法(repo 根目錄):
#   pwsh scripts/coverage-report.ps1            # 跑全部(unit + trading-worker)
#   pwsh scripts/coverage-report.ps1 -NoOpen    # 不自動開瀏覽器(CI 用)
#
# 產出:.test-output/coverage/index.html(瀏覽器開)+ Summary.txt + badge_*.svg
# ─────────────────────────────────────────────────────────────────────────────
param([switch]$NoOpen)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }  # 中文輸出不亂碼
# repo 根目錄:優先用腳本自身位置(scripts/ 的上層);某些叫用方式 $PSScriptRoot 會是 null,退回當前目錄。
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { $null }
$repoRoot = if ($scriptDir) { Split-Path -Parent $scriptDir } else { (Get-Location).Path }
if (-not (Test-Path (Join-Path $repoRoot 'packages/csharp'))) { $repoRoot = (Get-Location).Path }  # 驗證是 repo 根、否則用 cwd
Set-Location $repoRoot

$outRaw  = '.test-output/coverage-raw'
$outHtml = '.test-output/coverage'

# coverlet 已啟用的 xUnit 測試專案(broker-tests 是自製 Exe runner、不走 VSTest 覆蓋率管線)
$projects = @(
    'packages/csharp/tests/unit/Unit.Tests.csproj',
    'packages/csharp/tests/trading-worker-tests/TradingWorker.Tests.csproj'
)

Write-Host '== 1/3 還原 ReportGenerator 本地工具 ==' -ForegroundColor Cyan
dotnet tool restore | Out-Null

Write-Host '== 2/3 跑測試 + 收覆蓋率 ==' -ForegroundColor Cyan
Remove-Item -Recurse -Force $outRaw -ErrorAction SilentlyContinue
# 清 coverlet 殘留映射檔(避免偶發 MSB3491「檔案已存在無法建立」)
Get-ChildItem -Path packages/csharp/tests -Recurse -Filter '.msCoverageSourceRootsMapping_*' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
foreach ($p in $projects) {
    Write-Host "   -> $p"
    dotnet test $p --collect:'XPlat Code Coverage' --results-directory $outRaw --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Error "測試失敗、覆蓋率作廢: $p"; exit 1 }
}

Write-Host '== 3/3 產生 HTML 報告 ==' -ForegroundColor Cyan
Remove-Item -Recurse -Force $outHtml -ErrorAction SilentlyContinue
dotnet tool run reportgenerator `
    "-reports:$outRaw/**/coverage.cobertura.xml" `
    "-targetdir:$outHtml" `
    '-reporttypes:Html;TextSummary;Badges' `
    '-assemblyfilters:-*.Tests;-*Tests' `
    '-filefilters:-*.g.cs;-*AssemblyInfo.cs;-**/obj/**'
if ($LASTEXITCODE -ne 0) { Write-Error 'ReportGenerator 失敗'; exit 1 }

Write-Host ''
Write-Host '── 覆蓋率摘要 ──────────────────────────────' -ForegroundColor Green
Get-Content "$outHtml/Summary.txt" | Select-Object -First 16
$index = Join-Path $repoRoot "$outHtml/index.html"
Write-Host ''
Write-Host "完整報告: $index" -ForegroundColor Green
if (-not $NoOpen) { try { Start-Process $index } catch { } }
