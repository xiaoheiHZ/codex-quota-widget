"""Fixture-based WPF layout and timer smoke tests. No account data used."""
from pathlib import Path
import json
import subprocess
import time
import argparse

root = Path(__file__).resolve().parent.parent
output = root / "release" / "qa"
output.mkdir(parents=True, exist_ok=True)
parser = argparse.ArgumentParser()
parser.add_argument("--exe", type=Path, default=root / "release" / "app" / "CodexQuotaWidget.exe")
exe = parser.parse_args().exe.resolve()
stamp = int(time.time())

def window(used, mins, reset=3600):
    return {"usedPercent": used, "windowDurationMins": mins, "resetsAt": stamp + reset if reset is not None else None}

cases = {
    "weekly-only": ({"rateLimits": {"primary": window(31, 10080)}}, "69", False, True),
    "expired": ({"rateLimits": {"primary": window(60, 300, -60)}}, "—", False, True),
    "empty": ({"rateLimits": {"primary": None}}, None, False, False),
    "error": ({"testError": True}, None, False, False),
    "zero": ({"rateLimits": {"primary": window(100, 300)}}, "0", False, True),
    "full": ({"rateLimits": {"primary": window(0, 300)}}, "100", False, True),
    "unknown-reset": ({"rateLimits": {"primary": window(12.5, 300, None)}}, "87.5", False, True),
    "two-windows": ({"rateLimits": {"planType": "plus", "primary": window(69, 300), "secondary": window(11, 10080, 604800)}}, "31", True, True),
    "multi": ({"rateLimitsByLimitId": {"codex": {"primary": window(10, 300)}, "special": {"limitName": "Special", "primary": window(50, 1440)}}}, "90", False, True),
}
scenarios = [("panel", name, case, False) for name, case in cases.items()]
scenarios += [(style, name, cases[name], False) for style in ("strip", "clock", "orbit") for name in ("two-windows", "weekly-only", "expired", "unknown-reset")]
scenarios += [("clock", "two-windows", cases["two-windows"], True)]
for style, name, (fixture, percent, secondary, primary), no_motion in scenarios:
    path = output / (name + ".json")
    image = output / (style + "-" + name + ("-still" if no_motion else "") + ".png")
    path.write_text(json.dumps(fixture), encoding="utf-8")
    command = [str(exe), "--fixture", str(path), "--style", style, "--capture", str(image)]
    if no_motion:
        command.append("--no-motion")
    process = subprocess.run(command, timeout=20)
    assert process.returncode == 0, (name, process.returncode)
    state = json.loads(Path(str(image) + ".json").read_text(encoding="utf-8"))
    assert state["style"] == style, (name, state)
    assert "sourcehansanscn-medium.otf" in (state["fontFile"] or "").lower() and state["fontWeight"] == 500, (name, state)
    assert state["renderedFonts"] and all("sourcehansanscn-medium.otf" in font.lower() for font in state["renderedFonts"]), (name, state)
    assert state["primaryVisible"] == primary, (name, state)
    assert state["secondaryVisible"] == secondary, (name, state)
    assert not state["clipping"], (name, state)
    if percent is not None:
        assert state["percent"] == percent, (name, state)
    if primary and name not in ("expired", "unknown-reset"):
        assert state["clockTicks"] > 0 and state["countdownChanges"] >= 2, (name, state)
    if name == "expired":
        assert state["countdown"] == "正在确认重置", state
    print("PASS", style, name, "still" if no_motion else "", flush=True)
print(f"{len(scenarios)} UI scenarios passed.")
motion_path = output / "motion-check.json"
process = subprocess.run([str(exe), "--demo", "--check-motion", str(motion_path)], timeout=20)
results = json.loads(motion_path.read_text(encoding="utf-8"))
assert process.returncode == 0 and all(results.values()), results
print(f"{len(results)} animation checks passed.")
