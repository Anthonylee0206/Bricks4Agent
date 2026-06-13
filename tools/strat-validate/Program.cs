// 廣宇宙策略驗證:12 檔幣 × walk-forward OOS。同時跑 long-only 與 long-short 兩種引擎對照,
// 對照 buy&hold、給可用判定 + 相關矩陣 + 低相關「組合層」回測(下一步)。
// 單一參數集、要求跨多檔通用(不 per-symbol 調參 = 非 curve-fit)。
using BrokerCore.Trading;
using StrategyWorker.Engine;
using StrategyWorker.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
// 2026-06-07 平行化:重 walk-forward 迴圈用 Parallel.ForEach 打滿核心(原單執行緒只用 1 核)。
// 每個 (策略, 幣) 的 walk-forward 互相獨立 = 天生可平行;策略平行(同 s 實例只被一個 task 用、不並發)。
// 共享寫入改 ConcurrentDictionary、bootstrap RNG 改 per-strategy seed、輸出先收集後按原順序印。

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
// 固定 funding 假設(long 付正值):env FUNDING_RATE_PER_8H、預設 0.01%/8h(=0.03%/日)。
// 多頭實際常 3-5x;engine 按實際持有期累計(持越久咬越多)。灌進每根 bar、只在 applyFunding 時生效。
decimal fundingPer8h = decimal.TryParse(Environment.GetEnvironmentVariable("FUNDING_RATE_PER_8H"), out var fpr) && fpr >= 0 ? fpr : 0.0001m;

// ── 快速迭代模式(2026-05-26)──
// --fast       :5 主幣 × 1d 而已、跑 30-60s(原本 20 幣 × 5 時框 = 8-10 min)
// --only=PAT   :只跑名稱符合 PAT(支援 *)的策略;e.g. --only=harm_prz_*
// --bars=N     :抓 N 根 K 線(預設 1000、> 1000 走 KlineCache 分頁;2000 ≈ 5.5 年日線)
// --show-kelly: 跑完 t-stat 後額外印出每支顯著策略的 Kelly fraction 推薦 sizing
// 用 walk-forward OOS win rate + avg win/loss、quarter-Kelly、max 20% clamp
bool showKelly = args.Contains("--show-kelly");
bool fastMode = args.Contains("--fast");
string? onlyFilter = args.FirstOrDefault(a => a.StartsWith("--only="))?.Substring(7);
int barsLimit = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--bars="))?.Substring(7), out var bl) ? bl : 1000;
bool realFunding = args.Contains("--apply-funding");
bool realRetailLs = args.Contains("--apply-retail-ls");
// 真實成本覆寫:--cost-bps=N → 每側成本 N 基點(1bp=0.01%)。預設 8bps(=原 comm5+slip3、crypto realistic)。
// 台股套 ~22bps/側(round-trip ~0.44% = 0.3% 賣稅 + ~0.14% 手續費)、美股 ~6bps、crypto ~8bps。全算在 commission(引擎 comm/slip 同等扣)。
decimal costBps = decimal.TryParse(args.FirstOrDefault(a => a.StartsWith("--cost-bps="))?.Substring(11), out var cbVal) && cbVal > 0 ? cbVal : 8m;
decimal gComm = costBps / 10000m, gSlip = 0m;
if (costBps != 8m) Console.WriteLine($"💰 --cost-bps={costBps}:每側 {costBps:F0}bps(={costBps / 100m:F2}%/側、round-trip {costBps * 2m / 100m:F2}%)");
// --sl=N:LS 引擎固定初始止損 %(模擬 live 止損機制做存活測試;0=無止損,現有行為)
decimal slPct = decimal.TryParse(args.FirstOrDefault(a => a.StartsWith("--sl="))?.Substring(5), out var slv) ? slv : 0m;
// --conf-sizing:部位名目 × signal.Confidence(Carver forecast-strength sizing 實驗;對照固定倉位)
// 注意:LS 引擎只在 conf≥0.6 才開倉、故 scale 實際範圍 0.6-1.0(溫和);引擎版無 floor(live 版 floor 0.3)
bool confSizing = args.Contains("--conf-sizing");
if (confSizing) Console.WriteLine("📐 --conf-sizing:部位 × signal.Confidence(forecast-strength sizing 實驗、對照固定倉位)");
// --conf-diag:confidence 校準診斷(Q1 開放項)— per-trade entry confidence vs 實際 PnlPct。
// 答「confidence 有沒有預測力(分桶單調?corr≠0?)+ 跨策略可不可比(分布重疊?)」。用 full-sample(對找關係有利、null 結果更強)。
bool confDiag = args.Contains("--conf-diag");
if (confDiag) Console.WriteLine("🔬 --conf-diag:confidence 校準診斷(entry confidence vs 實際報酬;full-sample、找關係有利)");
// --vol-target:Q1.2 vol-targeting A/B(固定 sizing vs 反波動率調倉;比 Sharpe/maxDD/pool t-stat)。
// 紀律:先驗有沒有用、再談部署(同 conf-sizing 流程)。env VOL_TARGET_ANNUAL / VOL_TARGET_LOOKBACK 可調。
bool volTargetAB = args.Contains("--vol-target");
if (volTargetAB) Console.WriteLine("📏 --vol-target:vol-targeting A/B(反波動率調倉 vs 固定;比風險調整後報酬)");
if (fastMode) Console.WriteLine("⚡ --fast mode:5 幣 × 1d only");
if (onlyFilter != null) Console.WriteLine($"⚡ --only={onlyFilter}");
if (barsLimit != 1000) Console.WriteLine($"⚡ --bars={barsLimit}(歷史加深)");
if (realFunding) Console.WriteLine("💸 --apply-funding:用 Binance 真實 funding history(LS engine 雙向計費),非預設 0.01%/8h 假設");
if (realRetailLs) Console.WriteLine("📊 --apply-retail-ls:用 data.binance.vision metrics 注入 RetailLongShortRatio(retail_ls_contrarian 必要)");
if (slPct > 0m) Console.WriteLine($"🛑 --sl={slPct}%:LS 引擎固定初始止損(模擬 live 止損、存活測試)");
// --with-protection:回測套上「live 防禦鏈」(初始SL+BE-move+peak-trailing),用 VPS 真正在跑的參數值。
//   目的=測「strategy + 防禦」真實逐年績效 vs signal-only;NOT 優化防禦參數(用 live 固定值、不准 curve-fit)。
//   值來源(2026-06-04):broker-core ProtectionConfig 預設 InitialSl 5%/BE 3%+0.5% + VPS env Trailing 3%/2%。
//   殘留近似:%-based partial exit 未套(引擎的 partial 走 signal PartialTargetPrice);bar-close 觸發近似盤中。
bool withProtection = args.Contains("--with-protection");
decimal protSl = 5m, protBeTrig = 3m, protBeBuf = 0.5m, protTrailTrig = 3m, protTrailDist = 2m;
if (withProtection)
    Console.WriteLine($"🛡 --with-protection:套 live 防禦鏈(SL{protSl}% / BE {protBeTrig}%+{protBeBuf}% / trail {protTrailTrig}%→{protTrailDist}%);測 strategy+防禦真實績效、非優化防禦");
// --from=YYYY-MM-DD / --to=YYYY-MM-DD:把 bars 切到日期窗做 regime 分段(如 2022 純熊段)。
// 需 --bars 夠大涵蓋該窗(如 --from=2022-01-01 配 --bars=2000)。切窗後 OOS fold 變少 → 看 full-period ret/Sh/DD 為主。
DateTime? winFrom = DateTime.TryParse(args.FirstOrDefault(a => a.StartsWith("--from="))?.Substring(7), out var _wf) ? DateTime.SpecifyKind(_wf, DateTimeKind.Utc) : (DateTime?)null;
DateTime? winTo   = DateTime.TryParse(args.FirstOrDefault(a => a.StartsWith("--to="))?.Substring(5),   out var _wt) ? DateTime.SpecifyKind(_wt, DateTimeKind.Utc) : (DateTime?)null;
if (winFrom != null || winTo != null)
    Console.WriteLine($"🗓 日期窗 {winFrom?.ToString("yyyy-MM-dd") ?? "起"} ~ {winTo?.ToString("yyyy-MM-dd") ?? "今"}:切窗 regime 分段(OOS fold 變少、看 full-period 為主)");

// 多市場驗證(都走 Yahoo 日線 + StockBarCache、funding/retail_ls 自動失效):
// --stocks   美股跨 sector / --twstocks 台股跨 sector / --fx 外匯主流對+交叉
bool stocksMode = args.Contains("--stocks");
bool twMode     = args.Contains("--twstocks");
bool fxMode     = args.Contains("--fx");
bool etfMode    = args.Contains("--etf");
bool commoMode  = args.Contains("--commodities");
bool bondsMode  = args.Contains("--bonds");
bool yahooMode  = stocksMode || twMode || fxMode || etfMode || commoMode || bondsMode;   // 非 crypto perp:資料走 Yahoo、無 funding/retail_ls
if (stocksMode) Console.WriteLine("📈 --stocks:美股模式(Yahoo 日線、StockBarCache);funding/retail_ls 注入自動 skip");
if (twMode)     Console.WriteLine("🇹🇼 --twstocks:台股模式(Yahoo .TW 日線);funding/retail_ls 注入自動 skip");
if (fxMode)     Console.WriteLine("💱 --fx:外匯模式(Yahoo =X 日線);funding/retail_ls 注入自動 skip");
if (etfMode)    Console.WriteLine("🧺 --etf:ETF 模式(廣指數+SPDR sector+國際;驗 ETF 有無獨立 edge)");
if (commoMode)  Console.WriteLine("🛢️ --commodities:商品/期貨模式(Yahoo =F 日線、連續近月;金屬/能源/農產/股指期/債期);funding/retail_ls 自動 skip");
if (bondsMode)  Console.WriteLine("🏦 --bonds:債券/利率模式(多天期國債 ETF + 期貨;驗 harmonic 在利率資產=第5市場?跟股/幣/商品/FX ≈0 相關);funding/retail_ls 自動 skip");

string[] symbols = etfMode
    ? new[]
    {
        // 廣指數 + SPDR sector + 國際 — 驗 ETF(指數 beta)有沒有獨立於單股的 edge / sector rotation 趨勢
        "SPY","QQQ","IWM","DIA",                                          // 廣指數
        "XLK","XLF","XLE","XLV","XLP","XLY","XLI","XLU","XLB","XLRE","XLC", // 11 SPDR sector
        "EWT","EWJ","EEM","FXI",                                          // 國際(台/日/新興/中)
    }
    : stocksMode
    ? new[]
    {
        // 跨 sector 美股(科技/金融/醫療/能源/消費/工業)— breadth 給 cross-sectional + pooling
        // 2026-06-10 擴 24→~58(跟台股一致廣度、波動選股測;非 point-in-time、診斷用)
        "AAPL","MSFT","GOOGL","AMZN","NVDA","AMD","AVGO","CRM","INTC","META",
        "ORCL","ADBE","CSCO","QCOM","TXN","NFLX","TSLA","IBM","NOW","AMAT","MU","INTU","PANW",
        "JPM","BAC","V","MA","GS","MS","WFC","C","AXP","BLK","SCHW",
        "UNH","JNJ","LLY","ABBV","MRK","PFE","TMO","ABT","DHR",
        "XOM","CVX","COP","WMT","KO","PG","DIS","CAT","BA","COST","HD","MCD","NKE","PEP","GE","HON","LMT","RTX",
    }
    : twMode
    ? new[]
    {
        // 跨 sector 台股(半導體/金融/電信/塑化/鋼鐵/食品/零售/ETF)— 驗 harmonic 在 TWSE 是否延伸
        // 2026-06-09 擴宇宙 16→~50(離散度診斷要廣度;⚠️非 point-in-time、含倖存者偏誤、診斷用、正式回測要修)
        // 半導體/IC
        "2330.TW","2454.TW","2303.TW","3711.TW","2379.TW","3034.TW","3008.TW","2408.TW","3443.TW","6415.TW",
        // 電子(組裝/零組件/網通/面板/散熱)
        "2317.TW","2308.TW","2382.TW","2357.TW","2376.TW","4938.TW","6669.TW","2474.TW","2327.TW","3037.TW","2345.TW","2409.TW","3481.TW","3017.TW",
        // 金融
        "2881.TW","2882.TW","2891.TW","2886.TW","2884.TW","2885.TW","2892.TW","5880.TW","2880.TW","2890.TW",
        // 電信
        "2412.TW","3045.TW","4904.TW",
        // 傳產(塑化/鋼鐵/食品/車/航運/紡織/零售)
        "1301.TW","1303.TW","1326.TW","2002.TW","1216.TW","2207.TW","2603.TW","2609.TW","2615.TW","2618.TW","2105.TW","1402.TW","2912.TW",
        // ETF
        "0050.TW","0056.TW",
        // 2026-06-10 再擴 ~85(全策略完整回測要更廣;⚠️仍非 point-in-time、診斷用)
        "6488.TW","3529.TW","3035.TW","6285.TW","2449.TW","6239.TW","2344.TW","5269.TW","3661.TW","8046.TW",
        "4958.TW","3533.TW","2492.TW","2383.TW","2360.TW","2347.TW","3702.TW","2353.TW",
        "1102.TW","9910.TW","9904.TW","2915.TW","1210.TW","1722.TW","1605.TW","9921.TW","9914.TW",
        "2887.TW","2888.TW","2883.TW","5876.TW","2542.TW","8454.TW","2548.TW",
    }
    : fxMode
    ? new[]
    {
        // 外匯主流對 + 交叉盤(Yahoo =X 格式)— harmonic 源自 FX/股票 TA、先驗 majors
        "EURUSD=X","USDJPY=X","GBPUSD=X","USDCHF=X","AUDUSD=X","USDCAD=X","NZDUSD=X",  // 7 majors
        "EURJPY=X","GBPJPY=X","EURGBP=X","AUDJPY=X",                                    // 4 crosses
    }
    : bondsMode
    ? new[]
    {
        // 債券/利率(多天期美國國債 ETF + 期貨、Yahoo)— 驗 harmonic 在利率資產類是否成立 = 潛在第 5 個 ≈0 相關市場。
        // 長天期探針(ZB=F 30Y)顯示 harmonic OOS Sharpe 0.76;這裡擴成全曲線看穩不穩。
        // ⚠️ 國債本質≈單因子(利率)、彼此高相關 → per-symbol 廣度意義有限、重點是 harmonic 抓不抓得到利率反轉。
        "TLT","EDV","TLH","IEF","IEI","SHY",          // 國債 ETF:20Y+/25Y+/10-20Y/7-10Y/3-7Y/1-3Y
        "ZB=F","ZN=F","ZF=F","ZT=F",                  // 國債期:30Y/10Y/5Y/2Y
    }
    : commoMode
    ? new[]
    {
        // 商品/期貨(Yahoo =F 連續近月)— 不同市場性格(趨勢/季節/carry),驗策略庫有無新鮮 edge
        // ⚠️ 連續合約有換月 roll gap、診斷用;真部署要處理 roll-adjust
        "GC=F","SI=F","HG=F","PL=F","PA=F",                          // 金屬(金/銀/銅/鉑/鈀)
        "CL=F","BZ=F","NG=F","RB=F","HO=F",                          // 能源(WTI/Brent/天然氣/汽油/熱燃油)
        "ZC=F","ZW=F","ZS=F","KC=F","SB=F","CC=F","CT=F",            // 農產(玉米/小麥/黃豆/咖啡/糖/可可/棉)
        "ES=F","NQ=F","YM=F","RTY=F",                                // 股指期(SP500/那指/道瓊/羅素)
        "ZN=F","ZB=F",                                               // 債期(10年/30年)
    }
    : fastMode
    ? new[] { "BTCUSDT", "ETHUSDT", "BNBUSDT", "LTCUSDT", "OPUSDT" }
    : new[]
    {
        "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "ADAUSDT",
        "DOGEUSDT", "AVAXUSDT", "LINKUSDT", "LTCUSDT", "DOTUSDT", "ATOMUSDT",
        // 2026-05-25 擴幣宇宙 12→20(堆樣本:更多幣 = pooling 後 fold 更多、廣度濾網更有力)
        "TRXUSDT", "UNIUSDT", "NEARUSDT", "APTUSDT", "ARBUSDT", "OPUSDT", "SUIUSDT", "INJUSDT",
    };

// 2026-06-07 #2 倖存者偏誤:--graveyard 把已下市/歸零的幣加進 crypto 宇宙(走 KlineCache 的
// data.binance.vision archive 退路載入)→ 跑「倖存者 vs 含死幣」對照、量化 edge 被排除死幣高估多少。
// 預設不含(維持原宇宙可比性)。載不到的(archive 也沒)會在「資料就緒」那步自然 skip。
if (args.Contains("--graveyard") && symbols.Contains("BTCUSDT"))
{
    symbols = symbols.Concat(new[] { "LUNAUSDT", "FTTUSDT", "SRMUSDT" }).ToArray();
    Console.WriteLine("⚰️ --graveyard:宇宙加入已死幣(LUNA/FTT/SRM)測倖存者偏誤");
}

(string name, IStrategy s)[] strats =
{
    // 實際部署中(live/shadow)— 這次重點:看它們在哪個幣最強,精準分配
    ("smc",              new SmcStrategy()),
    ("mfi",              new MfiStrategy()),
    ("rsi_stoch",        new StochasticStrategy()),
    ("don_trend",        new DonTrendStrategy()),
    ("rsi2_rev",         new Rsi2RevStrategy()),
    ("boll_rev",         new BollRevStrategy()),
    // 2026-05-27 結構性 alpha 第一彈:資金費率 contrarian(funding 極低 = 空頭擁擠 → 進多)
    // 需 --apply-funding 注入 bar.FundingRate 才有意義(否則整段 hold)
    ("funding_extreme",  new FundingExtremeStrategy()),
    // 2026-05-27 反向組:funding momentum(funding 極端時跟同方向、非 contrarian)
    // funding_extreme 跑出 t=-3.76 顯著為負 → 反向應該為正
    ("funding_momentum_ls", new FundingMomentumLsStrategy()),
    // 2026-05-27 變體:tighter/looser threshold sweep,看是否更高 Sharpe
    ("fundmom_ls_tight",  new FundingMomentumLsStrategy("fundmom_ls_tight",  hotPct: 0.90m, coldPct: 0.10m)),  // 更嚴
    ("fundmom_ls_loose",  new FundingMomentumLsStrategy("fundmom_ls_loose",  hotPct: 0.80m, coldPct: 0.20m)),  // 更鬆
    ("fundmom_ls_xtight", new FundingMomentumLsStrategy("fundmom_ls_xtight", hotPct: 0.95m, coldPct: 0.05m)),  // 極嚴
    // 2026-05-28 Q2 第一個通過 IS+OOS 雙確認的真結構性 alpha(pool t=-2.89/-2.25、跨 8 幣方向全一致)
    // 需 --apply-retail-ls 注入 bar.RetailLongShortRatio 才有意義(否則整段 hold)
    ("retail_ls_contrarian",        new RetailLsContrarianStrategy()),
    ("retail_ls_contrarian_tight",  new RetailLsContrarianStrategy("retail_ls_contrarian_tight",  hotPct: 0.90m, coldPct: 0.10m)),  // 更嚴
    ("retail_ls_contrarian_xtight", new RetailLsContrarianStrategy("retail_ls_contrarian_xtight", hotPct: 0.95m, coldPct: 0.05m)),  // 極嚴
    ("retail_ls_daily_rebal", new RetailLsContrarianStrategy("retail_ls_daily_rebal", dailyRebalance: true)),  // 每根都 flip 捕 1d edge
    // 2026-05-28 Q2 第二個通過 IS+OOS 雙確認的 alpha(quantile pool t=+2.01/+2.39、線性不顯著=非線性 edge)
    // 需 --apply-retail-ls 注入(同 endpoint 順便 fill bar.OpenInterest)
    ("oi_momentum_ls",        new OiMomentumLsStrategy()),
    // [2026-06-10 從零開發] 流動性清算反轉:大move + OI驟降 = 強制平倉級聯 → 反向吃 overshoot
    ("liquidation_reversal",  new LiquidationReversalStrategy()),
    ("liq_reversal_loose",    new LiquidationReversalStrategy("liq_reversal_loose", moveZ: 1.5m, oiDrop: 0.02m)),
    ("liq_reversal_tight",    new LiquidationReversalStrategy("liq_reversal_tight", moveZ: 2.5m, oiDrop: 0.05m)),
    // [2026-06-10 v2 改寫] 多日 capitulation 投降耗盡(OI 連降+價連動 = 槓桿資金耗盡 → 反轉、日線抓得到)
    ("liq_capit_w5",          new LiquidationReversalStrategy("liq_capit_w5", moveZ: 1.5m, oiDrop: 0.05m, window: 5)),
    ("liq_capit_w3",          new LiquidationReversalStrategy("liq_capit_w3", moveZ: 1.5m, oiDrop: 0.03m, window: 3)),
    ("liq_capit_w7",          new LiquidationReversalStrategy("liq_capit_w7", moveZ: 1.5m, oiDrop: 0.07m, window: 7)),
    // [2026-06-10 從零開發] OI 確認突破:突破 + OI升(新錢)= 真突破 vs 假突破
    ("oi_breakout",           new OiBreakoutStrategy()),
    ("oi_breakout_55",        new OiBreakoutStrategy("oi_breakout_55", lookback: 55, oiRise: 0.03m)),
    ("oi_breakout_nofilter",  new OiBreakoutStrategy("oi_breakout_nofilter", lookback: 20, oiRise: 0.0m)),  // 對照組:無 OI 過濾
    // [2026-06-10 從零開發] BTC 領先-alt 滯後:BTC 近N日報酬領先 → alt 滯後跟(跨幣資訊流)
    ("btc_lead",              new BtcLeadStrategy()),
    ("btc_lead_l2",           new BtcLeadStrategy("btc_lead_l2", lag: 2, moveZ: 1.0m)),
    ("btc_lead_z05",          new BtcLeadStrategy("btc_lead_z05", lag: 1, moveZ: 0.5m)),
    // [2026-06-10 從零開發、盤中] Order-flow 失衡:taker 主動買賣量比 → 短期續航(OFI 微結構、盤中才有意義)
    ("order_flow",            new OrderFlowImbalanceStrategy()),
    ("order_flow_inv",        new OrderFlowImbalanceStrategy("order_flow_inv", invert: true)),
    // [2026-06-11 從零開發、結構性] COT 持倉:投機者極端淨持倉 → 反向(跟商業避險者站邊)
    ("cot_positioning",       new CotPositioningStrategy()),
    ("cot_positioning_inv",   new CotPositioningStrategy("cot_positioning_inv", invert: true)),
    ("oi_momentum_ls_tight",  new OiMomentumLsStrategy("oi_momentum_ls_tight",  hotPct: 0.90m, coldPct: 0.10m)),
    ("oi_momentum_ls_xtight", new OiMomentumLsStrategy("oi_momentum_ls_xtight", hotPct: 0.95m, coldPct: 0.05m)),
    // 2026-05-28 翻案測:OI 暴衝 contrarian(mean revert);momentum 蓋棺後對立假設
    ("oi_contrarian_ls",        new OiMomentumLsStrategy("oi_contrarian_ls",        hotPct: 0.80m, coldPct: 0.20m, invertSignal: true)),
    ("oi_contrarian_ls_tight",  new OiMomentumLsStrategy("oi_contrarian_ls_tight",  hotPct: 0.90m, coldPct: 0.10m, invertSignal: true)),
    ("oi_contrarian_ls_xtight", new OiMomentumLsStrategy("oi_contrarian_ls_xtight", hotPct: 0.95m, coldPct: 0.05m, invertSignal: true)),
    // 2026-05-28 翻案測:retail_ls momentum(跟單散戶);contrarian 已通過 OOS,對立假設證偽更穩
    ("retail_ls_momentum",        new RetailLsContrarianStrategy("retail_ls_momentum",        hotPct: 0.80m, coldPct: 0.20m, invertSignal: true)),
    ("retail_ls_momentum_tight",  new RetailLsContrarianStrategy("retail_ls_momentum_tight",  hotPct: 0.90m, coldPct: 0.10m, invertSignal: true)),
    // 2026-05-28 第二代 alpha 候選:retail_ls Δ(變化率) — oi-validate pool t=-3.51 / OOS -3.65 比 raw 更強
    ("retail_ls_delta_contrarian",        new RetailLsDeltaContrarianStrategy()),
    ("retail_ls_delta_contrarian_tight",  new RetailLsDeltaContrarianStrategy("retail_ls_delta_contrarian_tight",  hotPct: 0.90m, coldPct: 0.10m)),
    ("retail_ls_delta_contrarian_xtight", new RetailLsDeltaContrarianStrategy("retail_ls_delta_contrarian_xtight", hotPct: 0.95m, coldPct: 0.05m)),
    // momentum 對照組(預期應該負 — 驗證 contrarian 方向不是 fluke)
    ("retail_ls_delta_momentum",          new RetailLsDeltaContrarianStrategy("retail_ls_delta_momentum",          hotPct: 0.80m, coldPct: 0.20m, invertSignal: true)),
    // 2026-05-27 第二類結構性 alpha 候選:volume momentum + sweep
    ("volmom_ls",          new VolumeMomentumLsStrategy("volmom_ls",          volPct: 0.85m)),
    ("volmom_ls_tight",    new VolumeMomentumLsStrategy("volmom_ls_tight",    volPct: 0.90m)),
    ("volmom_ls_xtight",   new VolumeMomentumLsStrategy("volmom_ls_xtight",   volPct: 0.95m)),
    // 第一批(趨勢家族,原本偏多用、實為多空對稱)
    ("ts_momentum",      new TsMomentumStrategy()),
    // 2026-05-29 補註冊(原盲區):tsmom_btc_not_up = ts_momentum + BTC-not-up entry filter(外生 BTC regime)
    // 需設 BtcRegimeFilterStrategy.BtcBarsRef = BTC bars(下方資料載入後設),否則 pass-through 退化成 ts_momentum
    ("tsmom_btc_not_up", new BtcRegimeFilterStrategy(new TsMomentumStrategy(), new[] { "sideways", "down" }, emaFast: 20, emaSlow: 50, name: "tsmom_btc_not_up")),
    ("chandelier_trend", new ChandelierTrendStrategy()),
    ("ma_regime_trend",  new MaRegimeTrendStrategy()),
    ("dual_thrust",      new DualThrustStrategy()),
    ("accel_momentum",   new AccelMomentumStrategy()),
    // 第二批(原生多空)
    ("dual_mom_ls",      new DualMomentumLsStrategy()),
    ("di_trend_ls",      new DiTrendLsStrategy()),
    ("supertrend_ls",    new SuperTrendLsStrategy()),
    ("bb_revert_ls",     new BollingerRevertLsStrategy()),
    ("donchian_fade_ls", new DonchianFadeLsStrategy()),
    // 第三批(諧波 / 斐波那契)
    ("fib_retrace_ls",   new FibRetraceLsStrategy()),
    // 使用者完整斐波規則 v1(2026-06-02):分級點位+連2根站穩+目標1+深度+SL下一階。對照 fib_retrace_sl_ls。
    ("fib_confluence_ls", new FibConfluenceLsStrategy()),
    // Phase 2:同規則 + 1.13 反轉區平 50%(scale-out)。對照 fib_confluence_ls 看部分出場有無改善。
    ("fib_confluence_partial_ls", new FibConfluenceLsStrategy("fib_confluence_partial_ls", emitPartial: true)),
    ("harmonic_ls",      new HarmonicLsStrategy()),
    // 研究實驗(harmonic research log H1, 2026-05-26):諧波 + 橫盤 regime 閘
    ("harmonic_range_ls", new HarmonicRangeLsStrategy()),
    // 研究實驗(H5-Harmonic-PRZ, 2026-05-26):教科書 Carney 用法 - 4 點 XABC + PRZ 投影進場
    ("harmonic_prz_ls", new HarmonicPrzLsStrategy()),
    // H6 per-pattern breakdown(2026-05-26):每個 pattern 單獨測 OOS、找撐起 edge 的是誰
    ("harm_prz_gartley",      new HarmonicPrzLsStrategy(new[] { "gartley" },      "harm_prz_gartley")),
    ("harm_prz_bat",          new HarmonicPrzLsStrategy(new[] { "bat" },          "harm_prz_bat")),
    ("harm_prz_butterfly",    new HarmonicPrzLsStrategy(new[] { "butterfly" },    "harm_prz_butterfly")),
    ("harm_prz_crab",         new HarmonicPrzLsStrategy(new[] { "crab" },         "harm_prz_crab")),
    ("harm_prz_deep_crab",    new HarmonicPrzLsStrategy(new[] { "deep_crab" },    "harm_prz_deep_crab")),
    ("harm_prz_deep_gartley", new HarmonicPrzLsStrategy(new[] { "deep_gartley" }, "harm_prz_deep_gartley")),
    ("harm_prz_cypher",       new HarmonicPrzLsStrategy(new[] { "cypher" },       "harm_prz_cypher")),
    ("harm_prz_shark",        new HarmonicPrzLsStrategy(new[] { "shark" },        "harm_prz_shark")),
    ("harm_prz_alt_bat",      new HarmonicPrzLsStrategy(new[] { "alt_bat" },      "harm_prz_alt_bat")),
    ("harm_prz_five_o",       new HarmonicPrzLsStrategy(new[] { "five_o" },       "harm_prz_five_o")),
    // H11 PRZ + fib 重做 H-Combo(原版用錯版 harmonic、結論作廢)
    ("harm_prz_fib_5050", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new HarmonicPrzLsStrategy(), 0.5m), (new FibRetraceLsStrategy(), 0.5m) }, name: "harm_prz_fib_5050")),
    ("harm_prz_fib_3070", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new HarmonicPrzLsStrategy(), 0.3m), (new FibRetraceLsStrategy(), 0.7m) }, name: "harm_prz_fib_3070")),
    ("harm_prz_fib_7030", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new HarmonicPrzLsStrategy(), 0.7m), (new FibRetraceLsStrategy(), 0.3m) }, name: "harm_prz_fib_7030")),
    // H12 PRZ + RangeBound regime(H1 重做)
    ("harm_prz_range_ls", new HarmonicPrzLsStrategy(
        patternWhitelist: null, name: "harm_prz_range_ls",
        regimeWhitelist: new[] { StrategyWorker.Engine.Indicators.RegimeDetector.RegimeType.RangeBound })),
    // H13 PRZ + Trending regime(新對比:PRZ 在趨勢 vs 橫盤誰強)
    ("harm_prz_trend_ls", new HarmonicPrzLsStrategy(
        patternWhitelist: null, name: "harm_prz_trend_ls",
        regimeWhitelist: new[] {
            StrategyWorker.Engine.Indicators.RegimeDetector.RegimeType.TrendingUp,
            StrategyWorker.Engine.Indicators.RegimeDetector.RegimeType.TrendingDown
        })),
    // ── 潛在「王牌」候選(對沖價值 / 去相關 sleeve)──
    // 輕量 sleeve(貼近 decorr4 的 10% fib 比例)
    ("harm_prz_fib_2080", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new HarmonicPrzLsStrategy(), 0.2m), (new FibRetraceLsStrategy(), 0.8m) }, name: "harm_prz_fib_2080")),
    // decorr5:把 harm_prz 加進現行 decorr4(比例縮放讓總和 100%)
    ("decorr5_with_harmprz", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new DualMomentumLsStrategy(),    0.33m),
            (new DualThrustStrategy(),        0.27m),
            (new BollingerRevertLsStrategy(), 0.16m),
            (new FibRetraceLsStrategy(),      0.09m),
            (new HarmonicPrzLsStrategy(),     0.15m),
        }, name: "decorr5_with_harmprz")),
    // ── 2026-06-02 decorr4 弱腿對照:live decorr4(4腿)vs 只留硬腿(dual_mom+dual_thrust)──
    // 問:bb_revert(沒過 pool t-stat)+ fib_retrace(輸 fib_confluence)在 net ensemble 裡是幫助還是拖累?
    ("decorr4_full", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new DualMomentumLsStrategy(),    0.38m),
            (new DualThrustStrategy(),        0.32m),
            (new BollingerRevertLsStrategy(), 0.19m),
            (new FibRetraceLsStrategy(),      0.10m),
        }, name: "decorr4_full")),
    ("decorr4_lite", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new DualMomentumLsStrategy(),    0.38m),
            (new DualThrustStrategy(),        0.32m),
        }, name: "decorr4_lite")),
    // 純 mean-revert 家族對:harm_prz + bb_revert 互補測試
    ("harm_prz_bb_revert", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new HarmonicPrzLsStrategy(), 0.5m), (new BollingerRevertLsStrategy(), 0.5m) }, name: "harm_prz_bb_revert")),
    // ── H7 top-2 patterns(基於 H6 發現:butterfly t=1.79 + five_o t=1.60 撐起 edge)──
    ("harm_prz_top2", new HarmonicPrzLsStrategy(
        new[] { "butterfly", "five_o" }, "harm_prz_top2")),
    // butterfly 單獨 + fib 組合(最強單 pattern + 最穩非趨勢腿)
    ("harm_prz_butterfly_fib", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new HarmonicPrzLsStrategy(new[] { "butterfly" }, "_butterfly"), 0.5m),
          (new FibRetraceLsStrategy(), 0.5m) }, name: "harm_prz_butterfly_fib")),
    // decorr5 更新版:用 butterfly-only 而非全 pattern 版加進去(濃縮 edge)
    ("decorr5_butterfly", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new DualMomentumLsStrategy(),    0.33m),
            (new DualThrustStrategy(),        0.27m),
            (new BollingerRevertLsStrategy(), 0.16m),
            (new FibRetraceLsStrategy(),      0.09m),
            (new HarmonicPrzLsStrategy(new[] { "butterfly" }, "_butterfly"), 0.15m),
        }, name: "decorr5_butterfly")),
    // ── H15 acid test:多窗口掃描(放鬆 trigger 看 edge 撐不撐得住)──
    // top-2 + 不同 scan_windows 看 sweet spot
    ("harm_prz_top2_scan5",  new HarmonicPrzLsStrategy(
        new[] { "butterfly", "five_o" }, "harm_prz_top2_scan5",  scanWindows: 5)),
    ("harm_prz_top2_scan10", new HarmonicPrzLsStrategy(
        new[] { "butterfly", "five_o" }, "harm_prz_top2_scan10", scanWindows: 10)),
    ("harm_prz_top2_scan20", new HarmonicPrzLsStrategy(
        new[] { "butterfly", "five_o" }, "harm_prz_top2_scan20", scanWindows: 20)),
    // butterfly 單獨 + scan(看 butterfly 一個 pattern 能不能撐起)
    ("harm_prz_butterfly_scan10", new HarmonicPrzLsStrategy(
        new[] { "butterfly" }, "harm_prz_butterfly_scan10", scanWindows: 10)),
    // 全 pattern + scan(對照、看 scan 提升能否補平 noise pattern 的負拖)
    ("harm_prz_scan10", new HarmonicPrzLsStrategy(
        patternWhitelist: null, name: "harm_prz_scan10", scanWindows: 10)),
    // ── H14 PRZ 浮動 ±15%(疊加 H15 scan10、看是否再加乘)──
    ("harm_prz_scan10_widepz", new HarmonicPrzLsStrategy(
        patternWhitelist: null, name: "harm_prz_scan10_widepz",
        scanWindows: 10, przWidening: 0.15m)),
    ("harm_prz_top2_scan10_widepz", new HarmonicPrzLsStrategy(
        new[] { "butterfly", "five_o" }, "harm_prz_top2_scan10_widepz",
        scanWindows: 10, przWidening: 0.15m)),
    // ── 王牌候選 decorr5(用 scan10 版替換早先 marginal 的 decorr5)──
    ("decorr5_scan10", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new DualMomentumLsStrategy(),    0.33m),
            (new DualThrustStrategy(),        0.27m),
            (new BollingerRevertLsStrategy(), 0.16m),
            (new FibRetraceLsStrategy(),      0.09m),
            (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_scan10", scanWindows: 10), 0.15m),
        }, name: "decorr5_scan10")),
    ("decorr5_top2_scan10", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new DualMomentumLsStrategy(),    0.33m),
            (new DualThrustStrategy(),        0.27m),
            (new BollingerRevertLsStrategy(), 0.16m),
            (new FibRetraceLsStrategy(),      0.09m),
            (new HarmonicPrzLsStrategy(new[] { "butterfly", "five_o" }, "_top2_scan10", scanWindows: 10), 0.15m),
        }, name: "decorr5_top2_scan10")),
    // ── BTC 組合實驗(2026-06-04、諧波為主、跟現有 momentum-heavy 組合對照)──
    //   現有 decorr5 諧波只 0.15(被稀釋);這幾個把諧波拉到 0.4-0.5、看它真有份量時 BTC 怎麼跑。
    ("btc_harm_ma", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_h", scanWindows: 10, przWidening: 0.15m), 0.50m),
            (new MaRegimeTrendStrategy(), 0.50m),   // 諧波 + 趨勢(2 去相關機制;諧波可確認/否決趨勢)
        }, name: "btc_harm_ma")),
    ("btc_harm_lead", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_h", scanWindows: 10, przWidening: 0.15m), 0.50m),
            (new DualMomentumLsStrategy(),    0.25m),
            (new BollingerRevertLsStrategy(), 0.25m),   // 諧波主導 + 動量/均值回歸當輔
        }, name: "btc_harm_lead")),
    ("btc_harm_accel_bb", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_h", scanWindows: 10, przWidening: 0.15m), 0.40m),
            (new AccelMomentumStrategy(),     0.30m),
            (new BollingerRevertLsStrategy(), 0.30m),   // 諧波 + 最強動量 + 均值回歸(3 機制、諧波最大)
        }, name: "btc_harm_accel_bb")),
    // ── BTC 組合實驗 第二批(2026-06-04、拚不同走向看逐年比較)──
    ("btc_ma_accel", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new MaRegimeTrendStrategy(), 0.50m),
            (new AccelMomentumStrategy(), 0.50m),   // 純趨勢對(兩個最強趨勢)
        }, name: "btc_ma_accel")),
    ("btc_ma_bb", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new MaRegimeTrendStrategy(),     0.60m),
            (new BollingerRevertLsStrategy(), 0.40m),   // 趨勢 + 均值回歸
        }, name: "btc_ma_bb")),
    ("btc_accel_bb", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new AccelMomentumStrategy(),     0.60m),
            (new BollingerRevertLsStrategy(), 0.40m),   // 動量 + MR(無 ma)
        }, name: "btc_accel_bb")),
    ("btc_harm_bb", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_h", scanWindows: 10, przWidening: 0.15m), 0.50m),
            (new BollingerRevertLsStrategy(), 0.50m),   // 諧波 + MR(無動量、最去相關嘗試)
        }, name: "btc_harm_bb")),
    ("btc_tri_balanced", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new MaRegimeTrendStrategy(),     0.34m),
            (new AccelMomentumStrategy(),     0.33m),
            (new BollingerRevertLsStrategy(), 0.33m),   // 趨勢×2 + MR 三均衡
        }, name: "btc_tri_balanced")),
    ("btc_def", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new BollingerRevertLsStrategy(), 0.40m),
            (new FibRetraceLsStrategy(),      0.30m),
            (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_h", scanWindows: 10, przWidening: 0.15m), 0.30m),   // 防禦:MR + Fib + 諧波(無動量)
        }, name: "btc_def")),
    // ── BTC 組合實驗 第三批(2026-06-04、再備幾種不同角度)──
    ("btc_accel_heavy", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new AccelMomentumStrategy(),     0.70m),
            (new BollingerRevertLsStrategy(), 0.30m),   // accel 主導(最強單腿)+ MR 緩衝
        }, name: "btc_accel_heavy")),
    ("btc_trend_trio", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new MaRegimeTrendStrategy(),  0.40m),
            (new AccelMomentumStrategy(),  0.30m),
            (new DualMomentumLsStrategy(), 0.30m),   // 純趨勢/動量三
        }, name: "btc_trend_trio")),
    ("btc_harm_thrust", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_h", scanWindows: 10, przWidening: 0.15m), 0.50m),
            (new DualThrustStrategy(), 0.50m),   // 諧波 + 突破(都偏區間/反轉)
        }, name: "btc_harm_thrust")),
    ("btc_mr_pair", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new BollingerRevertLsStrategy(), 0.50m),
            (new FibRetraceLsStrategy(),      0.50m),   // 純均值回歸對
        }, name: "btc_mr_pair")),
    ("btc_kitchen", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_h", scanWindows: 10, przWidening: 0.15m), 0.20m),
            (new MaRegimeTrendStrategy(),     0.20m),
            (new AccelMomentumStrategy(),     0.20m),
            (new DualMomentumLsStrategy(),    0.20m),
            (new BollingerRevertLsStrategy(), 0.20m),   // 五機制等權「全餐」
        }, name: "btc_kitchen")),
    // ── 布林家族補測(原本沒進 strat-validate)──
    ("bollinger_bands",   new BollingerStrategy()),          // 基本版(無趨勢過濾、bb_revert_ls 的對照)
    ("squeeze_breakout",  new SqueezeBreakoutStrategy()),    // 完全不同邏輯:波動收縮後突破、順勢進
    // 維加斯通道(多層 EMA 趨勢跟隨、跟布林/斐波/諧波不同流派)
    // ⚠ MinBars=700,在 1000-bar 資料下 walk-forward fold 數會偏少;先註冊看
    ("vegas_tunnel",      new VegasTunnelStrategy()),
    // 潛在互補組合:squeeze 突破 + harm_prz_top2 反轉(理論上市場狀態互斥)
    ("squeeze_harm_prz_top2", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new SqueezeBreakoutStrategy(), 0.5m),
          (new HarmonicPrzLsStrategy(new[] { "butterfly", "five_o" }, "_top2"), 0.5m) },
        name: "squeeze_harm_prz_top2")),
    // ── 新組合候選(用 H15 scan10 強版本)──
    ("squeeze_harm_prz_scan10", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new SqueezeBreakoutStrategy(), 0.5m),
          (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_scan10", scanWindows: 10), 0.5m) },
        name: "squeeze_harm_prz_scan10")),
    // 動量 + 諧波(vol-managed momentum + 反轉、時序錯開)
    ("tsmom_harm_prz_scan10", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new TsMomentumStrategy(), 0.5m),
          (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_scan10", scanWindows: 10), 0.5m) },
        name: "tsmom_harm_prz_scan10")),
    // Chandelier 突破 + 諧波(Donchian 趨勢延續 + 反轉)
    ("chandelier_harm_prz_scan10", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new ChandelierTrendStrategy(), 0.5m),
          (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_scan10", scanWindows: 10), 0.5m) },
        name: "chandelier_harm_prz_scan10")),
    // 三腿:諧波 + fib 回撤 + 雙動量(三種不同 mechanism、理論上去相關最大)
    ("triple_pattern_mom", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_scan10", scanWindows: 10), 0.33m),
          (new FibRetraceLsStrategy(), 0.34m),
          (new DualMomentumLsStrategy(), 0.33m) },
        name: "triple_pattern_mom")),
    // ── Tier 1:延伸 H14 widepz 王牌候選的組合(2026-05-26 晚)──
    // H35:decorr5 用 widepz 取代 scan10
    ("decorr5_widepz", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        {
            (new DualMomentumLsStrategy(),    0.33m),
            (new DualThrustStrategy(),        0.27m),
            (new BollingerRevertLsStrategy(), 0.16m),
            (new FibRetraceLsStrategy(),      0.09m),
            (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_widepz", scanWindows: 10, przWidening: 0.15m), 0.15m),
        }, name: "decorr5_widepz")),
    // H32:widepz + ts_momentum(今天 alpha 王 t=3.22)
    ("tsmom_widepz", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new TsMomentumStrategy(), 0.5m),
          (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_widepz", scanWindows: 10, przWidening: 0.15m), 0.5m) },
        name: "tsmom_widepz")),
    // H33:widepz + chandelier(突破 + 反轉)
    ("chandelier_widepz", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new ChandelierTrendStrategy(), 0.5m),
          (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_widepz", scanWindows: 10, przWidening: 0.15m), 0.5m) },
        name: "chandelier_widepz")),
    // H34:4-leg(widepz + fib + dual_mom + bb_revert)
    ("quad_widepz", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new HarmonicPrzLsStrategy(patternWhitelist: null, name: "_widepz", scanWindows: 10, przWidening: 0.15m), 0.25m),
          (new FibRetraceLsStrategy(), 0.25m),
          (new DualMomentumLsStrategy(), 0.25m),
          (new BollingerRevertLsStrategy(), 0.25m) },
        name: "quad_widepz")),
    // 研究實驗(fib research log H1-Fib, 2026-05-26):FibRetrace + RegimeDetector 真趨勢
    ("fib_retrace_regime_ls", new FibRetraceRegimeLsStrategy()),
    // H2-Fib(2026-05-26):FibRetrace + textbook Fib SL,看 DD 能否從 96 砍下來
    ("fib_retrace_sl_ls", new FibRetraceSlLsStrategy()),
    // 諧波+斐波組合實驗(2026-05-26 H-Combo):失敗策略組合是否有突破口
    ("harm_fib_5050", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new HarmonicLsStrategy(), 0.5m), (new FibRetraceLsStrategy(), 0.5m) }, name: "harm_fib_5050")),
    ("harm_fib_3070", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new HarmonicLsStrategy(), 0.3m), (new FibRetraceLsStrategy(), 0.7m) }, name: "harm_fib_3070")),
    ("harm_range_fib_regime_5050", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new HarmonicRangeLsStrategy(), 0.5m), (new FibRetraceRegimeLsStrategy(), 0.5m) }, name: "harm_range_fib_regime_5050")),
    // 一鍵淨加權 ensemble(去相關4支、反波動率權重)— 對照「真組合」
    ("decorr4_ls", new NetWeightedEnsembleStrategy(new List<(IStrategy, decimal)>
        { (new DualMomentumLsStrategy(), 0.38m), (new DualThrustStrategy(), 0.32m),
          (new BollingerRevertLsStrategy(), 0.19m), (new FibRetraceLsStrategy(), 0.10m) }, name: "decorr4_ls")),
};

// --only=PAT1,PAT2 過濾(支援 *、可逗號分隔多 pattern OR-match)
if (onlyFilter != null)
{
    var patterns = onlyFilter.Split(',')
        .Select(p => "^" + System.Text.RegularExpressions.Regex.Escape(p.Trim()).Replace("\\*", ".*") + "$");
    var combined = string.Join("|", patterns);
    var regex = new System.Text.RegularExpressions.Regex(
        combined, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    var filtered = strats.Where(s => regex.IsMatch(s.name)).ToArray();
    Console.WriteLine($"⚡ 過濾後策略: {filtered.Length} 個 ({string.Join(", ", filtered.Select(s => s.name))})");
    strats = filtered;
    if (strats.Length == 0) { Console.WriteLine("(no strategy matched --only filter、退出)"); return; }
}

// ── --allocate:穩健配置引擎(獨立快速模式,不跑完整報告)──────────────
// 4 步:① 入場閘(顯著 + full 正 + sharpe>0) ② edge×逆波動 raw 權重 ③ 朝等權收縮(λ=T/T*)
//        + 相關 haircut + 單腿上限 ④ vol-target 算整體曝險。輸出每腿 budget_pct + N_eff + 畢業判定。
if (args.Contains("--allocate")) { await RunAllocate(); return; }

// ── --xmarket:跨市場諧波分散(crypto vs 美股 vs 台股 的 harmonic 日報酬相關 + 等權組合)──
if (args.Contains("--xmarket")) { await RunXMarket(); return; }

// ── --dispersion:edge 離散度診斷(選股能不能救活弱策略的前置判定)──
if (args.Contains("--dispersion")) { await RunDispersion(); return; }

// ── --selection-wf:嚴格版選股(trailing-vol per rebalance、ex-ante、無 lookahead)──
if (args.Contains("--selection-wf")) { await RunSelectionWf(); return; }

// ── --stock-router:個股性格路由回測(每股算 vol×ER 性格 → 配策略;驗證路由 vs 單策略 vs 隨機)──
if (args.Contains("--stock-router")) { await RunStockRouter(); return; }

// ── --xsmom:橫斷面動量(cross-sectional momentum)——結構不同的去相關 edge 研究 ──
// 跨幣排序:每 rebal 期 long 過去 lookback 報酬最強的 topK、short 最弱的 topK(等權)。
// 跟所有「單幣技術指標」正交;驗證有沒有 OOS edge + 跟現有書(decorr4)相不相關。
if (args.Contains("--xsmom")) { await RunXsMom(); return; }

// ── --carry:資金費 carry 研究(現貨多+永續空收 funding、近零風險、跟價格動量正交)──
// 量化:各幣 funding 年化多少、穩不穩、扣成本後淨 carry、小本金值不值得。
if (args.Contains("--carry")) { await RunCarry(); return; }

// ── --xsrev:短週期橫斷面反轉(long 跌最兇/short 漲最兇)+ 與動量相關性 ──
if (args.Contains("--xsrev")) { await RunXsRev(); return; }
// ── --fundsig:資金費當跨幣訊號(contrarian:long 低 funding/short 高 funding)──
if (args.Contains("--fundsig")) { await RunFundSig(); return; }
// ── --tsmom:時序動量(managed-futures:每幣按自己趨勢多/空、等權籃子、熊市自動翻空)──
if (args.Contains("--tsmom")) { await RunTsMom(); return; }
// ── --pairs:配對/協整 stat-arb(價差 z-score 均值回歸、market-neutral、應該真去相關)──
if (args.Contains("--pairs")) { await RunPairs(); return; }
if (args.Contains("--leadlag")) { await RunLeadLag(); return; }
if (args.Contains("--regime")) { await RunRegime(); return; }
// ── --lowvol:橫斷面低波動異常(long 低波動幣/short 高波動)──
if (args.Contains("--lowvol")) { await RunLowVol(); return; }
// ── --harmonic:諧波/fib 跨時框重驗(觸發頻率 + OOS + 顯著性;測低時框能否救回)──
if (args.Contains("--harmonic")) { await RunHarmonic(); return; }

async Task<List<BarData>> Fetch(string sym, string interval = "1d")
{
    // Yahoo 模式(美股/台股/FX):Yahoo 日線(StockBarCache)、無 funding;否則 Binance KlineCache
    if (yahooMode)
        return await ToolsShared.StockBarCache.FetchOrLoad(sym, interval, limit: barsLimit);
    // 走共享 KlineCache(~/.cache/brick4agent/klines/、24h TTL、FORCE_REFRESH_KLINES=1 強制刷)
    // --bars=N 可指定深度(預設 1000、> 1000 走分頁)
    var bars = await ToolsShared.KlineCache.FetchOrLoad(sym, interval, limit: barsLimit);
    // 套用 runtime funding 假設(cache 不存 FundingRate、避免不同 fundingPer8h 衝突)
    foreach (var b in bars) b.FundingRate = fundingPer8h;
    return bars;
}

decimal Median(List<decimal> xs)
{
    if (xs.Count == 0) return 0m;
    var s = xs.OrderBy(x => x).ToList(); int n = s.Count;
    return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2m;
}

// 從權益值序列算 (報酬%, 年化Sharpe, maxDD%)
(decimal ret, decimal sharpe, decimal dd) StatsOf(List<decimal> eq)
{
    if (eq.Count < 2) return (0, 0, 0);
    var rets = new List<decimal>();
    for (int i = 1; i < eq.Count; i++) { if (eq[i - 1] > 0) rets.Add((eq[i] - eq[i - 1]) / eq[i - 1]); }
    decimal tot = eq[0] > 0 ? (eq[^1] - eq[0]) / eq[0] * 100m : 0m;
    decimal sh = 0m;
    if (rets.Count > 1)
    {
        var a = rets.Average();
        var sd = (decimal)Math.Sqrt((double)rets.Select(r => (r - a) * (r - a)).Average());
        sh = sd > 0 ? a / sd * (decimal)Math.Sqrt(252) : 0m;
    }
    decimal peak = eq[0], mdd = 0m;
    foreach (var v in eq) { if (v > peak) peak = v; var d = peak > 0 ? (peak - v) / peak * 100m : 0m; if (d > mdd) mdd = d; }
    return (Math.Round(tot, 1), Math.Round(sh, 2), Math.Round(mdd, 1));
}

var data = new Dictionary<string, List<BarData>>();
foreach (var sym in symbols)
{
    try { var b = await Fetch(sym); if (b.Count >= 400) data[sym] = b; }
    catch (Exception ex) { Console.WriteLine($"{sym}: {ex.Message}"); }
}
Console.WriteLine($"\n資料就緒:{data.Count}/{symbols.Length} 檔(≥400 日線)");

// --coins=SYM1,SYM2,...:把宇宙篩到指定幣(測「濾掉弱 per-coin 幣對 book 的影響」)。可不帶 USDT 後綴。
var coinsFilter = args.FirstOrDefault(a => a.StartsWith("--coins="))?.Substring(8);
if (coinsFilter != null)
{
    var keep = coinsFilter.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(c => c.Trim().ToUpperInvariant()).Select(c => c.EndsWith("USDT") ? c : c + "USDT").ToHashSet();
    foreach (var k in data.Keys.ToList()) if (!keep.Contains(k)) data.Remove(k);
    Console.WriteLine($"🎯 --coins:宇宙篩到 {data.Count} 檔({string.Join(",", data.Keys.Select(c => c.Replace("USDT", "")))})");
}

// --concurrency:諧波多幣「同步觸發風險」診斷 — 跑全幣、把每筆交易疊日曆 → 每天同時幾倉 + entry 群聚。
// 判讀:平均同時持倉/最大同時/≥N同步%;entry 群聚高 = 常常同步成一個大相關賭注 → 要設 max-concurrent。
if (args.Contains("--concurrency"))
{
    var hstrat = new HarmonicPrzLsStrategy(patternWhitelist: null, name: "harm_prz_scan10_widepz", scanWindows: 10, przWidening: 0.15m);
    var trades = new List<(string coin, DateTime entry, DateTime exit)>();
    foreach (var kv in data)
        try { var bt = LongShortBacktestEngine.Run(hstrat, kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, commission: gComm, slippagePct: gSlip);
              foreach (var t in bt.Trades) trades.Add((kv.Key, t.EntryDate, t.ExitDate)); }
        catch { }
    Console.WriteLine($"\n=== 諧波多幣同步觸發風險(harm_prz_scan10_widepz、{data.Count} 幣、全期 1d)===");
    Console.WriteLine($"  總交易 {trades.Count} 筆、{trades.Count / Math.Max(1.0, data.Count):F1} 筆/幣");
    if (trades.Count > 0)
    {
        var cal = (data.TryGetValue("BTCUSDT", out var btc) ? btc.Select(b => b.OpenTime.Date) : trades.Select(t => t.entry.Date))
            .Distinct().OrderBy(d => d).ToList();
        var concs = cal.Select(day => trades.Count(t => t.entry <= day && day < t.exit)).ToList();
        double avg = concs.Average(); int max = concs.Max();
        var sortedC = concs.OrderBy(x => x).ToList(); int med = sortedC[sortedC.Count / 2];
        double Pct(int thr) => concs.Count(c => c >= thr) / (double)concs.Count * 100;
        Console.WriteLine($"  同時持倉:平均 {avg:F1} / 中位 {med} / 最大 {max}(共 {concs.Count} 交易日)");
        Console.WriteLine($"    ≥5 同時:{Pct(5):F0}% of 交易日、≥10:{Pct(10):F0}%、≥{data.Count / 2}(半數幣):{Pct(data.Count / 2):F0}%");
        var entriesPerDay = trades.GroupBy(t => t.entry.Date).Select(g => g.Count()).ToList();
        int maxEntries = entriesPerDay.Max();
        int entriesOn5plus = trades.GroupBy(t => t.entry.Date).Where(g => g.Count() >= 5).Sum(g => g.Count());
        Console.WriteLine($"  entry 群聚:單日最多 {maxEntries} 個同步進場、{entriesOn5plus * 100 / trades.Count}% 的進場落在「單日≥5 同步進」的日子");
        Console.WriteLine($"  → 判讀:平均同時持倉低 + ≥半數幣同時% 低 = 真分散;entry 群聚高 = 常同步成一賭注、需 max-concurrent 上限。");
    }
    return;
}

// --booksim:模擬「先到先得 + max N 槽 + 每槽槓桿」的 portfolio 權益路徑 → 真實 DD / 連虧 / 最差單筆 / 強平參考。
// 用法 --booksim(預設 max 2 槽、跑 1x/2x/3x 總槓桿對照)。每槽槓桿 = 總槓桿 / 槽數;常態 1 倉時 = 半個總槓桿。
if (args.Contains("--booksim"))
{
    var hstrat = new HarmonicPrzLsStrategy(patternWhitelist: null, name: "harm_prz_scan10_widepz", scanWindows: 10, przWidening: 0.15m);
    var bt0 = new List<(string coin, DateTime entry, DateTime exit, double pnl)>();
    foreach (var kv in data)
        try { var bt = LongShortBacktestEngine.Run(hstrat, kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, commission: gComm, slippagePct: gSlip,
                  peakTrailTriggerPct: withProtection ? protTrailTrig : 0m, peakTrailDistancePct: withProtection ? protTrailDist : 0m,
                  beTriggerPct: withProtection ? protBeTrig : 0m, beBufferPct: withProtection ? protBeBuf : 0m);
              foreach (var t in bt.Trades) bt0.Add((kv.Key, t.EntryDate, t.ExitDate, (double)t.PnlPct / 100.0)); }
        catch { }
    bt0 = bt0.OrderBy(t => t.entry).ToList();
    int maxSlots = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--slots="))?.Substring(8), out var msArg) ? Math.Max(1, msArg) : 2;
    double years = bt0.Count > 0 ? Math.Max(0.5, (bt0.Max(t => t.exit) - bt0.Min(t => t.entry)).TotalDays / 365.0) : 1;
    Console.WriteLine($"\n=== Book 模擬:先到先得 + max {maxSlots} 槽(harm_prz_scan10_widepz、{data.Count} 幣全宇宙、{bt0.Count} 筆候選、~{years:F1}年)===");
    Console.WriteLine($"  {"總槓桿",7} {"每槽",5} {"固定報酬x",9} {"固定DD%",8} {"報酬/DD",8} {"複利DD%",8} {"最長連虧",8} {"最差單筆%",9} {"接/跳",9}");
    foreach (var totalLev in new[] { 1.0, 2.0, 3.0 })
    {
        double perSlot = totalLev / maxSlots;
        var ev = new List<(DateTime d, int type, int idx)>();
        for (int i = 0; i < bt0.Count; i++) { ev.Add((bt0[i].entry, 0, i)); ev.Add((bt0[i].exit, 1, i)); }
        ev = ev.OrderBy(e => e.d).ThenBy(e => e.type == 1 ? 0 : 1).ToList();   // 同日先處理 exit 釋放槽
        double eqC = 1.0, peakC = 1.0, ddC = 0;   // 複利
        double eqF = 1.0, peakF = 1.0, ddF = 0;   // 固定倉位(加法、去複利失真;每筆 = perSlot × 起始權益)
        double worst = 0; int openCount = 0, taken = 0, skipped = 0, curStreak = 0, maxStreak = 0;
        var openSet = new HashSet<int>();
        foreach (var e in ev)
        {
            if (e.type == 0) { if (openCount < maxSlots) { openSet.Add(e.idx); openCount++; taken++; } else skipped++; }
            else if (openSet.Remove(e.idx))
            {
                openCount--;
                double impact = perSlot * bt0[e.idx].pnl;
                eqC *= 1 + impact; eqF += impact;
                if (impact < worst) worst = impact;
                if (bt0[e.idx].pnl < 0) { curStreak++; maxStreak = Math.Max(maxStreak, curStreak); } else curStreak = 0;
                if (eqC > peakC) peakC = eqC; if ((peakC - eqC) / peakC > ddC) ddC = (peakC - eqC) / peakC;
                if (eqF > peakF) peakF = eqF; if ((peakF - eqF) / peakF > ddF) ddF = (peakF - eqF) / peakF;
            }
        }
        double calmar = ddF > 0 ? (eqF - 1) / ddF : 0;   // 去複利報酬 / 去複利DD = 乾淨效率
        Console.WriteLine($"  {totalLev,6:F1}x {perSlot,4:F2}x {eqF,9:F2} {ddF * 100,8:F0} {calmar,8:F1} {ddC * 100,8:F0} {maxStreak,8} {worst * 100,9:F1} {$"{taken}/{skipped}",9}");
    }
    // 尾部:單筆 PnlPct 分布 + 最差幾筆(槓桿無關、看尾險來源)
    var pnls = bt0.Select(t => t.pnl).OrderBy(x => x).ToList();
    double Q(double q) => pnls.Count > 0 ? pnls[(int)Math.Clamp(q * (pnls.Count - 1), 0, pnls.Count - 1)] * 100 : 0;
    Console.WriteLine($"\n  單筆報酬分布(underlying、未槓桿):最差 {Q(0):F1}% / p1 {Q(.01):F1}% / p5 {Q(.05):F1}% / 中位 {Q(.5):F1}% / p95 {Q(.95):F1}% / 最佳 {Q(1):F1}%");
    Console.WriteLine($"  勝率 {pnls.Count(p => p > 0) * 100.0 / Math.Max(1, pnls.Count):F0}% · 最差 8 筆(coin / underlying% / 3x下對權益%):");
    foreach (var t in bt0.OrderBy(t => t.pnl).Take(8))
        Console.WriteLine($"    {t.coin,-10} {t.pnl * 100,7:F1}%   → 3x:{t.pnl * 1.5 * 100,6:F1}%");
    Console.WriteLine($"  → SL 距離解讀:最差 underlying 若遠超「結構 SL ~5-10%」= 該 setup SL 太寬或跳空穿。強平由交易所槓桿(5x)決定、與 book 槓桿無關:任何 underlying < ~−20% 的單即觸 5x 強平 → 看上面最差幾筆有沒有 < −20%。");
    Console.WriteLine($"  註:固定DD=去複利、較貼近真實感受;複利DD=滾倉(偏大)。全宇宙含低 edge 幣、quality 宇宙會更好。");
    return;
}

// --from/--to 切窗:在注入(funding/retail)前切,讓注入只 fetch 窗內、B&H 基準也對齊窗(2022 熊 B&H 大跌才是對的對照)
if (winFrom != null || winTo != null)
{
    var lo = winFrom ?? DateTime.MinValue; var hi = winTo ?? DateTime.MaxValue;
    foreach (var k in data.Keys.ToList())
    {
        var w = data[k].Where(b => b.OpenTime >= lo && b.OpenTime <= hi).ToList();
        if (w.Count >= 60) data[k] = w; else data.Remove(k);  // 窗內太少(< 約 2 個月)就丟
    }
    Console.WriteLine($"🗓 切窗後:{data.Count} 檔(窗內 ≥60 bars)");
}

// 2026-05-29:設 BTC bars 給 BtcRegimeFilterStrategy(tsmom_btc_not_up 才能評估外生 regime、否則退化成 ts_momentum)
if (data.TryGetValue("BTCUSDT", out var btcRef)) BtcRegimeFilterStrategy.BtcBarsRef = btcRef;

// 2026-05-27 D 路線:--apply-funding 注入真實 Binance funding history
// 每個 symbol 抓 + align、bar.FundingRate 填好,LongShortBacktestEngine(applyFunding=true)會用
// Yahoo 模式(美股/台股/FX):無 funding/retail_ls,自動 skip 注入(get_bars_funding 不適用)
if (realFunding && !yahooMode)
{
    Console.WriteLine("💸 注入真實 funding 歷史(Binance fapi):");
    foreach (var kv in data)
    {
        await ToolsShared.FundingCache.InjectInto(kv.Value, kv.Key, "1d");
        var nz = kv.Value.Count(b => b.FundingRate.HasValue && b.FundingRate != 0m);
        Console.WriteLine($"  {kv.Key}: {nz}/{kv.Value.Count} bars 注入");
    }
}

// 2026-05-28 Q2 retail_ls_contrarian:注入 RetailLongShortRatio(data.binance.vision daily metrics zip)
// Yahoo 模式 skip(股票/FX 無 perp metrics)
if (realRetailLs && !yahooMode)
{
    Console.WriteLine("📊 注入 retail_ls 歷史(data.binance.vision metrics):");
    foreach (var kv in data)
    {
        await ToolsShared.OiMetricsCache.InjectInto(kv.Value, kv.Key, "1d");
        var nz = kv.Value.Count(b => b.RetailLongShortRatio.HasValue);
        Console.WriteLine($"  {kv.Key}: {nz}/{kv.Value.Count} bars 注入 retail_ls");
    }
}

var bhRets = new List<decimal>(); var bhShs = new List<decimal>(); var bhDds = new List<decimal>();
foreach (var kv in data) { var st = StatsOf(kv.Value.Select(b => b.Close).ToList()); bhRets.Add(st.ret); bhShs.Add(st.sharpe); bhDds.Add(st.dd); }
if (data.Count > 0)
    Console.WriteLine($"=== Buy & Hold 基準(全期)===  中位 ret {Median(bhRets):F0}% | 平均 Sharpe {bhShs.Average():F2} | 平均 maxDD {bhDds.Average():F0}%");

// --conf-diag:confidence 校準診斷(複用已載入 data + strats)── 答「confidence 有預測力嗎 / 跨策略可比嗎」
if (confDiag) { RunConfDiagnostic(); return; }
if (volTargetAB) { RunVolTargetAB(); return; }

// 權益曲線對應日期(per coin、各策略共用同 bars;lsEq 最後跑 → 留 LS 引擎日期),給 regime-gate 用
// ConcurrentDictionary:PrintTable 平行化後多策略併發寫(同幣 dates 相同、寫入值一致、只需執行緒安全容器)
var curveDates = new ConcurrentDictionary<string, List<DateTime>>();
// 平行度上限 = 邏輯核心數(walk-forward 互相獨立、CPU-bound)
var ParOpts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

// 一張表 = 一種引擎(long-only / long-short);回傳 [strat][symbol]=全期權益曲線(給相關/組合用)
Dictionary<string, Dictionary<string, List<decimal>>> PrintTable(
    string title,
    Func<IStrategy, List<BarData>, StrategyConfig, BacktestEngine.WalkForwardResult> wf,
    Func<IStrategy, List<BarData>, StrategyConfig, BacktestEngine.BacktestResult> run)
{
    Console.WriteLine($"\n=== {title} 可用性(OOS train250/test90/stride60 跨檔;Full=全期連續、無調參)===");
    Console.WriteLine($"  {"strategy",-16}{"OOSsym+%",9}{"OOSmed%",9}{"+fold%",8}│{"fullRet%",10}{"fullSh",8}{"fullDD%",9}{"DD<BH%",8}  判定");
    var eqAll = new Dictionary<string, Dictionary<string, List<decimal>>>();
    var lines = new ConcurrentDictionary<string, string>();
    var eqByName = new ConcurrentDictionary<string, Dictionary<string, List<decimal>>>();
    Parallel.ForEach(strats, ParOpts, ns =>
    {
        var (name, s) = ns;
        int symPos = 0, symTot = 0, ddBeat = 0;
        var oosRets = new List<decimal>(); var foldRets = new List<decimal>();
        var fullRets = new List<decimal>(); var fullShs = new List<decimal>(); var fullDds = new List<decimal>();
        var perSym = new Dictionary<string, List<decimal>>();
        foreach (var kv in data)
        {
            var cfg = new StrategyConfig { Symbol = kv.Key, Interval = "1d" };
            BacktestEngine.WalkForwardResult w;
            try { w = wf(s, kv.Value, cfg); } catch { continue; }
            if (w.TotalFolds == 0) continue;
            symTot++;
            if (w.AvgTestReturnPct > 0) symPos++;
            oosRets.Add(w.AvgTestReturnPct);
            foreach (var f in w.Folds.Where(f => f.Test != null)) foldRets.Add(f.Test!.TotalReturnPct);
            var bt = run(s, kv.Value, cfg);
            fullRets.Add(bt.TotalReturnPct); fullShs.Add(bt.SharpeRatio); fullDds.Add(bt.MaxDrawdownPct);
            perSym[kv.Key] = bt.EquityCurve.Select(e => e.Value).ToList();
            curveDates[kv.Key] = bt.EquityCurve.Select(e => e.Date).ToList();   // 同幣 dates 各策略相同、併發寫安全
            if (bt.MaxDrawdownPct < StatsOf(kv.Value.Select(b => b.Close).ToList()).dd) ddBeat++;
        }
        if (symTot == 0) { lines[name] = $"  {name,-16} (無資料)"; return; }
        decimal symPosPct = (decimal)symPos / symTot * 100m;
        decimal medRet = Median(oosRets);
        decimal foldPos = foldRets.Count > 0 ? (decimal)foldRets.Count(r => r > 0) / foldRets.Count * 100m : 0m;
        decimal fSh = fullShs.Average();
        bool usable = medRet > 0 && symPosPct >= 60m && fSh > 0m;
        lines[name] = $"  {name,-16}{symPosPct,8:F0}%{medRet,9:F1}{foldPos,7:F0}%│{fullRets.Average(),10:F0}{fSh,8:F2}{fullDds.Average(),9:F0}{(decimal)ddBeat / symTot * 100m,7:F0}%  {(usable ? "✅ 可用" : "❌")}";
        eqByName[name] = perSym;
    });
    foreach (var (name, _) in strats)   // 按 strats 原順序印 + 建 eqAll(不被平行交錯)
    {
        if (lines.TryGetValue(name, out var ln)) Console.WriteLine(ln);
        if (eqByName.TryGetValue(name, out var eq)) eqAll[name] = eq;
    }
    return eqAll;
}

var loEq = PrintTable("Long-only(Benson 引擎)", (s, b, c) => BacktestEngine.RunWalkForward(s, b, c, 250, 90, 60), (s, b, c) => BacktestEngine.Run(s, b, c));
var lsEq = PrintTable("Long-short(新引擎)",
    (s, b, c) => LongShortBacktestEngine.RunWalkForward(s, b, c, 250, 90, 60,
        defaultInitialSlPct: withProtection ? protSl : slPct,
        peakTrailTriggerPct: withProtection ? protTrailTrig : 0m, peakTrailDistancePct: withProtection ? protTrailDist : 0m,
        beTriggerPct: withProtection ? protBeTrig : 0m, beBufferPct: withProtection ? protBeBuf : 0m),
    (s, b, c) => LongShortBacktestEngine.Run(s, b, c,
        defaultInitialSlPct: withProtection ? protSl : slPct,
        peakTrailTriggerPct: withProtection ? protTrailTrig : 0m, peakTrailDistancePct: withProtection ? protTrailDist : 0m,
        beTriggerPct: withProtection ? protBeTrig : 0m, beBufferPct: withProtection ? protBeBuf : 0m));

// ── 多時框 策略 × 幣 分析(預設 1h~1w,找跨時框穩健最優解)──────────────
// long-only 引擎(對應實際 perp_long_only)。每時框 per(策略,幣) OOS = walk-forward avg test%。
// 跨時框一致 = 真 edge 的證據;只在單一時框好 = 多半噪音/單一行情(如 XRP 那波大漲)。
// --notf:全宇宙但只跑 1d(跳過 5 時框矩陣 = 最大的 5× 成本)、迭代/全宇宙快跑用。edge 本來就在 1d。
string[] intervals = (fastMode || args.Contains("--notf")) ? new[] { "1d" } : new[] { "1h", "4h", "12h", "1d", "1w" };
string Sh(string s) => s.Replace("USDT", "");

// 跑單一時框的 策略×幣 grid(printGrid=true 才印完整表),回傳 strat->coin->OOS%
Dictionary<string, Dictionary<string, decimal>> PerCoinMatrix(string iv, Dictionary<string, List<BarData>> dv, bool printGrid)
{
    var coins = dv.Keys.ToList();
    var outp = new Dictionary<string, Dictionary<string, decimal>>();
    if (printGrid)
    {
        Console.WriteLine($"\n=== [{iv}] 策略 × 幣 OOS 報酬%矩陣(long-only、walk-forward avg test%;★=該策略最佳幣)===");
        Console.WriteLine("  " + new string(' ', 14) + string.Join("", coins.Select(c => $"{Sh(c),7}")) + "  │ 最佳 / 中位");
    }
    var rowByName = new ConcurrentDictionary<string, Dictionary<string, decimal>>();
    Parallel.ForEach(strats, ParOpts, ns =>
    {
        var (name, s) = ns;
        var row = new Dictionary<string, decimal>();
        foreach (var c in coins)
        {
            try { var w = BacktestEngine.RunWalkForward(s, dv[c], new StrategyConfig { Symbol = c, Interval = iv }, 250, 90, 60);
                  if (w.TotalFolds > 0) row[c] = Math.Round(w.AvgTestReturnPct, 1); }
            catch { }
        }
        if (row.Count > 0) rowByName[name] = row;
    });
    foreach (var (name, _) in strats)   // 按 strats 原順序填 outp + 印 grid
    {
        if (!rowByName.TryGetValue(name, out var row)) continue;
        outp[name] = row;
        if (printGrid)
        {
            var best = row.OrderByDescending(kv => kv.Value).First();
            var med = Median(row.Values.ToList());
            var cells = coins.Select(c => !row.ContainsKey(c) ? $"{"-",7}" : $"{row[c],6:F0}{(c == best.Key ? "★" : " ")}");
            Console.WriteLine($"  {name,-14}{string.Join("", cells)}  │ {Sh(best.Key)} {best.Value,4:F0}% / 中位{med,4:F0}%");
        }
    }
    return outp;
}

// 跑每個時框(1d 重用上面抓的;其餘現抓);只印 1d 完整 grid、其餘只收數據
var tf = new Dictionary<string, Dictionary<string, Dictionary<string, decimal>>>();
foreach (var iv in intervals)
{
    Dictionary<string, List<BarData>> dv;
    if (iv == "1d") dv = data;
    else { dv = new(); foreach (var sym in symbols) { try { var b = await Fetch(sym, iv); if (b.Count >= 350) dv[sym] = b; } catch { } } }
    tf[iv] = PerCoinMatrix(iv, dv, printGrid: iv == "1d");
    Console.WriteLine($"  [時框 {iv}] {dv.Count} 幣 × {tf[iv].Count} 策略 跑完");
}

// (1) 每策略 跨時框中位 OOS(穩定 = 多時框都正)
Console.WriteLine("\n=== 每策略 跨時框中位 OOS%(穩定 = 多時框都正)===");
Console.WriteLine("  " + $"{"strategy",-16}" + string.Join("", intervals.Select(iv => $"{iv,6}")) + $"   平均  正/{intervals.Length}");
var stratScore = new Dictionary<string, (decimal avgMed, int posTf)>();
foreach (var (name, _) in strats)
{
    var meds = new List<decimal>(); var cells = new List<string>(); int pos = 0;
    foreach (var iv in intervals)
    {
        if (tf[iv].TryGetValue(name, out var row) && row.Count > 0)
        { var m = Median(row.Values.ToList()); meds.Add(m); if (m > 0) pos++; cells.Add($"{m,6:F0}"); }
        else cells.Add($"{"-",6}");
    }
    var avg = meds.Count > 0 ? Math.Round(meds.Average(), 1) : 0;
    stratScore[name] = (avg, pos);
    Console.WriteLine($"  {name,-16}{string.Join("", cells)}   {avg,5:F1}  {pos}/{intervals.Length}");
}

// (2) 每幣 跨時框最佳策略(要求 ≥3/5 時框為正,濾單時框噪音;取跨時框平均最高)
Console.WriteLine("\n=== 每幣 跨時框最佳 long-only 策略(要求 ≥3/5 時框正、取跨時框平均最高)===");
var finalPick = new Dictionary<string, (string name, decimal avg, int pos)>();
foreach (var coin in data.Keys)
{
    var cand = new List<(string name, decimal avg, int pos)>();
    foreach (var (name, _) in strats)
    {
        var vals = new List<decimal>(); int pos = 0;
        foreach (var iv in intervals)
            if (tf[iv].TryGetValue(name, out var row) && row.TryGetValue(coin, out var v)) { vals.Add(v); if (v > 0) pos++; }
        if (vals.Count >= 3) cand.Add((name, Math.Round(vals.Average(), 1), pos));
    }
    var ok = cand.Where(c => c.pos >= 3).OrderByDescending(c => c.avg).ToList();
    if (ok.Count == 0) ok = cand.OrderByDescending(c => c.avg).ToList();
    if (ok.Count == 0) continue;
    finalPick[coin] = ok[0];
    var alt = string.Join(", ", ok.Skip(1).Take(2).Select(c => $"{c.name}({c.avg:F0},{c.pos}/5)"));
    Console.WriteLine($"    {Sh(coin),-6}→ {ok[0].name,-16} 跨時框avg {ok[0].avg,4:F0}% (正{ok[0].pos}/5)   次:{alt}");
}

// (3) 穩健策略總排名(正時框數優先,再平均中位)
Console.WriteLine("\n=== 穩健策略總排名(正時框數 → 平均中位 OOS%)===");
foreach (var kv in stratScore.OrderByDescending(x => x.Value.posTf).ThenByDescending(x => x.Value.avgMed))
    Console.WriteLine($"    {kv.Key,-16} 正時框 {kv.Value.posTf}/{intervals.Length}  平均中位 {kv.Value.avgMed,5:F1}%");

// (4) 成本敏感度(1d):edge 扣手續費+滑點後還剩多少 + 交易頻率(頻率高=被成本磨更兇)
// 上面矩陣已含預設 0.1%/邊手續費;這裡明列三種成本看 edge 衰減。
// 註:資金費(funding)未計 — Binance K 線不帶 funding_rate;多單在多頭通常「付」funding,
//     故真實淨值還會比下表 realistic 再差一點(尤其長抱)。
decimal MedOos1d(IStrategy s, decimal comm, decimal slip, bool funding = false)
{
    var oos = new List<decimal>();
    foreach (var kv in data)
        try { var w = BacktestEngine.RunWalkForward(s, kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, 250, 90, 60, commission: comm, slippagePct: slip, applyFunding: funding); if (w.TotalFolds > 0) oos.Add(w.AvgTestReturnPct); }
        catch { }
    return oos.Count > 0 ? Median(oos) : 0;
}
decimal AvgTrades1k(IStrategy s)
{
    var tr = new List<decimal>();
    foreach (var kv in data)
        try { var bt = BacktestEngine.Run(s, kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }); tr.Add(bt.TotalTrades / (decimal)kv.Value.Count * 1000m); }
        catch { }
    return tr.Count > 0 ? Math.Round(tr.Average(), 1) : 0;
}
Console.WriteLine($"\n=== 成本敏感度(1d、跨幣中位 OOS%;funding 假設 long 付 {fundingPer8h:P3}/8h)===");
Console.WriteLine($"  {"strategy",-16}{"gross",8}{"realistic",11}{"real+fund",11}{"pessim",9}{"trades/千根",13}");
Console.WriteLine($"  {"",-16}{"(0)",8}{"(.05費+.03滑)",11}{"(+funding)",11}{"(.15/邊)",9}");
foreach (var (name, s) in strats)
{
    var g  = MedOos1d(s, 0m, 0m);
    var r  = MedOos1d(s, 0.0005m, 0.0003m);
    var rf = MedOos1d(s, 0.0005m, 0.0003m, funding: true);
    var p  = MedOos1d(s, 0.0008m, 0.0007m);
    Console.WriteLine($"  {name,-16}{g,8:F1}{r,11:F1}{rf,11:F1}{p,9:F1}{AvgTrades1k(s),13:F1}");
}
Console.WriteLine("  → real+fund = realistic 再加 funding;realistic 正但 real+fund 轉負 = 被資金費(長抱)拖垮。");

// (5) 統計顯著性(1d、realistic 成本):pool 跨幣×fold 的 OOS 報酬,bootstrap 95% CI。
// CI 下界 > 0 才算「edge 跟 0 有顯著差異」(不是運氣);否則就是 noise。
// 2026-05-26 重構:改用 LongShortBacktestEngine(讀 Signal.StopPrice/TargetPrice、支援 H16 TP / Method C),
// Benson long-only 引擎吃不到這些訊號 → 對 harm_prz / fib_retrace_ls 等 SL/TP-emitting 策略
// pool t-stat 之前被嚴重低估(fib t=0.25 失敗的根因)。
List<decimal> PoolOosFolds(IStrategy s)
{
    var r = new List<decimal>();
    foreach (var kv in data)
        try { var w = LongShortBacktestEngine.RunWalkForward(s, kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, 250, 90, 60, commission: gComm, slippagePct: gSlip, confidenceSizing: confSizing, applyFunding: realFunding);
              foreach (var f in w.Folds.Where(f => f.Test != null)) r.Add(f.Test!.TotalReturnPct); }
        catch { }
    return r;
}
var rng = new Random(42);
(decimal mean, decimal lo, decimal hi, double t) BootCI(List<decimal> xs)
{
    if (xs.Count < 5) return (0, 0, 0, 0);
    double mean = (double)xs.Average();
    double sd = Math.Sqrt(xs.Select(x => ((double)x - mean) * ((double)x - mean)).Sum() / (xs.Count - 1));
    double se = sd / Math.Sqrt(xs.Count);
    double tStat = se > 0 ? mean / se : 0;
    var means = new double[2000];
    for (int b = 0; b < 2000; b++) { double sum = 0; for (int i = 0; i < xs.Count; i++) sum += (double)xs[rng.Next(xs.Count)]; means[b] = sum / xs.Count; }
    Array.Sort(means);
    return ((decimal)mean, (decimal)means[(int)(0.025 * 2000)], (decimal)means[(int)(0.975 * 2000)], tStat);
}
// 標準常態 CDF(Abramowitz-Stegun erf 近似、誤差 < 1.5e-7)
double NormCdf(double x)
{
    double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(x));
    double d = 0.3989422804014327 * Math.Exp(-x * x / 2.0);
    double p = d * t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
    return x >= 0 ? 1.0 - p : p;
}
// 標準常態反函數(Acklam 有理近似、誤差 ~1e-9)
double NormInv(double p)
{
    if (p <= 0) return -8; if (p >= 1) return 8;
    double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
    double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
    double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
    double[] dd = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
    double plow = 0.02425, phigh = 1 - 0.02425, q, r2;
    if (p < plow) { q = Math.Sqrt(-2 * Math.Log(p)); return (((((c[0]*q+c[1])*q+c[2])*q+c[3])*q+c[4])*q+c[5]) / ((((dd[0]*q+dd[1])*q+dd[2])*q+dd[3])*q+1); }
    if (p <= phigh) { q = p - 0.5; r2 = q*q; return (((((a[0]*r2+a[1])*r2+a[2])*r2+a[3])*r2+a[4])*r2+a[5])*q / (((((b[0]*r2+b[1])*r2+b[2])*r2+b[3])*r2+b[4])*r2+1); }
    q = Math.Sqrt(-2 * Math.Log(1 - p)); return -(((((c[0]*q+c[1])*q+c[2])*q+c[3])*q+c[4])*q+c[5]) / ((((dd[0]*q+dd[1])*q+dd[2])*q+dd[3])*q+1);
}
// 樣本 skewness(γ3)+ kurtosis(γ4、非超額、常態=3)
(double skew, double kurt) SkewKurt(List<decimal> xs)
{
    int n = xs.Count;
    if (n < 4) return (0, 3);
    double mean = (double)xs.Average();
    double m2 = xs.Sum(x => Math.Pow((double)x - mean, 2)) / n;
    double m3 = xs.Sum(x => Math.Pow((double)x - mean, 3)) / n;
    double m4 = xs.Sum(x => Math.Pow((double)x - mean, 4)) / n;
    double sd = Math.Sqrt(m2);
    return sd <= 0 ? (0, 3) : (m3 / Math.Pow(sd, 3), m4 / Math.Pow(sd, 4));
}

// 2026-06-07 相依穩健統計:把「跨幣×fold」攤平的 iid bootstrap(高估顯著性、因幣高相關+重疊窗)
// 換成「每期跨幣均值序列 + moving-block bootstrap」:
//   (a) 每個 fold 位置跨幣取均值 → 收掉「幣高相關」(共同因子 per period 攤平成一個分散後報酬)
//   (b) moving-block bootstrap(block 長 ~T^1/3)→ 收掉「重疊窗」的序列自相關
//   結果:有效樣本 n 從 ~512 降成 ~實際期數(誠實)、t 不再被相依灌水。
List<double> PerPeriodSeries(IStrategy s)
{
    var perCoin = new List<List<double>>();
    foreach (var kv in data)
        try
        {
            var w = LongShortBacktestEngine.RunWalkForward(s, kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, 250, 90, 60, commission: gComm, slippagePct: gSlip, confidenceSizing: confSizing, applyFunding: realFunding);
            var fs = w.Folds.Where(f => f.Test != null).Select(f => (double)f.Test!.TotalReturnPct).ToList();
            if (fs.Count > 0) perCoin.Add(fs);
        }
        catch { }
    if (perCoin.Count == 0) return new();
    int maxF = perCoin.Max(c => c.Count);
    var series = new List<double>();
    for (int p = 0; p < maxF; p++)
    {
        var vals = perCoin.Where(c => c.Count > p).Select(c => c[p]).ToList();
        if (vals.Count > 0) series.Add(vals.Average());   // 該 fold 位置跨幣均值 = 分散後每期報酬
    }
    return series;
}
// moving-block bootstrap on a (serially dependent) series → (point mean, 2.5%, 97.5%, t=mean/blockSE)
(decimal mean, decimal lo, decimal hi, double t) BlockBootCI(List<double> ys, Random rng)
{
    if (ys.Count < 5) return (0, 0, 0, 0);
    int T = ys.Count;
    double pointMean = ys.Average();
    int L = Math.Max(1, (int)Math.Round(Math.Pow(T, 1.0 / 3.0)));   // block 長 ~ T^(1/3)
    int nBlocks = (int)Math.Ceiling((double)T / L);
    var means = new double[2000];
    for (int b = 0; b < 2000; b++)
    {
        double sum = 0; int cnt = 0;
        for (int blk = 0; blk < nBlocks && cnt < T; blk++)
        {
            int start = rng.Next(T);                       // circular moving block
            for (int j = 0; j < L && cnt < T; j++) { sum += ys[(start + j) % T]; cnt++; }
        }
        means[b] = sum / cnt;
    }
    Array.Sort(means);
    double bMean = means.Average();
    double bSd = Math.Sqrt(means.Select(x => (x - bMean) * (x - bMean)).Sum() / (means.Length - 1));
    double tStat = bSd > 0 ? pointMean / bSd : 0;          // t = 點估計 / block-bootstrap SE
    return ((decimal)pointMean, (decimal)means[50], (decimal)means[1949], tStat);
}
Console.WriteLine("\n=== 統計顯著性(1d、realistic 成本;每期跨幣均值 + moving-block bootstrap、相依穩健)===");
Console.WriteLine($"  {"strategy",-16}{"T期",5}{"mean%",8}   {"95% CI",16}{"t",7}  判定   (n=有效期數、非 fold 數)");
int sigTested = 0, sigPassed = 0;
var sigNames = new HashSet<string>();
var sigT = new Dictionary<string, double>();
var trials = new List<(string name, double sr, int n, double skew, double kurt, double tstat)>();
var trialSeries = new List<List<double>>();   // #2b:每策略每期序列、用來算有效獨立試驗數 N_eff(跟 trials 對齊)
// 平行算每策略(sortKey + series 統計);bootstrap RNG 用 per-strategy seed(42+i)= 執行緒安全 + 可重現。
// 之後按 sortKey(= PoolOosFolds 均值、同 baseline)排序印 + 依序填 sigT/sigNames/trials(不被平行交錯)。
var sigRes = new ConcurrentDictionary<string, (decimal sortKey, string line, double t, bool sig, bool hasTrial, double sr, int n, double sk, double ku, List<double> series)>();
Parallel.For(0, strats.Length, ParOpts, i =>
{
    var (name, s) = strats[i];
    var series = PerPeriodSeries(s);                          // 每期跨幣均值(收幣相關)
    decimal sortKey = series.Count > 0 ? (decimal)series.Average() : -999m;   // 排序鍵用 series 均值 → 省掉 PoolOosFolds 那趟 walk-forward(顯著性砍半)
    var (m, lo, hi, t) = BlockBootCI(series, new Random(42 + i));   // per-strategy seed(收重疊窗)
    bool sig = lo > 0m && series.Count >= 5;
    string line = $"  {name,-16}{series.Count,5}{m,8:F1}   [{lo,6:F1},{hi,6:F1}]{t,7:F2}  {(sig ? "✅ 顯著" : "—")}";
    if (series.Count >= 5)
    {
        double mu = series.Average();
        double sd = Math.Sqrt(series.Select(x => (x - mu) * (x - mu)).Sum() / (series.Count - 1));
        var (sk, ku) = SkewKurt(series.Select(x => (decimal)x).ToList());
        sigRes[name] = (sortKey, line, t, sig, true, sd > 0 ? mu / sd : 0, series.Count, sk, ku, series);
    }
    else sigRes[name] = (sortKey, line, t, sig, false, 0, 0, 0, 0, new List<double>());
});
foreach (var (name, _) in strats.OrderByDescending(x => sigRes.TryGetValue(x.name, out var r) ? r.sortKey : -999m))
{
    if (!sigRes.TryGetValue(name, out var r)) continue;
    sigT[name] = r.t; if (r.sig) sigNames.Add(name);
    sigTested++; if (r.sig) sigPassed++;
    if (r.hasTrial) { trials.Add((name, r.sr, r.n, r.sk, r.ku, r.t)); trialSeries.Add(r.series); }   // sr/n 用每期序列 = DSR 也吃有效期數;series 留著算 N_eff
    Console.WriteLine(r.line);
}
Console.WriteLine($"  測 {sigTested} 支、{sigPassed} 支 95%CI 下界>0(已用每期跨幣均值收掉幣相關 + block-bootstrap 收掉重疊窗 → n=有效期數、t 不再灌水)。");
Console.WriteLine($"     多重檢定下純運氣約 {sigTested * 0.05:F0} 支會假陽性 → 仍以下方 DSR / BH-FDR 為準。");

// ── 多重檢定 + Deflated Sharpe(López de Prado、修「試 N 個變體 → 選擇偏誤」)──
// 跑 N 個策略變體、純運氣也會冒出幾個「95%CI 顯著」。兩個業界標準扣假陽性:
//   1) Deflated Sharpe Ratio:基準改成「試 N 次的期望最大 Sharpe」SR*,DSR≥0.95 才算真超過
//   2) Benjamini-Hochberg FDR / Bonferroni:對 one-sided p-value 多重檢定校正(把上面那行口頭警告變成正式計算)
if (trials.Count >= 3)
{
    int N = trials.Count;
    const double euler = 0.5772156649015329;   // Euler-Mascheroni
    double srMean = trials.Average(x => x.sr);
    double srVar = trials.Sum(x => (x.sr - srMean) * (x.sr - srMean)) / (N - 1);
    double srStd = Math.Sqrt(Math.Max(srVar, 0));
    // #2b:有效獨立試驗數 N_eff —— 變體間高度相關(harm_prz_* / retail_ls_* tight/loose 等近重複)不該各算一次獨立試驗。
    // N_eff = N² / Σᵢⱼ ρᵢⱼ²(相關矩陣 participation ratio / 有效獨立維度;全獨立→N、全相同→1)。餵 DSR 的 SR* 與 MinBTL;
    // BH-FDR/Bonferroni 仍用 raw N(那算的是「實際做了幾次檢定」)。源:Bailey & López de Prado DSR 論文 Appendix。
    double PairCorr(List<double> u, List<double> v)
    {
        int m = Math.Min(u.Count, v.Count); if (m < 5) return 0;
        double mu = 0, mv = 0; for (int k = 0; k < m; k++) { mu += u[k]; mv += v[k]; } mu /= m; mv /= m;
        double suv = 0, suu = 0, svv = 0;
        for (int k = 0; k < m; k++) { double du = u[k] - mu, dv = v[k] - mv; suv += du * dv; suu += du * du; svv += dv * dv; }
        return (suu > 0 && svv > 0) ? suv / Math.Sqrt(suu * svv) : 0;
    }
    double sumSqCorr = N; double absRhoSum = 0; int pairCnt = 0;   // 對角線 ρ_ii²=1 共 N 個
    for (int ia = 0; ia < trialSeries.Count; ia++)
        for (int ib = ia + 1; ib < trialSeries.Count; ib++)
        {
            double rho = PairCorr(trialSeries[ia], trialSeries[ib]);
            sumSqCorr += 2 * rho * rho; absRhoSum += Math.Abs(rho); pairCnt++;
        }
    double nEff = Math.Min(N, Math.Max(1.0, (double)N * N / sumSqCorr));
    double avgAbsRho = pairCnt > 0 ? absRhoSum / pairCnt : 0;
    double sr0 = srStd * ((1 - euler) * NormInv(1.0 - 1.0 / nEff) + euler * NormInv(1.0 - 1.0 / (nEff * Math.E)));
    var withP = trials.Select(x => (x.name, x.sr, x.n, x.skew, x.kurt, p: 1.0 - NormCdf(x.tstat))).ToList();
    // BH-FDR 閾值(α=0.05):p 升冪排序、找最大 k 使 p_(k) ≤ k/N·α
    var pAsc = withP.OrderBy(x => x.p).ToList();
    double bhThresh = 0;
    for (int k = 0; k < pAsc.Count; k++) if (pAsc[k].p <= (k + 1.0) / N * 0.05) bhThresh = pAsc[k].p;
    double bonf = 0.05 / N;
    Console.WriteLine($"\n=== 多重檢定 + Deflated Sharpe(N={N} 變體 → N_eff={nEff:F0} 有效獨立[平均|ρ|={avgAbsRho:F2}]、SR*={sr0:F3}=試 N_eff 次期望最大 Sharpe 基準)===");
    Console.WriteLine($"  {"strategy",-22}{"SR",7}{"DSR",8}{"p(1側)",10}{"Bonf",6}{"BH",5}");
    int dsrPass = 0, bonfPass = 0, bhPass = 0;
    var bySr = withP.OrderByDescending(x => x.sr).ToList();
    double cutoff = bySr.Count > 15 ? bySr[14].sr : double.MinValue;
    foreach (var x in bySr)
    {
        double denom = Math.Sqrt(Math.Max(1e-9, 1 - x.skew * x.sr + (x.kurt - 1) / 4.0 * x.sr * x.sr));
        double dsr = NormCdf((x.sr - sr0) * Math.Sqrt(Math.Max(1, x.n - 1)) / denom);
        bool dP = dsr >= 0.95, bP = x.p < bonf, hP = x.p <= bhThresh;
        if (dP) dsrPass++; if (bP) bonfPass++; if (hP) bhPass++;
        if (x.sr >= cutoff || dP || bP || hP)
            Console.WriteLine($"  {x.name,-22}{x.sr,7:F3}{dsr,8:F3}{x.p,10:F4}{(bP ? "✅" : "—"),6}{(hP ? "✅" : "—"),5}");
    }
    Console.WriteLine($"  → 原始 95%CI 顯著 {sigPassed} 支;多重檢定後:DSR≥0.95 {dsrPass} 支、Bonferroni {bonfPass} 支、BH-FDR(5%) {bhPass} 支。");
    Console.WriteLine($"     差額 = 多重檢定揪出的假陽性;只信過 DSR / BH-FDR 的才是真 edge。");

    // ── Minimum Backtest Length(Bailey, Borwein, López de Prado & Zhu 2014)──
    // 試 N 個配置時,需要的最小回測「年數」;樣本短於它 → 純噪音幾乎保證冒出年化 Sharpe 1 的假象。
    //   MinBTL ≈ 2·ln(N_eff) / SR*_annual²(SR*_annual=想排除的噪音年化 Sharpe、取 1.0;N 用有效獨立數 #2b)
    //   噪音期望最高年化 Sharpe ≈ √(2·ln(N_eff) / 樣本年數)（最佳策略要明顯高於它才可信)
    // 純加法、不動上面任何 DSR/p 值計算;只多一道「樣本夠不夠扛這麼多『獨立』變體」的可見警示。
    double sampleYears = data.Count > 0 ? data.Values.Max(v => v.Count) / 252.0 : 0;
    double minBtlYears = 2.0 * Math.Log(nEff) / (1.0 * 1.0);
    double expMaxAnnualSh = sampleYears > 0 ? Math.Sqrt(2.0 * Math.Log(nEff) / sampleYears) : 0;
    Console.WriteLine($"  → MinBTL(Bailey-LdP):試 {N} 個變體(有效獨立 ≈ {nEff:F0})需 ≥ {minBtlYears:F1} 年樣本(排除噪音年化 Sharpe 1);" +
        $"本資料 ≈ {sampleYears:F1} 年 {(sampleYears >= minBtlYears ? "✅ 足夠" : "⚠️ 不足 → 獨立變體太多/樣本太短、多重檢定假象風險高")}。");
    Console.WriteLine($"     純噪音在 {sampleYears:F1} 年 × {nEff:F0} 個獨立配置的期望最高『年化』Sharpe ≈ {expMaxAnnualSh:F2}" +
        $" —— 最佳策略的年化 Sharpe 要明顯高於這個才算真 edge(諧波/TA 尤其要嚴,文獻視其易過擬合)。");
}

// 2026-05-27 Q1.1:Kelly fraction sizing 推薦(每支顯著策略、用 walk-forward win-rate / avg win / avg loss)
// Q1.2(同 commit):疊加 vol-target scalar(用 BTC 當下 realized vol vs target 60%)
if (showKelly && sigNames.Count > 0)
{
    // Vol-target diagnostic — 用 BTC 1d closes 算當下 vol regime
    decimal btcRealizedVol = 0m;
    decimal volScalar = 1m;
    string volExplain = "(無 BTC 資料)";
    if (data.TryGetValue("BTCUSDT", out var btcBars) && btcBars.Count >= 35)
    {
        var btcCloses = btcBars.Select(b => b.Close).ToList();
        var diag = VolTargetSizer.Diagnose(btcCloses, kellyPct: 1m, targetVol: 0.60m, lookback: 30);
        btcRealizedVol = diag.RealizedVol;
        volScalar = diag.Scalar;
        volExplain = diag.Explanation;
    }

    Console.WriteLine($"\n=== Kelly fraction 推薦 sizing(quarter-Kelly、max 20% clamp、× vol-target scalar)===");
    Console.WriteLine($"  vol-target 假設 60% 年化、BTC 當下 realized vol = {btcRealizedVol:P0} → scalar = {volScalar:F2}");
    Console.WriteLine($"  {volExplain}");
    Console.WriteLine();
    Console.WriteLine($"  {"strategy",-22} {"win%",6} {"avgWin%",8} {"avgLoss%",9} {"b ratio",8} {"Kelly safe%",12} {"× vol-scalar",13} {"final %",9}");
    foreach (var (name, s) in strats.Where(x => sigNames.Contains(x.name)).OrderByDescending(x => sigT.GetValueOrDefault(x.name, 0)))
    {
        var pool = PoolOosFolds(s);
        if (pool.Count == 0) continue;
        var wins = pool.Where(r => r > 0).ToList();
        var losses = pool.Where(r => r < 0).Select(r => -r).ToList();
        decimal winRate = (decimal)wins.Count / pool.Count;
        decimal avgWin = wins.Count > 0 ? wins.Average() : 0m;
        decimal avgLoss = losses.Count > 0 ? losses.Average() : 0m;
        decimal b = avgLoss > 0m ? avgWin / avgLoss : 0m;
        decimal raw = KellyPositionSizer.Compute(winRate, avgWin, avgLoss);
        decimal safe = KellyPositionSizer.RecommendedPct(winRate, avgWin, avgLoss);
        decimal final = safe * volScalar;
        Console.WriteLine($"  {name,-22} {winRate * 100m,5:F0}% {avgWin,8:F1} {avgLoss,9:F1} {b,8:F2} {safe * 100m,11:F1}% {volScalar,12:F2}× {final * 100m,8:F1}%");
    }
    decimal totalFinal = strats
        .Where(x => sigNames.Contains(x.name))
        .Sum(x => {
            var pool = PoolOosFolds(x.s);
            if (pool.Count == 0) return 0m;
            var wins = pool.Where(r => r > 0).ToList();
            var losses = pool.Where(r => r < 0).Select(r => -r).ToList();
            decimal winRate = (decimal)wins.Count / pool.Count;
            decimal avgWin = wins.Count > 0 ? wins.Average() : 0m;
            decimal avgLoss = losses.Count > 0 ? losses.Average() : 0m;
            return KellyPositionSizer.RecommendedPct(winRate, avgWin, avgLoss) * volScalar;
        });
    Console.WriteLine($"\n  總配重(final):{totalFinal * 100m:F1}% of equity");
    if (totalFinal > 1m)
        Console.WriteLine($"  ⚠ 加總 > 100%、實作時須等比例 scale down(總曝險不能超過 equity)");
    else
        Console.WriteLine($"  ✅ 加總 < 100%、剩 {(1m - totalFinal) * 100m:F1}% 現金緩衝");

    Console.WriteLine($"\n  心法:Kelly 給「該配多少」、vol-target 給「現在該縮放多少」、兩個 ×。");
    Console.WriteLine($"        高 vol regime(scalar < 1)= 防御;低 vol regime(scalar > 1)= 機會、適度放大。");

    // Q1.3:Mean-variance portfolio optimization(取代「去相關精選 + 反波動率」啟發式)
    // 用 strat-validate 已算的 pool fold returns 算 cov matrix、解 min-var 跟 max-Sharpe
    var sigList = strats.Where(x => sigNames.Contains(x.name))
                        .OrderByDescending(x => sigT.GetValueOrDefault(x.name, 0))
                        .Take(8)   // 取 top 8(避免 N 太大 inversion 不穩)
                        .ToList();
    if (sigList.Count >= 2)
    {
        var returnsPerStrat = new List<List<decimal>>();
        var expectedReturns = new List<decimal>();
        var stratNames = new List<string>();
        foreach (var (name, s) in sigList)
        {
            var pool = PoolOosFolds(s);
            if (pool.Count < 10) continue;
            returnsPerStrat.Add(pool);
            expectedReturns.Add(pool.Average());
            stratNames.Add(name);
        }
        if (returnsPerStrat.Count >= 2)
        {
            var cov = MinVarianceOptimizer.Covariance(returnsPerStrat);
            var mvWeights = MinVarianceOptimizer.MinVarianceWeights(cov, maxWeight: 0.35m);
            var msWeights = MinVarianceOptimizer.MaxSharpeWeights(cov, expectedReturns.ToArray(), maxWeight: 0.35m);
            var eqWeights = MinVarianceOptimizer.EqualWeights(returnsPerStrat.Count);
            var rpWeights = RiskParityOptimizer.ErcWeights(cov, maxWeight: 0.35m);

            Console.WriteLine($"\n=== Q1.3-1.4 Portfolio config(top {returnsPerStrat.Count} sig 策略、cov from pool folds)===");
            Console.WriteLine($"  {"strategy",-22} {"E[ret]%",9} {"equal-w",9} {"inv-vol",9} {"min-var",9} {"risk-parity",12} {"max-Sharpe",11}");
            // inverse-vol baseline 對照(naive RP)
            var invVolRaw = new double[returnsPerStrat.Count];
            for (int i = 0; i < returnsPerStrat.Count; i++)
                invVolRaw[i] = 1.0 / Math.Sqrt(Math.Max(cov[i, i], 1e-12));
            double ivSum = invVolRaw.Sum();
            var ivWeights = invVolRaw.Select(x => (decimal)(x / ivSum)).ToArray();
            for (int i = 0; i < stratNames.Count; i++)
            {
                Console.WriteLine($"  {stratNames[i],-22} {expectedReturns[i],8:F1}% {eqWeights[i] * 100m,8:F1}% {ivWeights[i] * 100m,8:F1}% {mvWeights[i] * 100m,8:F1}% {rpWeights[i] * 100m,11:F1}% {msWeights[i] * 100m,10:F1}%");
            }

            // Portfolio metrics 對比
            decimal[] PortfolioMetrics(decimal[] w)
            {
                var r = MinVarianceOptimizer.PortfolioReturn(w, expectedReturns.ToArray());
                var v = MinVarianceOptimizer.PortfolioVariance(w, cov);
                var s = (decimal)Math.Sqrt((double)v);
                return new[] { r, s, s > 0 ? r / s : 0m };
            }
            var eqM = PortfolioMetrics(eqWeights);
            var ivM = PortfolioMetrics(ivWeights);
            var mvM = PortfolioMetrics(mvWeights);
            var rpM = PortfolioMetrics(rpWeights);
            var msM = PortfolioMetrics(msWeights);
            Console.WriteLine();
            Console.WriteLine($"  {"portfolio",-22} {"E[ret]%",9} {"vol%",9} {"Sharpe-like",11}");
            Console.WriteLine($"  {"equal-weight",-22} {eqM[0],8:F1}% {eqM[1],8:F1}% {eqM[2],10:F2}");
            Console.WriteLine($"  {"inverse-vol (naive)",-22} {ivM[0],8:F1}% {ivM[1],8:F1}% {ivM[2],10:F2}  baseline");
            Console.WriteLine($"  {"min-variance",-22} {mvM[0],8:F1}% {mvM[1],8:F1}% {mvM[2],10:F2}  ⭐ robust");
            Console.WriteLine($"  {"risk-parity (ERC)",-22} {rpM[0],8:F1}% {rpM[1],8:F1}% {rpM[2],10:F2}  ⭐⭐ Bridgewater-style");
            Console.WriteLine($"  {"max-Sharpe",-22} {msM[0],8:F1}% {msM[1],8:F1}% {msM[2],10:F2}  ⚠ μ 敏感");

            // ERC diagnostic:risk contribution check(目標 1/N 均等)
            var rcPct = RiskParityOptimizer.RiskContributions(rpWeights, cov);
            decimal targetRc = 1m / rcPct.Length;
            Console.WriteLine($"\n  Risk parity 風險貢獻檢查(目標每支 {targetRc:P0}、收斂越接近越好):");
            decimal maxDev = 0m;
            for (int i = 0; i < stratNames.Count; i++)
            {
                var dev = Math.Abs(rcPct[i] - targetRc);
                if (dev > maxDev) maxDev = dev;
                var marker = dev < 0.02m ? "✅" : (dev < 0.05m ? "⚠" : "❌");
                Console.WriteLine($"    {stratNames[i],-22} weight={rpWeights[i] * 100m,5:F1}%  RC={rcPct[i] * 100m,5:F1}%  dev={dev * 100m,4:F1}pp {marker}");
            }
            Console.WriteLine($"  最大偏離 target={maxDev * 100m:F1}pp(< 2pp = 收斂良好)");

            Console.WriteLine($"\n  心法:");
            Console.WriteLine($"    - min-variance:總風險最小、但常過度集中、丟掉高 ret/vol 策略");
            Console.WriteLine($"    - risk parity:每支對總風險貢獻平均、forced diversification、Bridgewater 主推");
            Console.WriteLine($"    - max-Sharpe:理論最佳但 μ-敏感、實務常極端");
            Console.WriteLine($"    - **業界經驗:risk parity + max-cap 35% 是 best default**(Carver / Roncalli)");
        }
    }
}

// (6) 顯著策略的最佳組合(只用統計顯著那群、去相關 |ρ|<0.4、反波動率配重)
// 全程用 long-only 權益(loEq)→ 跟顯著性(long-only OOS)+ 實際 perp_long_only 部署一致。
if (loEq.Count >= 2 && loEq.Values.Any(v => v.ContainsKey("BTCUSDT")))
{
    var members = sigNames.Where(n => loEq.ContainsKey(n) && loEq[n].ContainsKey("BTCUSDT")).ToList();
    if (members.Count >= 2)
    {
        // 相關用跨幣平均(不只 BTC)→ 對 long-only 更有代表性
        decimal Corr2(string a, string b)
        {
            var coins = loEq[a].Keys.Intersect(loEq[b].Keys).ToList();
            var cs = new List<decimal>();
            foreach (var co in coins)
            {
                int n = Math.Min(loEq[a][co].Count, loEq[b][co].Count);
                if (n < 3) continue;
                cs.Add(CorrelationGuard.PearsonOfReturns(loEq[a][co].Take(n).ToList(), loEq[b][co].Take(n).ToList()));
            }
            return cs.Count > 0 ? cs.Average() : 0m;
        }
        decimal AvgVol2(string n)
        {
            var vs = new List<decimal>();
            foreach (var c in loEq[n].Values)
            {
                if (c.Count < 3) continue;
                var rr = new List<decimal>();
                for (int t = 1; t < c.Count; t++) if (c[t - 1] > 0) rr.Add((c[t] - c[t - 1]) / c[t - 1]);
                if (rr.Count < 2) continue;
                var a = rr.Average();
                vs.Add((decimal)Math.Sqrt((double)rr.Select(x => (x - a) * (x - a)).Average()));
            }
            return vs.Count > 0 ? vs.Average() : 0m;
        }
        var ranked = members.OrderByDescending(n => sigT.GetValueOrDefault(n)).ToList();   // t 高的先選
        var picked = new List<string> { ranked[0] };
        foreach (var c in ranked.Skip(1))
            if (picked.All(p => Math.Abs(Corr2(c, p)) < 0.4m)) picked.Add(c);
        var invVol = picked.ToDictionary(n => n, n => { var v = AvgVol2(n); return v > 0 ? 1m / v : 0m; });
        var wsum = invVol.Values.Sum();
        Console.WriteLine("\n=== 顯著策略最佳組合(long-only、去相關精選 + 反波動率配重)===");
        Console.WriteLine($"  顯著候選({ranked.Count}): {string.Join(", ", ranked)}");
        Console.WriteLine($"  去相關精選({picked.Count}): {string.Join(", ", picked)}");
        Console.WriteLine("  建議配重(反波動率): " + string.Join("  ", picked.Select(n => $"{n} {(wsum > 0 ? invVol[n] / wsum : 0m):P0}")));
    }
}

// (7) 參數調校實驗:grid search + walk-forward,驗證「調參到底有沒有讓 OOS 變好」。
// degradation = OOS Sharpe / IS Sharpe;接近 1=參數穩健,遠<1 或負 = IS 漂亮 OOS 垃圾 = 過擬合。
// 只有 sma_cross / rsi_oversold 有 ParameterOptimizer grid;用它們示範原理(其餘策略本就單一固定參數、不調)。
Console.WriteLine("\n=== 參數調校實驗(anchored walk-forward;IS 找最佳參數 → OOS 驗證)===");
Console.WriteLine($"  {"strategy",-14}{"IS Sharpe",11}{"OOS Sharpe",12}{"degradation",13}{"OOS ret%",10}");
void WfOpt(string label, Func<List<BarData>, StrategyConfig, WalkForwardOptimizer.WalkForwardResult> run)
{
    var res = new ConcurrentBag<(decimal isS, decimal oosS, decimal oosR)>();
    Parallel.ForEach(data, ParOpts, kv =>
    {
        try { var r = run(kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = "1d" });
              if (r.WindowCount > 0) res.Add((r.AvgInSampleSharpe, r.AvgOutOfSampleSharpe, r.AggregateOosReturnPct)); }
        catch { }
    });
    if (res.IsEmpty) { Console.WriteLine($"  {label,-14}(無資料)"); return; }
    var avgIs = res.Average(x => x.isS); var avgOos = res.Average(x => x.oosS);
    var deg = avgIs != 0 ? avgOos / avgIs : 0;
    Console.WriteLine($"  {label,-14}{avgIs,11:F2}{avgOos,12:F2}{deg,13:F2}{res.Average(x => x.oosR),10:F1}");
}
WfOpt("sma_cross",    (b, c) => WalkForwardOptimizer.RunSma(b, c, 250, 90));
WfOpt("rsi_oversold", (b, c) => WalkForwardOptimizer.RunRsi(b, c, 250, 90));
Console.WriteLine("  → degradation 遠<1 = IS 找的最佳參數 OOS 站不住 = 調參=過擬合 → 印證「單一固定參數、不調參」是對的。");

// (8) 策略績效總表(1d、realistic 成本、full-period;跨幣中位)——年化報酬等白話指標
var liveStrats = new HashSet<string> { "decorr4_ls", "mfi", "dual_mom_ls", "donchian_fade_ls", "ts_momentum", "ma_regime_trend", "rsi_stoch" };
decimal Annualize(decimal totalRetPct, DateTime start, DateTime end)
{
    var days = (end - start).TotalDays;
    if (days < 30) return 0m;
    var basev = 1.0 + (double)totalRetPct / 100.0;
    if (basev <= 0) return -100m;
    return (decimal)((Math.Pow(basev, 365.0 / days) - 1.0) * 100.0);
}
var perfRows = new ConcurrentBag<(string name, decimal ann, decimal sh, decimal dd, decimal wr, decimal pf, decimal tpy, bool live)>();
Parallel.ForEach(strats, ParOpts, ns =>
{
    var (name, s) = ns;
    var anns = new List<decimal>(); var shs = new List<decimal>(); var dds = new List<decimal>();
    var wrs = new List<decimal>(); var pfs = new List<decimal>(); var tpys = new List<decimal>();
    foreach (var kv in data)
        try {
            var bt = BacktestEngine.Run(s, kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, commission: gComm, slippagePct: gSlip);
            if (bt.TotalBars < 100) continue;
            anns.Add(Annualize(bt.TotalReturnPct, bt.StartDate, bt.EndDate));
            shs.Add(bt.SharpeRatio); dds.Add(bt.MaxDrawdownPct); wrs.Add(bt.WinRate); pfs.Add(bt.ProfitFactor);
            var yrs = (bt.EndDate - bt.StartDate).TotalDays / 365.0;
            tpys.Add(yrs > 0 ? (decimal)(bt.TotalTrades / yrs) : 0m);
        } catch { }
    if (anns.Count == 0) return;
    perfRows.Add((name, Median(anns), Math.Round(shs.Average(), 2), Math.Round(dds.Average(), 0),
        Math.Round(wrs.Average(), 0), Math.Round(pfs.Average(), 2), Math.Round(tpys.Average(), 0), liveStrats.Contains(name)));
});   // 印出時 OrderByDescending(ann)、插入序不影響 → ConcurrentBag 即可
Console.WriteLine("\n=== 策略績效總表(1d、realistic 成本、full-period;跨幣中位;★=現行 live)===");
Console.WriteLine($"  {"strategy",-16}{"年化%",8}{"Sharpe",8}{"maxDD%",8}{"勝率%",7}{"PF",7}{"交易/年",9}");
foreach (var r in perfRows.OrderByDescending(x => x.ann))
    Console.WriteLine($"  {(r.live ? "★" : " ")}{r.name,-15}{r.ann,8:F0}{r.sh,8:F2}{r.dd,8:F0}{r.wr,7:F0}{r.pf,7:F2}{r.tpy,9:F0}");
Console.WriteLine("  註:無槓桿 / long-only / 跨幣中位 / 含 realistic 成本。實際 5x ≈ 年化×~5(maxDD 也×5、且有強平風險)。");

// 相關矩陣(long-short, BTC 全期權益報酬)
if (lsEq.Count >= 2 && lsEq.Values.First().ContainsKey("BTCUSDT"))
{
    var names = lsEq.Keys.Where(n => lsEq[n].ContainsKey("BTCUSDT")).ToList();
    int len = names.Min(n => lsEq[n]["BTCUSDT"].Count);
    Console.WriteLine("\n=== Long-short 相關矩陣(BTC 全期權益報酬)===");
    Console.WriteLine("  " + new string(' ', 16) + string.Join("", names.Select(n => $"{n.Substring(0, Math.Min(8, n.Length)),10}")));
    foreach (var a in names)
    {
        var ea = lsEq[a]["BTCUSDT"].Take(len).ToList();
        var cells = names.Select(b => $"{CorrelationGuard.PearsonOfReturns(ea, lsEq[b]["BTCUSDT"].Take(len).ToList()),10:F2}");
        Console.WriteLine($"  {a,-16}" + string.Join("", cells));
    }
}

// ── 組合層回測(long-short, 跨檔平均)──
// 用「報酬加權」建組合:每腿算日報酬,組合日報酬 = Σ w_i·r_i,再累乘成權益算 Sharpe/DD。
// riskWeighted=false → 等權(w=1/N);true → 反波動率加權(w∝1/vol、降權高波動腿)。
(decimal ret, decimal sharpe, decimal dd) Portfolio(List<string> members, bool riskWeighted = false)
{
    var rs = new List<decimal>(); var ss = new List<decimal>(); var ds = new List<decimal>();
    foreach (var kv in data)
    {
        var curves = members.Where(m => lsEq.ContainsKey(m) && lsEq[m].ContainsKey(kv.Key)).Select(m => lsEq[m][kv.Key]).ToList();
        if (curves.Count < members.Count) continue;
        int len = curves.Min(c => c.Count);
        if (len < 3) continue;

        // 每腿日報酬
        var legRets = new List<decimal[]>();
        foreach (var c in curves)
        {
            var r = new decimal[len - 1];
            for (int t = 1; t < len; t++) r[t - 1] = c[t - 1] > 0 ? (c[t] - c[t - 1]) / c[t - 1] : 0m;
            legRets.Add(r);
        }
        // 權重
        var w = new decimal[curves.Count];
        if (riskWeighted)
        {
            decimal wsum = 0m;
            for (int i = 0; i < curves.Count; i++)
            {
                var avg = legRets[i].Average();
                var vol = (decimal)Math.Sqrt((double)legRets[i].Select(x => (x - avg) * (x - avg)).Average());
                w[i] = vol > 0m ? 1m / vol : 0m; wsum += w[i];
            }
            if (wsum <= 0m) continue;
            for (int i = 0; i < w.Length; i++) w[i] /= wsum;
        }
        else for (int i = 0; i < w.Length; i++) w[i] = 1m / curves.Count;

        // 組合權益(初值 1、依加權日報酬累乘)
        var port = new List<decimal>(len) { 1m };
        for (int t = 0; t < len - 1; t++)
        {
            decimal pr = 0m;
            for (int i = 0; i < legRets.Count; i++) pr += w[i] * legRets[i][t];
            port.Add(port[^1] * (1m + pr));
        }
        var st = StatsOf(port);
        rs.Add(st.ret); ss.Add(st.sharpe); ds.Add(st.dd);
    }
    return (rs.Count > 0 ? Math.Round(rs.Average(), 1) : 0, ss.Count > 0 ? Math.Round(ss.Average(), 2) : 0, ds.Count > 0 ? Math.Round(ds.Average(), 1) : 0);
}

// 2026-05-29:防禦後組合 — 對風險加權組合曲線套 DrawdownAwareSizer(CB + 漸進縮倉),
// 看 live 防禦機制把裸 maxDD 壓到哪、報酬代價多少。cbDd=熔斷門檻(poly: DD→cbDd 時 scalar→0)。
(decimal origDd, decimal defDd, decimal origRet, decimal defRet) PortfolioDefended(
    List<string> members, decimal cbDd, string method = "poly")
{
    var oDd = new List<decimal>(); var dDd = new List<decimal>(); var oRet = new List<decimal>(); var dRet = new List<decimal>();
    foreach (var kv in data)
    {
        var curves = members.Where(m => lsEq.ContainsKey(m) && lsEq[m].ContainsKey(kv.Key)).Select(m => lsEq[m][kv.Key]).ToList();
        if (curves.Count < members.Count) continue;
        int len = curves.Min(c => c.Count);
        if (len < 3) continue;
        var legRets = new List<decimal[]>();
        foreach (var c in curves) { var r = new decimal[len - 1]; for (int t = 1; t < len; t++) r[t - 1] = c[t - 1] > 0 ? (c[t] - c[t - 1]) / c[t - 1] : 0m; legRets.Add(r); }
        // 風險加權(反波動率)
        var w = new decimal[curves.Count]; decimal wsum = 0m;
        for (int i = 0; i < curves.Count; i++) { var avg = legRets[i].Average(); var vol = (decimal)Math.Sqrt((double)legRets[i].Select(x => (x - avg) * (x - avg)).Average()); w[i] = vol > 0m ? 1m / vol : 0m; wsum += w[i]; }
        if (wsum <= 0m) continue;
        for (int i = 0; i < w.Length; i++) w[i] /= wsum;
        var port = new List<decimal>(len) { 1m };
        for (int t = 0; t < len - 1; t++) { decimal pr = 0m; for (int i = 0; i < legRets.Count; i++) pr += w[i] * legRets[i][t]; port.Add(port[^1] * (1m + pr)); }
        // 套防禦:DrawdownAwareSizer.Simulate(原曲線, maxAcceptableDd=cbDd, method)
        var sim = StrategyWorker.Engine.DrawdownAwareSizer.Simulate(port, cbDd, method);
        oRet.Add((sim.OrigFinal - 1m) * 100m); dRet.Add((sim.AdjFinal - 1m) * 100m);
        oDd.Add(sim.OrigMaxDd * 100m); dDd.Add(sim.AdjMaxDd * 100m);
    }
    return (oDd.Count > 0 ? Math.Round(oDd.Average(), 1) : 0, dDd.Count > 0 ? Math.Round(dDd.Average(), 1) : 0,
            oRet.Count > 0 ? Math.Round(oRet.Average(), 1) : 0, dRet.Count > 0 ? Math.Round(dRet.Average(), 1) : 0);
}

// BTC 外生 regime:BTC 收盤 < SMA50 = 市場 risk-off 下跌(用日期查、跟組合曲線對齊)
var btcDowntrend = new Dictionary<DateTime, bool>();
if (data.TryGetValue("BTCUSDT", out var btcRegimeBars) && btcRegimeBars.Count > 50)
{
    var bc = btcRegimeBars.OrderBy(b => b.OpenTime).ToList();
    for (int i = 0; i < bc.Count; i++)
    {
        if (i < 50) { btcDowntrend[bc[i].OpenTime] = false; continue; }
        decimal sma = 0m; for (int k = i - 50; k < i; k++) sma += bc[k].Close; sma /= 50;
        btcDowntrend[bc[i].OpenTime] = bc[i].Close < sma;
    }
}

// 2026-05-29 regime-gated 防禦:DD-aware 縮倉只在「持續下跌」才啟用,牛市/震盪暫時回撤不縮(省 whipsaw)。
// useBtc=true → 用 BTC 外生 regime(BTC<SMA50、按日期查、嚴謹);false → 組合自身 SMA_N(內生 proxy)。
(decimal defDd, decimal defRet) PortfolioDefendedRegime(List<string> members, decimal cbDd, int smaN = 20, decimal power = 2m, bool useBtc = false)
{
    var dDd = new List<decimal>(); var dRet = new List<decimal>();
    foreach (var kv in data)
    {
        var curves = members.Where(m => lsEq.ContainsKey(m) && lsEq[m].ContainsKey(kv.Key)).Select(m => lsEq[m][kv.Key]).ToList();
        if (curves.Count < members.Count) continue;
        int len = curves.Min(c => c.Count);
        if (len < smaN + 5) continue;
        var legRets = new List<decimal[]>();
        foreach (var c in curves) { var r = new decimal[len - 1]; for (int t = 1; t < len; t++) r[t - 1] = c[t - 1] > 0 ? (c[t] - c[t - 1]) / c[t - 1] : 0m; legRets.Add(r); }
        var w = new decimal[curves.Count]; decimal wsum = 0m;
        for (int i = 0; i < curves.Count; i++) { var avg = legRets[i].Average(); var vol = (decimal)Math.Sqrt((double)legRets[i].Select(x => (x - avg) * (x - avg)).Average()); w[i] = vol > 0m ? 1m / vol : 0m; wsum += w[i]; }
        if (wsum <= 0m) continue;
        for (int i = 0; i < w.Length; i++) w[i] /= wsum;
        var port = new List<decimal>(len) { 1m };
        for (int t = 0; t < len - 1; t++) { decimal pr = 0m; for (int i = 0; i < legRets.Count; i++) pr += w[i] * legRets[i][t]; port.Add(port[^1] * (1m + pr)); }
        // 此 coin 曲線對應日期(curveDates 是 LS 引擎全長、port 取 min len、對齊尾端? 實為前 len 個)
        var dts = curveDates.TryGetValue(kv.Key, out var dlist) ? dlist : null;
        // regime-gated 模擬:逐 bar、DD-aware scalar 只在「下跌 regime」時套用,否則 scalar=1
        decimal adj = port[0], adjPeak = adj, adjMaxDd = 0m, oPeak = port[0];
        for (int i = 1; i < port.Count; i++)
        {
            if (port[i] > oPeak) oPeak = port[i];   // 用 original 算 DD(決定縮倉幅度)
            decimal curDd = oPeak > 0m ? Math.Max(0m, (oPeak - port[i - 1]) / oPeak) : 0m;
            bool downtrend;
            if (useBtc)
                // BTC 外生:查 port[i-1] 對應日期的 BTC regime(日期不齊則 fallback 不縮)
                downtrend = dts != null && i - 1 < dts.Count && btcDowntrend.TryGetValue(dts[i - 1], out var dt) && dt;
            else
            {
                downtrend = false;
                if (i - 1 >= smaN) { decimal sma = 0m; for (int k = i - smaN; k < i; k++) sma += port[k]; sma /= smaN; downtrend = port[i - 1] < sma; }
            }
            decimal scalar = downtrend ? StrategyWorker.Engine.DrawdownAwareSizer.PolynomialScale(curDd, cbDd, power) : 1m;
            decimal origRet = port[i - 1] > 0m ? (port[i] - port[i - 1]) / port[i - 1] : 0m;
            adj *= (1m + origRet * scalar);
            if (adj > adjPeak) adjPeak = adj;
            if (adjPeak > 0m) { decimal d = (adjPeak - adj) / adjPeak; if (d > adjMaxDd) adjMaxDd = d; }
        }
        dRet.Add((adj - 1m) * 100m); dDd.Add(adjMaxDd * 100m);
    }
    return (dDd.Count > 0 ? Math.Round(dDd.Average(), 1) : 0, dRet.Count > 0 ? Math.Round(dRet.Average(), 1) : 0);
}

// 組合 OOS:每檔跑多空 RunPortfolioWalkForward、池化 test fold 報酬 → (avgOOS%, %fold正)
(decimal avgRet, decimal posPct) PortfolioOos(List<string> members)
{
    var subs = members.Select(m => strats.First(x => x.name == m).s).ToList();
    var foldRets = new List<decimal>();
    foreach (var kv in data)
    {
        var wf = LongShortBacktestEngine.RunPortfolioWalkForward(subs, kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, 250, 90, 60);
        foreach (var f in wf.Folds.Where(f => f.Test != null)) foldRets.Add(f.Test!.TotalReturnPct);
    }
    if (foldRets.Count == 0) return (0, 0);
    return (Math.Round(foldRets.Average(), 1), Math.Round((decimal)foldRets.Count(r => r > 0) / foldRets.Count * 100m, 0));
}

void PrintCombo(string label, List<string> members)
{
    var fe = Portfolio(members, false);
    var fr = Portfolio(members, true);
    var o = PortfolioOos(members);
    Console.WriteLine($"  {label,-26} 等權[Sh {fe.sharpe,5:F2} DD {fe.dd,4:F0}%]  風險加權[Sh {fr.sharpe,5:F2} DD {fr.dd,4:F0}%]  OOS[avg {o.avgRet,5:F1}% +fold {o.posPct,3:F0}%]");
}

if (lsEq.Count >= 2 && lsEq.Values.First().ContainsKey("BTCUSDT"))
{
    var all = lsEq.Keys.ToList();
    int len = all.Min(n => lsEq[n]["BTCUSDT"].Count);
    decimal Corr(string a, string b) => CorrelationGuard.PearsonOfReturns(lsEq[a]["BTCUSDT"].Take(len).ToList(), lsEq[b]["BTCUSDT"].Take(len).ToList());
    decimal AvgSharpe(string n) => lsEq[n].Values.Select(c => StatsOf(c).sharpe).Average();

    Console.WriteLine("\n=== 組合層回測(long-short、等權;full=全期跨檔平均, OOS=walk-forward 池化)===");
    foreach (var name in all.OrderByDescending(AvgSharpe))
    {
        var rs = new List<decimal>(); var ss = new List<decimal>(); var ds = new List<decimal>();
        foreach (var kv in lsEq[name]) { var st = StatsOf(kv.Value); rs.Add(st.ret); ss.Add(st.sharpe); ds.Add(st.dd); }
        Console.WriteLine($"  單腿 {name,-18} full[ret {rs.Average(),5:F0}% Sh {ss.Average(),5:F2} DD {ds.Average(),4:F0}%]");
    }

    PrintCombo($"組合 全部 {all.Count} 支等權", all);

    // 2026-05-29 防禦後組合 sweep:CB 門檻 × scale 方法,找最佳 DD/報酬 frontier(評估防禦改善空間)
    // 每格 = 套防禦後的 (maxDD%, 報酬%);report-per-DD 比率(報酬/DD)= 防禦效率,越高越好
    {
        var probe = PortfolioDefended(all, 0.08m, "poly");
        Console.WriteLine($"  防禦 sweep(風險加權、裸 DD {probe.origDd:F0}% / 裸報酬 {probe.origRet:F0}%)— 找最佳 CB×方法:");
        Console.WriteLine($"    {"CB門檻",-8}{"poly DD/ret",-20}{"step DD/ret",-20}{"linear DD/ret",-20}");
        foreach (var cb in new[] { 0.05m, 0.08m, 0.12m, 0.20m })
        {
            var p = PortfolioDefended(all, cb, "poly");
            var s = PortfolioDefended(all, cb, "step");
            var l = PortfolioDefended(all, cb, "linear");
            Console.WriteLine($"    {cb,-8:P0}{$"{p.defDd:F0}%/{p.defRet:F0}% (×{(p.defDd>0?p.defRet/p.defDd:0):F1})",-20}{$"{s.defDd:F0}%/{s.defRet:F0}% (×{(s.defDd>0?s.defRet/s.defDd:0):F1})",-20}{$"{l.defDd:F0}%/{l.defRet:F0}% (×{(l.defDd>0?l.defRet/l.defDd:0):F1})",-20}");
        }
        Console.WriteLine($"    (×N = 報酬/DD 效率比;DD 是 1x、實盤有效槓桿 L 倍則 DD×L 須 < ~80% 才有強平 margin)");
        // regime-gated 對照:plain vs 組合自身SMA(內生) vs BTC外生(嚴謹、查日期 BTC<SMA50)
        Console.WriteLine($"    regime-gated vs plain(驗 regime 增益是否為自我參照假象):");
        foreach (var cb in new[] { 0.08m, 0.12m })
        {
            var plain = PortfolioDefended(all, cb, "poly");
            var rgSelf = PortfolioDefendedRegime(all, cb, 20, useBtc: false);
            var rgBtc = PortfolioDefendedRegime(all, cb, 20, useBtc: true);
            string F((decimal dd, decimal ret) x) => $"{x.dd:F0}%/{x.ret:F0}% ×{(x.dd>0?x.ret/x.dd:0):F1}";
            Console.WriteLine($"      CB{cb:P0}  plain[{F((plain.defDd,plain.defRet))}]  自身SMA[{F(rgSelf)}]  BTC外生[{F(rgBtc)}]");
        }
    }

    // 貪婪挑去相關組合:候選先濾掉負期望(Sharpe≤0,去相關但賠錢的不要),再從 Sharpe 高起、納入「對已選全部 |ρ|<0.4」者
    var ranked = all.Where(n => AvgSharpe(n) > 0m).OrderByDescending(AvgSharpe).ToList();
    var picked = new List<string> { ranked[0] };
    foreach (var cand in ranked.Skip(1))
        if (picked.All(p => Math.Abs(Corr(cand, p)) < 0.4m)) picked.Add(cand);
    PrintCombo($"組合 去相關精選({picked.Count}支)", picked);
    Console.WriteLine($"     去相關精選成員:{string.Join(", ", picked)}");

    // 印出去相關精選的「反波動率權重」(跨檔平均、正規化)→ 給實盤獨立部署配重用
    decimal AvgVol(string n)
    {
        var vols = new List<decimal>();
        foreach (var c in lsEq[n].Values)
        {
            if (c.Count < 3) continue;
            var rr = new List<decimal>();
            for (int t = 1; t < c.Count; t++) if (c[t - 1] > 0) rr.Add((c[t] - c[t - 1]) / c[t - 1]);
            if (rr.Count < 2) continue;
            var a = rr.Average();
            vols.Add((decimal)Math.Sqrt((double)rr.Select(x => (x - a) * (x - a)).Average()));
        }
        return vols.Count > 0 ? vols.Average() : 0m;
    }
    var invVol = picked.ToDictionary(n => n, n => { var v = AvgVol(n); return v > 0 ? 1m / v : 0m; });
    var wsum2 = invVol.Values.Sum();
    Console.WriteLine("     建議風險加權(反波動率、正規化):" +
        string.Join("  ", picked.Select(n => $"{n} {(wsum2 > 0 ? invVol[n] / wsum2 : 0m):P0}")));
}

// ── decorr4_ls 一鍵淨加權 ensemble:fixed-notional vs confidence-sizing,對照真組合 ──
if (strats.Any(x => x.name == "decorr4_ls"))
{
    var ens = strats.First(x => x.name == "decorr4_ls").s;
    (decimal ret, decimal sharpe, decimal dd, decimal oos) EnsAgg(bool confSizing)
    {
        var rs = new List<decimal>(); var ss = new List<decimal>(); var ds = new List<decimal>(); var oosR = new List<decimal>();
        foreach (var kv in data)
        {
            var cfg = new StrategyConfig { Symbol = kv.Key, Interval = "1d" };
            var bt = LongShortBacktestEngine.Run(ens, kv.Value, cfg, confidenceSizing: confSizing);
            rs.Add(bt.TotalReturnPct); ss.Add(bt.SharpeRatio); ds.Add(bt.MaxDrawdownPct);
            var wf = LongShortBacktestEngine.RunWalkForward(ens, kv.Value, cfg, 250, 90, 60, confidenceSizing: confSizing);
            if (wf.TotalFolds > 0) oosR.Add(wf.AvgTestReturnPct);
        }
        return (Math.Round(rs.Average(), 0), Math.Round(ss.Average(), 2), Math.Round(ds.Average(), 0), oosR.Count > 0 ? Math.Round(oosR.Average(), 1) : 0);
    }
    var fix = EnsAgg(false);
    var cs = EnsAgg(true);
    Console.WriteLine("\n=== decorr4_ls 一鍵淨加權 ensemble(單一 watch 可部署)===");
    Console.WriteLine($"  固定部位         full[ret {fix.ret,4:F0}% Sh {fix.sharpe:F2} DD {fix.dd,3:F0}%] OOS {fix.oos:F1}%");
    Console.WriteLine($"  confidence-sizing full[ret {cs.ret,4:F0}% Sh {cs.sharpe:F2} DD {cs.dd,3:F0}%] OOS {cs.oos:F1}%  ← 分歧縮量、最接近真組合");
    Console.WriteLine("  對照 真組合(4獨立·風險加權):Sh 0.62 / DD 46%(見上方組合層)");
}

Console.WriteLine("\n判定 = OOS 中位報酬>0 且 ≥60% 檔 OOS 正報酬 且 全期連續 Sharpe>0。組合層比較單腿:Sharpe 升 / maxDD 降 = 去相關紅利。");

// ════════════════════════════════════════════════════════════════════════════
// --vol-target:Q1.2 vol-targeting A/B(roadmap milestone)。固定 sizing vs 反波動率調倉,
//   比【風險調整後】(Sharpe↑/maxDD↓),不是 mean↑。先驗有沒有用、再談部署(同 conf-sizing 紀律)。
// ════════════════════════════════════════════════════════════════════════════
void RunVolTargetAB()
{
    decimal tv = decimal.TryParse(Environment.GetEnvironmentVariable("VOL_TARGET_ANNUAL"), out var v) ? v : 0.60m;
    int lb = int.TryParse(Environment.GetEnvironmentVariable("VOL_TARGET_LOOKBACK"), out var l) ? l : 30;
    decimal mx = decimal.TryParse(Environment.GetEnvironmentVariable("VOL_TARGET_MAX"), out var m) ? m : 2.0m;
    string mode = mx <= 1.0m ? "de-risk-only 只縮不放、合存活紀律" : $"scalar 上限 {mx}(>1 會加槓桿)";

    Console.WriteLine($"\n=== vol-targeting A/B(target {tv:P0} 年化、lookback {lb}d、{mode};固定 vs 反波動率調倉)===");
    Console.WriteLine("解讀:vol-targeting 賣點是【風險調整後】改善(Sharpe↑/maxDD↓)、不是 mean↑;就算 mean 降,Sharpe 升+DD 降就值得。");
    Console.WriteLine("      clamp 0.3~2.0;crypto realized vol 常 >60% → scalar 多半 <1(縮倉)。\n");
    Console.WriteLine($"  {"strategy",-16}│ 固定 Sh/DD%/ret%/t  │ volTgt Sh/DD%/ret%/t │ 判定");

    // 自帶 t-stat(不依賴外層 BootCI/rng,因本函式在它們指派前就被呼叫)
    double Tstat(List<decimal> xs)
    {
        if (xs.Count < 5) return 0;
        double mean = (double)xs.Average();
        double sd = Math.Sqrt(xs.Select(x => ((double)x - mean) * ((double)x - mean)).Sum() / (xs.Count - 1));
        double se = sd / Math.Sqrt(xs.Count);
        return se > 0 ? mean / se : 0;
    }

    List<decimal> Pool(IStrategy s, bool vt)
    {
        var r = new List<decimal>();
        foreach (var kv in data)
            try { var w = LongShortBacktestEngine.RunWalkForward(s, kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, 250, 90, 60,
                       commission: gComm, slippagePct: gSlip, applyFunding: realFunding,
                       volTargetSizing: vt, volTargetAnnual: tv, volTargetLookback: lb, volTargetMaxScalar: mx);
                  foreach (var f in w.Folds.Where(f => f.Test != null)) r.Add(f.Test!.TotalReturnPct); }
            catch { }
        return r;
    }

    int shUp = 0, ddDown = 0, both = 0, total = 0;
    foreach (var (name, s) in strats)
    {
        var fSh = new List<decimal>(); var fDD = new List<decimal>(); var fRet = new List<decimal>();
        var vSh = new List<decimal>(); var vDD = new List<decimal>(); var vRet = new List<decimal>();
        foreach (var kv in data)
        {
            try
            {
                var cfg = new StrategyConfig { Symbol = kv.Key, Interval = "1d" };
                var bF = LongShortBacktestEngine.Run(s, kv.Value, cfg, commission: gComm, slippagePct: gSlip, applyFunding: realFunding);
                var bV = LongShortBacktestEngine.Run(s, kv.Value, cfg, commission: gComm, slippagePct: gSlip, applyFunding: realFunding,
                                                     volTargetSizing: true, volTargetAnnual: tv, volTargetLookback: lb, volTargetMaxScalar: mx);
                if (bF.TotalBars >= 100)
                {
                    fSh.Add(bF.SharpeRatio); fDD.Add(bF.MaxDrawdownPct); fRet.Add(bF.TotalReturnPct);
                    vSh.Add(bV.SharpeRatio); vDD.Add(bV.MaxDrawdownPct); vRet.Add(bV.TotalReturnPct);
                }
            }
            catch { }
        }
        if (fSh.Count < 3) { Console.WriteLine($"  {name,-16}(樣本不足、skip)"); continue; }
        decimal mfSh = Median(fSh), mfDD = Median(fDD), mfRet = Median(fRet);
        decimal mvSh = Median(vSh), mvDD = Median(vDD), mvRet = Median(vRet);
        double tF = Tstat(Pool(s, false)), tV = Tstat(Pool(s, true));
        bool sUp = mvSh > mfSh + 0.05m;     // Sharpe 明顯升
        bool dDn = mvDD < mfDD - 2m;        // maxDD 明顯降(> 2pp)
        total++; if (sUp) shUp++; if (dDn) ddDown++; if (sUp && dDn) both++;
        string verdict = (sUp && dDn) ? "✅ Sh↑DD↓" : dDn ? "⚠ 只 DD↓" : sUp ? "⚠ 只 Sh↑" : "✗ 沒改善";
        Console.WriteLine($"  {name,-16}│ {mfSh,5:F2}/{mfDD,4:F0}/{mfRet,5:F0}/{tF,4:F1} │ {mvSh,5:F2}/{mvDD,4:F0}/{mvRet,5:F0}/{tV,4:F1} │ {verdict}");
    }
    Console.WriteLine($"\n── 結論 ── {total} 策略:Sharpe 升 {shUp}、maxDD 降 {ddDown}、兩者皆 {both}。");
    Console.WriteLine(both > total / 2
        ? "→ vol-targeting 多數策略改善風險調整後表現,值得進一步評估(recommend 工具 → shadow → 循紀律部署)。"
        : "→ vol-targeting 對多數策略無明顯淨改善(crypto 多在縮倉、降報酬未換到 Sharpe);維持固定倉位、或只對 DD 過高的腿選擇性用。");
}

// ════════════════════════════════════════════════════════════════════════════
// --conf-diag:confidence 校準診斷(Q1 開放項;Carver gap-analysis §開放項)
//   per-trade entry confidence vs 實際 PnlPct。答兩問:
//   ① 有預測力嗎(分桶單調 / Pearson·Spearman ≠ 0)② 跨策略可比嗎(分布重疊 / 中位 spread)
//   full-sample(對「找到關係」有利、故 null 結果是保守且強的結論)。
// ════════════════════════════════════════════════════════════════════════════
void RunConfDiagnostic()
{
    double Pearson(List<double> xs, List<double> ys)
    {
        int n = xs.Count; if (n < 3) return 0;
        double mx = xs.Average(), my = ys.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < n; i++) { var dx = xs[i] - mx; var dy = ys[i] - my; sxy += dx * dy; sxx += dx * dx; syy += dy * dy; }
        return (sxx > 0 && syy > 0) ? sxy / Math.Sqrt(sxx * syy) : 0;
    }
    List<double> Rank(List<double> v)   // 平均秩處理 ties(confidence 多重複值)
    {
        var idx = Enumerable.Range(0, v.Count).OrderBy(i => v[i]).ToList();
        var r = new double[v.Count];
        int k = 0;
        while (k < idx.Count)
        {
            int j = k;
            while (j + 1 < idx.Count && v[idx[j + 1]] == v[idx[k]]) j++;
            double avgRank = (k + j) / 2.0 + 1;
            for (int t = k; t <= j; t++) r[idx[t]] = avgRank;
            k = j + 1;
        }
        return r.ToList();
    }
    double Spearman(List<double> xs, List<double> ys) => Pearson(Rank(xs), Rank(ys));
    double Pct(List<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        double pos = q * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    Console.WriteLine("\n=== confidence 校準診斷(full-sample;每筆 entry confidence vs 實際 PnlPct)===");
    Console.WriteLine("解讀:Pearson/Spearman ≈ 0 → confidence 對「單筆好壞」無預測力 → forecast-strength sizing 無 alpha、固定倉位正確。");
    Console.WriteLine("      conf 若全擠在 0.6-1.0(進場閘 0.6 截斷)且各策略中位差大 = 跨策略不可比(Carver forecast 需波動標準化、B4A 未做)。\n");
    Console.WriteLine($"  {"strategy",-22}{"n",5}  conf[min med max]    {"Pear",7}{"Spear",8}  │ 低桶Pnl%(WR) 中桶          高桶          趨勢");

    var rows = new List<(string name, double med, double pear, double spear, int n, bool constant)>();
    foreach (var (name, s) in strats)
    {
        var confs = new List<double>(); var pnls = new List<double>();
        foreach (var kv in data)
        {
            try
            {
                var cfg = new StrategyConfig { Symbol = kv.Key, Interval = "1d" };
                var bt = LongShortBacktestEngine.Run(s, kv.Value, cfg, commission: gComm, slippagePct: gSlip, defaultInitialSlPct: slPct, applyFunding: realFunding);
                foreach (var t in bt.Trades) { confs.Add((double)t.EntryConfidence); pnls.Add((double)t.PnlPct); }
            }
            catch { }
        }
        if (confs.Count < 20) { Console.WriteLine($"  {name,-22}{confs.Count,5}  (trades < 20、樣本不足、skip)"); continue; }
        var sortedConf = confs.OrderBy(x => x).ToList();
        double cmin = sortedConf.First(), cmed = Pct(sortedConf, 0.5), cmax = sortedConf.Last();
        double pear = Pearson(confs, pnls), spear = Spearman(confs, pnls);

        var pairs = confs.Zip(pnls, (c, p) => (c, p)).OrderBy(x => x.c).ToList();
        int b = pairs.Count / 3;
        var loG = pairs.Take(b).ToList();
        var midG = pairs.Skip(b).Take(b).ToList();
        var hiG = pairs.Skip(2 * b).ToList();
        double AvgP(List<(double c, double p)> g) => g.Count > 0 ? g.Average(x => x.p) : 0;
        double WR(List<(double c, double p)> g) => g.Count > 0 ? (double)g.Count(x => x.p > 0) / g.Count * 100 : 0;
        double loP = AvgP(loG), midP = AvgP(midG), hiP = AvgP(hiG);
        string trend = (hiP > midP && midP > loP) ? "↑單調" : (hiP > loP ? "弱↑" : (hiP < loP ? "✗反向" : "平"));

        bool constant = (cmax - cmin) < 0.001;
        Console.WriteLine($"  {name,-22}{confs.Count,5}  [{cmin:F2} {cmed:F2} {cmax:F2}] {pear,7:F3}{spear,8:F3}  │ {loP,5:F2}({WR(loG):F0}%) {midP,5:F2}({WR(midG):F0}%) {hiP,5:F2}({WR(hiG):F0}%)  {(constant ? "常數conf" : trend)}");
        rows.Add((name, cmed, pear, spear, confs.Count, constant));
    }

    if (rows.Count > 0)
    {
        double medSpread = rows.Max(x => x.med) - rows.Min(x => x.med);
        int constantCnt = rows.Count(x => x.constant);
        // 真預測力 = Pearson 與 Spearman 同號(線性+秩一致)且兩者皆 >0.1;反號 = 離群驅動的假關係、不算
        int consistent = rows.Count(x => Math.Sign(x.pear) == Math.Sign(x.spear) && Math.Abs(x.pear) > 0.1 && Math.Abs(x.spear) > 0.1);
        int conflicting = rows.Count(x => Math.Sign(x.pear) != Math.Sign(x.spear) && (Math.Abs(x.pear) > 0.1 || Math.Abs(x.spear) > 0.1));
        Console.WriteLine("\n── 結論 ──");
        Console.WriteLine($"① 範圍壓縮:conf 下限 ≈ 0.6(進場閘截斷);{constantCnt}/{rows.Count} 支吐【常數 confidence】(min=max、零資訊)。");
        Console.WriteLine($"② 跨策略不可比:conf 中位 spread = {medSpread:F2}({(medSpread > 0.15 ? "大 → 各策略的 0.X 不等義、不能放同一尺度 scale 倉位" : "小")})。");
        Console.WriteLine($"③ 預測力:Pearson·Spearman 同號且皆>0.1 的 {consistent}/{rows.Count};反號(離群假關係){conflicting} 支。");
        Console.WriteLine(consistent == 0
            ? "→ confidence 對單筆報酬【無一致預測力】→ forecast-strength sizing 無 alpha 可撈、固定倉位是對的(資料印證 conf-sizing 負結果)。"
            : $"→ {consistent} 支 conf 帶【一致】弱預測力,可對那幾支單獨深究 per-strategy sizing(但跨策略仍不可比、不能全域開)。");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// --allocate 配置引擎:把候選策略池跑成「可直接部署的權重」
//   每腿 = (策略 → 它的最佳幣);跨腿做穩健配重。全用 long-short 引擎 + realistic 成本。
// ════════════════════════════════════════════════════════════════════════════
// 嚴格版選股:每個 walk-forward fold 用「該 fold 訓練窗的波動」(ex-ante、無 lookahead)選 top-K vol 股,
// 在該 fold 測試窗收報酬 → 動態成員組合。對照隨機-K + 全宇宙(同折同窗、公平)。
// 過關 = trailing-vol(ex-ante)仍顯著贏隨機 → 證實 killer test 的 0.51 不是全期波動 lookahead 灌的。
async Task RunSelectionWf()
{
    var sname = args.FirstOrDefault(a => a.StartsWith("--strat="))?.Substring(8) ?? "harm_prz_scan10_widepz";
    var st = strats.FirstOrDefault(s => s.name == sname).s;
    if (st == null) { Console.WriteLine($"找不到策略 {sname}"); return; }
    bool longOnly = args.Contains("--long-only");   // 純多(台股散戶融券受限的現實);否則多空
    Console.WriteLine($"=== 嚴格版選股 trailing-vol per rebalance(ex-ante):{sname} · cost {costBps}bps/側 · {(longOnly ? "純多 long-only" : "多空 LS")} ===");
    var dat = new Dictionary<string, List<BarData>>();
    foreach (var sym in symbols) { try { var b = await Fetch(sym); if (b.Count >= 400) dat[sym] = b; } catch { } }
    const int train = 250, test = 90, step = 60;
    Console.WriteLine($"  宇宙 {dat.Count} 檔 · walk-forward {train}/{test}/{step}");
    // per stock:各 fold 測試窗報酬(純多走 BacktestEngine、多空走 LongShortBacktestEngine)
    var foldRet = new Dictionary<string, List<double>>();
    foreach (var kv in dat)
        try
        {
            var cfg = new StrategyConfig { Symbol = kv.Key, Interval = "1d" };
            List<double> fr;
            if (longOnly)
            {
                var w = BacktestEngine.RunWalkForward(st, kv.Value, cfg, train, test, step, commission: gComm, slippagePct: gSlip);
                fr = w.Folds.Where(f => f.Test != null).Select(f => (double)f.Test!.TotalReturnPct).ToList();
            }
            else
            {
                var w = LongShortBacktestEngine.RunWalkForward(st, kv.Value, cfg, train, test, step, commission: gComm, slippagePct: gSlip);
                fr = w.Folds.Where(f => f.Test != null).Select(f => (double)f.Test!.TotalReturnPct).ToList();
            }
            foldRet[kv.Key] = fr;
        }
        catch { }
    if (foldRet.Count == 0) { Console.WriteLine("無資料"); return; }
    int maxF = foldRet.Values.Max(v => v.Count);
    int K = Math.Max(5, dat.Count / 3);
    // fold f 的 trailing 波動 = bars[f*step : train+f*step](= fold f 訓練窗、在測試窗之前 = ex-ante)
    double TrailVol(string sym, int f)
    {
        var b = dat[sym]; int lo = f * step, hi = train + f * step;
        if (b.Count < hi) return double.NaN;
        var rr = new List<double>(); for (int i = lo + 1; i < hi; i++) { double pv = (double)b[i - 1].Close; if (pv > 0) rr.Add((double)(b[i].Close - b[i - 1].Close) / pv); }
        if (rr.Count < 20) return double.NaN;
        double m = rr.Average(); return Math.Sqrt(rr.Select(x => (x - m) * (x - m)).Sum() / rr.Count);
    }
    double Sharpe(List<double> ser) { if (ser.Count < 3) return 0; double m = ser.Average(); double sd = Math.Sqrt(ser.Select(x => (x - m) * (x - m)).Sum() / Math.Max(1, ser.Count - 1)); return sd > 1e-9 ? m / sd * Math.Sqrt(365.0 / step) : 0; }
    var volSel = new List<double>(); var uni = new List<double>();
    var selCount = new Dictionary<string, int>();
    for (int f = 0; f < maxF; f++)
    {
        var avail = foldRet.Where(kv => kv.Value.Count > f).Select(kv => kv.Key).ToList();
        var vols = avail.Select(s => (s, v: TrailVol(s, f))).Where(x => !double.IsNaN(x.v)).ToList();
        if (vols.Count < K) continue;
        var top = vols.OrderByDescending(x => x.v).Take(K).Select(x => x.s).ToList();
        volSel.Add(top.Average(s => foldRet[s][f]));
        uni.Add(avail.Average(s => foldRet[s][f]));
        foreach (var s in top) selCount[s] = selCount.GetValueOrDefault(s) + 1;
    }
    var rng = new Random(42); int M = 2000; var randSh = new List<double>();
    for (int b = 0; b < M; b++)
    {
        var ser = new List<double>();
        for (int f = 0; f < maxF; f++)
        {
            var avail = foldRet.Where(kv => kv.Value.Count > f).Select(kv => kv.Key).ToList();
            if (avail.Count < K) continue;
            var pick = avail.OrderBy(_ => rng.Next()).Take(K).ToList();
            ser.Add(pick.Average(s => foldRet[s][f]));
        }
        randSh.Add(Sharpe(ser));
    }
    double vsSh = Sharpe(volSel), uniSh = Sharpe(uni), randMean = randSh.Average();
    double pct = randSh.Count(x => x < vsSh) * 100.0 / M;
    // 絕對報酬(可部署性關鍵;Sharpe 被跨股平均灌水、絕對報酬才實在)。fold=90天測試窗 → 年化 ≈ ×(365/90)
    double vsRet = volSel.Average(), uniRet = uni.Average();
    Console.WriteLine($"\n  === 嚴格版結果({volSel.Count} folds、top-{K})===");
    Console.WriteLine($"  Sharpe — trailing-vol 選股: {vsSh:F2}   全宇宙: {uniSh:F2}   隨機-{K} 均值: {randMean:F2}   百分位: {pct:F0}%");
    Console.WriteLine($"  絕對報酬(平均每fold~季)— vol選股: {vsRet:F2}%/季(年化~{vsRet * 365 / 90:F0}%)   全宇宙: {uniRet:F2}%/季(年化~{uniRet * 365 / 90:F0}%)");
    Console.WriteLine($"  最常被選中(trailing-vol top-{K}):{string.Join(",", selCount.OrderByDescending(kv => kv.Value).Take(K).Select(kv => kv.Key.Replace(".TW", "") + $"({kv.Value})"))}");
    // 選股信號是否真實 = 只看「同檔數下贏不贏隨機」(贏全宇宙是檔數/廣度效應、不該當選股勝負)
    bool selReal = pct >= 95;
    string breadth = vsSh >= uniSh ? "選股 ≥ 全宇宙" : $"全宇宙({uniSh:F2})> 選股 = 廣度/分散效應(檔數多→更分散)、非選股輸";
    Console.WriteLine($"\n  判讀:{(selReal ? $"✅ trailing-vol(ex-ante)顯著贏隨機(百分位 {pct:F0}%)→ 選股信號真實、非 lookahead" : $"⚠️ 沒顯著贏隨機(百分位 {pct:F0}%)→ 選股是運氣")}");
    Console.WriteLine($"  廣度註記:{breadth} → 最佳解傾向「留廣宇宙 + 高波動 tilt 權重」、非縮檔數。");
    Console.WriteLine($"  (註:組合報酬序列 Sharpe 被跨股平均灌水、相對比較有效絕對值不可信;真實可部署看絕對報酬+成本+容量)");
}

// 個股性格路由回測:每股算 vol×ER 性格 → 配對應策略 → 比「性格路由」vs「全用單一策略」vs「隨機路由」。
// 股票版正確哲學:回測「能套用全市場的路由規則」、非手挑宇宙;每股都能用(算性格→查表配策略)。
// 不逐檔 cherry-pick(那會多重檢定假陽性)、驗證的是「一條路由規則」贏不贏 one-size-fits-all。
async Task RunStockRouter()
{
    bool exAnte = args.Contains("--ex-ante");
    bool longOnly = args.Contains("--long-only");
    const double VOL_HI = 0.40, ER_TREND = 0.30;   // 固定機制門檻(無 lookahead、不優化=避免過擬合)
    Console.WriteLine($"=== 個股性格路由回測({(exAnte ? "ex-ante per-fold 性格" : "全期性格")}{(longOnly ? " · 純多" : " · LS")};cost {costBps}bps/側)===");
    var dat = new Dictionary<string, List<BarData>>();
    foreach (var sym in symbols) { try { var b = await Fetch(sym); if (b.Count >= 350) dat[sym] = b; } catch { } }
    Console.WriteLine($"  宇宙 {dat.Count}/{symbols.Length} 檔\n");

    var sH = strats.FirstOrDefault(s => s.name == "harm_prz_scan10_widepz").s;  // 波動震盪 → 諧波反轉
    var sT = strats.FirstOrDefault(s => s.name == "ts_momentum").s;             // 趨勢 → 動量
    var sR = strats.FirstOrDefault(s => s.name == "bb_revert_ls").s;            // 均值回歸 → 布林回歸
    if (sH == null || sT == null || sR == null) { Console.WriteLine("  缺策略"); return; }
    var stratMap = new (string key, IStrategy st)[] { ("harmonic", sH), ("tsmom", sT), ("bb_revert", sR) };

    // 算 bars[s0:s1) 的性格(vol 年化 + ER Kaufman 效率比)→ 路由 key。ex-ante 時餵訓練窗 = 無 lookahead。
    string RouteSlice(List<BarData> b, int s0, int s1)
    {
        var rr = new List<double>();
        for (int i = s0 + 1; i < s1; i++) { double pv = (double)b[i - 1].Close; if (pv > 0) rr.Add((double)(b[i].Close - b[i - 1].Close) / pv); }
        if (rr.Count < 20) return "bb_revert";
        double mm = rr.Average();
        double vol = Math.Sqrt(rr.Select(x => (x - mm) * (x - mm)).Sum() / rr.Count) * Math.Sqrt(252);
        double net = Math.Abs((double)(b[s1 - 1].Close - b[s0].Close)); double tot = 0;
        for (int i = s0 + 1; i < s1; i++) tot += Math.Abs((double)(b[i].Close - b[i - 1].Close));
        double er = tot > 0 ? net / tot : 0;
        return er >= ER_TREND ? "tsmom" : (vol >= VOL_HI ? "harmonic" : "bb_revert");
    }

    // 從每-fold 報酬序列算 Sharpe(年化)+ maxDD(串接 fold 權益、fold 重疊→相對比較有效非絕對)
    (double sharpe, double mdd) RiskMetrics(List<double> folds)
    {
        if (folds.Count < 2) return (0, 0);
        double m = folds.Average();
        double sd = Math.Sqrt(folds.Select(x => (x - m) * (x - m)).Sum() / (folds.Count - 1));
        double sharpe = sd > 0 ? m / sd * Math.Sqrt(252.0 / 90.0) : 0;
        double eq = 1, peak = 1, mdd = 0;
        foreach (var r in folds) { eq *= 1 + r / 100; if (eq > peak) peak = eq; double dd = peak > 0 ? (peak - eq) / peak : 0; if (dd > mdd) mdd = dd; }
        return (sharpe, mdd * 100);
    }

    var rng = new Random(42);
    var perStock = new List<(string sym, double routed, Dictionary<string, double> single, Dictionary<string, double> sharpe, Dictionary<string, double> mdd)>();
    var routeCnt = new Dictionary<string, int> { ["tsmom"] = 0, ["harmonic"] = 0, ["bb_revert"] = 0 };
    const double ann = 252.0 / 90.0;   // per-fold(~90交易日)→ 年化
    foreach (var kv in dat)
    {
        var b = kv.Value;
        var foldRet = new Dictionary<string, List<double>>();
        foreach (var (key, st) in stratMap)
        {
            try
            {
                var w = longOnly
                    ? BacktestEngine.RunWalkForward(st, b, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, 250, 90, 60, commission: gComm, slippagePct: gSlip)
                    : LongShortBacktestEngine.RunWalkForward(st, b, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, 250, 90, 60, commission: gComm, slippagePct: gSlip);
                foldRet[key] = w.Folds.Where(f => f.Test != null).Select(f => (double)f.Test!.TotalReturnPct).ToList();
            }
            catch { foldRet[key] = new List<double>(); }
        }
        int F = foldRet.Values.Min(l => l.Count);
        if (F == 0) continue;
        string fullKey = RouteSlice(b, 0, b.Count);   // 全期性格(非 ex-ante 用)
        var routedFolds = new List<double>();
        for (int f = 0; f < F; f++)
        {
            // ex-ante:用 fold f 的訓練窗 bars[f*60 : f*60+250](測試窗之前)算性格 → 無 lookahead
            string key = exAnte ? RouteSlice(b, f * 60, Math.Min(b.Count, f * 60 + 250)) : fullKey;
            routeCnt[key]++;
            routedFolds.Add(foldRet[key][f]);
        }
        double routed = routedFolds.Average() * ann;
        var single = foldRet.ToDictionary(p => p.Key, p => p.Value.Take(F).Average() * ann);
        // B&H 基準:每 fold 測試窗 buy-hold(對齊策略 fold 窗 test=[f*60+250, +90))→ 判斷策略是 alpha 還是純多頭 beta
        var bhFolds = new List<double>();
        for (int f = 0; f < F; f++) { int ts = f * 60 + 250, te = ts + 90; if (te > b.Count) break; double c0 = (double)b[ts].Close; if (c0 > 0) bhFolds.Add(((double)b[te - 1].Close - c0) / c0 * 100); }
        single["bh"] = bhFolds.Count > 0 ? bhFolds.Average() * ann : 0;
        var sharpe = new Dictionary<string, double>(); var mdd = new Dictionary<string, double>();
        foreach (var key in new[] { "harmonic", "tsmom", "bb_revert" }) { var rm = RiskMetrics(foldRet[key].Take(F).ToList()); sharpe[key] = rm.sharpe; mdd[key] = rm.mdd; }
        var bhRm = RiskMetrics(bhFolds); sharpe["bh"] = bhRm.sharpe; mdd["bh"] = bhRm.mdd;
        perStock.Add((kv.Key, routed, single, sharpe, mdd));
    }
    if (perStock.Count < 8) { Console.WriteLine("  資料不足"); return; }

    double routedAvg = perStock.Average(r => r.routed);
    double allH = perStock.Average(r => r.single["harmonic"]);
    double allT = perStock.Average(r => r.single["tsmom"]);
    double allR = perStock.Average(r => r.single["bb_revert"]);
    double allBH = perStock.Average(r => r.single["bh"]);
    int n2 = perStock.Count;
    int bhWinH = perStock.Count(r => r.single["harmonic"] > r.single["bh"]);
    int bhWinT = perStock.Count(r => r.single["tsmom"] > r.single["bh"]);
    int bhWinR2 = perStock.Count(r => r.single["bb_revert"] > r.single["bh"]);
    double shH = perStock.Average(r => r.sharpe["harmonic"]), shT = perStock.Average(r => r.sharpe["tsmom"]), shR = perStock.Average(r => r.sharpe["bb_revert"]), shBH = perStock.Average(r => r.sharpe["bh"]);
    double ddH = perStock.Average(r => r.mdd["harmonic"]), ddT = perStock.Average(r => r.mdd["tsmom"]), ddR = perStock.Average(r => r.mdd["bb_revert"]), ddBH = perStock.Average(r => r.mdd["bh"]);
    var keys = new[] { "harmonic", "tsmom", "bb_revert" }; var rand = new List<double>();
    for (int bb = 0; bb < 5000; bb++) { double s = 0; foreach (var r in perStock) s += r.single[keys[rng.Next(3)]]; rand.Add(s / perStock.Count); }
    double randMean = rand.Average(); double pct = rand.Count(x => x < routedAvg) * 100.0 / 5000;

    Console.WriteLine($"  路由分配(fold 計):tsmom {routeCnt["tsmom"]} · harmonic {routeCnt["harmonic"]} · bb_revert {routeCnt["bb_revert"]}(門檻 ER≥{ER_TREND}→趨勢、否則 vol≥{VOL_HI}→震盪)");
    Console.WriteLine($"\n  === 風險-報酬完整對照(各檔平均、cost {costBps}bps、{(longOnly ? "純多" : "LS")})===");
    Console.WriteLine($"  {"配法",-13}{"年化%",8}{"Sharpe",8}{"maxDD%",8}{"vsB&H",8}{"勝B&H",7}");
    Console.WriteLine($"  {"買入持有B&H",-13}{allBH,8:F1}{shBH,8:F2}{ddBH,8:F0}{"—",8}{"—",7}");
    Console.WriteLine($"  {"tsmom",-13}{allT,8:F1}{shT,8:F2}{ddT,8:F0}{allT - allBH,8:+0.0;-0.0}{bhWinT * 100 / n2 + "%",7}");
    Console.WriteLine($"  {"harmonic",-13}{allH,8:F1}{shH,8:F2}{ddH,8:F0}{allH - allBH,8:+0.0;-0.0}{bhWinH * 100 / n2 + "%",7}");
    Console.WriteLine($"  {"bb_revert",-13}{allR,8:F1}{shR,8:F2}{ddR,8:F0}{allR - allBH,8:+0.0;-0.0}{bhWinR2 * 100 / n2 + "%",7}");
    Console.WriteLine($"  {"★性格路由",-13}{routedAvg,8:F1}{"—",8}{"—",8}{routedAvg - allBH,8:+0.0;-0.0}{$"隨機{pct:F0}%",7}");
    double bestSingle = Math.Max(allH, Math.Max(allT, allR));
    bool win2 = routedAvg > bestSingle + 0.5 && pct >= 90;
    Console.WriteLine($"\n  判讀:{(win2 ? $"✅ 性格路由 {routedAvg:F1}% 贏最佳單策略 {bestSingle:F1}% + 贏隨機(百分位{pct:F0}%)→ 路由規則{(exAnte ? "(ex-ante 無lookahead)" : "")}有價值、可推廣每股/全市場" : $"⚠️ 路由 {routedAvg:F1}% 沒明顯贏最佳單策略 {bestSingle:F1}% 或隨機(百分位{pct:F0}%)→ 路由無增益")}");
    string alphaV = allT > allBH + 1 ? $"tsmom +{allT - allBH:F1}% = 真 alpha(勝 B&H {bhWinT * 100 / n2}% 檔)" : allT > allBH ? $"tsmom 僅 +{allT - allBH:F1}% = 勉強贏、主要是 beta" : $"tsmom ≤ B&H = 純多頭 beta、無主動價值";
    Console.WriteLine($"  alpha 判讀(vs 買入持有):{alphaV}");
    Console.WriteLine($"  (caveat:{(exAnte ? "性格用訓練窗 ex-ante = 無lookahead;" : "性格全期算 = 輕微lookahead;")}宇宙手挑85非PIT(台股survivorship~2%小);門檻=固定機制值非優化;{(longOnly ? "純多可部署" : "LS")};跨股平均報酬仍有灌水、相對比較有效)");
}

// edge 離散度診斷:跑單一策略 per-symbol(LS、真實成本)→ 看 Sharpe 分布。
// 高離散(少數股強、多數弱)= 選股有東西抓;均勻弱 = 選股救不了(不能從無 alpha 選出 alpha)。
async Task RunDispersion()
{
    var sname = args.FirstOrDefault(a => a.StartsWith("--strat="))?.Substring(8) ?? "harm_prz_scan10_widepz";
    var st = strats.FirstOrDefault(s => s.name == sname).s;
    if (st == null) { Console.WriteLine($"找不到策略 {sname}"); return; }
    var tf = args.FirstOrDefault(a => a.StartsWith("--tf="))?.Substring(5) ?? "1d";
    int wfTrain = 250, wfTest = 90, wfStep = 60;   // 盤中用較大 bar 窗(1h:~30d/10d/7d)
    if (tf == "1h") { wfTrain = 720; wfTest = 240; wfStep = 168; }
    else if (tf == "4h") { wfTrain = 360; wfTest = 120; wfStep = 90; }
    Console.WriteLine($"=== edge 離散度診斷:{sname}(per-symbol LS、cost {costBps}bps/側、tf={tf}、wf {wfTrain}/{wfTest}/{wfStep})===");
    var dat = new Dictionary<string, List<BarData>>();
    foreach (var sym in symbols) { try { var b = await Fetch(sym, tf); if (b.Count >= wfTrain + wfTest) dat[sym] = b; } catch { } }
    // retail_ls / oi / liquidation 策略需注入 RetailLongShortRatio / OpenInterest(--apply-retail-ls)否則整段 hold → 0 edge
    if (realRetailLs)
    {
        int inj = 0;
        foreach (var kv in dat) try { await ToolsShared.OiMetricsCache.InjectInto(kv.Value, kv.Key, tf); inj++; } catch { }
        Console.WriteLine($"  📊 已注入 retail L/S + OI metrics({tf}):{inj}/{dat.Count} 檔");
    }
    // 注入 BTC 當日報酬(BTC-lead alt-lag 策略用;從 price bars 算、不需 metrics)
    if (dat.TryGetValue("BTCUSDT", out var btcB))
    {
        var btcRet = new Dictionary<DateTime, decimal>();
        for (int i = 1; i < btcB.Count; i++) { var pv = btcB[i - 1].Close; if (pv > 0m) btcRet[btcB[i].OpenTime.Date] = (btcB[i].Close - pv) / pv; }
        foreach (var kv in dat) foreach (var b in kv.Value) if (btcRet.TryGetValue(b.OpenTime.Date, out var r)) b.BtcRet = r;
        Console.WriteLine($"  📊 已注入 BTC 當日報酬:{btcRet.Count} 日(BTC-lead 策略用)");
    }
    // COT 持倉注入(商品/期貨;Yahoo =F ticker → CFTC market 關鍵字、Socrata 抓、無 lookahead)
    var cotMap = new Dictionary<string, string>
    {
        ["GC=F"] = "GOLD", ["SI=F"] = "SILVER", ["HG=F"] = "COPPER", ["PL=F"] = "PLATINUM", ["PA=F"] = "PALLADIUM",
        ["CL=F"] = "CRUDE OIL, LIGHT SWEET", ["NG=F"] = "NATURAL GAS", ["RB=F"] = "GASOLINE", ["HO=F"] = "ULSD",
        ["ZC=F"] = "CORN", ["ZW=F"] = "WHEAT-SRW", ["ZS=F"] = "SOYBEANS", ["KC=F"] = "COFFEE C", ["SB=F"] = "SUGAR NO. 11", ["CC=F"] = "COCOA", ["CT=F"] = "COTTON NO. 2",
        ["ES=F"] = "E-MINI S&P 500", ["NQ=F"] = "NASDAQ-100", ["YM=F"] = "DJIA", ["RTY=F"] = "RUSSELL 2000",
        ["ZN=F"] = "10Y", ["ZB=F"] = "TREASURY BONDS",
    };
    {
        int cotInj = 0, cotMiss = 0;
        foreach (var kv in dat)
            if (cotMap.TryGetValue(kv.Key, out var kw))
                try { if (await ToolsShared.COTCache.InjectInto(kv.Value, kw) > 0) cotInj++; else cotMiss++; } catch { cotMiss++; }
        if (cotInj > 0 || cotMiss > 0) Console.WriteLine($"  📊 已注入 COT 持倉:{cotInj} 檔命中、{cotMiss} 檔無資料(cot_positioning 策略用)");
    }
    Console.WriteLine($"  宇宙 {dat.Count}/{symbols.Length} 檔。判讀:高離散(少數強、多數弱)→選股有救;均勻弱→選股救不了\n");
    // OOS walk-forward(250/90/60)per-symbol:OOS Sharpe = 各 test fold Sharpe 均值;OOS ret = AvgTestReturnPct。
    // 用 OOS(非全期 in-sample)才能答「edge 在 OOS 是否集中在可辨識少數股」= 選股可行性。
    var rows = new List<(string sym, double sh, double ret, double dd, int n)>();
    foreach (var kv in dat)
        try
        {
            var w = LongShortBacktestEngine.RunWalkForward(st, kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = tf }, wfTrain, wfTest, wfStep, commission: gComm, slippagePct: gSlip);
            if (w.TotalFolds <= 0) continue;
            var fsh = w.Folds.Where(f => f.Test != null).Select(f => (double)f.Test!.SharpeRatio).ToList();
            if (fsh.Count == 0) continue;
            rows.Add((kv.Key, fsh.Average(), (double)w.AvgTestReturnPct, (double)w.WorstTestDdPct, w.TotalFolds));
        }
        catch { }
    if (rows.Count == 0) { Console.WriteLine("無資料"); return; }
    rows = rows.OrderByDescending(r => r.sh).ToList();
    Console.WriteLine($"  {"symbol",-12}{"OOS-Sh",8}{"OOSret%",9}{"worstDD%",9}{"folds",7}");
    foreach (var r in rows) Console.WriteLine($"  {r.sym,-12}{r.sh,8:F2}{r.ret,9:F1}{r.dd,9:F0}{r.n,7}");
    var shs = rows.Select(r => r.sh).OrderBy(x => x).ToList();
    int N = shs.Count;
    double mean = shs.Average();
    double median = N % 2 == 1 ? shs[N / 2] : (shs[N / 2 - 1] + shs[N / 2]) / 2;
    double sd = Math.Sqrt(shs.Select(x => (x - mean) * (x - mean)).Sum() / Math.Max(1, N - 1));
    int posCnt = shs.Count(x => x > 0), strongCnt = shs.Count(x => x > 0.5), veryStrong = shs.Count(x => x > 1.0);
    Console.WriteLine($"\n  === 離散度統計({N} 檔)===");
    Console.WriteLine($"  Sharpe 均值 {mean:F2} · 中位 {median:F2} · 標準差 {sd:F2} · 全距 [{shs.First():F2}, {shs.Last():F2}]");
    Console.WriteLine($"  Sharpe>0: {posCnt}/{N} ({posCnt * 100.0 / N:F0}%) · >0.5: {strongCnt} 檔 · >1.0: {veryStrong} 檔");
    int strongSubset = strongCnt;
    Console.WriteLine($"\n  === 離散度判讀 ===");
    Console.WriteLine($"  Sharpe>0.5 的 {strongSubset} 檔平均 = {(strongSubset > 0 ? rows.Where(r => r.sh > 0.5).Average(r => r.sh) : 0):F2}(vs 全宇宙 {mean:F2})→ 有可剝離的強子集");

    // ── 殺手鐧:按波動選股 vs 隨機選股(OOS Sharpe)──
    // 波動 = 日報酬年化 std(結構性、跨年持續高 → 近似 ex-ante;嚴格版用 trailing/rebalance、此為一階檢定)
    var vol = new Dictionary<string, double>();
    foreach (var kv in dat)
    {
        var b = kv.Value; var rr = new List<double>();
        for (int i = 1; i < b.Count; i++) { double pv = (double)b[i - 1].Close; if (pv > 0) rr.Add((double)(b[i].Close - b[i - 1].Close) / pv); }
        if (rr.Count > 20) { double m = rr.Average(); vol[kv.Key] = Math.Sqrt(rr.Select(x => (x - m) * (x - m)).Sum() / rr.Count) * Math.Sqrt(252); }
    }
    var wv = rows.Where(r => vol.ContainsKey(r.sym)).Select(r => (r.sym, r.sh, v: vol[r.sym])).ToList();
    int K = Math.Max(5, wv.Count / 3);
    var byVol = wv.OrderByDescending(x => x.v).ToList();
    double topKsh = byVol.Take(K).Average(x => x.sh);
    double botKsh = byVol.Skip(Math.Max(0, byVol.Count - K)).Average(x => x.sh);
    double uniSh = wv.Average(x => x.sh);
    var rng = new Random(42); int M = 5000; var rm = new List<double>();
    var poolSh = wv.Select(x => x.sh).ToList();
    for (int b = 0; b < M; b++) { double s = 0; for (int j = 0; j < K; j++) s += poolSh[rng.Next(poolSh.Count)]; rm.Add(s / K); }
    double randMean = rm.Average();
    double pct = rm.Count(x => x < topKsh) * 100.0 / M;
    Console.WriteLine($"\n  === 殺手鐧:波動選股 vs 隨機(top/bottom-{K} by 年化波動;OOS Sharpe)===");
    Console.WriteLine($"  高波動 top-{K}: {topKsh:F2}   低波動 bottom-{K}: {botKsh:F2}   全宇宙: {uniSh:F2}   隨機-{K}均值: {randMean:F2}");
    Console.WriteLine($"  高波動選股在隨機分布的百分位: {pct:F0}%（>95% = 顯著贏隨機、非運氣）");
    Console.WriteLine($"  高波動 top-{K} 成員: {string.Join(",", byVol.Take(K).Select(x => x.sym.Replace(".TW", "")))}");
    bool win = pct >= 95 && topKsh > uniSh + 0.05 && topKsh > botKsh + 0.1;
    Console.WriteLine($"\n  判讀:{(win ? $"✅ 波動選股顯著贏隨機(百分位{pct:F0}%)+贏全宇宙 → 選股是真的、TW 可救、值得建選股層" : "⚠️ 沒顯著贏隨機 → 波動選股是運氣/幻覺 → TW 放生、專心美股/crypto")}");
    Console.WriteLine($"  (caveat:波動用全期算=輕微 lookahead；過關才上嚴格版 trailing-vol per rebalance)");

    // ── Step 4:資金流(三大法人外資)× 波動 雙層選股(使用者原始 2 層論點)──
    if (args.Contains("--with-flow"))
    {
        Console.WriteLine($"\n  === Step 4:資金流 × 波動 選股(--with-flow)===");
        // 讀 curl 預抓的快取(C# HttpClient 對 TWSE 不穩 → 改外部 curl 可靠預抓、這裡只讀)
        var (flowSum, tradingDays) = ToolsShared.TwInstFlowCache.SumAllCached();
        Console.WriteLine($"  讀快取 T86:{tradingDays} 交易日有資料、{flowSum.Count} 檔有外資流");
        if (tradingDays < 5) { Console.WriteLine("  ⚠ 快取 T86 不足(<5 交易日)→ 先用 curl 預抓到 ~/.cache/brick4agent/twflow/、再跑"); return; }
        var flowScore = new Dictionary<string, double>();
        foreach (var r in wv)
        {
            var code = r.sym.Replace(".TW", "");
            if (!flowSum.TryGetValue(code, out var fs)) continue;
            double avgVol = dat[r.sym].Count > 0 ? (double)dat[r.sym].Average(x => x.Volume) : 0;
            if (avgVol > 0) flowScore[r.sym] = fs / avgVol;   // 累計外資淨股 / 平均日量 = 吸籌強度(跨股可比)
        }
        var wvf = wv.Where(r => flowScore.ContainsKey(r.sym)).Select(r => (r.sym, r.sh, v: r.v, f: flowScore[r.sym])).ToList();
        if (wvf.Count < 8) { Console.WriteLine($"  ⚠ 有資金流資料的股不足({wvf.Count})、跳過"); return; }
        int Kf = Math.Max(5, wvf.Count / 3);
        double volSh = wvf.OrderByDescending(x => x.v).Take(Kf).Average(x => x.sh);
        double flowShv = wvf.OrderByDescending(x => x.f).Take(Kf).Average(x => x.sh);
        var volRank = wvf.OrderBy(x => x.v).Select((x, i) => (x.sym, r: i)).ToDictionary(z => z.sym, z => z.r);
        var flowRank = wvf.OrderBy(x => x.f).Select((x, i) => (x.sym, r: i)).ToDictionary(z => z.sym, z => z.r);
        double combSh = wvf.OrderByDescending(x => volRank[x.sym] + flowRank[x.sym]).Take(Kf).Average(x => x.sh);
        double uniF = wvf.Average(x => x.sh);
        var poolF = wvf.Select(x => x.sh).ToList(); var rmF = new List<double>();
        for (int b = 0; b < 5000; b++) { double s = 0; for (int j = 0; j < Kf; j++) s += poolF[rng.Next(poolF.Count)]; rmF.Add(s / Kf); }
        double randF = rmF.Average();
        double pV = rmF.Count(x => x < volSh) * 100.0 / 5000, pF = rmF.Count(x => x < flowShv) * 100.0 / 5000, pC = rmF.Count(x => x < combSh) * 100.0 / 5000;
        Console.WriteLine($"  (有資金流 {wvf.Count} 檔、top-{Kf})OOS Sharpe:");
        Console.WriteLine($"    純波動:          {volSh,5:F2}  (隨機百分位 {pV:F0}%)");
        Console.WriteLine($"    純資金流:        {flowShv,5:F2}  (百分位 {pF:F0}%)");
        Console.WriteLine($"    波動+資金流組合: {combSh,5:F2}  (百分位 {pC:F0}%)");
        Console.WriteLine($"    全宇宙 {uniF:F2} · 隨機 {randF:F2}");
        bool flowAdds = combSh > volSh + 0.05;
        Console.WriteLine($"\n  判讀:{(flowAdds ? $"✅ 資金流加在波動上「有」加分(組合 {combSh:F2} > 純波動 {volSh:F2})→ 你原始雙層論點成立" : $"⚠️ 資金流「沒」加分(組合 {combSh:F2} ≤ 純波動 {volSh:F2})→ 波動已抓到主因、資金流冗餘、雙層不值得")}");
        Console.WriteLine($"  (caveat:flow_score 全期靜態 + 週採樣 = 輕微 lookahead;過關才上 ex-ante trailing-flow per rebalance)");
    }
}

// harmonic regime 分析:把 harmonic 的每個 OOS test fold 按市況(波動/趨勢度ER/方向)分桶,看它在哪種市況強/弱。
// 反轉策略理論:盤整/高波動強、強趨勢弱。量化確認 → 部署時可依市況調倉 + 早期察覺衰退。
async Task RunRegime()
{
    var harm = strats.FirstOrDefault(s => s.name == "harm_prz_scan10_widepz").s;
    if (harm == null) { Console.WriteLine("缺 harmonic"); return; }
    Console.WriteLine($"=== harmonic regime 分析(harm_prz_scan10_widepz、各 OOS fold 按市況分桶、cost {costBps}bps)===\n");
    var dat = new Dictionary<string, List<BarData>>();
    foreach (var sym in symbols) { try { var b = await Fetch(sym); if (b.Count >= 400) dat[sym] = b; } catch { } }
    Console.WriteLine($"  宇宙 {dat.Count}/{symbols.Length}\n");
    var folds = new List<(double ret, double vol, double dir, double er, double tvol, double ter)>();
    foreach (var kv in dat)
    {
        var b = kv.Value;
        try
        {
            var w = LongShortBacktestEngine.RunWalkForward(harm, b, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, 250, 90, 60, commission: gComm, slippagePct: gSlip);
            var fl = w.Folds.Where(f => f.Test != null).ToList();
            for (int f = 0; f < fl.Count; f++)
            {
                int s0 = f * 60 + 250, s1 = Math.Min(b.Count, s0 + 90);
                if (s0 < 1 || s1 - s0 < 20) continue;
                var rr = new List<double>();
                for (int i = s0 + 1; i < s1; i++) { double pv = (double)b[i - 1].Close; if (pv > 0) rr.Add((double)(b[i].Close - b[i - 1].Close) / pv); }
                if (rr.Count < 15) continue;
                double m = rr.Average(); double vol = Math.Sqrt(rr.Select(x => (x - m) * (x - m)).Sum() / rr.Count) * Math.Sqrt(252);
                double dir = (double)(b[s1 - 1].Close - b[s0].Close) / (double)b[s0].Close;
                double net = Math.Abs((double)(b[s1 - 1].Close - b[s0].Close)); double tot = 0; for (int i = s0 + 1; i < s1; i++) tot += Math.Abs((double)(b[i].Close - b[i - 1].Close)); double er = tot > 0 ? net / tot : 0;
                // train 窗 regime(ex-ante、用於 sizing:用測試窗「之前」的市況決定倉位、無 lookahead)
                int t0 = f * 60, t1 = Math.Min(b.Count, f * 60 + 250);
                var tr = new List<double>();
                for (int i = t0 + 1; i < t1; i++) { double pv = (double)b[i - 1].Close; if (pv > 0) tr.Add((double)(b[i].Close - b[i - 1].Close) / pv); }
                double tm = tr.Count > 0 ? tr.Average() : 0; double tvol = tr.Count > 1 ? Math.Sqrt(tr.Select(x => (x - tm) * (x - tm)).Sum() / tr.Count) * Math.Sqrt(252) : 0;
                double tnet = Math.Abs((double)(b[t1 - 1].Close - b[t0].Close)); double ttot = 0; for (int i = t0 + 1; i < t1; i++) ttot += Math.Abs((double)(b[i].Close - b[i - 1].Close)); double ter = ttot > 0 ? tnet / ttot : 0;
                folds.Add(((double)fl[f].Test!.TotalReturnPct, vol, dir, er, tvol, ter));
            }
        }
        catch { }
    }
    if (folds.Count < 20) { Console.WriteLine("fold 不足"); return; }
    Console.WriteLine($"  總 {folds.Count} folds(跨幣 OOS 90日 test 窗)。每 fold = 那段市況下 harmonic 的 OOS 報酬。\n");
    void Bucket(string label, Func<(double ret, double vol, double dir, double er, double tvol, double ter), bool> hi, string hiName, string loName)
    {
        var H = folds.Where(hi).Select(f => f.ret).ToList(); var Lo = folds.Where(f => !hi(f)).Select(f => f.ret).ToList();
        double am(List<double> l) => l.Count > 0 ? l.Average() : 0; double wr(List<double> l) => l.Count > 0 ? l.Count(x => x > 0) * 100.0 / l.Count : 0;
        Console.WriteLine($"  {label,-12} {hiName,-10} avg {am(H),6:F1}% 勝{wr(H),3:F0}% (n{H.Count})  │  {loName,-10} avg {am(Lo),6:F1}% 勝{wr(Lo),3:F0}% (n{Lo.Count})");
    }
    double volMed = folds.Select(f => f.vol).OrderBy(x => x).ElementAt(folds.Count / 2);
    double erMed = folds.Select(f => f.er).OrderBy(x => x).ElementAt(folds.Count / 2);
    Bucket("波動", f => f.vol >= volMed, "高波動", "低波動");
    Bucket("趨勢度ER", f => f.er >= erMed, "趨勢(高ER)", "盤整(低ER)");
    Bucket("方向", f => f.dir >= 0, "上漲窗", "下跌窗");
    Console.WriteLine($"\n  (門檻:vol中位={volMed:F2} ER中位={erMed:F2})");
    Console.WriteLine($"  判讀:harmonic=反轉策略 → 預期「盤整(低ER)/高波動」強、「強趨勢(高ER)」弱。看上面數據確認 + 量化差距。");

    // === regime-aware sizing:ex-ante train窗 regime 決定倉位、平均曝險正規化到 1.0(= 重分配、非加槓桿)===
    double tvolMed = folds.Select(f => f.tvol).OrderBy(x => x).ElementAt(folds.Count / 2);
    double terMed = folds.Select(f => f.ter).OrderBy(x => x).ElementAt(folds.Count / 2);
    var mults = folds.Select(f => (f.tvol >= tvolMed && f.ter < terMed) ? 1.5 : (f.tvol < tvolMed && f.ter >= terMed) ? 0.5 : 1.0).ToList();
    double mAvg = mults.Average();
    var retsL = folds.Select(f => f.ret).ToList();
    var sizedL = new List<double>(); for (int i = 0; i < folds.Count; i++) sizedL.Add(folds[i].ret * mults[i] / mAvg);
    double ShF(List<double> l) { double mm = l.Average(); double sd = Math.Sqrt(l.Select(x => (x - mm) * (x - mm)).Sum() / l.Count); return sd > 0 ? mm / sd * Math.Sqrt(252.0 / 90) : 0; }
    Console.WriteLine($"\n  === regime-aware sizing(ex-ante train窗 regime 調倉、平均曝險正規化到1.0、紀律=非槓桿)===");
    Console.WriteLine($"  固定倉位:   每fold avg {retsL.Average(),6:F1}% · Sharpe {ShF(retsL):F2}");
    Console.WriteLine($"  regime調倉: 每fold avg {sizedL.Average(),6:F1}% · Sharpe {ShF(sizedL):F2}(train甜蜜點1.5x/弱區0.5x、均1.0x)");
    bool sizeWin = ShF(sizedL) > ShF(retsL) + 0.05 && sizedL.Average() > retsL.Average();
    Console.WriteLine($"  判讀(sizing):{(sizeWin ? "✅ regime調倉贏固定(同曝險下用外生regime擇時加分)" : "⚠️ regime調倉沒明顯贏固定 → 固定倉位是對的(連外生regime都打不贏、別調)")}");
}

// 跨市場 lead-lag:A 今天報酬 vs B 明天報酬的相關。顯著 = A 領先 B、可用 A 預測 B(資訊流時差)。
// 對照同日相關 = lead-lag 要比同日「多出來」才是真領先(否則只是同步共動)。
async Task RunLeadLag()
{
    Console.WriteLine("=== 跨市場 lead-lag(A[t] 報酬 預測 B[t+1] 嗎;對照同日相關)===\n");
    var reps = new (string nm, string sym, bool yahoo)[]
    {
        ("BTC", "BTCUSDT", false), ("ETH", "ETHUSDT", false),
        ("US_SPY", "SPY", true), ("Nasdaq", "QQQ", true),
        ("Gold", "GC=F", true), ("Oil", "CL=F", true),
        ("EUR", "EURUSD=X", true), ("JPY", "USDJPY=X", true),
    };
    var ret = new Dictionary<string, SortedDictionary<DateTime, double>>();
    foreach (var (nm, sym, yahoo) in reps)
    {
        List<BarData> bars;
        try { bars = yahoo ? await ToolsShared.StockBarCache.FetchOrLoad(sym, "1d", barsLimit) : await ToolsShared.KlineCache.FetchOrLoad(sym, "1d", barsLimit); }
        catch { continue; }
        if (bars.Count < 200) continue;
        var sd = new SortedDictionary<DateTime, double>();
        for (int i = 1; i < bars.Count; i++) { double pv = (double)bars[i - 1].Close; if (pv > 0) sd[bars[i].OpenTime.Date] = (double)(bars[i].Close - bars[i - 1].Close) / pv; }
        if (sd.Count >= 200) { ret[nm] = sd; Console.WriteLine($"  {nm,-10} {sd.Count} 日"); }
    }
    var names = ret.Keys.ToList();
    double Corr(List<double> xs, List<double> ys)
    {
        if (xs.Count < 50) return double.NaN;
        double mx = xs.Average(), my = ys.Average(), sxy = 0, sx = 0, sy = 0;
        for (int i = 0; i < xs.Count; i++) { sxy += (xs[i] - mx) * (ys[i] - my); sx += (xs[i] - mx) * (xs[i] - mx); sy += (ys[i] - my) * (ys[i] - my); }
        return (sx > 0 && sy > 0) ? sxy / Math.Sqrt(sx * sy) : double.NaN;
    }
    // lag>=1:A[t] vs B 在 A 之後 1-4 日內的下一個交易日;lag=0:同日
    double XCorr(string a, string b, bool lead)
    {
        var da = ret[a]; var db = ret[b]; var xs = new List<double>(); var ys = new List<double>();
        foreach (var kv in da)
        {
            if (!lead) { if (db.TryGetValue(kv.Key, out var v0)) { xs.Add(kv.Value); ys.Add(v0); } continue; }
            for (int d = 1; d <= 4; d++) if (db.TryGetValue(kv.Key.AddDays(d), out var v)) { xs.Add(kv.Value); ys.Add(v); break; }
        }
        return Corr(xs, ys);
    }
    Console.WriteLine("\n=== lead-lag 相關(列=領先A、行=滯後B;A[t] vs B[t+1])===");
    Console.WriteLine("  " + new string(' ', 10) + string.Join("", names.Select(n => $"{n,8}".Substring(0, Math.Min(8, $"{n,8}".Length)))));
    foreach (var a in names)
        Console.WriteLine($"  {a,-10}" + string.Join("", names.Select(b => { var c = a == b ? double.NaN : XCorr(a, b, true); return double.IsNaN(c) ? $"{"-",8}" : $"{c,8:F2}"; })));
    Console.WriteLine("\n=== 同日相關(對照;lead-lag 要比這多出來才是真領先)===");
    foreach (var a in names)
        Console.WriteLine($"  {a,-10}" + string.Join("", names.Select(b => { var c = a == b ? double.NaN : XCorr(a, b, false); return double.IsNaN(c) ? $"{"-",8}" : $"{c,8:F2}"; })));
    Console.WriteLine("\n  判讀:|lead-lag| > 0.1 且明顯 > 同日 = 真領先(可交易);多半 < 0.1(日頻宏觀已被套利)。");
}

// 跨市場諧波分散:同一條 harmonic 跑 crypto/美股/台股 → 各市場日報酬序列 → 相關矩陣 + 等權組合。
// 低相關 = 真分散(市場/regime 不同);組合 Sharpe 升/DD 降 = 跨市場分散有效。對齊用共同交易日。
async Task RunXMarket()
{
    var harm = strats.FirstOrDefault(s => s.name == "harm_prz_scan10_widepz").s;
    var ditr = strats.FirstOrDefault(s => s.name == "di_trend_ls").s;
    if (harm == null || ditr == null) { Console.WriteLine("找不到 harm_prz_scan10_widepz / di_trend_ls"); return; }
    Console.WriteLine("=== 跨市場分散 --xmarket(crypto/美股/台股=harmonic、FX=di_trend_ls;各市場日報酬 + 相關)===\n");
    var markets = new (string name, string[] syms, bool yahoo)[]
    {
        ("crypto", new[]{"BTCUSDT","ETHUSDT","SOLUSDT","BNBUSDT","XRPUSDT","ADAUSDT","DOGEUSDT","AVAXUSDT","LINKUSDT","LTCUSDT","DOTUSDT","ATOMUSDT","TRXUSDT","UNIUSDT","NEARUSDT","APTUSDT","ARBUSDT","OPUSDT","SUIUSDT","INJUSDT"}, false),
        ("美股", new[]{"AAPL","MSFT","GOOGL","AMZN","NVDA","AMD","AVGO","CRM","INTC","META","ORCL","ADBE","CSCO","QCOM","TXN","NFLX","TSLA","IBM","NOW","AMAT","MU","INTU","PANW","JPM","BAC","V","MA","GS","MS","WFC","C","AXP","BLK","SCHW","UNH","JNJ","LLY","ABBV","MRK","PFE","TMO","ABT","DHR","XOM","CVX","COP","WMT","KO","PG","DIS","CAT","BA","COST","HD","MCD","NKE","PEP","GE","HON","LMT","RTX"}, true),
        ("台股", new[]{"2330.TW","2454.TW","2303.TW","3711.TW","2379.TW","3034.TW","3008.TW","2408.TW","3443.TW","6415.TW","2317.TW","2308.TW","2382.TW","2357.TW","2376.TW","4938.TW","6669.TW","2474.TW","2327.TW","3037.TW","2345.TW","2409.TW","3481.TW","3017.TW","2881.TW","2882.TW","2891.TW","2886.TW","2884.TW","2885.TW","2892.TW","5880.TW","2880.TW","2890.TW","2412.TW","3045.TW","4904.TW","1301.TW","1303.TW","1326.TW","2002.TW","1216.TW","2207.TW","2603.TW","2609.TW","2615.TW","2618.TW","2105.TW","1402.TW","2912.TW","0050.TW","0056.TW","6488.TW","3529.TW","3035.TW","6285.TW","2449.TW","6239.TW","2344.TW","5269.TW","3661.TW","8046.TW","4958.TW","3533.TW","2492.TW","2383.TW","2360.TW","2347.TW","3702.TW","2353.TW","1102.TW","9910.TW","9904.TW","2915.TW","1210.TW","1722.TW","1605.TW","9921.TW","9914.TW","2887.TW","2888.TW","2883.TW","5876.TW","2542.TW","8454.TW","2548.TW"}, true),
        ("商品", new[]{"GC=F","SI=F","HG=F","PL=F","PA=F","CL=F","BZ=F","NG=F","RB=F","HO=F","ZC=F","ZW=F","ZS=F","KC=F","SB=F","CC=F","CT=F","ES=F","NQ=F","YM=F","RTY=F","ZN=F","ZB=F"}, true),
    };
    (double sh, double dd, double ret) StatsOf(double[] r)
    {
        if (r.Length < 5) return (0, 0, 0);
        double mean = r.Average(), sd = Math.Sqrt(r.Select(x => (x - mean) * (x - mean)).Sum() / Math.Max(1, r.Length - 1));
        double sh = sd > 1e-12 ? mean / sd * Math.Sqrt(252) : 0;
        double eq = 1, peak = 1, mdd = 0;
        foreach (var x in r) { eq *= 1 + x; if (eq > peak) peak = eq; var d2 = peak > 0 ? (peak - eq) / peak : 0; if (d2 > mdd) mdd = d2; }
        return (sh, mdd * 100, (eq - 1) * 100);
    }
    var mret = new Dictionary<string, SortedDictionary<DateTime, double>>();
    var mstats = new Dictionary<string, (double sh, double dd, double ret, int n)>();
    var mSymRet = new Dictionary<string, Dictionary<string, SortedDictionary<DateTime, double>>>();   // 市場→標的→日報酬(真實模擬用)
    foreach (var (mn, syms, yahoo) in markets)
    {
        var perDay = new Dictionary<DateTime, List<double>>();
        var perSym = new Dictionary<string, SortedDictionary<DateTime, double>>();
        int nsym = 0;
        foreach (var sym in syms)
        {
            List<BarData> bars;
            try { bars = yahoo ? await ToolsShared.StockBarCache.FetchOrLoad(sym, "1d", barsLimit) : await ToolsShared.KlineCache.FetchOrLoad(sym, "1d", barsLimit); }
            catch { continue; }
            if (bars.Count < 200) continue;
            try
            {
                var bt = LongShortBacktestEngine.Run(harm, bars, new StrategyConfig { Symbol = sym, Interval = "1d" }, commission: gComm, slippagePct: gSlip);
                var eq = bt.EquityCurve;
                var sd = new SortedDictionary<DateTime, double>();
                for (int i = 1; i < eq.Count; i++)
                {
                    double pv = (double)eq[i - 1].Value; if (pv <= 0) continue;
                    double r = (double)(eq[i].Value - eq[i - 1].Value) / pv;
                    var d = eq[i].Date.Date;
                    if (!perDay.TryGetValue(d, out var l)) perDay[d] = l = new();
                    l.Add(r);
                    sd[d] = r;
                }
                if (sd.Count > 20) perSym[sym] = sd;
                nsym++;
            }
            catch { }
        }
        var daily = new SortedDictionary<DateTime, double>();
        foreach (var kv in perDay) if (kv.Value.Count > 0) daily[kv.Key] = kv.Value.Average();
        mret[mn] = daily;
        mSymRet[mn] = perSym;
        var st = StatsOf(daily.Values.ToArray());
        mstats[mn] = (st.sh, st.dd, st.ret, nsym);
        Console.WriteLine($"  {mn,-8} {nsym,2} 檔 · {daily.Count,4} 交易日 · Sharpe {st.sh,5:F2} · maxDD {st.dd,3:F0}% · 全期 {st.ret,6:F0}%");
    }
    // ── 加 di_trend_ls FX 進相關矩陣(驗證它跟 harmonic 去相關 = 真分散腿)──
    {
        var fxPairs = new[] { "EURUSD=X", "USDJPY=X", "GBPUSD=X", "USDCHF=X", "AUDUSD=X", "USDCAD=X", "NZDUSD=X", "EURJPY=X", "GBPJPY=X", "EURGBP=X", "AUDJPY=X" };
        var perDay = new Dictionary<DateTime, List<double>>(); var perSym2 = new Dictionary<string, SortedDictionary<DateTime, double>>(); int nsym = 0;
        foreach (var sym in fxPairs)
        {
            List<BarData> bars;
            try { bars = await ToolsShared.StockBarCache.FetchOrLoad(sym, "1d", barsLimit); } catch { continue; }
            if (bars.Count < 200) continue;
            try
            {
                var bt = LongShortBacktestEngine.Run(ditr, bars, new StrategyConfig { Symbol = sym, Interval = "1d" }, commission: gComm, slippagePct: gSlip);
                var eq = bt.EquityCurve;
                var sd = new SortedDictionary<DateTime, double>();
                for (int i = 1; i < eq.Count; i++) { double pv = (double)eq[i - 1].Value; if (pv <= 0) continue; double r = (double)(eq[i].Value - eq[i - 1].Value) / pv; var d = eq[i].Date.Date; if (!perDay.TryGetValue(d, out var l)) perDay[d] = l = new(); l.Add(r); sd[d] = r; }
                if (sd.Count > 20) perSym2[sym] = sd;
                nsym++;
            }
            catch { }
        }
        var daily = new SortedDictionary<DateTime, double>();
        foreach (var kv in perDay) if (kv.Value.Count > 0) daily[kv.Key] = kv.Value.Average();
        mret["FX-ditrend"] = daily;
        mSymRet["FX-ditrend"] = perSym2;
        var st2 = StatsOf(daily.Values.ToArray());
        mstats["FX-ditrend"] = (st2.sh, st2.dd, st2.ret, nsym);
        Console.WriteLine($"  {"FX-ditrend",-8} {nsym,2} 對 · {daily.Count,4} 交易日 · Sharpe {st2.sh,5:F2} · maxDD {st2.dd,3:F0}% · 全期 {st2.ret,6:F0}%");
    }
    // 結構性 alpha sleeve(crypto、非價格機制)— 測是否跟 harmonic 去相關 = 真機制分散
    bool withStruct = args.Contains("--with-structural");
    if (withStruct)
    {
        var structStrats = new (string nm, IStrategy st)[]
        {
            ("crypto-funding", strats.FirstOrDefault(s => s.name == "funding_momentum_ls").s),   // 你最強的結構訊號 t=5.93
            ("crypto-retail", strats.FirstOrDefault(s => s.name == "retail_ls_delta_contrarian").s),
            ("crypto-oi", strats.FirstOrDefault(s => s.name == "oi_momentum_ls").s),
        };
        var cu = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "ADAUSDT", "DOGEUSDT", "AVAXUSDT", "LINKUSDT", "LTCUSDT" };
        foreach (var (nm, sst) in structStrats)
        {
            if (sst == null) continue;
            var pd = new Dictionary<DateTime, List<double>>(); var ps = new Dictionary<string, SortedDictionary<DateTime, double>>(); int ns = 0;
            foreach (var sym in cu)
            {
                List<BarData> bars;
                try { bars = await ToolsShared.KlineCache.FetchOrLoad(sym, "1d", barsLimit); } catch { continue; }
                if (bars.Count < 200) continue;
                try { await ToolsShared.OiMetricsCache.InjectInto(bars, sym, "1d"); } catch { }
                try { await ToolsShared.FundingCache.InjectInto(bars, sym, "1d"); } catch { }   // funding 訊號(funding_momentum 用)
                try
                {
                    var bt = LongShortBacktestEngine.Run(sst, bars, new StrategyConfig { Symbol = sym, Interval = "1d" }, commission: gComm, slippagePct: gSlip);
                    var eq = bt.EquityCurve; var sd = new SortedDictionary<DateTime, double>();
                    for (int i = 1; i < eq.Count; i++) { double pv = (double)eq[i - 1].Value; if (pv <= 0) continue; double r = (double)(eq[i].Value - eq[i - 1].Value) / pv; var d = eq[i].Date.Date; if (!pd.TryGetValue(d, out var l)) pd[d] = l = new(); l.Add(r); sd[d] = r; }
                    if (sd.Count > 20) ps[sym] = sd; ns++;
                }
                catch { }
            }
            var dd2 = new SortedDictionary<DateTime, double>();
            foreach (var kv in pd) if (kv.Value.Count > 0) dd2[kv.Key] = kv.Value.Average();
            mret[nm] = dd2; mSymRet[nm] = ps;
            var stt = StatsOf(dd2.Values.ToArray()); mstats[nm] = (stt.sh, stt.dd, stt.ret, ns);
            Console.WriteLine($"  {nm,-12} {ns,2} 檔 · {dd2.Count,4} 交易日 · Sharpe {stt.sh,5:F2} · maxDD {stt.dd,3:F0}% · 全期 {stt.ret,6:F0}%");
        }
        // funding carry sleeve(delta-neutral 短perp 收 funding = 收益型、非方向;報酬 ≈ +fundingRate)
        {
            var pdc = new Dictionary<DateTime, List<double>>(); int nsc = 0;
            foreach (var sym in cu)
            {
                List<BarData> bars;
                try { bars = await ToolsShared.KlineCache.FetchOrLoad(sym, "1d", barsLimit); } catch { continue; }
                if (bars.Count < 200) continue;
                try { await ToolsShared.FundingCache.InjectInto(bars, sym, "1d"); } catch { continue; }
                bool any = false;
                foreach (var b in bars) if (b.FundingRate is decimal f && f != 0m) { var d = b.OpenTime.Date; if (!pdc.TryGetValue(d, out var l)) pdc[d] = l = new(); l.Add((double)f); any = true; }
                if (any) nsc++;
            }
            var ddc = new SortedDictionary<DateTime, double>();
            foreach (var kv in pdc) if (kv.Value.Count > 0) ddc[kv.Key] = kv.Value.Average();
            if (ddc.Count > 20) { mret["crypto-carry"] = ddc; var sc = StatsOf(ddc.Values.ToArray()); mstats["crypto-carry"] = (sc.sh, sc.dd, sc.ret, nsc); Console.WriteLine($"  {"crypto-carry",-12} {nsc,2} 檔 · {ddc.Count,4} 交易日 · Sharpe {sc.sh,5:F2} · maxDD {sc.dd,3:F0}% · 全期 {sc.ret,6:F0}%"); }
        }
    }
    var mnames = (withStruct
        ? markets.Select(m => m.name).Append("FX-ditrend").Append("crypto-funding").Append("crypto-retail").Append("crypto-oi").Append("crypto-carry")
        : markets.Select(m => m.name).Append("FX-ditrend")).Where(n => mret.ContainsKey(n)).ToArray();
    double PairCorr(string a, string b)
    {
        var da = mret[a]; var db = mret[b]; var xs = new List<double>(); var ys = new List<double>();
        foreach (var kv in da) if (db.TryGetValue(kv.Key, out var v)) { xs.Add(kv.Value); ys.Add(v); }
        if (xs.Count < 10) return double.NaN;
        double mx = xs.Average(), my = ys.Average(), sxy = 0, sx = 0, sy = 0;
        for (int i = 0; i < xs.Count; i++) { sxy += (xs[i] - mx) * (ys[i] - my); sx += (xs[i] - mx) * (xs[i] - mx); sy += (ys[i] - my) * (ys[i] - my); }
        return (sx > 0 && sy > 0) ? sxy / Math.Sqrt(sx * sy) : double.NaN;
    }
    Console.WriteLine("\n=== 跨市場 harmonic 日報酬 相關矩陣(對齊共同交易日)===");
    Console.WriteLine("  " + new string(' ', 8) + string.Join("", mnames.Select(n => $"{n,8}")));
    foreach (var a in mnames)
        Console.WriteLine($"  {a,-8}" + string.Join("", mnames.Select(b => { var c = a == b ? 1.0 : PairCorr(a, b); return double.IsNaN(c) ? $"{"n/a",8}" : $"{c,8:F2}"; })));
    // 等權跨市場組合(union 日期;缺市場當日 0 = 該市場休市/未交易、佔 1/N 權重的閒置)
    var allDates = mret.Values.SelectMany(d => d.Keys).Distinct().OrderBy(d => d).ToList();
    var comb = new List<double>();
    foreach (var d in allDates) { double s = 0; foreach (var mn in mnames) if (mret[mn].TryGetValue(d, out var v)) s += v; comb.Add(s / mnames.Length); }
    var cs = StatsOf(comb.ToArray());
    Console.WriteLine("\n=== 等權跨市場組合 vs 各單市場 ===");
    Console.WriteLine($"  {"配置",-12}{"Sharpe",9}{"maxDD%",9}{"全期報酬%",11}");
    foreach (var mn in mnames) Console.WriteLine($"  {mn,-12}{mstats[mn].sh,9:F2}{mstats[mn].dd,9:F0}{mstats[mn].ret,11:F0}");
    Console.WriteLine($"  {"跨市場等權",-12}{cs.sh,9:F2}{cs.dd,9:F0}{cs.ret,11:F0}");
    double bestSh = mnames.Max(mn => mstats[mn].sh), bestDd = mnames.Min(mn => mstats[mn].dd);
    Console.WriteLine($"  → 跨市場 Sharpe {cs.sh:F2} vs 最佳單市場 {bestSh:F2}({(bestSh > 0 ? (cs.sh / bestSh - 1) * 100 : 0):+0;-0}%);maxDD {cs.dd:F0}% vs 最佳單市場 {bestDd:F0}%");

    // 反波動率(風險平價)配重:weight ∝ 1/vol(等權會被高波動 crypto 主導、各市場貢獻等風險才對)
    var vols = mnames.ToDictionary(mn => mn, mn =>
    {
        var r = mret[mn].Values.ToArray();
        if (r.Length < 5) return 1.0;
        double mean = r.Average(); double sd = Math.Sqrt(r.Select(x => (x - mean) * (x - mean)).Sum() / Math.Max(1, r.Length - 1));
        return sd > 1e-9 ? sd : 1.0;
    });
    var invw = mnames.ToDictionary(mn => mn, mn => 1.0 / vols[mn]);
    double invSum = invw.Values.Sum();
    foreach (var mn in mnames) invw[mn] /= invSum;
    var combIv = new List<double>();
    foreach (var d in allDates)
    {
        double s = 0, wsum = 0;
        foreach (var mn in mnames) if (mret[mn].TryGetValue(d, out var v)) { s += invw[mn] * v; wsum += invw[mn]; }
        combIv.Add(wsum > 0 ? s / wsum : 0);   // 當日只用有交易的市場、權重重新正規化
    }
    var csIv = StatsOf(combIv.ToArray());
    Console.WriteLine($"  {"跨市場反波動率",-10}{csIv.sh,9:F2}{csIv.dd,9:F0}{csIv.ret,11:F0}");
    Console.WriteLine($"  反波動率權重:{string.Join(" · ", mnames.Select(mn => $"{mn} {invw[mn]:P0}"))}");

    // 分散效益:合體(反波動率)vs 單 crypto(使用者核心市場)
    if (mstats.ContainsKey("crypto"))
    {
        var cr = mstats["crypto"];
        Console.WriteLine($"\n  === 分散效益:合體 vs 單 crypto(你的核心)===");
        Console.WriteLine($"  單 crypto:        Sharpe {cr.sh,5:F2} · maxDD {cr.dd,3:F0}%");
        Console.WriteLine($"  跨市場反波動率:   Sharpe {csIv.sh,5:F2} · maxDD {csIv.dd,3:F0}%");
        Console.WriteLine($"  → Sharpe {(cr.sh > 0 ? (csIv.sh / cr.sh - 1) * 100 : 0),0:+0;-0}% · maxDD {cr.dd - csIv.dd,0:+0;-0} 個百分點({(csIv.dd < cr.dd ? "更低 = 分散有效" : "沒降")})");
    }
    // 真實部署模擬:每市場只持 N 個並行倉(bootstrap 隨機 N 檔)→ 誠實可部署數字(非全標的平均灌水)
    int realN = 0;
    var rnArg = args.FirstOrDefault(a => a.StartsWith("--realistic-n="));
    if (rnArg != null) int.TryParse(rnArg.Substring(14), out realN);
    if (realN > 0)
    {
        var rng2 = new Random(123);
        const int BOOT = 300;
        Console.WriteLine($"\n=== 真實部署模擬:每市場持 {realN} 個並行倉(bootstrap {BOOT}、vs 全標的灌水版)===");
        Console.WriteLine($"  {"市場",-12}{"真實Sharpe",11}{"真實maxDD%",12}{"灌水Sh",9}{"灌水DD%",9}");
        var rPerMkt = new Dictionary<string, (double sh, double dd)>();
        SortedDictionary<DateTime, double> PickStream(Dictionary<string, SortedDictionary<DateTime, double>> sm, int n)
        {
            var pick = sm.Keys.OrderBy(_ => rng2.Next()).Take(Math.Min(n, sm.Count)).ToList();
            var sd = new SortedDictionary<DateTime, double>();
            foreach (var d in pick.SelectMany(s => sm[s].Keys).Distinct())
            { double s = 0; int c = 0; foreach (var sy in pick) if (sm[sy].TryGetValue(d, out var v)) { s += v; c++; } if (c > 0) sd[d] = s / c; }
            return sd;
        }
        foreach (var mn in mnames)
        {
            if (!mSymRet.TryGetValue(mn, out var sm) || sm.Count < 2) continue;
            var shs = new List<double>(); var dds = new List<double>();
            for (int b = 0; b < BOOT; b++) { var st = StatsOf(PickStream(sm, realN).Values.ToArray()); shs.Add(st.sh); dds.Add(st.dd); }
            rPerMkt[mn] = (shs.Average(), dds.Average());
            Console.WriteLine($"  {mn,-12}{rPerMkt[mn].sh,11:F2}{rPerMkt[mn].dd,12:F0}{mstats[mn].sh,9:F1}{mstats[mn].dd,9:F0}");
        }
        // 真實跨市場書:每市場各持 N 倉、反波動率配重、每 bootstrap 重抽
        var mk = rPerMkt.Keys.Where(k => mSymRet.ContainsKey(k) && mSymRet[k].Count >= 2).ToList();
        var bSh = new List<double>(); var bDd = new List<double>();
        for (int b = 0; b < BOOT; b++)
        {
            var streams = mk.ToDictionary(mn => mn, mn => PickStream(mSymRet[mn], realN));
            var vv = mk.ToDictionary(mn => mn, mn => { var r = streams[mn].Values.ToArray(); if (r.Length < 5) return 1.0; double m = r.Average(); double s2 = Math.Sqrt(r.Select(x => (x - m) * (x - m)).Sum() / Math.Max(1, r.Length - 1)); return s2 > 1e-9 ? s2 : 1.0; });
            var iw = mk.ToDictionary(mn => mn, mn => 1.0 / vv[mn]); double iws = iw.Values.Sum(); foreach (var mn in mk) iw[mn] /= iws;
            var alld = streams.Values.SelectMany(d => d.Keys).Distinct().OrderBy(d => d).ToList();
            var book = new double[alld.Count];
            for (int k = 0; k < alld.Count; k++) { double s = 0, w = 0; foreach (var mn in mk) if (streams[mn].TryGetValue(alld[k], out var v)) { s += iw[mn] * v; w += iw[mn]; } book[k] = w > 0 ? s / w : 0; }
            var bst = StatsOf(book); bSh.Add(bst.sh); bDd.Add(bst.dd);
        }
        Console.WriteLine($"\n  === 真實跨市場書(每市場 {realN} 倉、反波動率配重)vs 單 crypto ===");
        if (rPerMkt.ContainsKey("crypto")) Console.WriteLine($"  單 crypto({realN}倉):       Sharpe {rPerMkt["crypto"].sh,5:F2} · maxDD {rPerMkt["crypto"].dd,3:F0}%");
        Console.WriteLine($"  跨市場書({realN}倉/市場):  Sharpe {bSh.Average(),5:F2} · maxDD {bDd.Average(),3:F0}%");
        Console.WriteLine($"  → 這才是「真的下單會拿到」的誠實數字(分散仍把回撤壓低、但不是灌水的 1%)");
    }
    Console.WriteLine("\n  判讀:相關低(<0.3)= 真分散;組合 Sharpe ≥ 最佳單市場 且 DD 更低 = 跨市場分散有效、值得鋪。");
}

async Task RunAllocate()
{
    decimal EnvD(string k, decimal def) => decimal.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : def;
    bool EnvB(string k, bool def) => bool.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : def;
    string EnvS(string k, string def) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } s ? s : def;
    decimal targetVol = EnvD("ALLOC_TARGET_VOL_ANNUAL", 0.40m);  // 目標組合年化波動(40%、crypto 多策略合理)
    decimal maxExp    = EnvD("ALLOC_MAX_EXPOSURE", 3.0m);         // 整體曝險上限(對齊真錢 3x)
    double  capW      = (double)EnvD("ALLOC_MAX_WEIGHT", 0.35m);  // 單腿權重上限
    double  shrinkT   = (double)EnvD("ALLOC_SHRINK_TRADES", 100m);// 收縮基準 T*(OOS fold 數達此才完全信最佳化)
    // BTC 核心腿:固定幣種 BTC、用集成(多策略共識 + confidence-sizing 依共識大小下注)、比例拉高、獨立碳出。
    bool    btcCore   = EnvB("ALLOC_BTC_CORE", true);
    double  btcCoreW  = (double)EnvD("ALLOC_BTC_CORE_WEIGHT", 0.40m); // BTC 核心固定佔書比重(拉高)
    string  btcStrat  = EnvS("ALLOC_BTC_CORE_STRATEGY", "decorr4_ls");// 集成策略(多策略共識下注大小)
    // forward 證據:抓 broker 的實盤 per-strategy P&L、當回測之外的第二證據。
    // 實盤(≥min 筆)實際賠 → 否決該腿(回測過但實盤垮 = 過擬合/regime 破)。沒設 URL → 純回測(向後相容)。
    string  fwdUrl    = EnvS("ALLOC_FORWARD_URL", "");
    string  fwdFile   = EnvS("ALLOC_FORWARD_FILE", "");   // 本地 JSON(cron 用 docker exec curl 產出、繞過 loopback 守衛)
    int     fwdMin    = (int)EnvD("ALLOC_FORWARD_MIN_TRADES", 10m);

    Console.WriteLine("=== 配置引擎 --allocate ===");
    Console.WriteLine($"  參數:目標年化波動 {targetVol:P0} · 曝險上限 {maxExp:F1}x · 單腿上限 {capW:P0} · 收縮基準 T*={shrinkT:F0} folds");
    if (btcCore) Console.WriteLine($"  BTC 核心腿:開(固定 BTC、策略 {btcStrat} 多策略共識、佔書 {btcCoreW:P0}、獨立於衛星配置)");
    Console.WriteLine("  (env 可調:ALLOC_TARGET_VOL_ANNUAL / ALLOC_MAX_EXPOSURE / ALLOC_MAX_WEIGHT / ALLOC_SHRINK_TRADES / ALLOC_BTC_CORE[_WEIGHT/_STRATEGY])\n");

    // 1. 抓 1d 資料
    var dat = new Dictionary<string, List<BarData>>();
    foreach (var sym in symbols) { try { var b = await Fetch(sym); if (b.Count >= 400) dat[sym] = b; } catch { } }
    Console.WriteLine($"資料就緒:{dat.Count}/{symbols.Length} 檔 1d\n");

    // retail_ls / oi 策略需注入 RetailLongShortRatio / OpenInterest(--apply-retail-ls);否則整段 hold → 0 edge。
    // (主流程在 line ~689 注入、但 --allocate 在那之前就 return,故 RunAllocate 要自己注入)
    if (realRetailLs)
    {
        int inj = 0;
        foreach (var kv in dat)
            try { await ToolsShared.OiMetricsCache.InjectInto(kv.Value, kv.Key, "1d"); inj++; } catch { }
        Console.WriteLine($"📊 已注入 retail L/S + OI metrics:{inj}/{dat.Count} 檔(retail_ls / oi 策略才有信號)\n");
    }

    // forward 實盤證據(per-strategy:成交數 / 已實現 P&L / 勝率)
    var fwd = new Dictionary<string, (int n, decimal pnl, decimal wr)>(StringComparer.OrdinalIgnoreCase);
    if (!string.IsNullOrEmpty(fwdFile) || !string.IsNullOrEmpty(fwdUrl))
    {
        try
        {
            string fjson = !string.IsNullOrEmpty(fwdFile) && File.Exists(fwdFile)
                ? File.ReadAllText(fwdFile)
                : await http.GetStringAsync(fwdUrl);
            using var fdoc = JsonDocument.Parse(fjson);
            var root = fdoc.RootElement;
            var dataEl = root.TryGetProperty("data", out var de) ? de : root;   // 兼容 ApiResponse 包裝
            if (dataEl.TryGetProperty("strategies", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var s in arr.EnumerateArray())
                {
                    var nm = s.TryGetProperty("strategy", out var sn) ? sn.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(nm)) continue;
                    fwd[nm] = (
                        s.TryGetProperty("trades", out var tn) ? tn.GetInt32() : 0,
                        s.TryGetProperty("realized_pnl", out var pp) ? pp.GetDecimal() : 0m,
                        s.TryGetProperty("win_rate", out var ww) ? ww.GetDecimal() : 0m);
                }
            Console.WriteLine($"forward 實盤證據:{fwd.Count} 策略有成交;否決門檻 = 實盤 ≥{fwdMin} 筆且賠錢\n");
        }
        catch (Exception ex) { Console.WriteLine($"⚠ forward 抓取失敗、退回純回測:{ex.Message}\n"); }
    }
    if (dat.Count == 0) { Console.WriteLine("無資料、中止。"); return; }

    // 2. 每策略 → 選它的最佳幣(full-period Sharpe 最高)當部署腿;收集 sharpe/vol/curve/OOS folds
    var rngA = new Random(7);
    (decimal mean, decimal lo, decimal hi, double t) Boot(List<decimal> xs)
    {
        if (xs.Count < 5) return (xs.Count > 0 ? xs.Average() : 0, 0, 0, 0);
        double mean = (double)xs.Average();
        double sd = Math.Sqrt(xs.Select(x => ((double)x - mean) * ((double)x - mean)).Sum() / (xs.Count - 1));
        double se = sd / Math.Sqrt(xs.Count);
        double tStat = se > 0 ? mean / se : 0;
        var means = new double[2000];
        for (int b = 0; b < 2000; b++) { double sum = 0; for (int i = 0; i < xs.Count; i++) sum += (double)xs[rngA.Next(xs.Count)]; means[b] = sum / xs.Count; }
        Array.Sort(means);
        return ((decimal)mean, (decimal)means[50], (decimal)means[1950], tStat);
    }
    List<double> DailyRets(List<decimal> curve, int take)
    {
        var c = curve.Skip(Math.Max(0, curve.Count - take)).ToList();
        var r = new List<double>();
        for (int t = 1; t < c.Count; t++) r.Add(c[t - 1] > 0 ? (double)((c[t] - c[t - 1]) / c[t - 1]) : 0);
        return r;
    }
    double AnnVol(List<decimal> curve)
    {
        var r = DailyRets(curve, curve.Count);
        if (r.Count < 2) return 0;
        var a = r.Average();
        return Math.Sqrt(r.Select(x => (x - a) * (x - a)).Average()) * Math.Sqrt(252);
    }

    // 顯著性改用「跨幣 pooling」(每支策略池化所有幣的 OOS folds → fold 數 ×N倍、CI 才有統計力);
    // 但 pooling 會因幣高相關高估顯著 → 另加「廣度濾網」:≥60% 幣 OOS 為正,防單一暴漲幣灌假顯著。
    // 部署腿仍取「最佳幣」(Sharpe 最高);vol/相關/權重用該腿曲線。
    decimal breadthMin = EnvD("ALLOC_BREADTH_MIN", 0.60m);
    int minPoolFolds = (int)EnvD("ALLOC_MIN_POOL_FOLDS", 30m);
    var cands = new List<Cand>();
    foreach (var (name, s) in strats)
    {
        var perCoin = new Dictionary<string, (decimal sh, List<decimal> curve, decimal ret, decimal dd)>();
        var poolFolds = new List<decimal>();   // 跨幣池化的 OOS test-fold 報酬
        int coinsTested = 0, coinsPos = 0;     // 廣度:幾個幣 OOS 為正
        foreach (var kv in dat)
        {
            try
            {
                var bt = LongShortBacktestEngine.Run(s, kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, commission: gComm, slippagePct: gSlip);
                if (bt.TotalBars >= 100) perCoin[kv.Key] = (bt.SharpeRatio, bt.EquityCurve.Select(e => e.Value).ToList(), bt.TotalReturnPct, bt.MaxDrawdownPct);
            }
            catch { }
            try
            {
                var wf = LongShortBacktestEngine.RunWalkForward(s, kv.Value, new StrategyConfig { Symbol = kv.Key, Interval = "1d" }, 250, 90, 60, commission: gComm, slippagePct: gSlip);
                if (wf.TotalFolds == 0) continue;
                coinsTested++;
                if (wf.AvgTestReturnPct > 0) coinsPos++;
                foreach (var f in wf.Folds.Where(f => f.Test != null)) poolFolds.Add(f.Test!.TotalReturnPct);
            }
            catch { }
        }
        if (perCoin.Count == 0 || coinsTested == 0) continue;
        var (m, lo, hi, tstat) = Boot(poolFolds);
        decimal breadth = (decimal)coinsPos / coinsTested;
        var best = perCoin.OrderByDescending(p => p.Value.sh).First();
        cands.Add(new Cand(name, perCoin, poolFolds.Count, lo, tstat, breadth, best.Value.sh, best.Value.ret));
    }

    // 3. 入場閘:① pooled CI 下界>0(顯著)② 廣度 ≥60% 幣正(防單幣灌水)③ 最佳幣 Sharpe>0 + 報酬正 ④ pool fold 夠
    bool Pass(Cand c) => c.CiLo > 0m && c.Breadth >= breadthMin && c.BestSharpe > 0m && c.BestRet > 0m && c.Folds >= minPoolFolds;
    var passed = cands.Where(Pass).OrderByDescending(c => c.T).ToList();
    var rejected = cands.Where(c => !Pass(c)).ToList();

    Console.WriteLine($"=== 入場閘:{cands.Count} 候選 → {passed.Count} 通過(跨幣顯著 + 廣度≥{breadthMin:P0} + full正)===");
    Console.WriteLine($"  (env 可調:ALLOC_BREADTH_MIN={breadthMin:F2} · ALLOC_MIN_POOL_FOLDS={minPoolFolds})");
    foreach (var c in rejected.OrderByDescending(c => c.BestSharpe))
    {
        var bc = c.PerCoin.OrderByDescending(p => p.Value.sh).First().Key;
        var why = c.Folds < minPoolFolds ? $"fold<{minPoolFolds}" : c.CiLo <= 0m ? "不顯著(CI含0)" : c.Breadth < breadthMin ? $"廣度低({c.Breadth:P0}幣正)" : c.BestSharpe <= 0m ? "Sharpe≤0" : c.BestRet <= 0m ? "full賠" : "?";
        Console.WriteLine($"   ✗ {c.Name,-16}@{Sh(bc),-5} Sharpe {c.BestSharpe,5:F2} 廣度 {c.Breadth,4:P0} CI下界 {c.CiLo,6:F1} fold {c.Folds,3} — 剔除:{why}");
    }
    // 3.5 forward 否決:回測過、但實盤 ≥fwdMin 筆且實際賠錢 → 剔除(過擬合/regime 破的鐵證)
    if (fwd.Count > 0)
    {
        var fwdVetoed = new List<(Cand c, int n, decimal pnl)>();
        passed = passed.Where(c =>
        {
            if (fwd.TryGetValue(c.Name, out var f) && f.n >= fwdMin && f.pnl < 0m) { fwdVetoed.Add((c, f.n, f.pnl)); return false; }
            return true;
        }).ToList();
        if (fwdVetoed.Count > 0)
        {
            Console.WriteLine($"=== forward 否決:{fwdVetoed.Count} 支回測過、但實盤賠錢被剔 ===");
            foreach (var (c, n, pnl) in fwdVetoed)
                Console.WriteLine($"   ⛔ {c.Name,-16} 實盤 {n} 筆、已實現 {pnl:F2} USDT — 回測過但實盤垮、不上真錢");
        }
    }
    if (passed.Count == 0) { Console.WriteLine("\n無腿通過入場閘 → 不建議配置任何真錢。先回 paper 累積樣本。"); return; }

    // 3a. BTC 核心腿:固定幣 BTC、用集成策略(多策略共識 + confidence-sizing 依共識大小下注)碳出,
    //     獨立於下面的衛星 Sharpe 最大化;它的策略 + BTC 幣都不進衛星指派。
    Leg? core = null;
    var btcBackers = cands.Where(c => c.PerCoin.TryGetValue("BTCUSDT", out var v) && v.sh > 0m)
                          .OrderByDescending(c => c.PerCoin["BTCUSDT"].sh).ToList();
    if (btcCore)
    {
        var cc = cands.FirstOrDefault(c => c.Name == btcStrat);
        if (cc != null && cc.PerCoin.TryGetValue("BTCUSDT", out var bv) && bv.sh > 0m)
            core = new Leg(cc.Name, "BTCUSDT", bv.sh, (decimal)AnnVol(bv.curve), bv.curve, cc.Folds, cc.CiLo, cc.T, bv.ret, bv.dd, cc.Breadth);
        else { Console.WriteLine($"   ⚠ BTC 核心策略 {btcStrat} 在 BTC 無正 edge → 本次停用核心腿"); btcCore = false; }
    }

    // 3b. 衛星指派:每腿不同幣(真錢一 symbol 一策略 + 真去相關);核心開時排除 BTC 幣 + 核心策略。
    //     t 高的先選它的最佳「可用」幣(需 Sharpe>0 且 full 正);撞到已佔的幣就退而選次佳。
    var taken = new HashSet<string>();
    if (btcCore) taken.Add("BTCUSDT");
    var pool = new List<Leg>();
    foreach (var c in passed)
    {
        if (btcCore && c.Name == btcStrat) continue;   // 核心策略已碳出到 BTC
        var pick = c.PerCoin.Where(p => !taken.Contains(p.Key) && p.Value.sh > 0m && p.Value.ret > 0m)
                            .OrderByDescending(p => p.Value.sh).Select(p => (k: p.Key, v: p.Value)).FirstOrDefault();
        if (pick.k == null) { Console.WriteLine($"   ⚠ {c.Name} 無剩餘可用幣可指派(都被佔/負)→ 跳過"); continue; }
        taken.Add(pick.k);
        pool.Add(new Leg(c.Name, pick.k, pick.v.sh, (decimal)AnnVol(pick.v.curve), pick.v.curve, c.Folds, c.CiLo, c.T, pick.v.ret, pick.v.dd, c.Breadth));
    }
    int coreIdx = -1;
    if (core != null) { pool.Insert(0, core); coreIdx = 0; }   // 核心腿放第一
    if (pool.Count == 0) { Console.WriteLine("\n指派後無可部署腿。"); return; }

    // 4a. 對齊腿權益曲線(共同尾長),算相關 + 日報酬(給 vol-target 共變異)
    int N = pool.Count;
    int L = pool.Min(l => l.Curve.Count);
    var rets = pool.Select(l => DailyRets(l.Curve, L)).ToList();
    int RL = rets.Min(r => r.Count);
    for (int i = 0; i < N; i++) rets[i] = rets[i].Skip(rets[i].Count - RL).ToList();
    double[,] rho = new double[N, N];
    for (int i = 0; i < N; i++)
        for (int j = 0; j < N; j++)
            rho[i, j] = (double)CorrelationGuard.PearsonOfReturns(
                pool[i].Curve.Skip(pool[i].Curve.Count - L).ToList(),
                pool[j].Curve.Skip(pool[j].Curve.Count - L).ToList());

    // 4b. raw = max(0,Sharpe)/Vol → 相關 haircut(被一堆腿相關 = 降權)→ tilt 權重
    var raw = new double[N];
    for (int i = 0; i < N; i++) raw[i] = pool[i].Vol > 0m ? Math.Max(0, (double)pool[i].Sharpe) / (double)pool[i].Vol : 0;
    var haircut = new double[N];
    for (int i = 0; i < N; i++)
    {
        double corrSum = 0;
        for (int j = 0; j < N; j++) if (j != i) corrSum += Math.Max(0, rho[i, j]);
        haircut[i] = 1.0 / (1.0 + corrSum);
    }
    var tilt = new double[N];
    for (int i = 0; i < N; i++) tilt[i] = raw[i] * haircut[i];
    double tsum = tilt.Sum();
    var w = new double[N];
    for (int i = 0; i < N; i++) w[i] = tsum > 0 ? tilt[i] / tsum : 1.0 / N;

    // 4c. 朝等權收縮(每腿 λ=min(1,T/T*),資料少就靠等權)→ 重新正規化
    for (int i = 0; i < N; i++) { double lam = Math.Min(1.0, pool[i].Folds / shrinkT); w[i] = lam * w[i] + (1 - lam) * (1.0 / N); }
    double ws = w.Sum(); for (int i = 0; i < N; i++) w[i] /= ws;

    // 4d. 單腿上限 + 多餘量按比例分給未封頂者(迭代);最後正規化
    for (int iter = 0; iter < 12; iter++)
    {
        double over = 0; var free = new List<int>();
        for (int i = 0; i < N; i++) { if (w[i] > capW + 1e-9) { over += w[i] - capW; w[i] = capW; } else free.Add(i); }
        if (over < 1e-9) break;
        double freeSum = free.Sum(i => w[i]);
        if (freeSum < 1e-9) break;
        foreach (var i in free) w[i] += over * (w[i] / freeSum);
    }
    double ws2 = w.Sum(); for (int i = 0; i < N; i++) w[i] /= ws2;

    // 4d-2. BTC 核心腿:把它的權重固定/拉高到 btcCoreW,其餘按比例縮放(獨立於 Sharpe 最大化、可超單腿上限)。
    if (coreIdx >= 0 && N > 1)
    {
        double cw = Math.Min(0.9, btcCoreW);
        double othersOld = 1.0 - w[coreIdx];
        double othersNew = 1.0 - cw;
        if (othersOld > 1e-9) for (int i = 0; i < N; i++) if (i != coreIdx) w[i] *= othersNew / othersOld;
        w[coreIdx] = cw;
    }

    // 4e. vol-target:組合年化波動 = sqrt(w'Σw)·sqrt(252);曝險 = min(maxExp, targetVol/組合波動)
    double portDailyVar = 0;
    for (int i = 0; i < N; i++)
        for (int j = 0; j < N; j++)
        {
            var ai = rets[i].Average(); var aj = rets[j].Average();
            double cov = 0; for (int t = 0; t < RL; t++) cov += (rets[i][t] - ai) * (rets[j][t] - aj);
            cov /= Math.Max(1, RL - 1);
            portDailyVar += w[i] * w[j] * cov;
        }
    double portAnnVol = Math.Sqrt(Math.Max(0, portDailyVar)) * Math.Sqrt(252);
    double exposure = portAnnVol > 1e-9 ? Math.Min((double)maxExp, (double)targetVol / portAnnVol) : (double)maxExp;

    // 4f. N_eff(有效獨立押注數)+ 平均相關
    double sumSq = w.Sum(x => x * x);
    double nEff = sumSq > 0 ? 1.0 / sumSq : 0;
    double offDiag = 0; int cnt = 0;
    for (int i = 0; i < N; i++) for (int j = i + 1; j < N; j++) { offDiag += rho[i, j]; cnt++; }
    double avgRho = cnt > 0 ? offDiag / cnt : 0;

    // ── 輸出 ──
    Console.WriteLine($"\n=== 建議配置({N} 腿;total exposure {exposure:F2}x;◆=BTC 核心腿)===");
    Console.WriteLine($"  {"strategy@coin",-24}{"Sharpe",8}{"annVol",8}{"廣度",7}{"folds",7}{"weight",8}{"budget_pct",12}");
    var budgets = new List<(string coin, string strat, decimal bp)>();
    for (int i = 0; i < N; i++)
    {
        decimal bp = Math.Round((decimal)(w[i] * exposure) * 100m, 0);
        budgets.Add((pool[i].Coin, pool[i].Name, bp));
        string tag = (i == coreIdx ? "◆ " : "  ") + pool[i].Name + "@" + Sh(pool[i].Coin);
        Console.WriteLine($"  {tag,-24}{pool[i].Sharpe,8:F2}{pool[i].Vol,7:P0}{pool[i].Breadth,7:P0}{pool[i].Folds,7}{w[i],8:P0}{bp,11:F0}%");
    }
    Console.WriteLine($"  {"合計",-24}{"",8}{"",8}{"",7}{"",7}{w.Sum(),8:P0}{w.Sum() * exposure * 100,11:F0}%");
    if (coreIdx >= 0)
        Console.WriteLine($"\n  ◆ BTC 核心:{pool[coreIdx].Name}(多策略集成、confidence-sizing 依共識大小下注)固定佔書 {btcCoreW:P0}。" +
            $"\n     撐腰共識 — {btcBackers.Count} 支策略在 BTC 上有正 edge:" +
            string.Join("、", btcBackers.Take(8).Select(c => $"{c.Name}({c.PerCoin["BTCUSDT"].sh:F2})")));
    Console.WriteLine($"\n  組合年化波動 {portAnnVol:P0} → 為打到目標 {targetVol:P0}、整體曝險 = {exposure:F2}x(上限 {maxExp:F1}x)");
    Console.WriteLine($"  有效獨立押注數 N_eff = {nEff:F1} / {N} 腿   平均兩兩相關 ρ̄ = {avgRho:F2}   " +
        (nEff < N * 0.6 ? "⚠ N_eff 遠低於腿數 = 假分散(腿太像)" : "✓ 分散有效"));

    // ── 組合 vs 諧波單獨 直接對照(分散到底改善多少風險調整後報酬)──
    // 用各腿日報酬 rets[] + 配置權重 w[] → 組合日報酬序列 → Sharpe(曝險不變)+ maxDD(exposure=1 raw);對照 100% 諧波腿。
    {
        (double sh, double dd, double ret) Stats(double[] r)
        {
            if (r.Length < 5) return (0, 0, 0);
            double mean = r.Average();
            double sd = Math.Sqrt(r.Select(x => (x - mean) * (x - mean)).Sum() / Math.Max(1, r.Length - 1));
            double sharpe = sd > 1e-12 ? mean / sd * Math.Sqrt(252) : 0;
            double eq = 1, peak = 1, mdd = 0;
            foreach (var x in r) { eq *= 1 + x; if (eq > peak) peak = eq; var d = peak > 0 ? (peak - eq) / peak : 0; if (d > mdd) mdd = d; }
            return (sharpe, mdd * 100, (eq - 1) * 100);
        }
        int harmIdx = -1;
        for (int i = 0; i < N; i++) if (pool[i].Name.StartsWith("harm")) { harmIdx = i; break; }
        var comb = new double[RL];
        for (int t = 0; t < RL; t++) { double s = 0; for (int i = 0; i < N; i++) s += w[i] * rets[i][t]; comb[t] = s; }
        var (cSh, cDd, cRet) = Stats(comb);
        Console.WriteLine($"\n=== 組合 vs 諧波單獨(exposure=1 raw、全期日報酬;Sharpe 與曝險無關、maxDD 等比放大)===");
        Console.WriteLine($"  {"配置",-14}{"Sharpe",9}{"maxDD%",9}{"全期報酬%",11}");
        if (harmIdx >= 0)
        {
            var (hSh, hDd, hRet) = Stats(rets[harmIdx].ToArray());
            Console.WriteLine($"  {"100% 諧波",-14}{hSh,9:F2}{hDd,9:F0}{hRet,11:F0}");
            Console.WriteLine($"  {$"分散{N}腿",-14}{cSh,9:F2}{cDd,9:F0}{cRet,11:F0}");
            string shGain = hSh > 0 ? $"{(cSh / hSh - 1) * 100:+0;-0}%" : "n/a";
            string ddGain = hDd > 1e-9 ? $"{(cDd / hDd - 1) * 100:+0;-0}%" : "n/a";
            Console.WriteLine($"  → 分散效果:Sharpe {shGain}、maxDD {ddGain}(負=DD 降、好)");
            Console.WriteLine($"  註:Sharpe 升 = 分散真改善風險調整後報酬;vol-target 下可把組合放大到同 DD → 賺更多。");
        }
        else Console.WriteLine($"  {$"分散{N}腿",-14}{cSh,9:F2}{cDd,9:F0}{cRet,11:F0}  (本次無諧波腿、無對照)");
    }

    // 為什麼沒選 BTC?把每條腿「選中幣 vs BTC」的回測 Sharpe/報酬攤出來。
    // 引擎選的是「策略主動交易 edge 最強的幣」(Sharpe),不是「最有價值的資產」(buy&hold)——
    // BTC 最有效率/最被研究透 → 主動策略 edge 通常最薄;alt 沒效率、波動大 → edge 反而高。
    Console.WriteLine("\n=== 衛星腿 選中幣 vs BTC 回測 Sharpe(引擎挑的是 edge 不是資產價值;BTC 已另由核心腿持有)===");
    for (int i = 0; i < N; i++)
    {
        if (i == coreIdx) continue;   // 核心腿本身就是 BTC、不比
        var l = pool[i];
        var c = cands.First(x => x.Name == l.Name);
        bool hasBtc = c.PerCoin.TryGetValue("BTCUSDT", out var bv);
        string btcCol = hasBtc ? $"BTC Sh {bv.sh,5:F2} (ret {bv.ret,5:F0}%)" : "BTC 無資料";
        string verdict = !hasBtc ? "" : bv.sh >= l.Sharpe ? " ⚠ BTC 其實更強?!" : bv.sh <= 0m ? " → BTC 上此策略賠錢/無 edge" : " → BTC edge 較弱";
        Console.WriteLine($"   {l.Name,-16} 選 {Sh(l.Coin),-5} Sh {l.Sharpe,5:F2}  ·  {btcCol}{verdict}");
    }
    Console.WriteLine("   註:量的是『策略在該幣上的主動 edge』。BTC edge 薄 ≠ BTC 不值得 → 故另設核心腿用多策略集成下注 BTC。");

    // 相關矩陣
    Console.WriteLine("\n=== 選中腿 相關矩陣(全期權益日報酬)===");
    Console.WriteLine("  " + new string(' ', 18) + string.Join("", pool.Select(l => $"{Sh(l.Coin),8}")));
    for (int i = 0; i < N; i++)
        Console.WriteLine($"  {pool[i].Name + "@" + Sh(pool[i].Coin),-18}" + string.Join("", Enumerable.Range(0, N).Select(j => $"{rho[i, j],8:F2}")));

    // 畢業判定(paper→真錢):通過入場閘 = 統計上夠格;再標出跟組內其他腿最大相關(>0.6 = 上真錢前要再想)
    Console.WriteLine("\n=== 畢業判定(paper → 真錢)===");
    Console.WriteLine("  通過入場閘 = 統計上夠格上真錢。max ρ = 跟其他選中腿的最高相關(>0.6 = 加它紅利低、考慮替換):");
    for (int i = 0; i < N; i++)
    {
        double maxR = 0; int who = -1;
        for (int j = 0; j < N; j++) if (j != i && rho[i, j] > maxR) { maxR = rho[i, j]; who = j; }
        string flag = maxR > 0.6 ? $"⚠ 與 {pool[who].Name}@{Sh(pool[who].Coin)} ρ={maxR:F2}" : $"✓ 獨立(max ρ {maxR:F2})";
        // forward 實盤確認狀態
        string live = "live —(無 forward 資料)";
        if (fwd.TryGetValue(pool[i].Name, out var f))
            live = f.n >= fwdMin ? (f.pnl > 0m ? $"live ✓ 確認({f.n}筆 +{f.pnl:F1} 勝率{f.wr:P0})" : $"live ⚠ {f.n}筆 {f.pnl:F1}")
                                 : $"live …{f.n}筆(<{fwdMin}、樣本不足、暫看回測)";
        Console.WriteLine($"   {pool[i].Name,-16}@{Sh(pool[i].Coin),-5} → {flag} · {live}");
    }

    // 可直接貼的 SQL(budget_pct 寫回 watchlist;預設印 bingx、實際看你部署在哪交易所)
    Console.WriteLine("\n=== 可貼 SQL(把 budget_pct 寫回真錢 watchlist;交易所/symbol 格式自行對應)===");
    foreach (var (coin, strat, bp) in budgets)
        Console.WriteLine($"   UPDATE auto_trade_watchlist SET budget_pct={bp} WHERE strategy='{strat}'; -- {strat}@{Sh(coin)}");
    Console.WriteLine("\n   ⚠ 真錢 budget 改完必須立刻 restart broker(否則跑動的 PersistWatch 會用記憶體舊值覆蓋回去)。");
}

// ════════════════════════════════════════════════════════════════════════════
// --xsmom 橫斷面動量:跨幣排序、long 強勢 topK / short 弱勢 topK。結構不同的去相關 edge。
// ════════════════════════════════════════════════════════════════════════════
async Task RunXsMom()
{
    decimal EnvX(string k, decimal def) => decimal.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : def;
    int topK = (int)EnvX("XSMOM_TOPK", 3m);
    decimal costSide = EnvX("XSMOM_COST_PER_SIDE", 0.0008m);   // 8bps/邊(perp taker+滑點)

    Console.WriteLine("=== 橫斷面動量 --xsmom(跨幣排序;long topK 強 / short topK 弱、等權)===");
    Console.WriteLine($"  topK={topK} · 成本 {costSide:P2}/邊(env XSMOM_TOPK / XSMOM_COST_PER_SIDE)\n");

    var dat = new Dictionary<string, List<BarData>>();
    foreach (var sym in symbols) { try { var b = await Fetch(sym); if (b.Count >= 400) dat[sym] = b; } catch { } }
    var coins = dat.Keys.ToList();
    if (coins.Count < topK * 2 + 1) { Console.WriteLine("幣數不足。"); return; }
    int L = dat.Values.Min(b => b.Count);
    var cl = coins.ToDictionary(c => c, c => dat[c].Skip(dat[c].Count - L).Select(b => b.Close).ToList());
    Console.WriteLine($"宇宙:{coins.Count} 幣、對齊尾段 {L} 日(~{L / 365.0:F1} 年)\n");

    // 核心回測 → (權益曲線, 平均換手率)。ls=true 多空、false 純多 topK。
    (List<decimal> eq, decimal turnover) Run(int lookback, int rebal, bool ls, decimal cost, Func<int, decimal>? exposure = null)
    {
        var eq = new List<decimal> { 1m };
        List<string> curL = new(), curS = new();
        int rebals = 0; decimal turnSum = 0m;
        for (int t = lookback; t < L - 1; t++)
        {
            if ((t - lookback) % rebal == 0)
            {
                var rank = coins.Select(c => (c, r: cl[c][t - lookback] > 0 ? (cl[c][t] - cl[c][t - lookback]) / cl[c][t - lookback] : 0m))
                                .OrderByDescending(x => x.r).ToList();
                var nL = rank.Take(topK).Select(x => x.c).ToList();
                var nS = ls ? rank.AsEnumerable().Reverse().Take(topK).Select(x => x.c).ToList() : new List<string>();
                int changed = nL.Except(curL).Count() + curL.Except(nL).Count() + nS.Except(curS).Count() + curS.Except(nS).Count();
                int basket = topK * (ls ? 2 : 1);
                decimal tov = basket > 0 ? (decimal)changed / (2 * basket) : 0m;
                turnSum += tov; rebals++;
                eq[^1] *= (1m - tov * 2m * cost);   // 換手比例 × 來回成本
                curL = nL; curS = nS;
            }
            decimal Ret(List<string> set) => set.Count == 0 ? 0m : set.Average(c => cl[c][t] > 0 ? (cl[c][t + 1] - cl[c][t]) / cl[c][t] : 0m);
            decimal pr = Ret(curL) - (ls ? Ret(curS) : 0m);
            decimal e = exposure?.Invoke(t) ?? 1m;   // 外生 exposure 閘(0..1);成本仍照付=對閘保守
            eq.Add(eq[^1] * (1m + pr * e));
        }
        return (eq, rebals > 0 ? turnSum / rebals : 0m);
    }

    // 敏感度:lookback × rebal(防單一參數僥倖;穩健 = 多格都正)
    int[] lbs = { 30, 60, 90, 120 };
    int[] rbs = { 7, 14, 30 };
    foreach (var ls in new[] { true, false })
    {
        Console.WriteLine($"=== {(ls ? "多空(market-neutral)" : "純多 topK")} 敏感度(realistic 成本;格=Sharpe / 年化%)===");
        Console.WriteLine("  lookback\\rebal " + string.Join("", rbs.Select(r => $"{r + "d",14}")));
        foreach (var lb in lbs)
        {
            var cells = rbs.Select(rb =>
            {
                var (eq, _) = Run(lb, rb, ls, costSide);
                var st = StatsOf(eq);
                var ann = eq.Count > 30 ? (decimal)((Math.Pow((double)eq[^1], 365.0 / eq.Count) - 1) * 100) : 0m;
                return $"{st.sharpe,6:F2}/{ann,5:F0}%";
            });
            Console.WriteLine($"  {lb + "d",-14}" + string.Join("  ", cells));
        }
        Console.WriteLine();
    }

    // 代表配置細看(env XSMOM_LOOKBACK/REBAL 可指定要驗哪格)+ OOS(後半段)+ vs 買入持有
    int repLb = (int)EnvX("XSMOM_LOOKBACK", 30m);
    int repRb = (int)EnvX("XSMOM_REBAL", 7m);
    var (eqF, tov2) = Run(repLb, repRb, true, costSide);
    var (eqG, _) = Run(repLb, repRb, true, 0m);
    var full = StatsOf(eqF);
    int half = eqF.Count / 2;
    var oos = StatsOf(eqF.Skip(half).ToList());
    Console.WriteLine($"=== 代表配置 多空 lookback{repLb}/rebal{repRb} ===");
    Console.WriteLine($"  全期(realistic): ret {full.ret,6:F0}%  Sharpe {full.sharpe:F2}  maxDD {full.dd:F0}%  換手 {tov2:P0}/rebal");
    Console.WriteLine($"  全期(gross 0成本): Sharpe {StatsOf(eqG).sharpe:F2}  → 成本侵蝕 {StatsOf(eqG).sharpe - full.sharpe:F2}");
    Console.WriteLine($"  後半段 OOS:      ret {oos.ret,6:F0}%  Sharpe {oos.sharpe:F2}  maxDD {oos.dd:F0}%");
    // 買入持有等權基準
    var bhEq = new List<decimal> { 1m };
    for (int t = 0; t < L - 1; t++) { decimal r = coins.Average(c => cl[c][t] > 0 ? (cl[c][t + 1] - cl[c][t]) / cl[c][t] : 0m); bhEq.Add(bhEq[^1] * (1m + r)); }
    var bh = StatsOf(bhEq);
    Console.WriteLine($"  vs 等權買入持有:  ret {bh.ret,6:F0}%  Sharpe {bh.sharpe:F2}  maxDD {bh.dd:F0}%");

    // ── 衰減診斷:split-half Sharpe 掉(2.21→1.12)是「因子死」還是「成本」還是「regime」?──
    //   切 4 等分,逐段比 net / gross / 同期等權B&H 的 Sharpe:
    //   · gross 也逐段降 = 真 alpha 衰減(因子擁擠/失效、最該擔心)
    //   · gross 穩、只 net 降 = 成本/換手侵蝕(可調 rebal 緩解)
    //   · 衰減跟著 B&H 走 = regime 依賴(動量在盤整/熊市本來就弱、屬正常、非死)
    var bhAlign = bhEq.Skip(Math.Max(0, bhEq.Count - eqF.Count)).ToList();  // 對齊到 eqF 同窗
    Console.WriteLine($"\n=== 衰減診斷(lookback{repLb}/rebal{repRb};切 4 等分;Sharpe)===");
    Console.WriteLine($"  {"區段",-7}{"net Sh",9}{"gross Sh",10}{"成本拖累",10}{"同期B&H Sh",12}{"net 年化%",11}");
    var qNet = new List<decimal>(); var qGross = new List<decimal>();
    int qn = eqF.Count / 4;
    for (int k = 0; k < 4; k++)
    {
        int a = k * qn, bnd = (k == 3) ? eqF.Count : (k + 1) * qn;
        var segN = eqF.Skip(a).Take(bnd - a).ToList();
        var segG = eqG.Skip(a).Take(bnd - a).ToList();
        var segB = bhAlign.Skip(a).Take(bnd - a).ToList();
        var sN = StatsOf(segN); var sG = StatsOf(segG); var sB = StatsOf(segB);
        var ann = segN.Count > 10 ? (decimal)((Math.Pow((double)(segN[^1] / segN[0]), 365.0 / segN.Count) - 1) * 100) : 0m;
        Console.WriteLine($"  Q{k + 1,-6}{sN.sharpe,9:F2}{sG.sharpe,10:F2}{sG.sharpe - sN.sharpe,10:F2}{sB.sharpe,12:F2}{ann,11:F0}");
        qNet.Add(sN.sharpe); qGross.Add(sG.sharpe);
    }
    // 前半 vs 後半(headline)
    int hh = eqF.Count / 2;
    var fhN = StatsOf(eqF.Take(hh).ToList()); var bhN = StatsOf(eqF.Skip(hh).ToList());
    var fhG = StatsOf(eqG.Take(hh).ToList()); var bhG = StatsOf(eqG.Skip(hh).ToList());
    Console.WriteLine($"  前半 net {fhN.sharpe:F2} → 後半 net {bhN.sharpe:F2}  │  前半 gross {fhG.sharpe:F2} → 後半 gross {bhG.sharpe:F2}");
    // 成因判定(優先序:成因互斥、先區分 regime vs 擁擠 —— 兩者都讓 gross 衰減,但 regime 同時伴隨大盤崩)
    var bhFh = StatsOf(bhAlign.Take(hh).ToList()).sharpe; var bhBh = StatsOf(bhAlign.Skip(hh).ToList()).sharpe;
    bool grossDecays = bhG.sharpe < fhG.sharpe * 0.7m;            // gross 後半 < 前半 70% = alpha 真衰(非成本)
    bool regimeCollapse = bhBh < bhFh - 0.5m;                     // 大盤後半 Sharpe 明顯轉弱(regime 換手)
    bool costGap = !grossDecays && (bhG.sharpe - bhN.sharpe) > (fhG.sharpe - fhN.sharpe) + 0.3m;  // gross 穩、淨成本拖累變大
    string cause =
        costGap ? "成本/換手侵蝕為主(gross 後半尚穩、是成本吃掉)→ 拉長 rebal 或降 topK 可救回淨 Sharpe"
        : (grossDecays && regimeCollapse) ? "★ regime/離散度依賴:gross 衰減與同期大盤崩【同步】(B&H Sh 也崩)= 相關性升高、橫斷面離散度消失、無 spread 可吃。XS 動量的結構特性、非永久死亡;裸跑會在相關熊市流血"
        : grossDecays ? "⚠ 真 alpha 衰減/擁擠:gross 後半崩、但同期大盤【沒】同步崩 = edge 自身失效(最該擔心、不部署或極小)"
        : "後半淨衰減但 gross 尚穩、與成本/regime 無明確對應 → 樣本/雜訊,維持小權重觀察";
    Console.WriteLine($"  → 成因:{cause}");
    Console.WriteLine($"     (因子 net 前半 {fhN.sharpe:F2}→後半 {bhN.sharpe:F2};同期大盤 B&H 前半 {bhFh:F2}→後半 {bhBh:F2})");

    // ── 離散度閘:fair-weather 修復測試 ──
    //   閘信號 = 橫斷面 lookback 報酬的標準差(離散度),外生於因子權益(不犯自我參照 [[feedback_self_referential_regime_overlay]])。
    //   門檻 = 自身 trailing 90 日中位(規則固定、只看 t 之前、絕不 fit 後半段),離散度 < 中位 → 縮倉到 0(相關市場沒 spread 可吃)。
    int dispWin = 90;
    var disp = new Dictionary<int, decimal>();
    for (int t = repLb; t < L; t++)
    {
        var rs = coins.Select(c => cl[c][t - repLb] > 0 ? (cl[c][t] - cl[c][t - repLb]) / cl[c][t - repLb] : 0m).ToList();
        var mu = rs.Average();
        disp[t] = (decimal)Math.Sqrt((double)rs.Select(r => (r - mu) * (r - mu)).Average());
    }
    decimal Gate(int t)
    {
        var hist = new List<decimal>();
        for (int k = Math.Max(repLb, t - dispWin); k < t; k++) if (disp.TryGetValue(k, out var d)) hist.Add(d);  // 嚴格 t 之前、無 lookahead
        if (hist.Count < 20 || !disp.ContainsKey(t)) return 1m;   // 暖機不足 → 滿倉
        var sorted = hist.OrderBy(x => x).ToList();
        return disp[t] >= sorted[sorted.Count / 2] ? 1m : 0m;     // 離散度 ≥ trailing 中位 → 在場、否則縮倉
    }
    var tsAll = Enumerable.Range(repLb, Math.Max(0, L - 1 - repLb)).ToList();
    decimal avgExp = tsAll.Count > 0 ? tsAll.Average(Gate) : 1m;
    var (eqGate, _) = Run(repLb, repRb, true, costSide, Gate);
    var gFull = StatsOf(eqGate);
    int hg = eqGate.Count / 2;
    var gBack = StatsOf(eqGate.Skip(hg).ToList());
    Console.WriteLine($"\n=== 離散度閘實驗(閘=橫斷面離散度≥自身trailing90中位才在場;門檻固定、不 fit 後半)===");
    Console.WriteLine($"  平均在場比例 {avgExp:P0}(設計上 ~50%、中位切)");
    Console.WriteLine($"  {"",-7}{"ungated ret/Sh/DD",26}{"gated ret/Sh/DD",26}");
    Console.WriteLine($"  {"全期",-7}{$"{full.ret,6:F0}% / {full.sharpe,4:F2} / {full.dd,3:F0}%",26}{$"{gFull.ret,6:F0}% / {gFull.sharpe,4:F2} / {gFull.dd,3:F0}%",26}");
    Console.WriteLine($"  {"後半",-7}{$"{oos.ret,6:F0}% / {oos.sharpe,4:F2} / {oos.dd,3:F0}%",26}{$"{gBack.ret,6:F0}% / {gBack.sharpe,4:F2} / {gBack.dd,3:F0}%",26}");
    bool ddCut = gBack.dd < oos.dd - 5m;                         // 後半 maxDD 明顯降
    bool backSharpeUp = gBack.sharpe > oos.sharpe + 0.1m;        // 後半 risk-adj 真改善
    bool keptUpside = gFull.sharpe >= full.sharpe - 0.2m;        // 全期沒被閘搞砸(縮倉踏空牛市)
    Console.WriteLine("  → 判定:" + (
        !keptUpside ? "❌ 閘代價>效益:全期 Sharpe 被踏空拖垮(中位切縮倉砍掉牛市)、後半 risk-adj 也沒實質改善 → 不加閘;這條靠【組合層防禦】(CB/DD-aware)+ 小權重去相關紅利更划算"
        : (ddCut && backSharpeUp) ? "✅ 閘有效:削熊市 DD 且後半 risk-adj 改善、又沒犧牲全期 → fair-weather 被外生離散度閘救回、值得當有閘 sleeve"
        : (ddCut || backSharpeUp) ? "⚠ 閘邊際(削了 DD 或改善 risk-adj 其一)、代價是踏空 → 看組合是否需要這條保命特性"
        : "❌ 閘無助(後半沒改善)→ 離散度(中位切)不是有效擇時信號"));

    // ★ 重點:跟現有書(decorr4_ls@BTC)相不相關 = 是不是真分散
    try
    {
        var ens = strats.First(x => x.name == "decorr4_ls").s;
        var dc = LongShortBacktestEngine.Run(ens, dat["BTCUSDT"], new StrategyConfig { Symbol = "BTCUSDT", Interval = "1d" }, commission: gComm, slippagePct: gSlip)
                 .EquityCurve.Select(e => e.Value).ToList();
        int n = Math.Min(eqF.Count, dc.Count);
        var rho = CorrelationGuard.PearsonOfReturns(eqF.Skip(eqF.Count - n).ToList(), dc.Skip(dc.Count - n).ToList());
        Console.WriteLine($"\n=== 與現有書 decorr4_ls@BTC 的相關 ρ = {rho:F2} ===");
        Console.WriteLine($"  → {(Math.Abs((double)rho) < 0.3 ? "✅ 低相關、真分散紅利:值得當新一類 sleeve" : "⚠ 相關偏高、分散紅利有限")}");
    }
    catch (Exception ex) { Console.WriteLine($"相關計算失敗:{ex.Message}"); }

    Console.WriteLine($"\n判定(數據驅動):OOS Sharpe {oos.sharpe:F2} → " +
        (oos.sharpe > 0.3m ? "✅ OOS 站得住、可進 paper" : oos.sharpe > 0m ? "⚠ OOS 微正但弱、再觀察" : "❌ OOS 翻負、edge 不穩、別上") +
        $" · 與 decorr4 ρ 低=分散有但 edge 才是門檻。");
    Console.WriteLine("  (env XSMOM_LOOKBACK / XSMOM_REBAL 換格驗 OOS;敏感度表跨越正負 = 參數脆、過擬合風險高。");
}

// ════════════════════════════════════════════════════════════════════════════
// --carry 資金費 carry:現貨多 + 永續空收 funding。近零方向風險、跟價格 edge 正交的「穩定底盤」。
// 量化各幣 funding 年化/穩定度 + 籃子淨 carry + 小本金可行性。
// ════════════════════════════════════════════════════════════════════════════
async Task RunCarry()
{
    int topN = (int)(decimal.TryParse(Environment.GetEnvironmentVariable("CARRY_TOPN"), out var tn) ? tn : 5m);
    Console.WriteLine("=== 資金費 carry --carry(現貨多+永續空收 funding;年化=rate×3×365)===\n");

    // 抓 Binance USDT-M funding 歷史(8h 一次、1000 筆≈333 天)
    async Task<List<decimal>> FetchFunding(string sym)
    {
        try
        {
            var json = await http.GetStringAsync($"https://fapi.binance.com/fapi/v1/fundingRate?symbol={sym}&limit=1000");
            using var doc = JsonDocument.Parse(json);
            var outl = new List<decimal>();
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.TryGetProperty("fundingRate", out var fr) && decimal.TryParse(fr.GetString(), System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    outl.Add(v);
            return outl;
        }
        catch { return new List<decimal>(); }
    }

    var rows = new List<(string coin, decimal ann, decimal recentAnn, decimal pctPos, decimal annStd, int n)>();
    foreach (var sym in symbols)
    {
        var f = await FetchFunding(sym);
        if (f.Count < 100) continue;
        decimal avg = f.Average();
        decimal ann = avg * 3m * 365m * 100m;
        var recent = f.Skip(Math.Max(0, f.Count - 90)).ToList();   // 近 ~30 天
        decimal recentAnn = recent.Average() * 3m * 365m * 100m;
        decimal pctPos = (decimal)f.Count(x => x > 0m) / f.Count * 100m;
        var a = f.Average();
        decimal std = (decimal)Math.Sqrt((double)f.Select(x => (x - a) * (x - a)).Average()) * 3m * 365m * 100m;
        rows.Add((Sh(sym), Math.Round(ann, 1), Math.Round(recentAnn, 1), Math.Round(pctPos, 0), Math.Round(std, 0), f.Count));
    }
    if (rows.Count == 0) { Console.WriteLine("無 funding 資料。"); return; }

    Console.WriteLine($"  {"coin",-7}{"年化%",8}{"近30d年化%",12}{"%正",7}{"年化波動%",11}{"n",6}");
    foreach (var r in rows.OrderByDescending(r => r.ann))
        Console.WriteLine($"  {r.coin,-7}{r.ann,8:F1}{r.recentAnn,12:F1}{r.pctPos,6:F0}%{r.annStd,11:F0}{r.n,6}");

    // 籃子:取年化 funding 最高的 topN(現貨多+永續空、等權)→ 平均 carry
    var top = rows.OrderByDescending(r => r.ann).Take(topN).ToList();
    decimal basketAnn = top.Average(r => r.ann);
    decimal basketRecent = top.Average(r => r.recentAnn);
    // 成本拖累估:每幣一對(現貨多+永續空)、開+平 4 條腿 × ~0.05% taker ≈ 0.2%/輪;月 rebal ≈ 2.4%/年 + 點差滑點 ~1% → 抓 ~3%/年
    decimal costDrag = 3.0m;
    decimal netAnn = basketAnn - costDrag;
    Console.WriteLine($"\n=== 籃子(年化 funding 最高 {topN} 幣、現貨多+永續空、等權)===");
    Console.WriteLine($"  成員:{string.Join(", ", top.Select(r => $"{r.coin}({r.ann:F0}%)"))}");
    Console.WriteLine($"  毛 carry 年化 ~{basketAnn:F1}%(近30d ~{basketRecent:F1}%)− 成本拖累 ~{costDrag:F0}% = **淨 ~{netAnn:F1}%/年**");
    Console.WriteLine($"  風險:近零方向(delta-neutral)、跟價格動量/TA 正交 → 真分散底盤;尾部=交易所/穩定幣脫鉤、強平、funding 反轉。");

    // 小本金可行性
    Console.WriteLine($"\n=== 小本金可行性 ===");
    Console.WriteLine($"  {topN} 幣 carry = {topN * 2} 個小倉(每幣 現貨+永續各一)。BingX 最小名目 ~5 USDT/倉。");
    Console.WriteLine($"  $350 本金 → 每倉 ~{350.0 / (topN * 2):F0} USDT,逼近最小單、手續費吃掉大半 carry → **不划算**。");
    Console.WriteLine($"  建議:本金 ≥ ${topN * 2 * 75} 左右(每倉 ~75 USDT、fee 佔比夠小)再上;當『穩定底盤』、跟方向性 edge 並存。");
    Console.WriteLine($"  ⚠ 毛 carry {basketAnn:F0}% 看似高多半是少數高波動 alt 灌的(年化波動欄越大越不穩);BTC/ETH funding 通常才年化個位數。");
}

// ════════════════════════════════════════════════════════════════════════════
// --xsrev 短週期橫斷面反轉:long 近期跌最兇 / short 漲最兇。跟動量負/低相關 = 配對紅利。
// ⚠ 高換手(日頻 rebal)→ 成本是生死關。
// ════════════════════════════════════════════════════════════════════════════
async Task RunXsRev()
{
    decimal EnvR(string k, decimal def) => decimal.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : def;
    int topK = (int)EnvR("XSREV_TOPK", 3m);
    decimal costSide = EnvR("XSREV_COST_PER_SIDE", 0.0008m);
    Console.WriteLine("=== 短週期橫斷面反轉 --xsrev(long 跌最兇 / short 漲最兇、等權市場中性)===");
    Console.WriteLine($"  topK={topK} · 成本 {costSide:P2}/邊\n");

    var dat = new Dictionary<string, List<BarData>>();
    foreach (var sym in symbols) { try { var b = await Fetch(sym); if (b.Count >= 400) dat[sym] = b; } catch { } }
    var coins = dat.Keys.ToList();
    int L = dat.Values.Min(b => b.Count);
    var cl = coins.ToDictionary(c => c, c => dat[c].Skip(dat[c].Count - L).Select(b => b.Close).ToList());
    Console.WriteLine($"宇宙:{coins.Count} 幣、對齊尾段 {L} 日\n");

    // reversal=true:long bottomK(跌最兇)/short topK(漲最兇);false=動量
    (List<decimal> eq, decimal tov) Run(int lookback, int rebal, bool reversal, decimal cost)
    {
        var eq = new List<decimal> { 1m }; List<string> curL = new(), curS = new(); int rb = 0; decimal ts = 0m;
        for (int t = lookback; t < L - 1; t++)
        {
            if ((t - lookback) % rebal == 0)
            {
                var rank = coins.Select(c => (c, r: cl[c][t - lookback] > 0 ? (cl[c][t] - cl[c][t - lookback]) / cl[c][t - lookback] : 0m)).OrderByDescending(x => x.r).ToList();
                var winners = rank.Take(topK).Select(x => x.c).ToList();
                var losers = rank.AsEnumerable().Reverse().Take(topK).Select(x => x.c).ToList();
                var nL = reversal ? losers : winners;
                var nS = reversal ? winners : losers;
                int changed = nL.Except(curL).Count() + curL.Except(nL).Count() + nS.Except(curS).Count() + curS.Except(nS).Count();
                decimal tov = (decimal)changed / (4 * topK); ts += tov; rb++;
                eq[^1] *= (1m - tov * 2m * cost); curL = nL; curS = nS;
            }
            decimal Ret(List<string> s) => s.Count == 0 ? 0m : s.Average(c => cl[c][t] > 0 ? (cl[c][t + 1] - cl[c][t]) / cl[c][t] : 0m);
            eq.Add(eq[^1] * (1m + Ret(curL) - Ret(curS)));
        }
        return (eq, rb > 0 ? ts / rb : 0m);
    }

    int[] lbs = { 1, 2, 3, 5 }; int[] rbs = { 1, 2, 3 };
    Console.WriteLine("=== 反轉敏感度(realistic 成本;格=Sharpe / 年化%)===");
    Console.WriteLine("  lookback\\rebal " + string.Join("", rbs.Select(r => $"{r + "d",14}")));
    foreach (var lb in lbs)
    {
        var cells = rbs.Select(rb => { var (eq, _) = Run(lb, rb, true, costSide); var st = StatsOf(eq); var ann = eq.Count > 30 ? (decimal)((Math.Pow((double)eq[^1], 365.0 / eq.Count) - 1) * 100) : 0m; return $"{st.sharpe,6:F2}/{ann,5:F0}%"; });
        Console.WriteLine($"  {lb + "d",-14}" + string.Join("  ", cells));
    }

    int repLb = (int)EnvR("XSREV_LOOKBACK", 3m), repRb = (int)EnvR("XSREV_REBAL", 2m);
    var (eqF, tov2) = Run(repLb, repRb, true, costSide);
    var (eqG, _) = Run(repLb, repRb, true, 0m);
    var full = StatsOf(eqF); var oos = StatsOf(eqF.Skip(eqF.Count / 2).ToList());
    Console.WriteLine($"\n=== 代表 反轉 lookback{repLb}/rebal{repRb} ===");
    Console.WriteLine($"  全期 realistic: Sharpe {full.sharpe:F2} ret {full.ret:F0}% maxDD {full.dd:F0}% 換手 {tov2:P0}/rebal");
    Console.WriteLine($"  全期 gross 0成本: Sharpe {StatsOf(eqG).sharpe:F2}  → ⚠ 成本侵蝕 {StatsOf(eqG).sharpe - full.sharpe:F2}(高換手致命傷)");
    Console.WriteLine($"  後半 OOS: Sharpe {oos.sharpe:F2} ret {oos.ret:F0}%");
    // 與動量的相關(配對紅利關鍵)
    var (eqMom, _) = Run(30, 7, false, costSide);
    int n = Math.Min(eqF.Count, eqMom.Count);
    var rhoMom = CorrelationGuard.PearsonOfReturns(eqF.Skip(eqF.Count - n).ToList(), eqMom.Skip(eqMom.Count - n).ToList());
    Console.WriteLine($"\n  與 xsmom 動量(30/7)相關 ρ = {rhoMom:F2} → {(rhoMom < 0m ? "✅ 負相關、絕佳配對(動量+反轉互補)" : rhoMom < 0.3m ? "○ 低相關、可配對" : "⚠ 同向、配對紅利低")}");
    Console.WriteLine($"  判定:OOS Sharpe {oos.sharpe:F2} + 成本後 {(full.sharpe > 0.3m ? "撐得住" : "被成本咬爛")} → {(oos.sharpe > 0.3m && full.sharpe > 0.3m ? "可進 paper" : "反轉被換手成本吃掉、典型結果、不上")}");
}

// ════════════════════════════════════════════════════════════════════════════
// --fundsig 資金費當跨幣訊號:contrarian — long funding 最低(空擁擠)/short 最高(多擁擠)。
// ════════════════════════════════════════════════════════════════════════════
async Task RunFundSig()
{
    int topK = (int)(decimal.TryParse(Environment.GetEnvironmentVariable("FUNDSIG_TOPK"), out var tk) ? tk : 3m);
    decimal costSide = 0.0008m;
    Console.WriteLine("=== 資金費跨幣訊號 --fundsig(contrarian:long 低 funding / short 高 funding、等權)===\n");

    async Task<List<decimal>> FetchFundDaily(string sym)
    {
        // Binance fundingRate 單次 limit 實測只回 ~200 筆 → 分頁往回(endTime 遞減)抓 ~2 年。
        try
        {
            var all = new List<(long t, decimal r)>();
            long? endTime = null;
            for (int page = 0; page < 12; page++)
            {
                var url = $"https://fapi.binance.com/fapi/v1/fundingRate?symbol={sym}&limit=1000" + (endTime.HasValue ? $"&endTime={endTime.Value}" : "");
                var json = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var batch = new List<(long, decimal)>();
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.TryGetProperty("fundingTime", out var ft) && decimal.TryParse(e.GetProperty("fundingRate").GetString(), System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        batch.Add((ft.GetInt64(), v));
                if (batch.Count == 0) break;
                all.AddRange(batch);
                endTime = batch.Min(x => x.Item1) - 1;   // 往更早抓
                if (batch.Count < 150) break;            // 沒更多了
                await Task.Delay(120);
            }
            var pts = all.OrderBy(x => x.t).Select(x => x.r).ToList();
            var daily = new List<decimal>();   // 3 個 8h ≈ 1 日
            for (int i = 0; i + 2 < pts.Count; i += 3) daily.Add(pts[i] + pts[i + 1] + pts[i + 2]);
            return daily;
        }
        catch { return new List<decimal>(); }
    }

    var fund = new Dictionary<string, List<decimal>>(); var px = new Dictionary<string, List<decimal>>();
    foreach (var sym in symbols)
    {
        var fd = await FetchFundDaily(sym); var b = await Fetch(sym);
        // 要求 ≥250 天 funding+price,避免某個短歷史幣把對齊窗口截到很短(→ 過擬合假象)
        if (fd.Count >= 250 && b.Count >= 250) { fund[sym] = fd; px[sym] = b.Select(x => x.Close).ToList(); }
        else Console.WriteLine($"  (跳過 {Sh(sym)}:funding {fd.Count}d / price {b.Count}d 不足 250)");
    }
    var coins = fund.Keys.ToList();
    if (coins.Count < topK * 2 + 1) { Console.WriteLine("資料不足。"); return; }
    int L = coins.Min(c => Math.Min(fund[c].Count, px[c].Count));
    var fd2 = coins.ToDictionary(c => c, c => fund[c].Skip(fund[c].Count - L).ToList());
    var px2 = coins.ToDictionary(c => c, c => px[c].Skip(px[c].Count - L).ToList());
    Console.WriteLine($"宇宙:{coins.Count} 幣、對齊尾段 {L} 日\n");

    (List<decimal> eq, decimal tov) Run(int sigLb, int rebal, decimal cost)
    {
        var eq = new List<decimal> { 1m }; List<string> curL = new(), curS = new(); int rb = 0; decimal ts = 0m;
        for (int t = sigLb; t < L - 1; t++)
        {
            if ((t - sigLb) % rebal == 0)
            {
                var rank = coins.Select(c => (c, s: fd2[c].Skip(t - sigLb).Take(sigLb).DefaultIfEmpty(0m).Average())).OrderBy(x => x.s).ToList();   // 升序:funding 最低在前
                var nL = rank.Take(topK).Select(x => x.c).ToList();          // long 低 funding
                var nS = rank.AsEnumerable().Reverse().Take(topK).Select(x => x.c).ToList();  // short 高 funding
                int changed = nL.Except(curL).Count() + curL.Except(nL).Count() + nS.Except(curS).Count() + curS.Except(nS).Count();
                decimal tov = (decimal)changed / (4 * topK); ts += tov; rb++;
                eq[^1] *= (1m - tov * 2m * cost); curL = nL; curS = nS;
            }
            decimal Ret(List<string> s) => s.Count == 0 ? 0m : s.Average(c => px2[c][t] > 0 ? (px2[c][t + 1] - px2[c][t]) / px2[c][t] : 0m);
            eq.Add(eq[^1] * (1m + Ret(curL) - Ret(curS)));
        }
        return (eq, rb > 0 ? ts / rb : 0m);
    }

    int[] lbs = { 3, 7, 14 }; int[] rbs = { 3, 7 };
    Console.WriteLine("=== 敏感度(realistic 成本;格=Sharpe / 年化%)===");
    Console.WriteLine("  sigLookback\\rebal " + string.Join("", rbs.Select(r => $"{r + "d",14}")));
    foreach (var lb in lbs)
    {
        var cells = rbs.Select(rb => { var (eq, _) = Run(lb, rb, costSide); var st = StatsOf(eq); var ann = eq.Count > 30 ? (decimal)((Math.Pow((double)eq[^1], 365.0 / eq.Count) - 1) * 100) : 0m; return $"{st.sharpe,6:F2}/{ann,5:F0}%"; });
        Console.WriteLine($"  {lb + "d",-17}" + string.Join("  ", cells));
    }
    var (eqF, tov2) = Run(7, 7, costSide);
    var full = StatsOf(eqF); var oos = StatsOf(eqF.Skip(eqF.Count / 2).ToList());
    Console.WriteLine($"\n=== 代表 funding-contrarian sigLb7/rebal7 ===");
    Console.WriteLine($"  全期: Sharpe {full.sharpe:F2} ret {full.ret:F0}% maxDD {full.dd:F0}% 換手 {tov2:P0}");
    Console.WriteLine($"  後半 OOS: Sharpe {oos.sharpe:F2} ret {oos.ret:F0}%");
    Console.WriteLine($"  判定:OOS Sharpe {oos.sharpe:F2} → {(oos.sharpe > 0.3m ? "✅ 有戲、可進 paper" : oos.sharpe > 0m ? "⚠ 微弱" : "❌ 無 edge")}(funding 含 carry 成分、本就帶 contrarian 味)");
}

// ════════════════════════════════════════════════════════════════════════════
// --tsmom 時序動量(managed futures):每幣按自己趨勢符號 +1/−1、等權籃子。
// 牛市多數幣 +1→淨多、熊市 −1→淨空(危機 alpha)。歷史最穩健系統性 edge,測它在 crypto 成不成立。
// ════════════════════════════════════════════════════════════════════════════
async Task RunTsMom()
{
    decimal EnvT(string k, decimal def) => decimal.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : def;
    decimal costSide = EnvT("TSMOM_COST_PER_SIDE", 0.0008m);
    Console.WriteLine("=== 時序動量 --tsmom(每幣按自己趨勢 +1/−1、等權;熊市自動翻空)===\n");

    var dat = new Dictionary<string, List<BarData>>();
    foreach (var sym in symbols) { try { var b = await Fetch(sym); if (b.Count >= 400) dat[sym] = b; } catch { } }
    var coins = dat.Keys.ToList();
    int L = dat.Values.Min(b => b.Count);
    var cl = coins.ToDictionary(c => c, c => dat[c].Skip(dat[c].Count - L).Select(b => b.Close).ToList());
    Console.WriteLine($"宇宙:{coins.Count} 幣、對齊尾段 {L} 日\n");

    // longOnly=false:多空(趨勢上+1/下−1);true:純多(上+1/下0)。回傳 (eq, 平均淨曝險, 換手)
    (List<decimal> eq, decimal netExp, decimal tov) Run(int lookback, int rebal, bool longOnly, decimal cost)
    {
        var eq = new List<decimal> { 1m }; var sig = new Dictionary<string, int>(); int rb = 0; decimal ts = 0m, netSum = 0m; int netN = 0;
        foreach (var c in coins) sig[c] = 0;
        for (int t = lookback; t < L - 1; t++)
        {
            if ((t - lookback) % rebal == 0)
            {
                int changed = 0;
                foreach (var c in coins)
                {
                    int s = cl[c][t] > cl[c][t - lookback] ? 1 : (longOnly ? 0 : -1);
                    if (s != sig[c]) changed++;
                    sig[c] = s;
                }
                decimal tov = (decimal)changed / coins.Count; ts += tov; rb++;
                eq[^1] *= (1m - tov * 2m * cost);
                netSum += (decimal)sig.Values.Sum() / coins.Count; netN++;
            }
            // 等權:每幣權重 = sig/N(總 gross 曝險 ≤ 1)
            decimal pr = coins.Where(c => sig[c] != 0).Sum(c => (decimal)sig[c] / coins.Count * (cl[c][t] > 0 ? (cl[c][t + 1] - cl[c][t]) / cl[c][t] : 0m));
            eq.Add(eq[^1] * (1m + pr));
        }
        return (eq, netN > 0 ? netSum / netN : 0m, rb > 0 ? ts / rb : 0m);
    }

    int[] lbs = { 20, 50, 100, 150, 200 }; int[] rbs = { 3, 7, 14 };
    foreach (var lo in new[] { false, true })
    {
        Console.WriteLine($"=== {(lo ? "純多趨勢" : "多空(熊市翻空)")} 敏感度(realistic;格=Sharpe / 年化%)===");
        Console.WriteLine("  lookback\\rebal " + string.Join("", rbs.Select(r => $"{r + "d",14}")));
        foreach (var lb in lbs)
        {
            var cells = rbs.Select(rb => { var (eq, _, _) = Run(lb, rb, lo, costSide); var st = StatsOf(eq); var ann = eq.Count > 30 ? (decimal)((Math.Pow((double)eq[^1], 365.0 / eq.Count) - 1) * 100) : 0m; return $"{st.sharpe,6:F2}/{ann,5:F0}%"; });
            Console.WriteLine($"  {lb + "d",-14}" + string.Join("  ", cells));
        }
        Console.WriteLine();
    }

    int repLb = (int)EnvT("TSMOM_LOOKBACK", 100m), repRb = (int)EnvT("TSMOM_REBAL", 7m);
    var (eqF, net, tov2) = Run(repLb, repRb, false, costSide);
    var full = StatsOf(eqF); var oos = StatsOf(eqF.Skip(eqF.Count / 2).ToList());
    // 熊市表現:用等權買入持有的最差 1/4 段對照(危機 alpha 關鍵)
    var bhEq = new List<decimal> { 1m };
    for (int t = 0; t < L - 1; t++) { decimal r = coins.Average(c => cl[c][t] > 0 ? (cl[c][t + 1] - cl[c][t]) / cl[c][t] : 0m); bhEq.Add(bhEq[^1] * (1m + r)); }
    var bh = StatsOf(bhEq);
    Console.WriteLine($"=== 代表 多空 lookback{repLb}/rebal{repRb} ===");
    Console.WriteLine($"  全期 realistic: Sharpe {full.sharpe:F2} ret {full.ret:F0}% maxDD {full.dd:F0}% · 平均淨曝險 {net:P0}(>0偏多)· 換手 {tov2:P0}");
    Console.WriteLine($"  後半 OOS: Sharpe {oos.sharpe:F2} ret {oos.ret:F0}% maxDD {oos.dd:F0}%");
    Console.WriteLine($"  vs 等權買入持有: Sharpe {bh.sharpe:F2} maxDD {bh.dd:F0}%  → TSMOM maxDD {(full.dd < bh.dd ? "更低(趨勢避險生效)" : "沒更低")}");
    try
    {
        var ens = strats.First(x => x.name == "decorr4_ls").s;
        var dc = LongShortBacktestEngine.Run(ens, dat["BTCUSDT"], new StrategyConfig { Symbol = "BTCUSDT", Interval = "1d" }, commission: gComm, slippagePct: gSlip).EquityCurve.Select(e => e.Value).ToList();
        int n = Math.Min(eqF.Count, dc.Count);
        var rho = CorrelationGuard.PearsonOfReturns(eqF.Skip(eqF.Count - n).ToList(), dc.Skip(dc.Count - n).ToList());
        Console.WriteLine($"  與 decorr4@BTC ρ = {rho:F2}");
    }
    catch { }
    Console.WriteLine($"\n  判定:OOS Sharpe {oos.sharpe:F2} + maxDD {(full.dd < bh.dd ? "<B&H(危機 alpha)" : "")} → {(oos.sharpe > 0.3m ? "✅ 可進 paper" : oos.sharpe > 0m ? "⚠ 微弱" : "❌ 無 edge")}");
}

// ════════════════════════════════════════════════════════════════════════════
// --pairs 配對 stat-arb:log 價差(比值)均值回歸。formation 選會回歸的對、trading(OOS)交易 z-score。
// 結構上跟方向性策略完全不同(價差回歸)→ 最可能真去相關。
// ════════════════════════════════════════════════════════════════════════════
async Task RunPairs()
{
    decimal EnvP(string k, decimal def) => decimal.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : def;
    decimal entryZ = EnvP("PAIRS_ENTRY_Z", 2.0m), exitZ = EnvP("PAIRS_EXIT_Z", 0.5m), stopZ = EnvP("PAIRS_STOP_Z", 4.0m);
    int zWin = (int)EnvP("PAIRS_ZWIN", 30m);
    decimal costSide = EnvP("PAIRS_COST_PER_SIDE", 0.0008m);
    Console.WriteLine($"=== 配對 stat-arb --pairs(log 價差均值回歸;entryZ={entryZ} exitZ={exitZ} zWin={zWin})===\n");

    var dat = new Dictionary<string, List<BarData>>();
    foreach (var sym in symbols) { try { var b = await Fetch(sym); if (b.Count >= 400) dat[sym] = b; } catch { } }
    var coins = dat.Keys.ToList();
    int L = dat.Values.Min(b => b.Count);
    var lp = coins.ToDictionary(c => c, c => dat[c].Skip(dat[c].Count - L).Select(b => (double)Math.Log((double)b.Close)).ToList());
    var ret = coins.ToDictionary(c => c, c => { var r = dat[c].Skip(dat[c].Count - L).Select(b => b.Close).ToList(); var o = new List<decimal>(); for (int t = 1; t < r.Count; t++) o.Add(r[t - 1] > 0 ? (r[t] - r[t - 1]) / r[t - 1] : 0m); return o; });
    int form = L / 2;   // formation = 前半;trading(OOS)= 後半
    Console.WriteLine($"宇宙:{coins.Count} 幣、{L} 日(formation 前 {form} / trading 後 {L - form})\n");

    // formation 選會均值回歸的對:half-life(AR(1) φ via lag-1 autocorr)在可交易區間
    var selected = new List<(string a, string b, double half)>();
    for (int i = 0; i < coins.Count; i++)
        for (int j = i + 1; j < coins.Count; j++)
        {
            var s = new double[form];
            for (int t = 0; t < form; t++) s[t] = lp[coins[i]][t] - lp[coins[j]][t];
            double mean = s.Average();
            double num = 0, den = 0;
            for (int t = 1; t < form; t++) { num += (s[t] - mean) * (s[t - 1] - mean); den += (s[t - 1] - mean) * (s[t - 1] - mean); }
            double phi = den > 0 ? num / den : 1;
            if (phi <= 0 || phi >= 1) continue;
            double half = -Math.Log(2) / Math.Log(phi);
            if (half >= 3 && half <= 40) selected.Add((coins[i], coins[j], half));
        }
    selected = selected.OrderBy(x => x.half).Take((int)EnvP("PAIRS_MAX", 25m)).ToList();
    Console.WriteLine($"formation 選出 {selected.Count} 對可交易(half-life 3-40 天)。前幾對:");
    foreach (var p in selected.Take(6)) Console.WriteLine($"   {Sh(p.a)}-{Sh(p.b)} half-life {p.half:F0}d");

    if (selected.Count == 0) { Console.WriteLine("無可交易配對。"); return; }

    // trading(OOS):每對 rolling z-score 進出、dollar-neutral 1:1、組合等權
    var portRet = new List<decimal>();   // 每日組合報酬(交易窗)
    for (int t = form + zWin; t < L - 1; t++)
    {
        var dayRets = new List<decimal>();
        foreach (var (ca, cb, _) in selected)
        {
            // rolling mean/std of spread over [t-zWin, t]
            double m = 0; for (int k = t - zWin; k < t; k++) m += lp[ca][k] - lp[cb][k]; m /= zWin;
            double v = 0; for (int k = t - zWin; k < t; k++) { var d = (lp[ca][k] - lp[cb][k]) - m; v += d * d; } v = Math.Sqrt(v / zWin);
            if (v <= 0) continue;
            double z = ((lp[ca][t] - lp[cb][t]) - m) / v;
            // 簡化:當期 z 決定當日持倉(|z|>entry 進、>stop 不進、否則 flat);P&L = pos×(r_a−r_b)
            int pos = z > (double)entryZ && z < (double)stopZ ? -1 : z < -(double)entryZ && z > -(double)stopZ ? 1 : 0;
            if (pos == 0) continue;
            dayRets.Add((decimal)pos * (ret[ca][t] - ret[cb][t]) - 2m * costSide / zWin);  // 攤平成本(粗估)
        }
        portRet.Add(dayRets.Count > 0 ? dayRets.Average() : 0m);
    }
    var eq = new List<decimal> { 1m }; foreach (var r in portRet) eq.Add(eq[^1] * (1m + r));
    var st = StatsOf(eq);
    Console.WriteLine($"\n=== 組合(OOS 交易窗、{selected.Count} 對等權)===");
    Console.WriteLine($"  Sharpe {st.sharpe:F2}  ret {st.ret:F0}%  maxDD {st.dd:F0}%  交易日 {portRet.Count(r => r != 0)}/{portRet.Count}");
    try
    {
        var ens = strats.First(x => x.name == "decorr4_ls").s;
        var dc = LongShortBacktestEngine.Run(ens, dat["BTCUSDT"], new StrategyConfig { Symbol = "BTCUSDT", Interval = "1d" }, commission: gComm, slippagePct: gSlip).EquityCurve.Select(e => e.Value).ToList();
        int n = Math.Min(eq.Count, dc.Count);
        var rho = CorrelationGuard.PearsonOfReturns(eq.Skip(eq.Count - n).ToList(), dc.Skip(dc.Count - n).ToList());
        Console.WriteLine($"  與 decorr4@BTC ρ = {rho:F2} → {(Math.Abs((double)rho) < 0.3 ? "✅ 低相關" : "⚠ 偏相關")}");
    }
    catch { }
    Console.WriteLine($"  判定:OOS Sharpe {st.sharpe:F2} → {(st.sharpe > 0.5m ? "✅ 有戲" : st.sharpe > 0.2m ? "⚠ 弱" : "❌ 無 edge")}(stat-arb 對成本/流動性敏感、實盤更難)");
}

// ════════════════════════════════════════════════════════════════════════════
// --lowvol 橫斷面低波動異常:long 低波動幣 / short 高波動幣(風險異常、與動量不同維度)。
// ════════════════════════════════════════════════════════════════════════════
async Task RunLowVol()
{
    int topK = (int)(decimal.TryParse(Environment.GetEnvironmentVariable("LOWVOL_TOPK"), out var k) ? k : 3m);
    decimal costSide = 0.0008m;
    Console.WriteLine("=== 橫斷面低波動 --lowvol(long 低波動 / short 高波動、等權)===\n");
    var dat = new Dictionary<string, List<BarData>>();
    foreach (var sym in symbols) { try { var b = await Fetch(sym); if (b.Count >= 400) dat[sym] = b; } catch { } }
    var coins = dat.Keys.ToList();
    int L = dat.Values.Min(b => b.Count);
    var cl = coins.ToDictionary(c => c, c => dat[c].Skip(dat[c].Count - L).Select(b => b.Close).ToList());
    Console.WriteLine($"宇宙:{coins.Count} 幣、{L} 日\n");

    (List<decimal> eq, decimal tov) Run(int volLb, int rebal, decimal cost)
    {
        var eq = new List<decimal> { 1m }; List<string> curL = new(), curS = new(); int rb = 0; decimal ts = 0m;
        decimal Vol(string c, int t) { var r = new List<double>(); for (int k = t - volLb + 1; k <= t; k++) if (cl[c][k - 1] > 0) r.Add((double)((cl[c][k] - cl[c][k - 1]) / cl[c][k - 1])); if (r.Count < 2) return 0m; var a = r.Average(); return (decimal)Math.Sqrt(r.Select(x => (x - a) * (x - a)).Average()); }
        for (int t = volLb; t < L - 1; t++)
        {
            if ((t - volLb) % rebal == 0)
            {
                var rank = coins.Select(c => (c, v: Vol(c, t))).Where(x => x.v > 0).OrderBy(x => x.v).ToList();
                var nL = rank.Take(topK).Select(x => x.c).ToList();           // 低波動
                var nS = rank.AsEnumerable().Reverse().Take(topK).Select(x => x.c).ToList();  // 高波動
                int changed = nL.Except(curL).Count() + curL.Except(nL).Count() + nS.Except(curS).Count() + curS.Except(nS).Count();
                decimal tov = (decimal)changed / (4 * topK); ts += tov; rb++;
                eq[^1] *= (1m - tov * 2m * cost); curL = nL; curS = nS;
            }
            decimal Ret(List<string> s) => s.Count == 0 ? 0m : s.Average(c => cl[c][t] > 0 ? (cl[c][t + 1] - cl[c][t]) / cl[c][t] : 0m);
            eq.Add(eq[^1] * (1m + Ret(curL) - Ret(curS)));
        }
        return (eq, rb > 0 ? ts / rb : 0m);
    }
    int[] lbs = { 14, 30, 60 }; int[] rbs = { 7, 14 };
    Console.WriteLine("=== 敏感度(realistic;格=Sharpe / 年化%)===");
    Console.WriteLine("  volLb\\rebal " + string.Join("", rbs.Select(r => $"{r + "d",14}")));
    foreach (var lb in lbs)
    {
        var cells = rbs.Select(rb => { var (eq, _) = Run(lb, rb, costSide); var s = StatsOf(eq); var ann = eq.Count > 30 ? (decimal)((Math.Pow((double)eq[^1], 365.0 / eq.Count) - 1) * 100) : 0m; return $"{s.sharpe,6:F2}/{ann,5:F0}%"; });
        Console.WriteLine($"  {lb + "d",-11}" + string.Join("  ", cells));
    }
    var (eqF, _) = Run(30, 14, costSide);
    var full = StatsOf(eqF); var oos = StatsOf(eqF.Skip(eqF.Count / 2).ToList());
    Console.WriteLine($"\n  代表 30/14: 全期 Sharpe {full.sharpe:F2} / OOS {oos.sharpe:F2} → {(oos.sharpe > 0.3m ? "✅ 有戲" : oos.sharpe > 0m ? "⚠ 弱" : "❌ 無 edge")}");
}

// ════════════════════════════════════════════════════════════════════════════
// --harmonic 諧波/fib 跨時框重驗:觸發頻率(諧波常常幾乎不觸發)+ OOS + 池化顯著性。
// 測「換低時框(更多觸發)能不能把無 edge 的機械諧波救回來」這個還沒系統試過的變體。
// ════════════════════════════════════════════════════════════════════════════
async Task RunHarmonic()
{
    Console.WriteLine("=== 諧波 / Fib 跨時框重驗(機械版;memory 已記 1d 無 OOS edge、本次測低時框變體)===\n");
    var rng2 = new Random(7);
    (decimal lo, double t) Boot(List<decimal> xs)
    {
        if (xs.Count < 5) return (0, 0);
        double mean = (double)xs.Average();
        double sd = Math.Sqrt(xs.Select(x => ((double)x - mean) * ((double)x - mean)).Sum() / (xs.Count - 1));
        double se = sd / Math.Sqrt(xs.Count); double tStat = se > 0 ? mean / se : 0;
        var ms = new double[1500];
        for (int b = 0; b < 1500; b++) { double s = 0; for (int i = 0; i < xs.Count; i++) s += (double)xs[rng2.Next(xs.Count)]; ms[b] = s / xs.Count; }
        Array.Sort(ms); return ((decimal)ms[37], tStat);   // ~2.5% 分位
    }

    // 受測:harmonic_ls / fib_retrace_ls(多空)+ harmonic_pattern / fibonacci_retracement(純多)
    var ls = new HashSet<string> { "harmonic_ls", "fib_retrace_ls" };
    var cand = new (string name, IStrategy s, bool isLs)[]
    {
        ("harmonic_ls",            strats.First(x => x.name == "harmonic_ls").s, true),
        ("fib_retrace_ls",         strats.First(x => x.name == "fib_retrace_ls").s, true),
        ("harmonic_pattern",       new HarmonicStrategy(), false),
        ("fibonacci_retracement",  new FibonacciStrategy(), false),
    };
    string[] tfs = { "1d", "12h", "4h" };

    foreach (var iv in tfs)
    {
        // 抓該時框宇宙
        var dv = new Dictionary<string, List<BarData>>();
        foreach (var sym in symbols) { try { var b = await Fetch(sym, iv); if (b.Count >= 350) dv[sym] = b; } catch { } }
        Console.WriteLine($"=== 時框 {iv}({dv.Count} 幣)===");
        Console.WriteLine($"  {"策略",-22}{"交易/千根",11}{"full Sh",9}{"OOS中位%",10}{"顯著CI下界",12}  判定");
        foreach (var (name, s, isLs) in cand)
        {
            var trades = new List<decimal>(); var fullSh = new List<decimal>(); var oos = new List<decimal>(); var pool = new List<decimal>();
            foreach (var kv in dv)
            {
                var cfg = new StrategyConfig { Symbol = kv.Key, Interval = iv };
                try
                {
                    if (isLs)
                    {
                        var bt = LongShortBacktestEngine.Run(s, kv.Value, cfg, commission: gComm, slippagePct: gSlip);
                        if (bt.TotalBars < 100) continue;
                        trades.Add(bt.TotalTrades / (decimal)kv.Value.Count * 1000m); fullSh.Add(bt.SharpeRatio);
                        var w = LongShortBacktestEngine.RunWalkForward(s, kv.Value, cfg, 250, 90, 60, commission: gComm, slippagePct: gSlip);
                        if (w.TotalFolds > 0) { oos.Add(w.AvgTestReturnPct); foreach (var f in w.Folds.Where(f => f.Test != null)) pool.Add(f.Test!.TotalReturnPct); }
                    }
                    else
                    {
                        var bt = BacktestEngine.Run(s, kv.Value, cfg, commission: gComm, slippagePct: gSlip);
                        if (bt.TotalBars < 100) continue;
                        trades.Add(bt.TotalTrades / (decimal)kv.Value.Count * 1000m); fullSh.Add(bt.SharpeRatio);
                        var w = BacktestEngine.RunWalkForward(s, kv.Value, cfg, 250, 90, 60, commission: gComm, slippagePct: gSlip);
                        if (w.TotalFolds > 0) { oos.Add(w.AvgTestReturnPct); foreach (var f in w.Folds.Where(f => f.Test != null)) pool.Add(f.Test!.TotalReturnPct); }
                    }
                }
                catch { }
            }
            if (trades.Count == 0) { Console.WriteLine($"  {name,-22}(無資料)"); continue; }
            var (ciLo, _) = Boot(pool);
            decimal tpk = Math.Round(trades.Average(), 1);
            decimal medOos = Median(oos);
            bool sig = ciLo > 0m && pool.Count >= 5;
            bool tooRare = tpk < 1m;
            string verdict = tooRare ? "❌ 幾乎不觸發" : sig && medOos > 0m ? "✅ 有戲?!" : "❌ 無 edge";
            Console.WriteLine($"  {name,-22}{tpk,11:F1}{fullSh.Average(),9:F2}{medOos,10:F1}{ciLo,12:F1}  {verdict}");
        }
        Console.WriteLine();
    }
    Console.WriteLine("判定:諧波機械版的死穴是『1d 幾乎不觸發』;換低時框觸發變多 → 看 OOS+顯著性有沒有救回。");
}

// --allocate 用:一條部署腿(策略 → 指派到的幣)的統計。Folds=跨幣池化 fold 數、Breadth=幾成幣 OOS 正
record Leg(string Name, string Coin, decimal Sharpe, decimal Vol, System.Collections.Generic.List<decimal> Curve, int Folds, decimal CiLo, double T, decimal Ret, decimal Dd, decimal Breadth);

// --allocate 候選:一支策略跨所有幣的成績(PerCoin)+ 池化顯著性,供入場閘 + 唯一幣指派
record Cand(string Name, System.Collections.Generic.Dictionary<string, (decimal sh, System.Collections.Generic.List<decimal> curve, decimal ret, decimal dd)> PerCoin, int Folds, decimal CiLo, double T, decimal Breadth, decimal BestSharpe, decimal BestRet);
