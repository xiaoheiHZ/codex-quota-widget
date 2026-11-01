"""Read only quota fields through the local Codex app-server; never reads auth files."""
import json
import queue
import shutil
import subprocess
import threading


def main():
    proc = subprocess.Popen(
        [shutil.which("codex"), "app-server", "--listen", "stdio://"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW,
    )
    lines = queue.Queue()
    threading.Thread(target=lambda: [lines.put(line) for line in proc.stdout], daemon=True).start()

    def send(msg):
        proc.stdin.write(json.dumps(msg) + "\n")
        proc.stdin.flush()

    def receive(request_id):
        while True:
            msg = json.loads(lines.get(timeout=40))
            if msg.get("id") == request_id:
                if "error" in msg:
                    raise RuntimeError(msg["error"].get("message", "RPC error"))
                return msg["result"]

    try:
        send({"id": 1, "method": "initialize", "params": {"clientInfo": {
            "name": "codex_quota_widget", "title": "Codex Quota Widget", "version": "1.0.0"}}})
        receive(1)
        send({"method": "initialized", "params": {}})
        send({"id": 2, "method": "account/rateLimits/read"})
        result = receive(2)
        safe = {key: result[key] for key in ("rateLimits", "rateLimitsByLimitId", "ordinaryUsageAllowed") if key in result}
        print(json.dumps(safe, ensure_ascii=False, indent=2))
    finally:
        proc.kill()
        proc.wait()


if __name__ == "__main__":
    main()
