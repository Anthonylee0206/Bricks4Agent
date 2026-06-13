#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# Bricks4Agent — 測報(業界標準 HTML 程式碼覆蓋率報告)。Linux/CI 版,等同 coverage-report.ps1。
#   用法(repo 根目錄):bash scripts/coverage-report.sh
#   產出:.test-output/coverage/index.html + Summary.txt + badge_*.svg
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail
cd "$(dirname "$0")/.."

OUT_RAW=".test-output/coverage-raw"
OUT_HTML=".test-output/coverage"
PROJECTS=(
  "packages/csharp/tests/unit/Unit.Tests.csproj"
  "packages/csharp/tests/trading-worker-tests/TradingWorker.Tests.csproj"
)

echo "== 1/3 還原 ReportGenerator 本地工具 =="
dotnet tool restore >/dev/null

echo "== 2/3 跑測試 + 收覆蓋率 =="
rm -rf "$OUT_RAW"
find packages/csharp/tests -name '.msCoverageSourceRootsMapping_*' -delete 2>/dev/null || true
for p in "${PROJECTS[@]}"; do
  echo "   -> $p"
  dotnet test "$p" --collect:"XPlat Code Coverage" --results-directory "$OUT_RAW" --nologo -v q
done

echo "== 3/3 產生 HTML 報告 =="
rm -rf "$OUT_HTML"
dotnet tool run reportgenerator \
  "-reports:$OUT_RAW/**/coverage.cobertura.xml" \
  "-targetdir:$OUT_HTML" \
  "-reporttypes:Html;TextSummary;Badges" \
  "-assemblyfilters:-*.Tests;-*Tests" \
  "-filefilters:-*.g.cs;-*AssemblyInfo.cs;-**/obj/**"

echo ""
echo "── 覆蓋率摘要 ──────────────────────────────"
head -16 "$OUT_HTML/Summary.txt"
echo ""
echo "完整報告: $OUT_HTML/index.html"
