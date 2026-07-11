import csv
import math

def analyze_log(filepath):
    print(f"\n========================================\nANALYZING: {filepath}\n========================================")
    
    try:
        with open(filepath, mode='r', newline='', encoding='utf-8-sig') as f:
            reader = csv.DictReader(f)
            rows = [row for row in reader]
    except Exception as e:
        print(f"Error reading {filepath}: {e}")
        return None
        
    total_steps = len(rows)
    if total_steps == 0:
        print("Empty log file")
        return None
        
    # Helper to parse values safely
    def get_val(row, key, default=0.0, is_int=False):
        if key not in row or row[key] == '':
            return default
        try:
            return int(row[key]) if is_int else float(row[key])
        except ValueError:
            return default

    first_row = rows[0]
    last_row = rows[-1]
    
    duration = get_val(last_row, 'time') - get_val(first_row, 'time')
    
    seen_count = 0
    angle_sum = 0.0
    angle_max = 0.0
    dist_sum = 0.0
    dist_min = 999.0
    dist_max = 0.0
    yolo_conf_sum = 0.0
    yolo_conf_min = 1.0
    bbox_w_sum = 0.0
    bbox_h_sum = 0.0
    
    gas_sum = 0.0
    gas_min = 999.0
    gas_max = -999.0
    steering_sum = 0.0
    steering_abs_max = 0.0
    speed_sum = 0.0
    speed_max = 0.0
    
    pwm_l_sum = 0.0
    pwm_l_max = 0.0
    pwm_r_sum = 0.0
    pwm_r_max = 0.0
    
    grab_steps = []
    has_ball_steps = 0
    stuck_steps_count = 0
    
    for row in rows:
        seen = get_val(row, 'ballSeen', is_int=True)
        gas = get_val(row, 'gas')
        steer = get_val(row, 'steering')
        speed = get_val(row, 'speed')
        
        gas_sum += gas
        if gas < gas_min: gas_min = gas
        if gas > gas_max: gas_max = gas
        
        steering_sum += steer
        if abs(steer) > steering_abs_max: steering_abs_max = abs(steer)
        
        speed_sum += speed
        if speed > speed_max: speed_max = speed
        
        if seen == 1:
            seen_count += 1
            angle = get_val(row, 'ballAngle')
            dist = get_val(row, 'ballDist')
            angle_sum += abs(angle)
            if abs(angle) > angle_max: angle_max = abs(angle)
            dist_sum += dist
            if dist < dist_min: dist_min = dist
            if dist > dist_max: dist_max = dist
            
            if 'yoloConf' in row:
                yolo_conf = get_val(row, 'yoloConf')
                yolo_conf_sum += yolo_conf
                if yolo_conf < yolo_conf_min: yolo_conf_min = yolo_conf
                bbox_w_sum += get_val(row, 'bboxW')
                bbox_h_sum += get_val(row, 'bboxH')
                
        if 'realPwmL' in row:
            pwmL = get_val(row, 'realPwmL')
            pwmR = get_val(row, 'realPwmR')
            pwm_l_sum += abs(pwmL)
            if abs(pwmL) > pwm_l_max: pwm_l_max = abs(pwmL)
            pwm_r_sum += abs(pwmR)
            if abs(pwmR) > pwm_r_max: pwm_r_max = abs(pwmR)
            
        grip_ir = get_val(row, 'gripIR', is_int=True)
        if grip_ir == 1:
            grab_steps.append((get_val(row, 'step', is_int=True), get_val(row, 'time')))
            
        has_ball = get_val(row, 'hasBall', is_int=True)
        if has_ball == 1:
            has_ball_steps += 1
            
        if speed < 0.01 and abs(gas) > 0.15:
            stuck_steps_count += 1
            
    seen_pct = (seen_count / total_steps) * 100
    
    print(f"Total Steps: {total_steps}")
    print(f"Duration: {duration:.2f} seconds")
    print(f"Ball Seen: {seen_count} steps ({seen_pct:.1f}%)")
    
    if seen_count > 0:
        print(f"Ball Angle: mean_abs={angle_sum/seen_count:.3f}, max_abs={angle_max:.3f}")
        print(f"Ball Dist: mean={dist_sum/seen_count:.3f}, min={dist_min:.3f}, max={dist_max:.3f}")
        if 'yoloConf' in first_row and first_row['yoloConf'] != '':
            print(f"YOLO Conf: mean={yolo_conf_sum/seen_count:.3f}, min={yolo_conf_min:.3f}")
            print(f"YOLO bboxW: mean={bbox_w_sum/seen_count:.1f}, bboxH: mean={bbox_h_sum/seen_count:.1f}")
            
    print(f"Gas: mean={gas_sum/total_steps:.3f}, range=[{gas_min:.3f}, {gas_max:.3f}]")
    print(f"Steering: mean={steering_sum/total_steps:.3f}, max_abs={steering_abs_max:.3f}")
    print(f"Speed: mean={speed_sum/total_steps:.3f}, max={speed_max:.3f}")
    
    if 'realPwmL' in first_row and first_row['realPwmL'] != '':
        print(f"PWM Left: mean_abs={pwm_l_sum/total_steps:.1f}, max_abs={pwm_l_max:.1f}")
        print(f"PWM Right: mean_abs={pwm_r_sum/total_steps:.1f}, max_abs={pwm_r_max:.1f}")
        
    print(f"Gripper IR (gripIR): {len(grab_steps)} steps")
    print(f"Has Ball (hasBall): {has_ball_steps} steps")
        
    print(f"Stuck steps (speed=0 while gas>0.15): {stuck_steps_count} ({stuck_steps_count/total_steps*100:.1f}%)")
    
    if 'posX' in first_row and first_row['posX'] != '' and 'posZ' in first_row and first_row['posZ'] != '':
        dx = get_val(last_row, 'posX') - get_val(first_row, 'posX')
        dz = get_val(last_row, 'posZ') - get_val(first_row, 'posZ')
        dist_traveled = math.sqrt(dx**2 + dz**2)
        print(f"Net displacement (posX/posZ): {dist_traveled:.3f} meters")

    # Dump LAST step of telemetry
    print("\nLAST STEP TELEMETRY:")
    r = last_row
    print(f"  Step {r.get('step')}: t={r.get('time')}, gas={r.get('gas')}, steer={r.get('steering')}, speed={r.get('speed')}, ballAngle={r.get('ballAngle')}, ballDist={r.get('ballDist')}, gripIR={r.get('gripIR')}, hasBall={r.get('hasBall')}, realPwmL={r.get('realPwmL')}, realPwmR={r.get('realPwmR')}")

print("Starting log analysis...")
analyze_log("sim_log1.csv")
analyze_log("sim_log2.csv")
analyze_log("real_log1.csv")
analyze_log("real_log2.csv")
