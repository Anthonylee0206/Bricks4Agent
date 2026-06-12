#!/usr/bin/env python3
"""
BingX perp clientOrderID 冪等驗證 —— 自動移轉階段③的開放問題。

驗:BingX 接不接受 clientOrderID 參數、對「重複 clientOrderID」拒不拒單(=failover 不雙下單的基礎)。

預設打 VST demo(虛擬 USDT、零真錢)。下「遠離市價的 LIMIT 單」→ 掛著不成交 → 同 clientOrderID 送兩次
→ 看第二次是否被拒 → 取消。零市場曝險(不成交=不開倉)。

簽章照搬 broker BingxPerpetualClient(簽 raw 值 / 送 encoded 值 / signature 接 encoded URL 後),
避免 code=100001 簽章坑。

用法:
  python3 tools/ha/test-bingx-idempotency.py
可選 env:
  BINGX_TEST_BASE   預設 https://open-api-vst.bingx.com(VST demo);實盤=https://open-api.bingx.com
  SYMBOL            預設 BTC-USDT
  QTY               預設 0.001(太小會 code≠0、依提示調)
  CLIENT_ID_PARAM   預設 clientOrderID(若被忽略再試 clientOrderId / newClientOrderId)
  BINGX_API_KEY / BINGX_API_SECRET  覆寫(預設讀 tools/secrets/bingx_perp_api_*.txt、不印值)
"""
import os, time, hmac, hashlib, urllib.request, urllib.parse, json, sys, pathlib, socket

# VPS 出站預設可能走 IPv6,但 BingX 白名單綁的是 VPS 的 IPv4 → 強制 IPv4(否則 code=100419 IP 不符)
_orig_gai = socket.getaddrinfo
def _ipv4_only(host, port, family=0, type=0, proto=0, flags=0):
    return _orig_gai(host, port, socket.AF_INET, type, proto, flags)
socket.getaddrinfo = _ipv4_only


def read_secret(name):
    here = pathlib.Path(__file__).resolve()
    root = here.parents[2] if len(here.parents) >= 3 else pathlib.Path.cwd()
    p = root / "tools" / "secrets" / name
    if not p.exists():
        sys.exit(f"找不到憑證檔 {p}(或用 env BINGX_API_KEY/SECRET 覆寫)")
    return p.read_text().strip()

API_KEY = os.environ.get("BINGX_API_KEY") or read_secret("bingx_perp_api_key.txt")
API_SECRET = os.environ.get("BINGX_API_SECRET") or read_secret("bingx_perp_api_secret.txt")
BASE = os.environ.get("BINGX_TEST_BASE", "https://open-api-vst.bingx.com")
SYMBOL = os.environ.get("SYMBOL", "BTC-USDT")
QTY = os.environ.get("QTY", "0.001")
CID_PARAM = os.environ.get("CLIENT_ID_PARAM", "clientOrderID")
CLIENT_ID = "b4a-idem-test-1"
IS_LIVE = "vst" not in BASE.lower()


def _signed(method, path, params):
    items = list(params.items()) + [("timestamp", str(int(time.time() * 1000)))]
    raw = "&".join(f"{k}={v}" for k, v in items)                                  # 簽 raw 值
    sig = hmac.new(API_SECRET.encode(), raw.encode(), hashlib.sha256).hexdigest()
    enc = "&".join(f"{k}={urllib.parse.quote(str(v), safe='')}" for k, v in items)  # 送 encoded 值
    url = f"{BASE}{path}?{enc}&signature={sig}"
    req = urllib.request.Request(url, data=b"" if method == "POST" else None, method=method,
                                 headers={"X-BX-APIKEY": API_KEY,
                                          "Content-Type": "application/x-www-form-urlencoded"})
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        return json.loads(e.read())


def get_price():
    url = f"{BASE}/openApi/swap/v2/quote/price?symbol={SYMBOL}"
    with urllib.request.urlopen(url, timeout=15) as r:
        d = json.loads(r.read())
    return float((d.get("data") or {}).get("price") or 0)


def place(n, limit_price):
    p = {"symbol": SYMBOL, "side": "BUY", "positionSide": "LONG", "type": "LIMIT",
         "quantity": QTY, "price": str(limit_price), CID_PARAM: CLIENT_ID}
    resp = _signed("POST", "/openApi/swap/v2/trade/order", p)
    data = resp.get("data") if isinstance(resp.get("data"), dict) else {}
    order = data.get("order", data) if isinstance(data, dict) else {}
    oid = order.get("orderId") if isinstance(order, dict) else None
    print(f"[下單 #{n}] code={resp.get('code')} msg={resp.get('msg')!r} orderId={oid}")
    return resp, oid


def main():
    print("=== BingX clientOrderID 冪等測試 ===")
    print(f"endpoint : {BASE}  {'⚠️  實盤(真錢)!' if IS_LIVE else '✅ VST demo(虛擬錢、零真錢)'}")
    print(f"param 名 : {CID_PARAM}   clientOrderID 值: {CLIENT_ID}")
    if IS_LIVE and input("這是實盤、確定要送真錢測試?輸入 yes 繼續:").strip() != "yes":
        sys.exit("已取消。")

    mkt = get_price()
    if mkt <= 0:
        sys.exit(f"抓不到 {SYMBOL} 市價、無法設安全 limit 價。檢查 SYMBOL。")
    limit_price = round(mkt * 0.9)   # BUY limit 比市價低 10% → 掛著不成交、又在價格帶內
    print(f"市價 ~{mkt} → BUY limit {limit_price}(低 10%、不會成交)  qty={QTY}\n")

    print("--- 第一次下單(clientOrderID 全新)---")
    r1, oid1 = place(1, limit_price)
    time.sleep(1)
    print("\n--- 第二次下單(同 clientOrderID)---")
    r2, oid2 = place(2, limit_price)

    print("\n=== 判讀 ===")
    c1, c2 = r1.get("code"), r2.get("code")
    if c1 == 0 and c2 not in (0, None):
        print(f"✅ 第一次成功、第二次被拒(code={c2} msg={r2.get('msg')!r})")
        print("   → BingX 對重複 clientOrderID 拒單 = 冪等可用!param 名正確。")
    elif c1 == 0 and c2 == 0:
        print("⚠️ 兩次都成功 → BingX 沒對此 clientOrderID dedup。")
        print(f"   可能:param 名 '{CID_PARAM}' 被忽略(看回應有無回填 clientOrderID),")
        print("   試 CLIENT_ID_PARAM=clientOrderId 或 newClientOrderId 重跑。")
    elif c1 not in (0, None):
        print(f"❓ 第一次就失敗 code={c1} msg={r1.get('msg')!r}")
        print("   常見:qty 太小(調 QTY)、param 名不被接受、或 key 在 VST 無權限(換實盤小額)。")
    else:
        print(f"❓ 看上面 code/msg。c1={c1} c2={c2}")

    print("\n--- 清理:取消測試掛單 ---")
    for oid in {oid1, oid2}:
        if not oid:
            continue
        c = _signed("DELETE", "/openApi/swap/v2/trade/order",
                    {"symbol": SYMBOL, "orderId": oid})
        print(f"取消 orderId={oid}: code={c.get('code')} msg={c.get('msg')!r}")
    print("（掛單不成交、無風險;若取消沒成功,去 BingX app 手動取消那筆 LIMIT 單即可）")


if __name__ == "__main__":
    main()
