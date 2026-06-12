# 停掉 + 拆除 failover-sim demo。用法:.\demos\failover-sim\stop.ps1
docker compose -p fsim -f "$PSScriptRoot\docker-compose.yml" down
Write-Host "failover-sim 已拆除乾淨。" -ForegroundColor Green
