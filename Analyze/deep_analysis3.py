import csv

with open("diagnostic_log.csv", encoding="utf-8-sig") as f:
    rows = list(csv.DictReader(f))

print("=== FIRST 50 STEPS: WHAT DID THE MODEL DO? ===")
print("(Robot sees ball initially, then what?)")
print()
for i in range(min(50, len(rows))):
    r = rows[i]
    step = r["step"]
    ball = int(r["ballSeen"])
    angle = float(r["ballAngle"])
    dist = float(r["ballDist"])
    gas = float(r["gas"])
    steer = float(r["steering"])
    uz = float(r["uz"])
    cam = float(r["camYaw"])
    mark = " <== BALL" if ball else ""
    print(f"  [{step:>4}] gas={gas:+.3f} steer={steer:+.3f} cam={cam:+.3f} | "
          f"ball={ball} angle={angle:+.3f} dist={dist:.3f} uz={uz:.3f}{mark}")

print()
print("=== WHAT COMMANDS WERE SENT TO REAL ROBOT? ===")
print("(gas>0 should now = FORWARD after motor fix)")
print()

# Check: is gas MOSTLY positive? Does steering flip a lot?
gases = [float(r["gas"]) for r in rows[:200]]
steers = [float(r["steering"]) for r in rows[:200]]

pos_gas = sum(1 for g in gases if g > 0.1)
neg_gas = sum(1 for g in gases if g < -0.1)
print(f"First 200 steps: gas>0.1={pos_gas}, gas<-0.1={neg_gas}")
print(f"  avg gas = {sum(gases)/len(gases):.3f}")
print(f"  avg |steer| = {sum(abs(s) for s in steers)/len(steers):.3f}")

# The KEY: what was the gas COMMAND sent to ROS?
# ROSBridge applies EMA (alpha=0.8), then publishes linear.x = smoothGas * 0.5
# Then unity_master receives it and applies: v_left = linear.x - angular.z*0.5
# Let's simulate:
ema_alpha = 0.8
smooth_gas = 0.0
smooth_steer = 0.0
print()
print("=== SIMULATED ROS COMMANDS (first 30 steps) ===")
for i in range(min(30, len(rows))):
    r = rows[i]
    gas = float(r["gas"])
    steer = float(r["steering"])
    
    if abs(gas) < 0.001 and abs(steer) < 0.001:
        smooth_gas = 0
        smooth_steer = 0
    else:
        smooth_gas = ema_alpha * gas + (1 - ema_alpha) * smooth_gas
        smooth_steer = ema_alpha * steer + (1 - ema_alpha) * smooth_steer
    
    linear_x = smooth_gas * 0.5
    angular_z = smooth_steer * 1.0
    
    v_left = linear_x - angular_z * 0.5
    v_right = linear_x + angular_z * 0.5
    pwm_left = max(min(v_left * 200, 100), -100)
    pwm_right = max(min(v_right * 200, 100), -100)
    
    # Apply MIN_MOTOR_PWM
    dir_l = "FWD" if pwm_left > 0 else ("REV" if pwm_left < 0 else "STOP")
    dir_r = "FWD" if pwm_right > 0 else ("REV" if pwm_right < 0 else "STOP")
    
    abs_l = abs(pwm_left)
    abs_r = abs(pwm_right)
    if 0 < abs_l < 35: abs_l = 35
    if 0 < abs_r < 35: abs_r = 35
    
    ball = int(r["ballSeen"])
    print(f"  [{r['step']:>4}] gas={gas:+.3f} steer={steer:+.3f} => "
          f"L={dir_l}@{abs_l:.0f} R={dir_r}@{abs_r:.0f} ball={ball}")

# Check if robot is SPINNING (one motor fwd, other rev)
print()
print("=== SPINNING DETECTION (L-fwd + R-rev or vice versa) ===")
spin_count = 0
for i in range(len(rows)):
    r = rows[i]
    gas = float(r["gas"])
    steer = float(r["steering"])
    
    smooth_gas_t = gas  # simplified
    smooth_steer_t = steer
    
    linear_x = smooth_gas_t * 0.5
    angular_z = smooth_steer_t * 1.0
    
    v_left = linear_x - angular_z * 0.5
    v_right = linear_x + angular_z * 0.5
    
    if (v_left > 0 and v_right < 0) or (v_left < 0 and v_right > 0):
        spin_count += 1

print(f"Steps where motors oppose each other: {spin_count}/{len(rows)} = {100*spin_count//len(rows)}%")
