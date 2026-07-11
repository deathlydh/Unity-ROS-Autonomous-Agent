"""
Сравнение GFSX_Brain 8 (старая, 294KB) vs GFSX_Brain 25 (текущая, 1.1MB)
Полный реверс-инжиниринг обеих моделей + side-by-side поведенческий анализ
"""
import onnx
import onnxruntime as ort
import numpy as np
import os

def get_arch(path):
    model = onnx.load(path)
    graph = model.graph
    info = {
        "file": os.path.basename(path),
        "size_kb": os.path.getsize(path) / 1024,
        "producer": f"{model.producer_name} v{model.producer_version}",
        "inputs": [],
        "outputs": [],
        "ops": {},
        "total_params": 0,
        "layers": [],
        "normalizer_mean": None,
        "normalizer_std": None,
    }
    for inp in graph.input:
        shape = [d.dim_value if d.dim_value else d.dim_param for d in inp.type.tensor_type.shape.dim]
        info["inputs"].append((inp.name, shape))
    for out in graph.output:
        shape = [d.dim_value if d.dim_value else d.dim_param for d in out.type.tensor_type.shape.dim]
        info["outputs"].append((out.name, shape))
    for node in graph.node:
        info["ops"][node.op_type] = info["ops"].get(node.op_type, 0) + 1
    for init in graph.initializer:
        arr = onnx.numpy_helper.to_array(init)
        info["total_params"] += arr.size
        info["layers"].append((init.name, arr.shape, arr.size, arr.mean(), arr.std()))
        if "running_mean" in init.name:
            info["normalizer_mean"] = arr
        if "Div" in init.name and arr.ndim == 1:
            info["normalizer_std"] = arr
    return info

def run_behavior(path, obs_size):
    sess = ort.InferenceSession(path)
    inputs = sess.get_inputs()
    outputs = sess.get_outputs()
    
    # Determine stack count
    # Brain 8: obs_size=40 → 10 obs × 4 stacks
    # Brain 25: obs_size=? → need to check
    input_shapes = {}
    for inp in inputs:
        shape = []
        for d in inp.shape:
            if isinstance(d, str) or d is None or d < 0:
                shape.append(1)
            else:
                shape.append(d)
        input_shapes[inp.name] = shape
    
    obs_key = [k for k in input_shapes if "obs" in k.lower()][0] if any("obs" in k.lower() for k in input_shapes) else list(input_shapes.keys())[0]
    total_obs = input_shapes[obs_key][-1]
    
    # Find memory input
    mem_key = [k for k in input_shapes if "recurrent" in k.lower()]
    mem_key = mem_key[0] if mem_key else None
    mem_size = input_shapes[mem_key][-1] if mem_key else 0
    
    # Find mask input
    mask_key = [k for k in input_shapes if "mask" in k.lower()]
    mask_key = mask_key[0] if mask_key else None
    
    def make_feeds(obs_arr, mem=None):
        feeds = {}
        feeds[obs_key] = obs_arr.astype(np.float32)
        if mem_key:
            feeds[mem_key] = mem if mem is not None else np.zeros(input_shapes[mem_key], dtype=np.float32)
        if mask_key:
            feeds[mask_key] = np.ones(input_shapes[mask_key], dtype=np.float32)
        # Any other inputs
        for k, shape in input_shapes.items():
            if k not in feeds:
                feeds[k] = np.zeros(shape, dtype=np.float32)
        return feeds
    
    def infer(obs_arr, mem=None):
        feeds = make_feeds(obs_arr, mem)
        results = sess.run(None, feeds)
        out_dict = {}
        for o, r in zip(outputs, results):
            out_dict[o.name] = r
        
        det = out_dict.get("deterministic_continuous_actions", out_dict.get("continuous_actions"))
        vals = det.flatten()
        gas = vals[0] if len(vals) > 0 else 0
        steer = vals[1] if len(vals) > 1 else 0
        cam = vals[2] if len(vals) > 2 else 0
        
        disc = out_dict.get("deterministic_discrete_actions", out_dict.get("discrete_actions"))
        grip = int(disc.flatten()[0]) if disc is not None else -1
        
        mem_out = out_dict.get("recurrent_out")
        return gas, steer, cam, grip, mem_out
    
    return total_obs, mem_size, infer

# Load both models
models = {
    "Brain 8 (OnnxSuccessful)": r"c:\Users\Admin\Unity\ROS_test\Assets\OnnxSuccessful\GFSX_Brain 8.onnx",
    "Brain 25 (OnnxSuccessful)": r"c:\Users\Admin\Unity\ROS_test\Assets\OnnxSuccessful\GFSX_Brain 25.onnx",
}

archs = {}
infers = {}

for label, path in models.items():
    if not os.path.exists(path):
        print(f"SKIP: {path} not found")
        continue
    arch = get_arch(path)
    archs[label] = arch
    total_obs, mem_size, infer_fn = run_behavior(path, 0)
    infers[label] = (total_obs, mem_size, infer_fn)

# 1. Architecture comparison
print("=" * 100)
print("ARCHITECTURE COMPARISON")
print("=" * 100)

for label, arch in archs.items():
    print(f"\n--- {label} ---")
    print(f"  Size: {arch['size_kb']:.0f} KB, Params: {arch['total_params']:,}")
    print(f"  Producer: {arch['producer']}")
    print(f"  Inputs: {[(n, s) for n, s in arch['inputs']]}")
    print(f"  Outputs: {[n for n, s in arch['outputs']]}")
    print(f"  Key ops: {dict(sorted(arch['ops'].items(), key=lambda x: -x[1])[:8])}")
    print(f"  Layers:")
    for name, shape, size, mean, std in arch["layers"]:
        if size > 50:
            short = name.split(".")[-1] if "." in name else name
            print(f"    {name:55s} {str(shape):20s} {size:>7,} mean={mean:+.4f} std={std:.4f}")

# 2. Observation map comparison
print(f"\n{'=' * 100}")
print("OBSERVATION MAP COMPARISON")
print("=" * 100)

for label, arch in archs.items():
    total_obs, mem_size, _ = infers[label]
    print(f"\n--- {label}: {total_obs} observations, LSTM={mem_size} ---")
    if arch["normalizer_mean"] is not None:
        means = arch["normalizer_mean"]
        stds = arch["normalizer_std"] if arch["normalizer_std"] is not None else np.ones_like(means)
        
        # Detect stacking pattern
        # Try to find repeating block
        for block_size in [10, 12, 15, 16, 20]:
            if total_obs % block_size == 0:
                n_stacks = total_obs // block_size
                # Check if blocks are similar
                blocks = [means[i*block_size:(i+1)*block_size] for i in range(n_stacks)]
                diffs = [np.max(np.abs(blocks[0] - blocks[i])) for i in range(1, n_stacks)]
                if all(d < 0.1 for d in diffs):
                    print(f"  Stacking detected: {n_stacks}x {block_size} obs")
                    print(f"  Block 0 mean values:")
                    for j in range(block_size):
                        print(f"    [{j:2d}] mean={means[j]:+.4f} std={stds[j]:.4f}")
                    break
        else:
            print(f"  No clear stacking. All {total_obs} means:")
            for j in range(min(total_obs, 40)):
                print(f"    [{j:2d}] mean={means[j]:+.4f} std={stds[j]:.4f}")

# 3. Behavioral comparison
print(f"\n{'=' * 100}")
print("BEHAVIORAL COMPARISON: Side-by-side")
print("=" * 100)

# Build obs based on what each model expects
def make_obs_brain8(uz=0.5, leftIR=0, rightIR=0, gripIR=0, angle=0, dist=1, lastDir=0, seen=0, camYaw=0, hasBall=0):
    return [uz, leftIR, rightIR, gripIR, angle, dist, lastDir, seen, camYaw, hasBall]

tests = [
    ("Ball CENTER close",     dict(uz=0.5, angle=0.0, dist=0.2, seen=1)),
    ("Ball CENTER far",       dict(uz=0.5, angle=0.0, dist=0.8, seen=1)),
    ("Ball RIGHT 30° close",  dict(uz=0.5, angle=0.3, dist=0.3, seen=1, lastDir=1)),
    ("Ball RIGHT 30° far",    dict(uz=0.5, angle=0.3, dist=0.7, seen=1, lastDir=1)),
    ("Ball LEFT 30° far",     dict(uz=0.5, angle=-0.3, dist=0.7, seen=1, lastDir=-1)),
    ("Ball FAR RIGHT 80°",    dict(uz=0.5, angle=0.8, dist=0.7, seen=1, lastDir=1)),
    ("Ball VERY close",       dict(uz=0.3, angle=0.0, dist=0.05, seen=1, gripIR=1)),
    ("BLIND open",            dict(uz=0.5)),
    ("BLIND wall",            dict(uz=0.1)),
    ("BLIND last RIGHT",      dict(uz=0.5, lastDir=1)),
    ("BLIND last LEFT",       dict(uz=0.5, lastDir=-1)),
    ("Wall + ball ahead",     dict(uz=0.1, angle=0.0, dist=0.3, seen=1)),
    ("HAS BALL",              dict(uz=0.5, hasBall=1)),
]

header = f"{'Scenario':25s}"
for label in infers:
    short = label.split("(")[0].strip()
    header += f" | {short:>7} {'gas':>6} {'steer':>6} {'cam':>5} {'grip':>4}"
print(header)
print("-" * len(header))

for name, kwargs in tests:
    line = f"{name:25s}"
    for label in infers:
        total_obs, mem_size, infer_fn = infers[label]
        
        # Determine block size
        frame = make_obs_brain8(**kwargs)
        
        # Figure out total_obs/stacks
        if total_obs == 40:
            n_stacks = 4; block = 10
        elif total_obs == 30:
            n_stacks = 2; block = 15
        elif total_obs == 20:
            n_stacks = 2; block = 10
        elif total_obs == 60:
            n_stacks = 4; block = 15
        else:
            # Try obs/10
            block = 10; n_stacks = total_obs // block
            if n_stacks == 0:
                block = total_obs; n_stacks = 1
        
        obs = np.zeros((1, total_obs), dtype=np.float32)
        for s in range(n_stacks):
            for j, v in enumerate(frame):
                if j < block:
                    obs[0, s * block + j] = v
        
        gas, steer, cam, grip, _ = infer_fn(obs)
        grip_name = {0: "N", 1: "C", 2: "O"}.get(grip, "?")
        line += f" |  {gas:+6.3f} {steer:+6.3f} {cam:+5.3f} {grip_name:>4}"
    print(line)

# 4. Speed-distance curve comparison
print(f"\n{'=' * 100}")
print("SPEED vs DISTANCE CURVE")
print("=" * 100)
print(f"{'dist':>5}", end="")
for label in infers:
    short = label.split("(")[0].strip()
    print(f" | {short:>7} gas  steer", end="")
print()
print("-" * 80)

for dist in [0.9, 0.7, 0.5, 0.3, 0.2, 0.1, 0.05]:
    line = f"{dist:5.2f}"
    for label in infers:
        total_obs, mem_size, infer_fn = infers[label]
        frame = make_obs_brain8(uz=0.5, angle=0.1, dist=dist, seen=1, lastDir=1)
        if total_obs == 40:
            n_stacks = 4; block = 10
        else:
            block = 10; n_stacks = total_obs // block
        obs = np.zeros((1, total_obs), dtype=np.float32)
        for s in range(n_stacks):
            for j, v in enumerate(frame):
                if j < block:
                    obs[0, s*block+j] = v
        gas, steer, cam, grip, _ = infer_fn(obs)
        line += f" | {gas:+7.3f} {steer:+6.3f}"
    print(line)

# 5. LSTM sequence comparison
print(f"\n{'=' * 100}")
print("LSTM SEQUENCE: Ball appears → approaches → grab → lost")
print("=" * 100)

sequence = [
    ("Blind",         dict(uz=0.5)),
    ("Blind",         dict(uz=0.5)),
    ("Blind",         dict(uz=0.5)),
    ("Ball R far",    dict(uz=0.5, angle=0.5, dist=0.8, seen=1, lastDir=1)),
    ("Ball R med",    dict(uz=0.5, angle=0.3, dist=0.5, seen=1, lastDir=1)),
    ("Ball C close",  dict(uz=0.4, angle=0.1, dist=0.3, seen=1, lastDir=1)),
    ("Ball C grab",   dict(uz=0.3, angle=0.0, dist=0.1, seen=1, gripIR=1)),
    ("Ball C grab",   dict(uz=0.3, angle=0.0, dist=0.05, seen=1, gripIR=1)),
    ("LOST",          dict(uz=0.5, lastDir=0)),
    ("LOST",          dict(uz=0.5, lastDir=0)),
    ("LOST",          dict(uz=0.5, lastDir=0)),
    ("LOST",          dict(uz=0.5, lastDir=0)),
]

for label in infers:
    total_obs, mem_size, infer_fn = infers[label]
    short = label.split("(")[0].strip()
    print(f"\n--- {short} ---")
    mem = None
    if total_obs == 40:
        n_stacks = 4; block = 10
    else:
        block = 10; n_stacks = total_obs // block
    
    frame_hist = []
    for name, kwargs in sequence:
        frame = make_obs_brain8(**kwargs)
        frame_hist.append(frame)
        
        obs = np.zeros((1, total_obs), dtype=np.float32)
        for s in range(n_stacks):
            idx = max(0, len(frame_hist) - n_stacks + s)
            if idx < len(frame_hist):
                for j, v in enumerate(frame_hist[idx]):
                    if j < block:
                        obs[0, s*block+j] = v
        
        gas, steer, cam, grip, mem = infer_fn(obs, mem)
        grip_name = {0: "N", 1: "C", 2: "O"}.get(grip, "?")
        print(f"  {name:15s} gas={gas:+.3f} steer={steer:+.3f} cam={cam:+.3f} grip={grip_name}")
