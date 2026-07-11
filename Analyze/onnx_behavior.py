"""
ONNX Brain 8 — правильный поведенческий анализ
Observation map (4 stacked frames × 10 obs = 40):
  [0] UZ_normalized (0-1, ~0.48)
  [1] leftIR (0/1)
  [2] rightIR (0/1)
  [3] gripperIR (0/1)
  [4] ballAngle (-1 to 1, 0 if not seen)
  [5] ballDist (0-1, 1 if not seen)
  [6] lastKnownBallDirection (-1/0/1)
  [7] ballSeen (0/1)
  [8] cameraYaw (-1 to 1)
  [9] hasBall (0/1)
  Repeated at [10-19], [20-29], [30-39]
"""
import onnxruntime as ort
import numpy as np

path = r"c:\Users\Admin\Unity\ROS_test\Assets\GFSX_Brain 8.onnx"
sess = ort.InferenceSession(path)
inputs = sess.get_inputs()
outputs = sess.get_outputs()

def make_obs(uz=0.5, leftIR=0, rightIR=0, gripIR=0, angle=0, dist=1, lastDir=0, seen=0, camYaw=0, hasBall=0):
    """Create a single frame observation"""
    return [uz, leftIR, rightIR, gripIR, angle, dist, lastDir, seen, camYaw, hasBall]

def make_stacked(frames):
    """Stack 4 frames into 40-dim obs"""
    obs = np.zeros((1, 40), dtype=np.float32)
    for i, frame in enumerate(frames[-4:]):
        for j, v in enumerate(frame):
            obs[0, i*10 + j] = v
    return obs

def run_inference(obs, memory=None):
    if memory is None:
        memory = np.zeros((1, 1, 128), dtype=np.float32)
    masks = np.ones((1, 3), dtype=np.float32)
    
    results = sess.run(None, {
        "obs_0": obs,
        "action_masks": masks,
        "recurrent_in": memory
    })
    
    # Parse outputs
    out_dict = {}
    for o, r in zip(outputs, results):
        out_dict[o.name] = r
    
    det = out_dict.get("deterministic_continuous_actions", out_dict.get("continuous_actions"))
    gas, steer, cam = det.flatten()[:3]
    
    disc = out_dict.get("deterministic_discrete_actions", out_dict.get("discrete_actions"))
    grip = int(disc.flatten()[0]) if disc is not None else -1
    
    mem = out_dict.get("recurrent_out")
    return gas, steer, cam, grip, mem

print("="*100)
print("GFSX_Brain 8 — BEHAVIORAL ANALYSIS (4-stacked, LSTM)")
print("="*100)

# Single-shot tests (no memory accumulation)
print(f"\n{'Scenario':40s} | {'gas':>7} {'steer':>7} {'cam':>7} | {'grip':>5} | interp")
print("-" * 100)

tests = [
    # Ball visible scenarios
    ("Ball CENTER, close",           make_obs(uz=0.5, angle=0.0, dist=0.2, seen=1, lastDir=0)),
    ("Ball CENTER, far",             make_obs(uz=0.5, angle=0.0, dist=0.8, seen=1, lastDir=0)),
    ("Ball RIGHT 30°, close",        make_obs(uz=0.5, angle=0.3, dist=0.2, seen=1, lastDir=1)),
    ("Ball RIGHT 30°, far",          make_obs(uz=0.5, angle=0.3, dist=0.8, seen=1, lastDir=1)),
    ("Ball LEFT 30°, close",         make_obs(uz=0.5, angle=-0.3, dist=0.2, seen=1, lastDir=-1)),
    ("Ball LEFT 30°, far",           make_obs(uz=0.5, angle=-0.3, dist=0.8, seen=1, lastDir=-1)),
    ("Ball FAR RIGHT 80°",           make_obs(uz=0.5, angle=0.8, dist=0.7, seen=1, lastDir=1)),
    ("Ball FAR LEFT 80°",            make_obs(uz=0.5, angle=-0.8, dist=0.7, seen=1, lastDir=-1)),
    ("Ball VERY close (grab range)", make_obs(uz=0.3, angle=0.0, dist=0.05, seen=1, gripIR=1, lastDir=0)),
    # Blind scenarios
    ("BLIND, open space",            make_obs(uz=0.5, angle=0, dist=1, seen=0, lastDir=0)),
    ("BLIND, wall ahead",            make_obs(uz=0.1, angle=0, dist=1, seen=0, lastDir=0)),
    ("BLIND, last seen RIGHT",       make_obs(uz=0.5, angle=0, dist=1, seen=0, lastDir=1)),
    ("BLIND, last seen LEFT",        make_obs(uz=0.5, angle=0, dist=1, seen=0, lastDir=-1)),
    # Obstacle scenarios
    ("Ball ahead, WALL close",       make_obs(uz=0.1, angle=0.0, dist=0.3, seen=1, lastDir=0)),
    ("Ball RIGHT, IR LEFT triggered", make_obs(uz=0.3, angle=0.3, dist=0.3, seen=1, leftIR=1, lastDir=1)),
    ("Ball LEFT, IR RIGHT triggered", make_obs(uz=0.3, angle=-0.3, dist=0.3, seen=1, rightIR=1, lastDir=-1)),
    # Already caught
    ("HAS BALL in gripper",          make_obs(uz=0.5, angle=0, dist=1, seen=0, hasBall=1, lastDir=0)),
    # Camera rotated
    ("Ball ahead, cam LEFT",         make_obs(uz=0.5, angle=0.0, dist=0.5, seen=1, camYaw=-0.5, lastDir=0)),
    ("Ball ahead, cam RIGHT",        make_obs(uz=0.5, angle=0.0, dist=0.5, seen=1, camYaw=0.5, lastDir=0)),
]

for name, frame in tests:
    stacked = make_stacked([frame, frame, frame, frame])
    gas, steer, cam, grip, _ = run_inference(stacked)
    
    interp = []
    if gas > 0.3: interp.append(f"FWD({gas:.2f})")
    elif gas < -0.1: interp.append(f"REV({gas:.2f})")
    else: interp.append(f"slow({gas:.2f})")
    
    if steer > 0.15: interp.append(f"RIGHT({steer:.2f})")
    elif steer < -0.15: interp.append(f"LEFT({steer:.2f})")
    else: interp.append(f"straight")
    
    if abs(cam) > 0.1: interp.append(f"cam{'R' if cam>0 else 'L'}")
    
    grip_name = {0: "NOOP", 1: "CLOSE", 2: "OPEN"}.get(grip, f"?{grip}")
    
    print(f"{name:40s} | {gas:+7.3f} {steer:+7.3f} {cam:+7.3f} | {grip_name:>5} | {' '.join(interp)}")

# LSTM MEMORY test — simulate a sequence
print(f"\n{'='*100}")
print("LSTM SEQUENCE TEST: Ball appears, approaches, then lost")
print("="*100)

sequence = [
    # 4 frames blind
    ("t=0: Blind start",     make_obs(uz=0.5, seen=0, lastDir=0)),
    ("t=1: Blind",           make_obs(uz=0.5, seen=0, lastDir=0)),
    ("t=2: Blind",           make_obs(uz=0.5, seen=0, lastDir=0)),
    ("t=3: Blind",           make_obs(uz=0.5, seen=0, lastDir=0)),
    # Ball appears far right
    ("t=4: Ball RIGHT far",  make_obs(uz=0.5, angle=0.6, dist=0.7, seen=1, lastDir=1)),
    ("t=5: Ball RIGHT med",  make_obs(uz=0.5, angle=0.4, dist=0.5, seen=1, lastDir=1)),
    ("t=6: Ball CENTER med", make_obs(uz=0.5, angle=0.1, dist=0.4, seen=1, lastDir=1)),
    ("t=7: Ball CENTER close", make_obs(uz=0.4, angle=0.0, dist=0.2, seen=1, lastDir=0)),
    ("t=8: Ball CENTER very close", make_obs(uz=0.3, angle=0.0, dist=0.1, seen=1, gripIR=1, lastDir=0)),
    # Ball lost after passing
    ("t=9: LOST (was center)", make_obs(uz=0.5, angle=0, dist=1, seen=0, lastDir=0)),
    ("t=10: LOST",           make_obs(uz=0.5, angle=0, dist=1, seen=0, lastDir=0)),
    ("t=11: LOST",           make_obs(uz=0.5, angle=0, dist=1, seen=0, lastDir=0)),
]

memory = np.zeros((1, 1, 128), dtype=np.float32)
frame_history = []

print(f"{'Step':30s} | {'gas':>7} {'steer':>7} {'cam':>7} | {'grip':>5} | interp")
print("-" * 100)

for name, frame in sequence:
    frame_history.append(frame)
    # Stack last 4
    while len(frame_history) < 4:
        frame_history.insert(0, make_obs())
    
    stacked = make_stacked(frame_history[-4:])
    gas, steer, cam, grip, memory = run_inference(stacked, memory)
    
    interp = []
    if gas > 0.3: interp.append(f"FWD({gas:.2f})")
    elif gas < -0.1: interp.append(f"REV({gas:.2f})")
    else: interp.append(f"slow({gas:.2f})")
    
    if steer > 0.15: interp.append(f"RIGHT({steer:.2f})")
    elif steer < -0.15: interp.append(f"LEFT({steer:.2f})")
    else: interp.append("straight")
    
    grip_name = {0: "NOOP", 1: "CLOSE", 2: "OPEN"}.get(grip, f"?{grip}")
    
    print(f"{name:30s} | {gas:+7.3f} {steer:+7.3f} {cam:+7.3f} | {grip_name:>5} | {' '.join(interp)}")

# Sensitivity analysis: how does gas change with distance?
print(f"\n{'='*100}")
print("SPEED vs DISTANCE: Does model slow down when close?")
print("="*100)
print(f"{'dist':>5} | {'gas':>7} {'steer':>7} | interp")
print("-" * 50)
for dist in [0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3, 0.2, 0.1, 0.05]:
    frame = make_obs(uz=0.5, angle=0.1, dist=dist, seen=1, lastDir=1)
    stacked = make_stacked([frame]*4)
    gas, steer, cam, grip, _ = run_inference(stacked)
    tag = "FAST" if gas > 0.5 else ("SLOW" if gas < 0.2 else "MED")
    print(f"{dist:5.2f} | {gas:+7.3f} {steer:+7.3f} | {tag}")
