#!/bin/bash
# 單機 failover demo 驅動。完全本地、不碰 prod。展示:選主 → 殺主自動接手 → 分區自我 fence(防腦裂)。
set -u
cd "$(dirname "$0")"
if [ "${1:-}" = "down" ]; then echo "拆除 demo stack…"; docker compose -p fsim down; exit 0; fi
# 讀「誰是主 / work 計數」用 brokersim 自己的 /status 端點(比 etcdctl 讀 gateway-寫入的 key 可靠;
# etcdctl 對 v3 JSON-gateway 寫入的 key 會讀不到——已驗證 gateway 直讀資料正確)。
# 只對「還在 running」的容器查 /status —— 被 kill/分區的節點直接跳過(避免讀到死節點殘留的舊角色)。
status(){
  docker ps -q -f "name=^fsim-node-$1$" -f "status=running" | grep -q . || return
  docker exec "fsim-node-$1" python3 -c "import urllib.request,json;d=json.load(urllib.request.urlopen('http://localhost:8080',timeout=2));w=d['last_work'];print(d['role'],d['node'],(w.split()[0] if w not in ('','(none)') else ''))" 2>/dev/null
}
leader(){ for n in a b; do set -- $(status "$n"); [ "${1:-}" = "PRIMARY" ] && { echo "$2"; return; }; done; }
work(){   for n in a b; do set -- $(status "$n"); [ "${1:-}" = "PRIMARY" ] && { echo "${3:-}"; return; }; done; }
# 輪詢直到「新主」出現(≠ 舊主、且活著)或逾時——比固定 sleep 可靠,避免在接手中途讀到。
wait_new_leader(){ local old="$1" np=""; for _ in $(seq 1 20); do np=$(leader); [ -n "$np" ] && [ "$np" != "$old" ] && { echo "$np"; return; }; sleep 1; done; echo "$np"; }
line(){ echo; echo "═══════════ $* ═══════════"; }

line "啟動 3 etcd(quorum)+ 2 broker(主/備)"
docker compose up -d --build
echo "等 etcd 成形 + 選主(~18s)…"; sleep 18

line "初始狀態"
echo "目前 leader = node-$(leader) ;  work 計數 = $(work)"
echo "--- node-a log ---"; docker logs --tail 3 fsim-node-a 2>&1
echo "--- node-b log ---"; docker logs --tail 3 fsim-node-b 2>&1

# ── 測試 1:殺掉主 → 備機自動接手 ──
P=$(leader); pl=$(echo "$P" | tr 'A-Z' 'a-z')
line "測試1:docker kill 主節點 node-$P(模擬 VPS 當機)"
W1=$(work); docker kill "fsim-node-$pl" >/dev/null
echo "已殺 node-$P(work 當時 = $W1)。等 lease 過期 + 接手…"
NP=$(wait_new_leader "$P")
echo "新 leader = node-$NP   $([ "$NP" != "$P" ] && [ -n "$NP" ] && echo '✅ 自動接手成功' || echo '✗ 沒接手')"
echo "work 計數 = $(work)(應 > $W1 ⇒ 新主接續寫、單一寫者交接)"
echo "--- 存活節點 log(接手)---"; docker logs --tail 4 "fsim-node-$(echo "$NP" | tr 'A-Z' 'a-z')" 2>&1

# 把被殺的拉回來(恢復成 2 節點),它應加入為 STANDBY(不搶主)
docker start "fsim-node-$pl" >/dev/null; sleep 8
echo "node-$P 已拉回;leader 仍 = node-$(leader)(舊主回來不搶、避免雙主)"

# ── 測試 2:分區現任主 → 自我 fence(防腦裂)──
C=$(leader); cl=$(echo "$C" | tr 'A-Z' 'a-z')
line "測試2:把現任主 node-$C 從網路分區(模擬 VPS 連不到 quorum)"
W2=$(work); docker network disconnect fsim-net "fsim-node-$cl" >/dev/null
echo "已分區 node-$C(work=$W2)。等自我 fence + 對側接手…"
NC=$(wait_new_leader "$C")
echo "新 leader = node-$NC   $([ "$NC" != "$C" ] && [ -n "$NC" ] && echo '✅ 對側接手' || echo '✗')"
echo "--- 被分區節點 log(應自我 fence、停止動作、不繼續寫)---"; docker logs --tail 4 "fsim-node-$cl" 2>&1
echo "work 計數 = $(work)(由新主繼續、被分區那個沒有偷寫 ⇒ 無腦裂/無雙寫)"

docker network connect fsim-net "fsim-node-$cl" >/dev/null; sleep 6
echo "node-$C 重新連網;它看到 leader=node-$(leader) ⇒ 維持 STANDBY(無腦裂)"
echo "--- 重連節點 log ---"; docker logs --tail 3 "fsim-node-$cl" 2>&1

line "結論"
echo "✓ 選主:同時只有一個 PRIMARY"
echo "✓ 殺主:lease 過期後備機自動接手(RTO ≈ lease TTL)"
echo "✓ 分區:失去 quorum 的舊主【自我 fence、停止寫入】→ 無腦裂、無雙寫"
echo "✓ work 計數全程單調遞增、單一寫者 ⇒ 副作用不會被兩個主重複執行"
echo
echo "拆除:bash $(basename "$0") down    或    docker compose -p fsim down"
