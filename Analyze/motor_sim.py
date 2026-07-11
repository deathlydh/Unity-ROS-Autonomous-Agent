import csv

with open("diagnostic_log.csv", encoding="utf-8-sig") as f:
    rows = list(csv.DictReader(f))

# Simulate the EXACT motor pipeline: ROSBridge → vel_callback → set_motors_pwm
# ROSBridge: linear.x = smoothGas * 0.5, angular.z = smoothSteering * 1.0
# vel_callback: v_left = linear.x + angular.z * TURN_K, v_right = linear.x - angular.z * TURN_K
# set_motors_pwm: pwm_left = -pwm_left (inversion), ramp, dead zone

TURN_K = 0.15
PWM_FACTOR = 200
MIN_PWM = 35
DEAD_ZONE = 10
MAX_STEP = 15
EMA_ALPHA = 0.8

smooth_gas = 0.0
smooth_steer = 0.0
prev_l = 0.0
prev_r = 0.0

print("=== SIMULATED MOTOR COMMANDS (first 100 steps) ===")
print(f"{'step':>5} {'ball':>4} {'gas':>6} {'steer':>6} | {'linX':>6} {'angZ':>6} | {'PWM_L':>6} {'PWM_R':>6} | {'physL':>6} {'physR':>6} | note")

for i in range(min(100, len(rows))):
    r = rows[i]
    gas = float(r["gas"])
    steer = float(r["steering"])
    ball = int(r["ballSeen"])
    step = r["step"]
    
    # EMA
    if abs(gas) < 0.001 and abs(steer) < 0.001:
        smooth_gas = 0; smooth_steer = 0
    else:
        smooth_gas = EMA_ALPHA * gas + (1-EMA_ALPHA) * smooth_gas
        smooth_steer = EMA_ALPHA * steer + (1-EMA_ALPHA) * smooth_steer
    
    lin_x = smooth_gas * 0.5
    ang_z = smooth_steer * 1.0
    
    # Differential drive
    vl = lin_x + ang_z * TURN_K
    vr = lin_x - ang_z * TURN_K
    
    pwm_l = max(min(vl * PWM_FACTOR, 100), -100)
    pwm_r = max(min(vr * PWM_FACTOR, 100), -100)
    
    # Left inversion
    pwm_l = -pwm_l
    
    # Ramp
    dl = pwm_l - prev_l
    if abs(dl) > MAX_STEP:
        pwm_l = prev_l + (MAX_STEP if dl > 0 else -MAX_STEP)
    dr = pwm_r - prev_r
    if abs(dr) > MAX_STEP:
        pwm_r = prev_r + (MAX_STEP if dr > 0 else -MAX_STEP)
    prev_l = pwm_l
    prev_r = pwm_r
    
    # Dead zone
    abs_l = abs(pwm_l)
    if abs_l < DEAD_ZONE: abs_l = 0
    elif abs_l < MIN_PWM: abs_l = MIN_PWM
    abs_r = abs(pwm_r)
    if abs_r < DEAD_ZONE: abs_r = 0
    elif abs_r < MIN_PWM: abs_r = MIN_PWM
    
    # Physical direction
    phys_l_dir = "FWD" if pwm_l < 0 else ("REV" if pwm_l > 0 else "STP")  # inverted
    phys_r_dir = "FWD" if pwm_r > 0 else ("REV" if pwm_r < 0 else "STP")
    
    note = ""
    if phys_l_dir != phys_r_dir and phys_l_dir != "STP" and phys_r_dir != "STP":
        note = "SPIN!"
    elif abs_l == abs_r and abs_l > 0:
        note = "STRAIGHT"
    elif abs_l > abs_r and phys_l_dir == "FWD" and phys_r_dir == "FWD":
        note = f"turn R ({abs_l-abs_r:.0f} diff)"
    elif abs_r > abs_l and phys_l_dir == "FWD" and phys_r_dir == "FWD":
        note = f"turn L ({abs_r-abs_l:.0f} diff)"
    
    b = "*" if ball else " "
    print(f"{step:>5} {b:>4} {gas:+.3f} {steer:+.3f} | {lin_x:+.3f} {ang_z:+.3f} | "
          f"{phys_l_dir}{abs_l:3.0f} {phys_r_dir}{abs_r:3.0f} | {note}")

# Summary stats
print("\n=== SIMULATED TURN CAPABILITY ===")
print("For typical model outputs:")
for g, s in [(0.5, 0.3), (0.5, 0.5), (0.4, 0.4), (0.3, 0.6), (0.8, 0.2)]:
    lin = g * 0.5
    ang = s * 1.0
    vl = lin + ang * TURN_K
    vr = lin - ang * TURN_K
    pl = max(min(-vl * PWM_FACTOR, 100), -100)  # after inversion
    pr = max(min(vr * PWM_FACTOR, 100), -100)
    al = abs(pl); ar = abs(pr)
    if al < DEAD_ZONE: al = 0
    elif al < MIN_PWM: al = MIN_PWM
    if ar < DEAD_ZONE: ar = 0
    elif ar < MIN_PWM: ar = MIN_PWM
    diff_pct = (al-ar)/max(ar,1)*100 if ar > 0 else 999
    print(f"  gas={g:.1f} steer={s:.1f} => L={al:.0f} R={ar:.0f} diff={al-ar:+.0f} ({diff_pct:+.0f}%)")
