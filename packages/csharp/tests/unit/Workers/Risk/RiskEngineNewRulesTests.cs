using RiskWorker.Engine;
using RiskWorker.Models;

namespace Unit.Tests.Workers.Risk;

/// <summary>
/// 鎖住 cut 5 加的兩條新規則的契約：
///   - min_cash_reserve：買入後不能讓 cash 低於 threshold
///   - max_position_count：不能讓持有的不同標的數超過 threshold
/// 既有 6 條規則的測試覆蓋是更早的 PR 該補的，這裡只覆蓋本 cut 新加的。
/// </summary>
public class RiskEngineNewRulesTests
{
    // ── min_cash_reserve ───────────────────────────────────────────

    [Fact]
    public void MinCashReserve_BuyKeepingCashAboveFloor_Passes()
    {
        var engine = new RiskEngine(new() { Rule("r1", "min_cash_reserve", 500m) });
        var portfolio = new PortfolioSnapshot { Cash = 2000m };

        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 1m, estimatedPrice: 100m, portfolio);

        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void MinCashReserve_BuyDroppingCashBelowFloor_Fails()
    {
        var engine = new RiskEngine(new() { Rule("r1", "min_cash_reserve", 500m) });
        var portfolio = new PortfolioSnapshot { Cash = 600m };

        // 600 - 200 = 400 < 500 floor
        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 1m, estimatedPrice: 200m, portfolio);

        r.Passed.Should().BeFalse();
        r.Violations.Should().ContainSingle(v => v.RuleId == "r1");
    }

    [Fact]
    public void MinCashReserve_Sell_AlwaysPasses()
    {
        // 賣出會增加現金，不該被 reserve 規則卡到（即使現在低於 floor）
        var engine = new RiskEngine(new() { Rule("r1", "min_cash_reserve", 500m) });
        var portfolio = new PortfolioSnapshot { Cash = 100m };

        var r = engine.Check("AAPL", "alpaca", "sell", quantity: 1m, estimatedPrice: 200m, portfolio);

        r.Passed.Should().BeTrue();
    }

    // ── max_position_count ─────────────────────────────────────────

    [Fact]
    public void MaxPositionCount_BuyExistingHolding_Passes()
    {
        // 已經持有 AAPL，加碼不算「新增」一個位
        var engine = new RiskEngine(new() { Rule("r1", "max_position_count", 3m) });
        var portfolio = new PortfolioSnapshot
        {
            Positions = new()
            {
                Pos("AAPL", qty: 5),
                Pos("TSLA", qty: 2),
                Pos("MSFT", qty: 1),
            },
        };

        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 1m, estimatedPrice: 100m, portfolio);

        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void MaxPositionCount_BuyNewSymbolWhenAtLimit_Fails()
    {
        var engine = new RiskEngine(new() { Rule("r1", "max_position_count", 3m) });
        var portfolio = new PortfolioSnapshot
        {
            Positions = new()
            {
                Pos("AAPL", qty: 5),
                Pos("TSLA", qty: 2),
                Pos("MSFT", qty: 1),
            },
        };

        // 已持有 3 個（達上限），買 NVDA 會新增第 4 個
        var r = engine.Check("NVDA", "alpaca", "buy", quantity: 1m, estimatedPrice: 100m, portfolio);

        r.Passed.Should().BeFalse();
        r.Violations.Should().ContainSingle(v => v.RuleId == "r1");
    }

    [Fact]
    public void MaxPositionCount_BuyNewSymbolWhenBelowLimit_Passes()
    {
        var engine = new RiskEngine(new() { Rule("r1", "max_position_count", 5m) });
        var portfolio = new PortfolioSnapshot
        {
            Positions = new() { Pos("AAPL", qty: 5), Pos("TSLA", qty: 2) },
        };

        var r = engine.Check("NVDA", "alpaca", "buy", quantity: 1m, estimatedPrice: 100m, portfolio);

        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void MaxPositionCount_ZeroQtyPositionsDoNotCount()
    {
        // 殘留 0 quantity 的歷史 position 紀錄不該佔名額
        var engine = new RiskEngine(new() { Rule("r1", "max_position_count", 2m) });
        var portfolio = new PortfolioSnapshot
        {
            Positions = new()
            {
                Pos("AAPL", qty: 5),
                Pos("TSLA", qty: 0),    // 已平倉、紀錄還在
                Pos("MSFT", qty: 0),    // 同上
            },
        };

        var r = engine.Check("NVDA", "alpaca", "buy", quantity: 1m, estimatedPrice: 100m, portfolio);

        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void MaxPositionCount_Sell_AlwaysPasses()
    {
        var engine = new RiskEngine(new() { Rule("r1", "max_position_count", 2m) });
        var portfolio = new PortfolioSnapshot
        {
            Positions = new() { Pos("A", 1), Pos("B", 1), Pos("C", 1), Pos("D", 1) },
        };

        var r = engine.Check("X", "alpaca", "sell", quantity: 1m, estimatedPrice: 100m, portfolio);

        r.Passed.Should().BeTrue();
    }

    // ── cooldown_seconds ───────────────────────────────────────────

    [Fact]
    public void Cooldown_NoLastTrade_AlwaysPasses()
    {
        // 第一次交易這個 symbol、dict 裡沒紀錄 → 不違反
        var engine = new RiskEngine(new() { Rule("r1", "cooldown_seconds", 60m) });
        var portfolio = new PortfolioSnapshot();   // 空 LastTradeBySymbol

        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 1m, estimatedPrice: 100m, portfolio);

        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void Cooldown_LastTradeRecent_BlocksOrder()
    {
        var engine = new RiskEngine(new() { Rule("r1", "cooldown_seconds", 60m) });
        var portfolio = new PortfolioSnapshot
        {
            LastTradeBySymbol = { ["alpaca:AAPL"] = DateTime.UtcNow.AddSeconds(-10) },   // 10 秒前剛交易
        };

        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 1m, estimatedPrice: 100m, portfolio);

        r.Passed.Should().BeFalse();
        r.Violations.Should().ContainSingle(v => v.RuleId == "r1");
    }

    [Fact]
    public void Cooldown_LastTradeBeyondWindow_Passes()
    {
        var engine = new RiskEngine(new() { Rule("r1", "cooldown_seconds", 60m) });
        var portfolio = new PortfolioSnapshot
        {
            LastTradeBySymbol = { ["alpaca:AAPL"] = DateTime.UtcNow.AddSeconds(-120) },   // 2 分鐘前 OK
        };

        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 1m, estimatedPrice: 100m, portfolio);

        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void Cooldown_DifferentSymbol_DoesNotShareCooldown()
    {
        // AAPL 剛交易過、TSLA 沒交易過 —— cooldown 不該擋 TSLA
        var engine = new RiskEngine(new() { Rule("r1", "cooldown_seconds", 60m) });
        var portfolio = new PortfolioSnapshot
        {
            LastTradeBySymbol = { ["alpaca:AAPL"] = DateTime.UtcNow.AddSeconds(-5) },
        };

        var r = engine.Check("TSLA", "alpaca", "buy", quantity: 1m, estimatedPrice: 100m, portfolio);

        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void Cooldown_SameSymbolDifferentExchange_DoesNotShareCooldown()
    {
        // 同一 symbol 但不同交易所 —— 各自獨立的 cooldown
        var engine = new RiskEngine(new() { Rule("r1", "cooldown_seconds", 60m) });
        var portfolio = new PortfolioSnapshot
        {
            LastTradeBySymbol = { ["alpaca:BTC"] = DateTime.UtcNow.AddSeconds(-5) },
        };

        var r = engine.Check("BTC", "binance", "buy", quantity: 1m, estimatedPrice: 100m, portfolio);

        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void Cooldown_BlocksBothBuyAndSell()
    {
        // signal 抖動可能 buy→sell→buy 連環觸發、cooldown 必須兩個方向都擋
        var engine = new RiskEngine(new() { Rule("r1", "cooldown_seconds", 60m) });
        var portfolio = new PortfolioSnapshot
        {
            LastTradeBySymbol = { ["alpaca:AAPL"] = DateTime.UtcNow.AddSeconds(-10) },
        };

        var buyResult  = engine.Check("AAPL", "alpaca", "buy",  quantity: 1m, estimatedPrice: 100m, portfolio);
        var sellResult = engine.Check("AAPL", "alpaca", "sell", quantity: 1m, estimatedPrice: 100m, portfolio);

        buyResult.Passed.Should().BeFalse();
        sellResult.Passed.Should().BeFalse();
    }

    // ── time_window ────────────────────────────────────────────────

    [Fact]
    public void TimeWindow_NoParams_PassesAsNoOp()
    {
        // Params=null 時 silently 不擋，避免設定缺資料就把所有單擋掉
        var engine = new RiskEngine(new() { Rule("r1", "time_window", 0m, paramsJson: null) });
        var portfolio = new PortfolioSnapshot();

        engine.Check("AAPL", "alpaca", "buy", 1m, 100m, portfolio).Passed.Should().BeTrue();
    }

    [Fact]
    public void TimeWindow_NowInsideWindow_Passes()
    {
        // 視窗 = 現在 ±30 分鐘 → 一定包含現在
        var now = DateTime.UtcNow;
        var startHm = now.AddMinutes(-30).ToString("HH:mm");
        var endHm   = now.AddMinutes(30).ToString("HH:mm");
        var rule = Rule("r1", "time_window", 0m, paramsJson: $"{{\"start_hm\":\"{startHm}\",\"end_hm\":\"{endHm}\"}}");

        new RiskEngine(new() { rule }).Check("AAPL", "alpaca", "buy", 1m, 100m, new PortfolioSnapshot())
            .Passed.Should().BeTrue();
    }

    [Fact]
    public void TimeWindow_NowOutsideWindow_Fails()
    {
        // 視窗 = 現在 +1h ~ +2h → 不包含現在
        var now = DateTime.UtcNow;
        var startHm = now.AddHours(1).ToString("HH:mm");
        var endHm   = now.AddHours(2).ToString("HH:mm");
        var rule = Rule("r1", "time_window", 0m, paramsJson: $"{{\"start_hm\":\"{startHm}\",\"end_hm\":\"{endHm}\"}}");

        var r = new RiskEngine(new() { rule }).Check("AAPL", "alpaca", "buy", 1m, 100m, new PortfolioSnapshot());

        r.Passed.Should().BeFalse();
        r.Violations.Should().ContainSingle(v => v.RuleId == "r1");
    }

    [Fact]
    public void TimeWindow_CrossMidnightSyntax_StartGreaterThanEnd_ParsesWithoutCrash()
    {
        // 跨午夜語法：start=22:00 end=04:00 應該被解析為「22:00 → 隔天 04:00」分支
        // 這個 test 鎖「不會 crash + 行為定義」：22:00→04:00 視窗下、now 是 12:00 不該在視窗內
        // （exact pass/fail 取決於跑測時的 UTC.Now，所以這裡不斷言通過 / 失敗，只斷言 engine 不 throw）
        var rule = Rule("r1", "time_window", 0m, paramsJson: "{\"start_hm\":\"22:00\",\"end_hm\":\"04:00\"}");
        var act = () => new RiskEngine(new() { rule }).Check("AAPL", "alpaca", "buy", 1m, 100m, new PortfolioSnapshot());
        act.Should().NotThrow();
    }

    [Fact]
    public void TimeWindow_FullDayWindow_AlwaysPasses()
    {
        // 00:00-23:59 視窗無論何時跑都該包含 now —— 確認 same-day 分支運作正常
        var rule = Rule("r1", "time_window", 0m, paramsJson: "{\"start_hm\":\"00:00\",\"end_hm\":\"23:59\"}");
        new RiskEngine(new() { rule }).Check("AAPL", "alpaca", "buy", 1m, 100m, new PortfolioSnapshot())
            .Passed.Should().BeTrue();
    }

    [Fact]
    public void TimeWindow_MalformedParams_FailsSafe_DoesNotBlockTrades()
    {
        var rule = Rule("r1", "time_window", 0m, paramsJson: "this is not json");

        // 解析失敗時 rule 該 silently 跳過，不該因為設定錯就把交易擋光
        new RiskEngine(new() { rule }).Check("AAPL", "alpaca", "buy", 1m, 100m, new PortfolioSnapshot())
            .Passed.Should().BeTrue();
    }

    [Fact]
    public void TimeWindow_MissingFieldsInParams_FailsSafe()
    {
        var rule = Rule("r1", "time_window", 0m, paramsJson: "{\"start_hm\":\"09:30\"}");   // 缺 end_hm

        new RiskEngine(new() { rule }).Check("AAPL", "alpaca", "buy", 1m, 100m, new PortfolioSnapshot())
            .Passed.Should().BeTrue();
    }

    // ── DefaultRules 整合 ──────────────────────────────────────────

    [Fact]
    public void DefaultRules_IncludesAllNewRules()
    {
        // 確保新規則被加進預設集，避免之後不小心被移掉
        var defaults = RiskEngine.DefaultRules();
        defaults.Select(r => r.Type).Should()
            .Contain("min_cash_reserve")
            .And.Contain("max_position_count")
            .And.Contain("cooldown_seconds")
            .And.Contain("time_window");
    }

    [Fact]
    public void DefaultRules_TimeWindowDisabledByDefault_AndHasParams()
    {
        // time_window 預設停用（24/7 crypto 用戶不會被擋），但 params 必須有預設值給 US market
        var rule = RiskEngine.DefaultRules().Single(r => r.Type == "time_window");
        rule.Enabled.Should().BeFalse();
        rule.Params.Should().NotBeNullOrWhiteSpace();
        rule.Params.Should().Contain("start_hm").And.Contain("end_hm");
    }

    // ── max_loss_per_trade_pct (spot)：學自 ai-quant-starter2 commit 449398a─

    [Fact]
    public void MaxLossPerTradePctSpot_BuySmallEnough_Passes()
    {
        // Equity 10000, SL 5%, threshold 2% → 最大可損 200 USD → 最大名目 = 200/5% = 4000
        // 下單 1000 USD（1 share × $1000）→ 預估損 50 = 0.5% ≤ 2% → 過
        var engine = new RiskEngine(new() { Rule("r17", "max_loss_per_trade_pct", 2m) });
        var portfolio = new PortfolioSnapshot { PortfolioValue = 10000m };

        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 1m, estimatedPrice: 1000m, portfolio, initialSlPct: 5m);

        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void MaxLossPerTradePctSpot_BuyTooLarge_Fails()
    {
        // Equity 10000、SL 5%、threshold 2% → 最大可損 200、最大名目 4000
        // 下單 5000 USD（5 × $1000）→ 預估損 250 = 2.5% > 2% → 擋
        var engine = new RiskEngine(new() { Rule("r17", "max_loss_per_trade_pct", 2m) });
        var portfolio = new PortfolioSnapshot { PortfolioValue = 10000m };

        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 5m, estimatedPrice: 1000m, portfolio, initialSlPct: 5m);

        r.Passed.Should().BeFalse();
        r.Violations.Should().ContainSingle(v => v.RuleId == "r17");
        r.Violations[0].Message.Should().Contain("account risk limit");
    }

    [Fact]
    public void MaxLossPerTradePctSpot_Sell_NotChecked()
    {
        // 賣 = 平倉、不該被 account-risk 卡（即使單筆預估損 > 2%）
        var engine = new RiskEngine(new() { Rule("r17", "max_loss_per_trade_pct", 2m) });
        var portfolio = new PortfolioSnapshot { PortfolioValue = 10000m };

        var r = engine.Check("AAPL", "alpaca", "sell", quantity: 100m, estimatedPrice: 1000m, portfolio, initialSlPct: 5m);

        r.Violations.Should().NotContain(v => v.RuleId == "r17");
    }

    [Fact]
    public void MaxLossPerTradePctSpot_ZeroEquity_SkipRule()
    {
        // 零 equity 算不出比例（除零）→ 跳過、不算違規（讓其他規則決定）
        var engine = new RiskEngine(new() { Rule("r17", "max_loss_per_trade_pct", 2m) });
        var portfolio = new PortfolioSnapshot { PortfolioValue = 0m };

        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 1m, estimatedPrice: 1000m, portfolio, initialSlPct: 5m);

        r.Violations.Should().NotContain(v => v.RuleId == "r17");
    }

    [Fact]
    public void MaxLossPerTradePctSpot_TighterSl_AllowsLargerSize()
    {
        // SL 較緊（2%）→ 同 threshold 下、可下更大名目
        // Equity 10000、SL 2%、threshold 2% → 最大可損 200、最大名目 = 200/2% = 10000
        // 下單 8000 USD → 預估損 160 = 1.6% ≤ 2% → 過
        var engine = new RiskEngine(new() { Rule("r17", "max_loss_per_trade_pct", 2m) });
        var portfolio = new PortfolioSnapshot { PortfolioValue = 10000m };

        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 8m, estimatedPrice: 1000m, portfolio, initialSlPct: 2m);

        r.Passed.Should().BeTrue("SL 較緊 → 等同 risk 下允許更大倉");
    }

    [Fact]
    public void MaxLossPerTradePctSpot_DefaultSlPct_FallbackTo5()
    {
        // 不傳 initialSlPct → 預設 5%（match perp default）
        var engine = new RiskEngine(new() { Rule("r17", "max_loss_per_trade_pct", 2m) });
        var portfolio = new PortfolioSnapshot { PortfolioValue = 10000m };

        // 5000 USD × 5% = 250 = 2.5% > 2% → 應該擋
        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 5m, estimatedPrice: 1000m, portfolio);

        r.Passed.Should().BeFalse();
        r.Violations.Should().ContainSingle(v => v.RuleId == "r17");
    }

    [Fact]
    public void DefaultRules_IncludesSpotMaxLossPerTradePct()
    {
        // r17 預設啟用、2% threshold（業界 Van Tharp）
        var rule = RiskEngine.DefaultRules().Single(r => r.RuleId == "r17");
        rule.Type.Should().Be("max_loss_per_trade_pct");
        rule.Threshold.Should().Be(2m);
        rule.Enabled.Should().BeTrue();
    }

    // ── Scope 過濾：r14 (perp) / r17 (spot) 同 Type 不該重複觸發 ─────

    [Fact]
    public void Scope_PerpRule_NotFiredOnSpotPath()
    {
        // Scope="perp" 的規則不該在 spot Check() 路徑觸發、避免 r14 r17 同 Type 在 spot 跑兩次
        var perpRule = new RiskRule
        {
            RuleId = "r14", Name = "Max Loss Per Trade %",
            Type = "max_loss_per_trade_pct", Threshold = 2m, Enabled = true, Scope = "perp",
        };
        var engine = new RiskEngine(new() { perpRule });
        var portfolio = new PortfolioSnapshot { PortfolioValue = 10000m };

        // 5000 名目 × 5% SL = 250 = 2.5% > 2%、若 perp 規則錯誤套上 spot 路徑就會擋
        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 5m, estimatedPrice: 1000m, portfolio, initialSlPct: 5m);

        r.Passed.Should().BeTrue("Scope=perp 規則必須在 spot 路徑被略過");
        r.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Scope_SpotRule_NotFiredOnPerpPath()
    {
        // 對稱檢查：Scope="spot" 的規則不該在 perp CheckPerp() 路徑觸發
        var spotRule = new RiskRule
        {
            RuleId = "r17", Name = "Spot Max Loss Per Trade %",
            Type = "max_loss_per_trade_pct", Threshold = 2m, Enabled = true, Scope = "spot",
        };
        var engine = new RiskEngine(new() { spotRule });
        var snap = new PerpetualSnapshot { Balance = 100m };

        // 50 名目 × 5% = 2.5 = 2.5% > 2%、若 spot 規則錯誤套上 perp 就會擋
        var r = engine.CheckPerp(
            symbol: "BTCUSDT", exchange: "bingx", side: "BUY", positionSide: "LONG",
            quantity: 0.001m, estimatedPrice: 50000m, leverage: 5, snap, initialSlPct: 5m);

        r.Passed.Should().BeTrue("Scope=spot 規則必須在 perp 路徑被略過");
        r.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Scope_NullOrAny_FiresOnBothPaths_Backward()
    {
        // 向後相容：未設 Scope（null）的舊規則應該照舊在 spot 跟 perp 兩條路徑都套
        var rule = Rule("legacy", "max_loss_per_trade_pct", 2m); // helper 不設 Scope = null
        var spotEngine = new RiskEngine(new() { rule });
        var perpEngine = new RiskEngine(new() { rule });

        var spotR = spotEngine.Check("AAPL", "alpaca", "buy",
            quantity: 5m, estimatedPrice: 1000m,
            new PortfolioSnapshot { PortfolioValue = 10000m }, initialSlPct: 5m);
        var perpR = perpEngine.CheckPerp("BTCUSDT", "bingx", "BUY", "LONG",
            quantity: 0.001m, estimatedPrice: 50000m, leverage: 5,
            new PerpetualSnapshot { Balance = 100m }, initialSlPct: 5m);

        spotR.Passed.Should().BeFalse("Scope=null 仍套用於 spot");
        perpR.Passed.Should().BeFalse("Scope=null 仍套用於 perp");
    }

    [Fact]
    public void DefaultRules_R14_HasPerpScope()
    {
        var r14 = RiskEngine.DefaultRules().Single(r => r.RuleId == "r14");
        r14.Scope.Should().Be("perp");
    }

    [Fact]
    public void DefaultRules_R17_HasSpotScope()
    {
        var r17 = RiskEngine.DefaultRules().Single(r => r.RuleId == "r17");
        r17.Scope.Should().Be("spot");
    }

    [Fact]
    public void DefaultRules_SpotCheckOnly_R17Fires_R14Skipped()
    {
        // 整套預設規則在 spot 路徑、r17 應該觸發、r14 必須被 Scope 過濾掉、violations 不重複
        var engine = new RiskEngine(RiskEngine.DefaultRules());
        var portfolio = new PortfolioSnapshot { PortfolioValue = 10000m };

        // 5000 USD × 5% SL = 250 = 2.5% > 2% threshold → max_loss_per_trade_pct 觸發
        var r = engine.Check("AAPL", "alpaca", "buy", quantity: 5m, estimatedPrice: 1000m, portfolio, initialSlPct: 5m);

        r.Passed.Should().BeFalse();
        var lossViolations = r.Violations.Where(v => v.RuleId == "r14" || v.RuleId == "r17").ToList();
        lossViolations.Should().HaveCount(1, "spot 路徑只能有一條 max_loss_per_trade_pct violation");
        lossViolations[0].RuleId.Should().Be("r17");
    }

    // ── finding H：開倉餘額不可驗證(balance<=0)時 fail-closed ─────────

    [Fact]
    public void CheckPerp_OpenWithZeroBalance_FailsClosed()
    {
        // get_account 查不到餘額(balance=0)時、r14/r16 都算不準 → 開倉必須擋(fail-closed)、不能 fail-open
        var engine = new RiskEngine(RiskEngine.DefaultRules());
        var snap = new PerpetualSnapshot { Balance = 0m };

        var r = engine.CheckPerp("BTCUSDT", "bingx", "BUY", "LONG",
            quantity: 0.001m, estimatedPrice: 50000m, leverage: 5, snap, initialSlPct: 5m);

        r.Passed.Should().BeFalse("balance 不可驗證時開倉必須 fail-closed");
        r.Violations.Should().ContainSingle(v => v.RuleId == "rH");
    }

    [Fact]
    public void CheckPerp_CloseWithZeroBalance_StillAllowed()
    {
        // 平倉永遠放行、不被新閘擋(擋出場才是真風險);平多=SELL/LONG、平空=BUY/SHORT 都驗
        var engine = new RiskEngine(RiskEngine.DefaultRules());
        var snap = new PerpetualSnapshot { Balance = 0m };

        var closeLong = engine.CheckPerp("BTCUSDT", "bingx", "SELL", "LONG",
            quantity: 0.001m, estimatedPrice: 50000m, leverage: 5, snap, initialSlPct: 5m);
        var closeShort = engine.CheckPerp("BTCUSDT", "bingx", "BUY", "SHORT",
            quantity: 0.001m, estimatedPrice: 50000m, leverage: 5, snap, initialSlPct: 5m);

        closeLong.Passed.Should().BeTrue("平多 balance=0 仍須放行");
        closeShort.Passed.Should().BeTrue("平空 balance=0 仍須放行");
    }

    [Fact]
    public void CheckPerp_OpenWithPositiveBalance_R14StillEvaluated()
    {
        // 有餘額時新閘不短路掉正常規則:過大 notional 仍以 r14 擋(不是 rH)
        var r14 = new RiskRule
        {
            RuleId = "r14", Name = "Max Loss Per Trade %",
            Type = "max_loss_per_trade_pct", Threshold = 2m, Enabled = true, Scope = "perp",
        };
        var engine = new RiskEngine(new() { r14 });
        var snap = new PerpetualSnapshot { Balance = 100m };

        // maxLoss=100×2%=2 → maxNotional=2/5%=40;下單 50 名目 > 40 → r14 擋
        var r = engine.CheckPerp("BTCUSDT", "bingx", "BUY", "LONG",
            quantity: 0.001m, estimatedPrice: 50000m, leverage: 5, snap, initialSlPct: 5m);

        r.Passed.Should().BeFalse();
        r.Violations.Should().ContainSingle(v => v.RuleId == "r14");
        r.Violations.Should().NotContain(v => v.RuleId == "rH", "有餘額時不該觸發 rH");
    }

    [Fact]
    public void CheckPerp_OpenWithPositiveBalance_SmallSize_Passes()
    {
        // happy path:有餘額 + 小倉 → 全過、不被 rH 誤傷
        var engine = new RiskEngine(RiskEngine.DefaultRules());
        var snap = new PerpetualSnapshot { Balance = 10000m };

        var r = engine.CheckPerp("BTCUSDT", "bingx", "BUY", "LONG",
            quantity: 0.0001m, estimatedPrice: 50000m, leverage: 5, snap, initialSlPct: 5m);

        r.Passed.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(0.01, true)]
    [InlineData(100, true)]
    public void PerpBalanceVerifiableForOpen_Contract(decimal balance, bool expected)
        => RiskEngine.PerpBalanceVerifiableForOpen(balance).Should().Be(expected);

    // ── Helpers ──────────────────────────────────────────────────

    private static RiskRule Rule(string id, string type, decimal threshold, string? paramsJson = null)
        => new() { RuleId = id, Name = type, Type = type, Threshold = threshold, Enabled = true, Params = paramsJson };

    private static PositionEntry Pos(string symbol, decimal qty)
        => new() { Symbol = symbol, Exchange = "alpaca", Quantity = qty, MarketValue = qty * 100m };
}
