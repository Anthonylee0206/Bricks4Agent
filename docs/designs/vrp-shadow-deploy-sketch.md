# VRP / 波動 carry — Shadow 部署設計 sketch

**寫於**:2026-06-16
**狀態**:設計、未實作。**先審範圍、再動 live infra。**
**研究來源(IP 隔離)**:私有 repo `b4a-quant-research` — `research/新研究方向深挖-四方向-2026-06-16.md` + `tools/vrp-validate.py`。
> ⚠ **本檔只放*平台管線*,不放 alpha 參數 / 完整 edge 數字**(t-stat/strike 選擇/sizing 公式留私有,對齊 [Platform-Trading-Separation-ADR](Platform-Trading-Separation-ADR.md) 層 3)。

---

## 1. 為什麼上(精簡)

- **變異數風險溢酬(VRP)**:隱含波動長期 > 實現波動,賣方收溢酬;crypto(BTC)的 VRP 比股市厚。
- 跟現有部署中 edge(harmonic / di_trend / funding carry)**去相關**(對 BTC 日報酬、對 funding 都 corr ≪ 0.3)→ 真分散的**第 4 條腿**。
- 🔴 **左尾兇**:裸賣變異數單窗可 −1xx%、不可部署 → **必須 defined-risk 封頂**(iron condor / 變異數交換 + 封頂)。
- 驗證明細(robust t / Sharpe / DSR / tail / DVOL 門檻過擬合檢驗)全在**私有 repo**;判決 = **值得上 shadow,BTC only**。

---

## 2. 核心架構張力(最重要的設計決策)

現有 scanner / shadow 軌道([DispatchScannerLegAsync](../../packages/csharp/broker/Services/AutoTraderService.cs#L3536-L3612))是為**方向性 spot/perp 策略**設計:

- leg = `long`/`short`、`EntryPrice = bar close`([AutoTraderService.cs:3574](../../packages/csharp/broker/Services/AutoTraderService.cs#L3574))
- `realized_pnl_pct` 來自**價格反轉**(B.4 close path,反向訊號平倉 × round-trip 成本)

但 **VRP 是 delta 中性的波動 carry 結構**(賣 condor / 變異數):

- PnL ≈ **(隱含 − 實現變異數) × size**,封頂 → **跟標的價格漲跌無關**

> **→ 不能把 VRP 硬塞進方向性 leg 的「價格反轉」PnL 模型**:會記成 BTC 方向曝險、完全測錯訊號品質,違反「shadow 必須對帳 backtest」紀律([[feedback_walkforward_vs_pool_tstat]])。

**決策(recommended)**:VRP shadow 走**獨立的 carry 記錄路徑**(專用表 + recorder),**不**借用方向性 scanner leg 的 close PnL;但**沿用** quote-worker 資料軌、strategy-worker 註冊縫、週報推播管線。理由:阻抗不匹配是結構性的,overload 既有 leg 會讓 B.4 generic close path 算錯,且日後真錢執行也是兩條完全不同的 adapter(見 §6)。

---

## 3. 三個整合點

### 3.1 資料源(quote-worker)

- 新增 Deribit **DVOL**(每日波動指數)feed:新表 `deribit_dvol(symbol, sample_time, dvol_value)`,比照既有純量 feed [`open_interest_hist` / `retail_ls_ratio`](../../packages/csharp/workers/quote-worker/Storage/QuoteDbStorage.cs)(同樣 `(symbol, sample_time)` PK + Save/Get 方法)。
- **實現波動**從**現有 BTC OHLCV** 算(免新外部資料源)。
- BarData 加 `decimal? DvolValue`(接在 [IStrategy.cs:86](../../packages/csharp/workers/strategy-worker/Engine/IStrategy.cs#L86) `CotSpecNet` 旁,同 nullable factor 慣例)。
- align:比照 `AlignRetailLs` / OI 對齊 → 新增 `AlignDvol(bars, dvolPoints)` 把每日 DVOL 併進 BarData。
- Deribit **public REST 免金鑰**可建 cache;成本 ~3bps/側(研究已估),fetch 失敗該 roll **skip、不要 silently 當 0**。

### 3.2 Alpha 縫(strategy-worker,永久私有)

- VRP 的 **condor 定價 / strike 選擇 / sizing∝DVOL cap1.0** = **alpha**,實作放 [strategy-worker](../../packages/csharp/workers/strategy-worker/)(ADR 層 3、永久私有、**不進 trading-clean public PR**)。
- 介面:可實作 `IStrategy` 產出「carry 訊號」(implied−realized 價差 + size scalar),或獨立 `IVrpPricer`;**註冊一行**進 [Program.cs strategies dict](../../packages/csharp/workers/strategy-worker/Program.cs#L40-L109)(比照諧波補註冊 `10de8ed`,避免 "Unknown strategy")。
- 平台公開面只見**薄 shim + 介面**,看不到參數。

### 3.3 Shadow 記錄 + 週報

- 專用表 `vrp_shadow_legs`:`entry_time / dvol_entry / spot_entry / size_scalar /`(roll 結束)`realized_vol / pnl_pct_naked / pnl_pct_capped`。
- Recorder:每 roll 邊界開倉、settlement 算 PnL(piggyback scanner sweep 或獨立 hosted loop)。
- **同時記裸賣 + 封頂兩條 PnL** → 重現研究的「裸賣 = 不可部署幻覺 vs 封頂 = 可部署」對照,讓 shadow 自證封頂有效。
- 整合既有週報 [`push-scanner-shadow`](../../packages/csharp/broker/Endpoints/TradingEndpoints.cs) / `DailyReportService.BuildAndPushScannerShadowAsync` → 加 **VRP 段**、對照 backtest 基準、偏離 > 閾值 Discord 告警([[reference_shadow_scanner_weekly_report]])。

---

## 4. 私有 / 公開切割(對齊 ADR 層 3)

| 元件 | 歸屬 |
|---|---|
| `deribit_dvol` 表 + fetcher + backfill + `AlignDvol` | **平台**(通用市場資料管線) |
| `BarData.DvolValue` 欄 | **平台** |
| `vrp_shadow_legs` 表 + recorder + 週報 VRP 段 | **平台** |
| **condor 定價 / strike / sizing∝DVOL cap1.0 / edge 參數** | 🔒 **私有 strategy-worker(永不 public)** |

---

## 5. 分階段(每階段可獨立驗、可停)

- **Phase 0 — 資料地基**(~1.5h):`deribit_dvol` 表 + fetcher + backfill + `AlignDvol` + `BarData.DvolValue`。先讓資料流起來、dashboard 看得到 DVOL 曲線。**純本地、可逆、不碰真錢。**
- **Phase 1 — Shadow recorder**(~2-3h):`vrp_shadow_legs` + recorder + 模擬 condor/變異數 PnL(裸 + 封頂)+ 週報段。**BTC only、always-on(不門檻擇時)、size∝DVOL cap1.0。**
- **Phase 2 — 4 週紀律期**:跑 shadow、對帳 backtest(Sharpe ≈ 私有 memo 目標 ± 容差、左尾 ≤ 封頂界)、週報盯偏離。
- **Phase 3 / B-future — 真錢執行 adapter**(大、獨立、高風險):Deribit 選擇權下單 adapter(**全新、非 BingX spot/perp**)+ portfolio margin + 多簽 + 冪等鎖 + risk gate。**過 Phase 2 才談,需明確點頭。**

---

## 6. 風險 / 注意

- 🔴 **左尾**:裸賣不可部署;封頂(condor)是硬需求;shadow 必同記裸/封兩條 PnL 證明封頂有效。
- 🔴 **執行 adapter 是全新一條**:Deribit 選擇權 ≠ BingX spot/perp;賣方保證金清算左尾需 **portfolio margin + 價差(非裸賣)**;這是 Phase 3 的大工、別低估、跟現有下單路徑零共用。
- ⚠ **fidelity gap**:Phase 1 用 implied(DVOL)vs realized(BTC bars)算 PnL 是 model proxy;**真 option-chain OTM 價差**是 open Q(研究記載),Phase 2 可加真 mark 校準。
- ⚠ **ETH 弱**且跟 BTC VRP corr 0.80(同一條)→ **BTC only**,別擴 ETH。
- ⚠ **DVOL 門檻擇時過擬合**(研究 OOS 已驗無加分)→ **always-on,不門檻**;只 size∝DVOL **cap1.0**(過 [[feedback_sizing_overlay_sharpe_is_leverage]] 槓桿檢驗,cap1.0=cap2.0 證非槓桿)。
- ⚠ 加風險(上 shadow)**先驗後上**;真錢只在 Phase 2 達標後談([[feedback_derisk_now_addrisk_waits]])。

---

## 7. Shadow 階段 Done 條件

- [ ] `deribit_dvol` 表 + fetcher + backfill 通、dashboard 看得到 DVOL 曲線
- [ ] `BarData.DvolValue` + `AlignDvol`
- [ ] `vrp_shadow_legs` + recorder + 裸/封頂雙 PnL
- [ ] 週報加 VRP 段、對照 backtest 基準
- [ ] VPS 部署、第一個 roll 邊界確實記到 leg
- [ ] 4 週後 Sharpe / 左尾對帳 backtest,達標才談 Phase 3
- [ ] alpha(定價/sizing)確認只在私有 strategy-worker、**未進 public trading-clean PR**

---

## 8. 參考

- 整合點 anchors:本 session Explore 掃出(strategy 註冊 / quote-worker 資料軌 / scanner shadow 路徑)
- 紀律:[[project_new_directions_2026_06_16]](shadow 設計定案)、[Platform-Trading-Separation-ADR](Platform-Trading-Separation-ADR.md)(IP 層 3)、[[feedback_sizing_overlay_sharpe_is_leverage]]、[[feedback_self_referential_regime_overlay]]、[[feedback_walkforward_overlap_inflates_significance]]、[[feedback_real_money_idempotency]](Phase 3 冪等鎖)、[[reference_shadow_scanner_weekly_report]]
- 同類範式:[tsmom_btcNotUp 部署設計](tsmom-btcregime-deploy-sketch.md)(scanner shadow 升 live 流程)
