import csv

with open("diagnostic_log.csv", encoding="utf-8-sig") as f:
    rows = list(csv.DictReader(f))
n = len(rows)
duration = float(rows[-1]["time"])
print(f"=== REAL LOG ({n} steps, {duration:.1f}s) ===")

seen = [r for r in rows if int(r["ballSeen"]) == 1]
blind = [r for r in rows if int(r["ballSeen"]) == 0]
print(f"BallSeen: {len(seen)}/{n} = {100*len(seen)//n}%")

gases = [float(r["gas"]) for r in rows]
steers = [float(r["steering"]) for r in rows]
print(f"Gas:  avg={sum(gases)/n:.3f}, min={min(gases):.3f}, max={max(gases):.3f}")
rev = sum(1 for g in gases if g < -0.1)
fwd = sum(1 for g in gases if g > 0.1)
idle = n - rev - fwd
print(f"  Forward: {fwd}/{n}={100*fwd//n}%, Reverse: {rev}/{n}={100*rev//n}%, Idle: {idle}/{n}={100*idle//n}%")
print(f"Steer: avg={sum(steers)/n:.3f}, min={min(steers):.3f}, max={max(steers):.3f}")
act_steer = sum(1 for s in steers if abs(s) > 0.15)
print(f"  Active steer: {act_steer}/{n}={100*act_steer//n}%")

if seen:
    sg = [float(r["gas"]) for r in seen]
    ss = [float(r["steering"]) for r in seen]
    sa = [float(r["ballAngle"]) for r in seen]
    sd = [float(r["ballDist"]) for r in seen]
    print(f"When SEEN ({len(seen)} steps):")
    print(f"  gas avg={sum(sg)/len(sg):.3f}, steer avg={sum(ss)/len(ss):.3f}")
    print(f"  angle=[{min(sa):.3f},{max(sa):.3f}] avg|a|={sum(abs(a) for a in sa)/len(sa):.3f}")
    print(f"  dist=[{min(sd):.3f},{max(sd):.3f}] avg={sum(sd)/len(sd):.3f}")
    rev_seen = sum(1 for g in sg if g < -0.05)
    print(f"  gas REVERSE when seen: {rev_seen}/{len(seen)}")

if blind:
    bg = [float(r["gas"]) for r in blind]
    bs = [float(r["steering"]) for r in blind]
    print(f"When BLIND ({len(blind)} steps):")
    print(f"  gas avg={sum(bg)/len(bg):.3f}, steer avg={sum(bs)/len(bs):.3f}")

# Ball visibility timeline
print("\n=== BALL VISIBILITY TIMELINE ===")
transitions = []
for i in range(1, n):
    p = int(rows[i-1]["ballSeen"])
    c = int(rows[i]["ballSeen"])
    if p != c:
        transitions.append((i, int(rows[i]["step"]), "FOUND" if c == 1 else "LOST",
                           float(rows[i]["gas"]), float(rows[i]["steering"]),
                           float(rows[i]["uz"]), float(rows[i]["ballAngle"]),
                           float(rows[i]["ballDist"])))
print(f"Total transitions: {len(transitions)}")
for idx, step, state, gas, steer, uz, ba, bd in transitions:
    print(f"  row={idx:4d} step={step:4d} [{state:5s}] gas={gas:+.3f} steer={steer:+.3f} uz={uz:.3f} angle={ba:+.3f} dist={bd:.3f}")

# After last LOST event — what does robot do?
lost_events = [(i, step) for i, step, state, *_ in transitions if state == "LOST"]
if lost_events:
    last_lost_idx = lost_events[-1][0]
    print(f"\n=== AFTER LAST BALL LOST (row {last_lost_idx}) ===")
    for j in range(last_lost_idx, min(last_lost_idx + 40, n)):
        r = rows[j]
        print(f"  step={r['step']:>5} ball={r['ballSeen']} gas={float(r['gas']):+.3f} "
              f"steer={float(r['steering']):+.3f} uz={float(r['uz']):.3f} "
              f"blind={r['blindTicks']} wasClose={r['wasClose']} retry={r['isRetrying']}")

# UZ over time
uzs = [float(r["uz"]) for r in rows]
print(f"\nUZ: start={uzs[0]:.3f} end={uzs[-1]:.3f} min={min(uzs):.3f} max={max(uzs):.3f}")
hb = sum(1 for r in rows if int(r["hasBall"]) == 1)
print(f"HasBall: {hb}/{n}")
dx = [float(r["displacementX"]) for r in rows]
dz = [float(r["displacementZ"]) for r in rows]
print(f"Displacement: X=[{min(dx):.4f},{max(dx):.4f}] Z=[{min(dz):.4f},{max(dz):.4f}]")

times = [float(r["time"]) for r in rows]
intervals = [times[i]-times[i-1] for i in range(1, n)]
print(f"Step interval: avg={sum(intervals)/len(intervals)*1000:.1f}ms max={max(intervals)*1000:.1f}ms")

# Key question: is robot moving TOWARD the ball?
print("\n=== MOVEMENT ANALYSIS ===")
# First 100 steps
print("First 100 steps (every 10th):")
for i in range(0, min(100, n), 10):
    r = rows[i]
    print(f"  step={r['step']:>5} ball={r['ballSeen']} gas={float(r['gas']):+.3f} "
          f"steer={float(r['steering']):+.3f} uz={float(r['uz']):.3f} "
          f"dx={float(r['displacementX']):.4f} dz={float(r['displacementZ']):.4f}")

# Last 100 steps
print("Last 100 steps (every 10th):")
for i in range(max(0, n-100), n, 10):
    r = rows[i]
    print(f"  step={r['step']:>5} ball={r['ballSeen']} gas={float(r['gas']):+.3f} "
          f"steer={float(r['steering']):+.3f} uz={float(r['uz']):.3f} "
          f"dx={float(r['displacementX']):.4f} dz={float(r['displacementZ']):.4f}")
