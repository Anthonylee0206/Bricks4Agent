# ADR:平台 / 交易層分離

- **狀態**:Accepted（2026-06-13）— 分離層 1（repo/分享)已落地;層 2（架構)列為增量目標
- **決策者**:AnthonyLee(專題作者)
- **背景脈絡**:組長反映「交易內容太多、會搶平台主體的重點」;專題主體 = Bricks4Agent 平台本身,交易是個人容器延伸。

## 背景與問題

Bricks4Agent 的定位(CLAUDE.md)是:**broker = 控制平面、非自主規劃者;執行層消費結構化 intent**。
但目前 `AutoTraderService`(~3,800 行)與 perp/trading 端點**焊在 broker 專案內**,使得:

1. **違反平台自身論述** —— 通用控制平面被特定交易邏輯污染。
2. **IP 風險** —— Benson 的 `origin`(forself/Bricks4Agent)是 **public**;交易層含 `strategy-worker`(策略庫/回測 = alpha),不能進公開 repo。
3. **論文重點被稀釋** —— 交易混在平台敘事裡,搶走「平台治理」這個主體。
4. **演進節奏衝突** —— 交易是真錢、天天動;平台需穩定供 review/PR。綁一起 → 交易一改就攪動平台。

關鍵觀察:**接縫天生存在** —— `trading-worker` 原始碼不引用 `strategy-worker`,只透過 broker 的結構化 intent 溝通(`strategy-worker` 提訊號 → broker 治理 → `trading-worker` 執行)。分離是順縫走、非動刀。

## 決策:三層分離

| 層 | 內容 | 現況 | 行動 |
|---|---|---|---|
| **層 1 — Repo / 分享** | 你的 repo(myorigin)= 完整;Benson public = 平台機制(走 trading-clean 乾淨 PR);alpha 在零協作者私有 repo | ✅ **已落地** | 維持紀律 |
| **層 2 — 架構** | autotrader 應為**外部消費者**(獨立 worker/服務,吃 broker capability/approval、提 intent),broker 回歸通用 | ⏳ **目標** | 真錢不忙時**增量**重構、非大爆改 |
| **層 3 — Alpha** | `strategy-worker`、研究結論、edge 參數 | 🔒 **已隔離私有** | 永久私有、碼焊死留平台 |

## 目標架構（層 2 完成後）

```
平台(generic、可公開、論文主體)
  broker(通用控制平面:capability / approval / 治理 / 觀測)
  worker-sdk / FunctionPool / risk-worker(通用風控引擎)
        ▲ 結構化 intent（ApprovedRequest）
        │
交易層(私有、真錢、作為平台的「一個被治理的工作負載」)
  autotrader 服務（提 intent，不在 broker 內）
  trading-worker（執行 adapter）/ quote-worker（資料）
  strategy-worker（alpha,永久私有)
```

完成後一次滿足:**平台論述自洽 + IP 安全 + 論文重點乾淨 + 交易獨立演進**。

## 增量步驟(層 2,真錢不忙時)

1. 把 `AutoTraderService` 的「決策/sizing/保護」純邏輯持續抽成純函式(已大量完成:`ComputeExposureSizing`/`ResolvePerpOpenRiskMode`/`DeriveIdemKey`…)。
2. 將 autotrader 從 broker 內的 hosted service 改為**獨立服務/worker**,透過既有 capability + approval gate 與 broker 互動(不再直接 `_dispatcher` 內呼)。
3. broker 移除 perp/trading 端點 → 改由交易層自帶或經通用 execution 端點。
4. broker 專案回歸通用、可作為乾淨 dependency 被交易層引用。

## 不要做(風險控管)

- ❌ **不要**在真錢還在跑 + 平台 PR 還沒談定時,把 autotrader 從 broker 大爆改抽出 —— 風險高、收益不急。
- ❌ **不要**把 `strategy-worker` / 任何 alpha 推進 public repo。
- ❌ 層 2 重構必須走真錢關鍵路徑之外、增量驗證。

## 後果

- **正面**:平台敘事乾淨(組長/教授)、IP 安全、交易與平台各自演進、符合平台原本宣稱的架構。
- **成本**:層 2 是不小的重構(autotrader 解 broker 內呼);跨 repo 協調有額外開銷。
- **緩解**:層 1 已給了大部分好處(乾淨分享 + IP 隔離)且零風險;層 2 留待時機、增量做。

## 相關

- 乾淨版交易 PR 策展:`docs/`(trading-clean = 治理+執行機制上、alpha 私有)
- 量化 IP 隔離:私有 repo `b4a-quant-research`
- 真錢 perp 風控審查(2026-06-13):11 修已上線
