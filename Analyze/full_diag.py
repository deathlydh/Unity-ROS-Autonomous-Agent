import csv, math

with open("diagnostic_log.csv", encoding="utf-8-sig") as f:
    rows = list(csv.DictReader(f))
n = len(rows)
dur = float(rows[-1]["time"])
print(f"=== DIAGNOSTIC: {n} steps, {dur:.1f}s ===\n")

# Basic stats
seen = [r for r in rows if int(r["ballSeen"]) == 1]
blind = [r for r in rows if int(r["ballSeen"]) == 0]
print(f"BallSeen: {len(seen)}/{n} = {100*len(seen)/n:.1f}%")

gases = [float(r["gas"]) for r in rows]
steers = [float(r["steering"]) for r in rows]
print(f"Gas: avg={sum(gases)/n:.3f} min={min(gases):.3f} max={max(gases):.3f}")
fwd = sum(1 for g in gases if g > 0.1)
rev = sum(1 for g in gases if g < -0.1)
print(f"  Forward: {fwd}/{n}={100*fwd/n:.0f}%, Reverse: {rev}/{n}={100*rev/n:.0f}%")
print(f"Steer: avg={sum(steers)/n:.3f}, |steer|={sum(abs(s) for s in steers)/n:.3f}")

if seen:
    sg = [float(r["gas"]) for r in seen]
    ss = [float(r["steering"]) for r in seen]
    sa = [float(r["ballAngle"]) for r in seen]
    sd = [float(r["ballDist"]) for r in seen]
    print(f"\nWhen SEEN ({len(seen)} steps):")
    print(f"  gas={sum(sg)/len(sg):.3f}, steer={sum(ss)/len(ss):.3f}")
    print(f"  angle: [{min(sa):.3f}, {max(sa):.3f}] avg={sum(sa)/len(sa):.3f}")
    print(f"  dist:  [{min(sd):.3f}, {max(sd):.3f}] avg={sum(sd)/len(sd):.3f}")
    
    # Steering toward ball analysis
    correct = wrong = 0
    for r in seen:
        a = float(r["ballAngle"]); s = float(r["steering"])
        if abs(a) > 0.1 and abs(s) > 0.05:
            if a * s > 0: correct += 1
            else: wrong += 1
    total = correct + wrong
    if total > 0:
        print(f"  Steer TOWARD ball: {correct}/{total} = {100*correct/total:.0f}%")
        print(f"  Steer AWAY:        {wrong}/{total} = {100*wrong/total:.0f}%")

if blind:
    bg = [float(r["gas"]) for r in blind]
    bs = [float(r["steering"]) for r in blind]
    print(f"\nWhen BLIND ({len(blind)} steps):")
    print(f"  gas={sum(bg)/len(bg):.3f}, steer={sum(bs)/len(bs):.3f}")

# Displacement
dx = [float(r["displacementX"]) for r in rows]
dz = [float(r["displacementZ"]) for r in rows]
total_disp = math.sqrt(max(dx)**2 + max(abs(min(dz)), max(dz))**2)
print(f"\nDisplacement: X=[{min(dx):.3f},{max(dx):.3f}] Z=[{min(dz):.3f},{max(dz):.3f}]")
print(f"  Total ~{total_disp:.3f}m in {dur:.0f}s = {total_disp/dur:.4f} m/s")

# UZ + hasBall
uzs = [float(r["uz"]) for r in rows]
print(f"\nUZ: start={uzs[0]:.3f} end={uzs[-1]:.3f} min={min(uzs):.3f}")
hb = sum(1 for r in rows if int(r["hasBall"]) == 1)
print(f"HasBall: {hb}/{n}")

# Ball visibility transitions
transitions = []
for i in range(1, n):
    p = int(rows[i-1]["ballSeen"]); c = int(rows[i]["ballSeen"])
    if p != c:
        transitions.append((i, rows[i]["step"], "FOUND" if c == 1 else "LOST"))
print(f"\nBall transitions: {len(transitions)}")
# Show all
for idx, step, state in transitions:
    r = rows[idx]
    print(f"  row={idx:4d} step={step:>5} [{state:5s}] gas={float(r['gas']):+.3f} "
          f"steer={float(r['steering']):+.3f} angle={float(r['ballAngle']):+.3f} "
          f"dist={float(r['ballDist']):.3f} uz={float(r['uz']):.3f}")

# Timeline every 50 steps
print(f"\n=== TIMELINE (every 50 steps) ===")
for i in range(0, n, 50):
    r = rows[i]
    print(f"  [{r['step']:>5}] t={float(r['time']):5.1f}s ball={r['ballSeen']} "
          f"gas={float(r['gas']):+.3f} steer={float(r['steering']):+.3f} "
          f"uz={float(r['uz']):.3f} cam={float(r['camYaw']):+.3f} "
          f"dx={float(r['displacementX']):.3f} dz={float(r['displacementZ']):.3f}")

# Key: did dist DECREASE when ball was seen?
print(f"\n=== BALL APPROACH ANALYSIS ===")
seen_blocks = []
in_block = False
block = []
for i, r in enumerate(rows):
    if int(r["ballSeen"]) == 1:
        if not in_block:
            in_block = True
            block = []
        block.append((i, float(r["ballDist"]), float(r["ballAngle"]), float(r["gas"]), float(r["steering"])))
    else:
        if in_block:
            seen_blocks.append(block)
            in_block = False
            block = []
if in_block:
    seen_blocks.append(block)

for bi, blk in enumerate(seen_blocks):
    start_dist = blk[0][1]
    end_dist = blk[-1][1]
    avg_gas = sum(g for _, _, _, g, _ in blk) / len(blk)
    avg_steer = sum(s for _, _, _, _, s in blk) / len(blk)
    avg_angle = sum(a for _, _, a, _, _ in blk) / len(blk)
    delta = end_dist - start_dist
    direction = "CLOSER" if delta < -0.02 else ("FARTHER" if delta > 0.02 else "SAME")
    print(f"  Block {bi}: steps={len(blk)} dist {start_dist:.3f}->{end_dist:.3f} ({delta:+.3f} {direction}) "
          f"gas={avg_gas:+.3f} steer={avg_steer:+.3f} angle={avg_angle:+.3f}")
