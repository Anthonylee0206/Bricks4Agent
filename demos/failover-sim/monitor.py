#!/usr/bin/env python3
"""
failover-sim 的 Web 監測儀表板 + 監測者(後端橋接)。

瀏覽器看不到 docker 內部 → 這個小服務當「橋」:每次被問就去查兩個節點的狀態,
回 JSON 給網頁;網頁(monitor.html)把它畫成「狀態卡 + 即時 work 折線圖 + 當機警報」。
這個服務本身就是「監測者」——它持續觀察、網頁據此跳警報(對應真平台的 watchdog→Discord 推播)。

用法:
  bash demo.sh down ; docker compose -p fsim up -d --build   # 先把 stack 起好(等~20s)
  python monitor.py                          # 起監測服務
  # 瀏覽器開 http://localhost:8090
  # 另一個視窗:docker kill fsim-node-a       # 殺主、看網頁警報 + 折線圖斷掉再恢復
"""
import subprocess, json, os, http.server, socketserver

NODES = ["a", "b"]
PORT = 8090
HERE = os.path.dirname(os.path.abspath(__file__))


def node_status(n):
    r = subprocess.run(["docker", "ps", "-q", "-f", f"name=^fsim-node-{n}$", "-f", "status=running"],
                       capture_output=True, text=True)
    if not r.stdout.strip():
        return {"role": "DOWN", "work": None}
    try:
        out = subprocess.run(
            ["docker", "exec", f"fsim-node-{n}", "python3", "-c",
             "import urllib.request,json;d=json.load(urllib.request.urlopen('http://localhost:8080',timeout=2));"
             "print(d['role'],d['last_work'])"],
            capture_output=True, text=True, timeout=4).stdout.strip()
        p = out.split(None, 1)
        role = p[0] if p else "?"
        wt = p[1].split()[0] if len(p) > 1 and p[1] not in ("", "(none)") else None
        return {"role": role, "work": int(wt) if wt and wt.isdigit() else None}
    except Exception:
        return {"role": "?", "work": None}


def state():
    nodes = {n: node_status(n) for n in NODES}
    primary = next((n.upper() for n in NODES if nodes[n]["role"] == "PRIMARY"), None)
    work = next((nodes[n]["work"] for n in NODES
                 if nodes[n]["role"] == "PRIMARY" and nodes[n]["work"] is not None), None)
    return {"nodes": nodes, "primary": primary, "work": work}


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith("/api/state"):
            body = json.dumps(state()).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)
        else:
            try:
                with open(os.path.join(HERE, "monitor.html"), "rb") as f:
                    body = f.read()
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.end_headers()
                self.wfile.write(body)
            except FileNotFoundError:
                self.send_response(404); self.end_headers()

    def log_message(self, *a):
        pass


class Server(socketserver.ThreadingMixIn, socketserver.TCPServer):
    allow_reuse_address = True


if __name__ == "__main__":
    print(f"監測儀表板:在瀏覽器開  http://localhost:{PORT}   (Ctrl-C 結束)")
    try:
        Server(("0.0.0.0", PORT), Handler).serve_forever()
    except KeyboardInterrupt:
        print()
