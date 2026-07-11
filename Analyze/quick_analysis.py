import csv

with open("diagnostic_log.csv", encoding="utf-8-sig") as f:
    rows = list(csv.DictReader(f))
n = len(rows)
dur = float(rows[-1]["time"])
print(f"Steps: {n}, Duration: {dur:.1f}s")

seen = [r for r in rows if int(r["ballSeen"]) == 1]
blind = [r for r in rows if int(r["ballSeen"]) == 0]
print(f"BallSeen: {len(seen)}/{n} = {100*len(seen)//n}%")

gases = [float(r["gas"]) for r in rows]
steers = [float(r["steering"]) for r in rows]
print(f"Gas avg={sum(gases)/n:.3f} min={min(gases):.3f} max={max(gases):.3f}")
fwd = sum(1 for g in gases if g > 0.1)
rev = sum(1 for g in gases if g < -0.1)
print(f"Forward: {fwd}/{n}={100*fwd//n}% Reverse: {rev}/{n}={100*rev//n}%")
print(f"Steer avg={sum(steers)/n:.3f} |steer|={sum(abs(s) for s in steers)/n:.3f}")

if seen:
    sg = [float(r["gas"]) for r in seen]
    ss = [float(r["steering"]) for r in seen]
    sa = [float(r["ballAngle"]) for r in seen]
    sd = [float(r["ballDist"]) for r in seen]
    print(f"SEEN: gas={sum(sg)/len(sg):.3f} steer={sum(ss)/len(ss):.3f}")
    print(f"  angle=[{min(sa):.3f},{max(sa):.3f}] avg={sum(sa)/len(sa):.3f}")
    print(f"  dist=[{min(sd):.3f},{max(sd):.3f}] avg={sum(sd)/len(sd):.3f}")

if blind:
    bg = [float(r["gas"]) for r in blind]
    bs = [float(r["steering"]) for r in blind]
    print(f"BLIND: gas={sum(bg)/len(bg):.3f} steer={sum(bs)/len(bs):.3f}")

uzs = [float(r["uz"]) for r in rows]
print(f"UZ: start={uzs[0]:.3f} end={uzs[-1]:.3f} min={min(uzs):.3f}")
dx = [float(r["displacementX"]) for r in rows]
dz = [float(r["displacementZ"]) for r in rows]
print(f"Displacement: X=[{min(dx):.3f},{max(dx):.3f}] Z=[{min(dz):.3f},{max(dz):.3f}]")
hb = sum(1 for r in rows if int(r["hasBall"]) == 1)
print(f"HasBall: {hb}/{n}")

# Ball angle when seen - does model steer TOWARD ball?
print("\n=== DOES MODEL STEER TOWARD BALL? ===")
correct_steer = 0
wrong_steer = 0
for r in seen:
    angle = float(r["ballAngle"])  # positive = ball is right, negative = left
    steer = float(r["steering"])   # positive = turn right, negative = turn left
    if abs(angle) > 0.1 and abs(steer) > 0.05:
        if angle * steer > 0:  # same sign = steering toward ball
            correct_steer += 1
        else:
            wrong_steer += 1
print(f"Steering TOWARD ball: {correct_steer}")
print(f"Steering AWAY from ball: {wrong_steer}")
if correct_steer + wrong_steer > 0:
    print(f"Correct rate: {100*correct_steer//(correct_steer+wrong_steer)}%")

# Does model accelerate toward ball?
print("\n=== DOES MODEL DRIVE TOWARD BALL? ===")
for r in seen[:30]:
    angle = float(r["ballAngle"])
    dist = float(r["ballDist"])
    gas = float(r["gas"])
    steer = float(r["steering"])
    cam = float(r["camYaw"])
    step = r["step"]
    print(f"  [{step:>4}] angle={angle:+.3f} dist={dist:.3f} => gas={gas:+.3f} steer={steer:+.3f} cam={cam:+.3f}")

# Timeline: every 50th step
print("\n=== TIMELINE (every 100 steps) ===")
for i in range(0, n, 100):
    r = rows[i]
    print(f"  [{r['step']:>4}] t={float(r['time']):5.1f}s ball={r['ballSeen']} "
          f"gas={float(r['gas']):+.3f} steer={float(r['steering']):+.3f} "
          f"uz={float(r['uz']):.3f} dx={float(r['displacementX']):.3f} dz={float(r['displacementZ']):.3f}")
