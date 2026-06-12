#!/usr/bin/env python3
"""
failover-sim 的 LIVE 視覺儀表板 —— demo 專用:讓觀眾「親眼看到」殺主 → 秒級接手。

在一個視窗跑這個 → 兩個節點變成方塊(整框上色:綠=PRIMARY、灰=STANDBY、紅=DOWN)、
中間一個「系統 work 計數」一直往上跳。在另一個視窗 `docker kill fsim-node-X` 殺掉「綠色」主節點,
觀眾即時看到:該框變紅、另一框變綠、而且上面那個數字一秒都沒停(= 系統不中斷)。

用法:
  bash demo.sh down ; docker compose -p fsim up -d --build   # 先把 stack 起好
  python dashboard.py                    # 這個視窗看畫面(Windows 用 python、不是 python3)
  # 另一個視窗:docker kill fsim-node-a   # 殺掉現在的綠色主、看接手
  python dashboard.py --once             # 測試:只畫一格就結束
"""
import subprocess, time, sys
try:
    sys.stdout.reconfigure(encoding="utf-8")   # 防 Windows cp950 console 編碼問題
except Exception:
    pass

G = "\033[42m\033[30m"   # 綠底黑字 = PRIMARY
GREY = "\033[100m\033[97m"  # 灰底白字 = STANDBY
R = "\033[41m\033[97m"   # 紅底白字 = DOWN
Y = "\033[93m"; B = "\033[1m"; DIM = "\033[90m"; RST = "\033[0m"
CLR = "\033[2J\033[H"
NODES = ["a", "b"]


def status(n):
    """回 (role, work)。容器沒在 running → DOWN。"""
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


def box(n, role):
    if role == "PRIMARY":   c, label = G,    "   PRIMARY    "
    elif role == "STANDBY": c, label = GREY, "   standby    "
    elif role == "DOWN":    c, label = R,    "   X DOWN X   "
    else:                   c, label = Y,    f" {('…' + role)[:12]:^12} "
    return [f"+----------------+",
            f"|   NODE-{n.upper()}        |",
            f"| {c}{label}{RST} |",
            f"+----------------+"]


def render(states, max_work):
    a, b = box("a", states["a"][0]), box("b", states["b"][0])
    primary = next((n.upper() for n in NODES if states[n][0] == "PRIMARY"), None)
    head = f"{G} node-{primary} 在服務 {RST}" if primary else f"{Y}切換中…{RST}"
    lines = [CLR, f"{B}  ======  B4A 自動移轉 — LIVE  ======{RST}", ""]
    lines.append(f"  系統 work 計數:{B}{Y} {max_work if max_work is not None else '—'} {RST}↑    {head}")
    lines.append(f"  {DIM}(這個數字持續往上 = 系統一直在做事、沒中斷){RST}")
    lines.append("")
    for la, lb in zip(a, b):
        lines.append("   " + la + "        " + lb)
    lines += ["",
              f"  {DIM}→ 在另一個視窗打:{RST}docker kill fsim-node-{(primary or 'A').lower()}   {DIM}(殺掉綠色的主){RST}",
              f"  {DIM}  看那框變「紅」、另一框變「綠」、而上面數字「不中斷」。 (Ctrl-C 結束){RST}"]
    print("\n".join(lines), flush=True)


def main():
    once = "--once" in sys.argv
    max_work = None
    while True:
        states = {n: status(n) for n in NODES}
        for n in NODES:
            w = states[n][1]
            if w is not None and (max_work is None or w > max_work):
                max_work = w
        render(states, max_work)
        if once:
            return
        time.sleep(0.5)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print()
