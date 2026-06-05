#!/usr/bin/env python3
"""Diagnostic analysis of sim_log.csv vs real_log.csv"""

def parse_line(parts):
    i = 0
    def take_float():
        nonlocal i
        val = float(parts[i] + '.' + parts[i+1])
        i += 2
        return val
    def take_int():
        nonlocal i
        val = int(parts[i])
        i += 1
        return val
    time = take_float()
    step = take_int()
    ballSeen = take_int()
    ballAngle = take_float()
    ballDist = take_float()
    uz = take_float()
    irL = take_int()
    irR = take_int()
    gripIR = take_int()
    camYaw = take_float()
    gas = take_float()
    steering = take_float()
    cameraAction = take_float()
    return dict(t=time, step=step, seen=ballSeen, angle=ballAngle, dist=ballDist,
                uz=uz, irL=irL, irR=irR, grip=gripIR, cam=camYaw,
                gas=gas, steer=steering, camA=cameraAction)

def load(path):
    rows = []
    with open(path) as f:
        f.readline()
        for line in f:
            parts = line.strip().split(',')
            if len(parts) >= 21:
                try:
                    rows.append(parse_line(parts))
                except:
                    pass
    return rows

def avg(lst):
    return sum(lst) / max(1, len(lst))

sim = load('sim_log.csv')
real = load('real_log.csv')

print("=" * 60)
print("SIM ANALYSIS")
print("=" * 60)
print(f"Total steps: {len(sim)}")
seen_s = [r for r in sim if r['seen'] == 1]
unseen_s = [r for r in sim if r['seen'] == 0]
print(f"BallSeen: {len(seen_s)} ({100*len(seen_s)/max(1,len(sim)):.0f}%)")
if seen_s:
    dists = [r['dist'] for r in seen_s]
    angles = [r['angle'] for r in seen_s]
    gases = [r['gas'] for r in seen_s]
    steers = [r['steer'] for r in seen_s]
    print(f"  Dist: avg={avg(dists):.3f}, range=[{min(dists):.3f}, {max(dists):.3f}]")
    print(f"  Angle: avg_abs={avg([abs(a) for a in angles]):.3f}, range=[{min(angles):.3f}, {max(angles):.3f}]")
    print(f"  Gas: avg={avg(gases):.3f}, min={min(gases):.3f}, max={max(gases):.3f}")
    print(f"  Steer: avg={avg(steers):.3f}")
    rev = sum(1 for r in seen_s if r['gas'] < -0.1)
    print(f"  Reverse (gas<-0.1): {rev}/{len(seen_s)} = {100*rev/max(1,len(seen_s)):.0f}%")

print()
print("=" * 60)
print("REAL ANALYSIS")
print("=" * 60)
print(f"Total steps: {len(real)}")
seen_r = [r for r in real if r['seen'] == 1]
unseen_r = [r for r in real if r['seen'] == 0]
print(f"BallSeen: {len(seen_r)} ({100*len(seen_r)/max(1,len(real)):.0f}%)")
print(f"BallLost: {len(unseen_r)} ({100*len(unseen_r)/max(1,len(real)):.0f}%)")
if seen_r:
    dists_r = [r['dist'] for r in seen_r]
    angles_r = [r['angle'] for r in seen_r]
    gases_r = [r['gas'] for r in seen_r]
    print(f"  Dist: avg={avg(dists_r):.3f}, range=[{min(dists_r):.3f}, {max(dists_r):.3f}]")
    print(f"  |Angle|: avg={avg([abs(a) for a in angles_r]):.3f}, range=[{min(angles_r):.3f}, {max(angles_r):.3f}]")
    print(f"  Gas (sees ball): avg={avg(gases_r):.3f}")
if unseen_r:
    gases_blind = [r['gas'] for r in unseen_r]
    rev_blind = sum(1 for g in gases_blind if g < -0.1)
    print(f"  Gas (blind): avg={avg(gases_blind):.3f}")
    print(f"  Reverse (blind): {rev_blind}/{len(unseen_r)} = {100*rev_blind/max(1,len(unseen_r)):.0f}%")

# Timing
print()
print("=" * 60)
print("TIMING")
print("=" * 60)
if len(sim) > 1:
    sim_dt = [sim[i+1]['t'] - sim[i]['t'] for i in range(len(sim)-1) if sim[i+1]['t'] > sim[i]['t']]
    if sim_dt:
        print(f"SIM step interval: avg={avg(sim_dt)*1000:.1f}ms, min={min(sim_dt)*1000:.1f}ms, max={max(sim_dt)*1000:.1f}ms")
if len(real) > 1:
    real_dt = [real[i+1]['t'] - real[i]['t'] for i in range(len(real)-1) if real[i+1]['t'] > real[i]['t']]
    if real_dt:
        print(f"REAL step interval: avg={avg(real_dt)*1000:.1f}ms, min={min(real_dt)*1000:.1f}ms, max={max(real_dt)*1000:.1f}ms")

# UZ comparison
print()
print("=" * 60)
print("UZ SENSOR")
print("=" * 60)
uz_s = [r['uz'] for r in sim]
uz_r = [r['uz'] for r in real]
print(f"SIM uz: avg={avg(uz_s):.3f}, range=[{min(uz_s):.3f}, {max(uz_s):.3f}]")
print(f"REAL uz: avg={avg(uz_r):.3f}, range=[{min(uz_r):.3f}, {max(uz_r):.3f}]")

# Ball visibility gaps
print()
print("=" * 60)
print("REAL: BALL VISIBILITY GAPS")
print("=" * 60)
gap_start = None
gaps = []
for i, r in enumerate(real):
    if r['seen'] == 0 and gap_start is None:
        gap_start = i
    elif r['seen'] == 1 and gap_start is not None:
        gaps.append(i - gap_start)
        gap_start = None
if gap_start is not None:
    gaps.append(len(real) - gap_start)
if gaps:
    print(f"Number of blind gaps: {len(gaps)}")
    print(f"Avg gap length: {avg(gaps):.1f} steps")
    print(f"Max gap length: {max(gaps)} steps ({max(gaps)*20:.0f}ms)")
    print(f"Total blind steps: {sum(gaps)}/{len(real)} ({100*sum(gaps)/len(real):.0f}%)")
else:
    print("No gaps found")

# Gas histogram
print()
print("=" * 60)
print("GAS DISTRIBUTION (REAL)")
print("=" * 60)
bins = [(-1.1, -0.5), (-0.5, -0.1), (-0.1, 0.1), (0.1, 0.5), (0.5, 1.1)]
labels = ['hard_rev', 'soft_rev', 'idle', 'soft_fwd', 'hard_fwd']
for (lo, hi), label in zip(bins, labels):
    count = sum(1 for r in real if lo <= r['gas'] < hi)
    pct = 100 * count / max(1, len(real))
    bar = '#' * int(pct / 2)
    print(f"  {label:10s} ({lo:+.1f} to {hi:+.1f}): {count:4d}/{len(real)} = {pct:4.0f}% {bar}")

# Gas histogram SIM
print()
print("=" * 60)
print("GAS DISTRIBUTION (SIM)")
print("=" * 60)
for (lo, hi), label in zip(bins, labels):
    count = sum(1 for r in sim if lo <= r['gas'] < hi)
    pct = 100 * count / max(1, len(sim))
    bar = '#' * int(pct / 2)
    print(f"  {label:10s} ({lo:+.1f} to {hi:+.1f}): {count:4d}/{len(sim)} = {pct:4.0f}% {bar}")

# CamYaw
print()
print("=" * 60)
print("CAMERA YAW")
print("=" * 60)
cam_s = [r['cam'] for r in sim]
cam_r = [r['cam'] for r in real]
print(f"SIM camYaw: avg={avg(cam_s):.3f}, range=[{min(cam_s):.3f}, {max(cam_s):.3f}]")
print(f"REAL camYaw: avg={avg(cam_r):.3f}, range=[{min(cam_r):.3f}, {max(cam_r):.3f}]")
