# 測報 Runbook — 程式碼覆蓋率報告(業界標準 HTML)

> 一鍵產生業界標準的程式碼覆蓋率報告(Coverlet 收集 → ReportGenerator 出 HTML)。
> 取代手寫 markdown、組員不需手動裝工具即可重現。

## 一鍵產生

repo 根目錄執行其一:

```powershell
# Windows
pwsh scripts/coverage-report.ps1
```
```bash
# Linux / macOS / CI
bash scripts/coverage-report.sh
```

腳本會自動:
1. `dotnet tool restore` —— 還原 ReportGenerator(版本鎖在 `.config/dotnet-tools.json`、組員不用自己裝)
2. `dotnet test` 跑 xUnit 測試並用 **Coverlet**(`--collect:"XPlat Code Coverage"`)收覆蓋率
3. **ReportGenerator** 把 cobertura XML 轉成 HTML + 文字摘要 + SVG badge

**前置需求**:只要有 .NET 8 SDK。其餘工具自動還原。CI 用 `-NoOpen`(ps1)不開瀏覽器。

## 產出位置(`.test-output/coverage/`、已 gitignore)

| 檔案 | 用途 |
|------|------|
| `index.html` | **主報告**(瀏覽器開):總覽 + 逐組件/逐類別/逐行覆蓋、紅綠標示未覆蓋行 |
| `Summary.txt` | 純文字摘要(貼進報告/CI log)|
| `badge_linecoverage.svg` | README/儀錶板用的覆蓋率徽章 |

## 怎麼讀(三個指標 = 業界標準)

- **Line coverage(行覆蓋)**:被測到的可執行行 ÷ 總可執行行。最常引用。
- **Branch coverage(分支覆蓋)**:if/switch 等分支被走過的比例。比行覆蓋嚴格(同一行的兩個分支都要測到)。
- **Method coverage(方法覆蓋)**:有被呼叫到的方法比例。

## 重要:整體 % vs 平台核心 %(誠實解讀)

整體行覆蓋目前約 **15~16%** —— 這個數字會被稀釋,因為**分母是「整包程式碼」**:含
單一檔 3,700+ 行的 AutoTrader、整個 broker monolith、UI、quant/回測、產生碼等「不以單元測試為目標」的部分。

**該看的是逐組件(per-assembly)/逐類別視圖**:打開 `index.html`,治理/控制平面的純邏輯
(風控引擎、sizing 純函式、冪等 key、approval/capability 等)覆蓋率明顯高很多 —— 那才是
反映「平台核心受測程度」的數字。**判讀原則:看趨勢(每次 PR 不能往下掉)+ 看核心類別,
不是盯整體單一數字。**

> 報告排除了測試組件本身(`*.Tests`)與產生碼(`*.g.cs`/`AssemblyInfo`/`obj/`),分母才誠實。
> 篩選規則在 `scripts/coverage-report.*` 的 `-assemblyfilters` / `-filefilters`。

## 想提高覆蓋率?

加 xUnit 測試到 `packages/csharp/tests/unit/`(風格見既有 `*Tests.cs`、用 FluentAssertions)。
**解耦合是前提**:把核心邏輯抽成無 I/O 的純函式(如 `ComputeExposureSizing`、`ResolvePerpOpenRiskMode`、
`DeriveIdemKey`)就能直接單元測 —— 這也是組長強調的「開發時注意解耦合」。重跑本腳本即看到提升。

## 注意事項

- 涵蓋的測試專案:`Unit.Tests`(主)、`TradingWorker.Tests`。`broker-tests` 是自製 Exe runner、
  不走 VSTest 覆蓋率管線,故不計入此報告(它有自己的 pass/fail 輸出)。
- 偶發 `MSB3491 檔案已存在`:腳本已自動清除 coverlet 殘留映射檔(`.msCoverageSourceRootsMapping_*`)。
- 測試若有失敗,腳本會中止、不產覆蓋率(失敗的覆蓋率無意義)。
