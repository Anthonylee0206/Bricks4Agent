#!/usr/bin/env python3
"""
failover-sim 的 LIVE 視覺儀表板 —— demo 專用:讓觀眾「親眼看到 + 有對照」殺主 → 秒級接手。

每個節點顯示自己的「work 計數」:
  - 綠色「服務中」的那台 → work **持續往上跳**(證明系統正在運作)。
  - 殺掉它 → 它變紅「已停擺」、**work 凍結不動**(證明那台真的停了);另一台變綠、work 繼續跳。
→ 紅的數字凍住 vs 綠的數字一直跳 = 「綠=運作中、紅=停擺」的具體對照。

用法:
  bash demo.sh down ; docker compose -p fsim up -d --build   # 先把 stack 起好(等~20s)
  python dashboard.py                    # 這個視窗看畫面(Windows 用 python、不是 python3)
  # 另一個視窗:docker kill fsim-node-a   # 殺掉現在「服務中」那台、看接手
"""
import subprocess, time, sys, os
try:
    sys.stdout.reconfigure(encoding="utf-8")   # 防 Windows cp950 console 編碼問題
except Exception:
    pass
if os.name == "nt":   # 開 Windows VT 處理:讓 ANSI 色碼 + 游標控制生效,畫面才會「原地更新」不捲動
    try:
        import ctypes
        _k = ctypes.windll.kernel32
        _k.SetConsoleMode(_k.GetStdHandle(-11), 7)
    except Exception:
        pass

GG = "\033[92m"; RR = "\033[91m"; Y = "\033[93m"; B = "\033[1m"; DIM = "\033[90m"; RST = "\033[0m"
NODES = ["a", "b"]


def status(n):
    """回 (role, work)。容器沒在 running → DOWN;非主節點 work 為 None。"""
    r = subprocess.run(["docker", "ps", "-q", "-f", f"name=^fsim-node-{n}$", "-f", "status=running"],
                       capture_output=True, text=True)
    if not r.stdout.strip():
        return ("DOWN", None)
    try:
        out = subprocess.run(
            ["docker", "exec", f"fsim-node-{n}", "python3", "-c",
             "import urllib.request,json;d=json.load(urllib.request.urlopen('http://localhost:8080',timeout=2));"
             "print(d['role'],d['last_work'])"],
            capture_output=True, text=True, timeout=4).stdout.strip()
        parts = out.split(None, 1)
        role = parts[0] if parts else "?"
        wtxt = parts[1].split()[0] if len(parts) > 1 and parts[1] not in ("", "(none)") else None
        return (role, int(wtxt) if wtxt and wtxt.isdigit() else None)
    except Exception:
        return ("?", None)


def render(states, work_seen):
    primary = next((n.upper() for n in NODES if states[n][0] == "PRIMARY"), None)
    head = f"{GG}● node-{primary} 在服務{RST}" if primary else f"{Y}⏳ 切換中…(這 2-3 秒暫時無人服務){RST}"
    lines = [f"{B}  ======  B4A 自動移轉 — LIVE  ======{RST}", "",
             f"  目前:{head}", ""]
    for n in NODES:
        role = states[n][0]
        w = work_seen[n]
        if role == "PRIMARY":
            st = f"{GG}●  服務中 {RST}"
            wk = f"{GG}work {w} ↑   (持續往上跳 = 正在運作){RST}" if w is not None else "work …"
        elif role == "DOWN":
            st = f"{RR}✗  已停擺 {RST}"
            wk = f"{RR}work 凍結 @ {w}   (不動了 = 真的停了){RST}" if w is not None else f"{RR}—{RST}"
        elif role == "STANDBY":
            st = f"{DIM}·  熱備   {RST}"
            wk = f"{DIM}待命中(隨時可接手){RST}"
        else:
            st = f"{Y}…  {role}{RST}"
            wk = ""
        lines.append(f"   {B}NODE-{n.upper()}{RST}    {st}    {wk}")
    lines += ["",
              f"  {DIM}對照:綠的 work 一直跳 = 系統在運作;紅的 work 凍住不動 = 那台真的停了。{RST}",
              f"  {DIM}→ 在另一視窗打:{RST}docker kill fsim-node-{(primary or 'A').lower()}  {DIM}(殺掉服務中那台){RST}",
              f"  {DIM}  (Ctrl-C 結束){RST}"]
    sys.stdout.write("\033[H" + "\n".join(l + "\033[K" for l in lines) + "\033[J")
    sys.stdout.flush()


def main():
    once = "--once" in sys.argv
    work_seen = {"a": None, "b": None}   # 記住每台「最後看到的 work」→ 死掉後就凍結在那個值
    while True:
        states = {n: status(n) for n in NODES}
        for n in NODES:
            if states[n][1] is not None:   # 只有「服務中」的台會回 work → 更新它;死的台保留舊值=凍結
                work_seen[n] = states[n][1]
        render(states, work_seen)
        if once:
            return
        time.sleep(0.5)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print()
