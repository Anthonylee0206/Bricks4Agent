# 一鍵啟動 failover-sim 監測儀表板(demo 用)。
# 用法:在專案任意位置開 PowerShell,跑:  .\demos\failover-sim\start.ps1
# 結束:在這個視窗按 Ctrl-C,再跑 .\demos\failover-sim\stop.ps1(或 docker compose -p fsim down)
$dir = $PSScriptRoot
Write-Host "[1/4] 起 stack(3 etcd 見證 + 2 broker 主備)..." -ForegroundColor Cyan
docker compose -p fsim -f "$dir\docker-compose.yml" up -d --build | Out-Null
Write-Host "[2/4] 等 etcd 成形 + 選主(18 秒)..." -ForegroundColor Cyan
Start-Sleep -Seconds 18
Write-Host "[3/4] 開瀏覽器 http://localhost:8090 ..." -ForegroundColor Cyan
Start-Process "http://localhost:8090"
Write-Host "[4/4] 起監測服務(這個視窗保持開著;要結束按 Ctrl-C)" -ForegroundColor Green
Write-Host ""
Write-Host "  → 另開一個終端機殺主看效果:  docker kill fsim-node-<綠色那台>" -ForegroundColor Yellow
Write-Host ""
python "$dir\monitor.py"
