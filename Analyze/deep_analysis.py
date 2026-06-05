import csv

with open('sim_log.csv') as f:
    rows = list(csv.DictReader(f))
n = len(rows)

# Timeline: when does ball get seen/lost?
print("=== BALL VISIBILITY TIMELINE ===")
transitions = []
for i in range(1, n):
    prev = int(rows[i-1]['ballSeen'])
    curr = int(rows[i]['ballSeen'])
    if prev != curr:
        step = int(rows[i]['step'])
        state = 'FOUND' if curr == 1 else 'LOST'
        gas = float(rows[i]['gas'])
        steer = float(rows[i]['steering'])
        uz = float(rows[i]['uz'])
        dx = float(rows[i]['displacementX'])
        dz = float(rows[i]['displacementZ'])
        transitions.append((i, step, state, gas, steer, uz, dx, dz))

for idx, step, state, gas, steer, uz, dx, dz in transitions:
    print(f"  row={idx:4d} step={step:4d} [{state:5s}] gas={gas:+.3f} steer={steer:+.3f} uz={uz:.3f} disp=({dx:.3f},{dz:.3f})")

found_count = sum(1 for _,_,s,_,_,_,_,_ in transitions if s == "FOUND")
lost_count = sum(1 for _,_,s,_,_,_,_,_ in transitions if s == "LOST")
print(f"\nTotal transitions: {len(transitions)}")
print(f"FOUND events: {found_count}, LOST events: {lost_count}")

# After ball is lost, what does robot do?
print("\n=== AFTER EACH BALL LOST EVENT ===")
lost_events = [i for i, _, s, _, _, _, _, _ in transitions if s == "LOST"]
for li, lost_idx in enumerate(lost_events[:5]):
    print(f"\n--- LOST event {li+1} at row {lost_idx} (step {rows[lost_idx]['step']}) ---")
    for j in range(lost_idx, min(lost_idx + 30, n)):
        r = rows[j]
        print(f"  step={r['step']:>5} ball={r['ballSeen']} gas={float(r['gas']):+.3f} steer={float(r['steering']):+.3f} "
              f"uz={float(r['uz']):.3f} blind={r['blindTicks']} retry={r['isRetrying']} wasClose={r['wasClose']}")

# REAL log analysis - was robot driving backwards?
print("\n\n===== REAL LOG ANALYSIS =====")
with open('real_log.csv') as f:
    rrows = list(csv.DictReader(f))
rn = len(rrows)

print(f"Total steps: {rn}")
# Gas sign analysis
neg_gas = sum(1 for r in rrows if float(r['gas']) < -0.05)
pos_gas = sum(1 for r in rrows if float(r['gas']) > 0.05)
print(f"Gas > 0.05 (forward?): {pos_gas}/{rn} = {100*pos_gas//rn}%")
print(f"Gas < -0.05 (reverse?): {neg_gas}/{rn} = {100*neg_gas//rn}%")

print("\nFirst 30 steps:")
for r in rrows[:30]:
    print(f"  step={r['step']:>5} ball={r['ballSeen']} gas={float(r['gas']):+.3f} steer={float(r['steering']):+.3f} "
          f"uz={float(r['uz']):.3f} dx={float(r['displacementX']):.4f} dz={float(r['displacementZ']):.4f}")

print("\nLast 30 steps:")
for r in rrows[-30:]:
    print(f"  step={r['step']:>5} ball={r['ballSeen']} gas={float(r['gas']):+.3f} steer={float(r['steering']):+.3f} "
          f"uz={float(r['uz']):.3f} hasBall={r['hasBall']}")
