import csv

with open("diagnostic_log.csv", encoding="utf-8-sig") as f:
    rows = list(csv.DictReader(f))

# UZ over time
print("=== UZ OVER TIME ===")
for i in range(0, len(rows), 100):
    r = rows[i]
    step = r["step"]
    t = float(r["time"])
    uz = float(r["uz"])
    ball = r["ballSeen"]
    gas = float(r["gas"])
    steer = float(r["steering"])
    dx = float(r["displacementX"])
    dz = float(r["displacementZ"])
    print(f"  step={step:>5} t={t:5.1f}s uz={uz:.3f} ball={ball} gas={gas:+.3f} steer={steer:+.3f} dx={dx:.4f} dz={dz:.4f}")

# Closest ball approaches
print("\n=== CLOSEST BALL APPROACHES ===")
seen = [(i, float(r["ballDist"]), float(r["ballAngle"]), float(r["gas"]), float(r["steering"]))
        for i, r in enumerate(rows) if int(r["ballSeen"]) == 1]
if seen:
    sorted_by_dist = sorted(seen, key=lambda x: x[1])
    print("Top 10 closest:")
    for idx, dist, angle, gas, steer in sorted_by_dist[:10]:
        r = rows[idx]
        step = r["step"]
        uz = float(r["uz"])
        print(f"  row={idx:4d} step={step:>5} dist={dist:.3f} angle={angle:+.3f} gas={gas:+.3f} steer={steer:+.3f} uz={uz:.3f}")

# Displacement
dxs = [float(r["displacementX"]) for r in rows]
dzs = [float(r["displacementZ"]) for r in rows]
total_dist = (max(abs(min(dxs)), max(dxs))**2 + max(abs(min(dzs)), max(dzs))**2)**0.5
print(f"\nTotal displacement: ~{total_dist:.3f}m in 41.5s = {total_dist/41.5:.4f} m/s")

# Steering oscillation
steers = [float(r["steering"]) for r in rows]
sign_changes = sum(1 for i in range(1, len(steers)) if steers[i]*steers[i-1] < 0 and abs(steers[i]) > 0.1)
print(f"Steering sign flips: {sign_changes}/{len(rows)}")

# GAS when ball is close
print("\n=== BEHAVIOR WHEN BALL CLOSE (dist < 0.3) ===")
close_seen = [(i, float(r["ballDist"]), float(r["ballAngle"]), float(r["gas"]), float(r["steering"]))
              for i, r in enumerate(rows) if int(r["ballSeen"]) == 1 and float(r["ballDist"]) < 0.3]
print(f"Steps with ball close: {len(close_seen)}")
if close_seen:
    cg = [g for _, _, _, g, _ in close_seen]
    cs = [s for _, _, _, _, s in close_seen]
    print(f"  gas avg={sum(cg)/len(cg):.3f} steer avg={sum(cs)/len(cs):.3f}")
    for idx, dist, angle, gas, steer in close_seen[:10]:
        print(f"    row={idx} dist={dist:.3f} angle={angle:+.3f} gas={gas:+.3f} steer={steer:+.3f}")
