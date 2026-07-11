"""
ONNX Brain Reverse Engineering — полный анализ ML-Agents модели
Анализирует архитектуру, веса, и поведение нейросети
"""
import onnx
import onnxruntime as ort
import numpy as np
import sys
import os

def analyze_model(path, label):
    print(f"\n{'='*80}")
    print(f"  MODEL: {label}")
    print(f"  File: {os.path.basename(path)} ({os.path.getsize(path)/1024:.0f} KB)")
    print(f"{'='*80}\n")
    
    # 1. ONNX Graph Analysis
    model = onnx.load(path)
    print(f"IR Version: {model.ir_version}")
    print(f"Opset: {[o.version for o in model.opset_import]}")
    print(f"Producer: {model.producer_name} v{model.producer_version}")
    
    graph = model.graph
    
    # Inputs
    print(f"\n--- INPUTS ({len(graph.input)}) ---")
    for inp in graph.input:
        shape = [d.dim_value if d.dim_value else d.dim_param for d in inp.type.tensor_type.shape.dim]
        dtype = onnx.TensorProto.DataType.Name(inp.type.tensor_type.elem_type)
        print(f"  {inp.name}: {shape} ({dtype})")
    
    # Outputs
    print(f"\n--- OUTPUTS ({len(graph.output)}) ---")
    for out in graph.output:
        shape = [d.dim_value if d.dim_value else d.dim_param for d in out.type.tensor_type.shape.dim]
        dtype = onnx.TensorProto.DataType.Name(out.type.tensor_type.elem_type)
        print(f"  {out.name}: {shape} ({dtype})")
    
    # Count layers by type
    print(f"\n--- OPERATIONS ({len(graph.node)} nodes) ---")
    op_counts = {}
    for node in graph.node:
        op_counts[node.op_type] = op_counts.get(node.op_type, 0) + 1
    for op, cnt in sorted(op_counts.items(), key=lambda x: -x[1]):
        print(f"  {op}: {cnt}")
    
    # Weights analysis
    print(f"\n--- WEIGHTS ({len(graph.initializer)} tensors) ---")
    total_params = 0
    layers = []
    for init in graph.initializer:
        arr = onnx.numpy_helper.to_array(init)
        total_params += arr.size
        layers.append((init.name, arr.shape, arr.size, arr.mean(), arr.std(), arr.min(), arr.max()))
    
    print(f"  Total parameters: {total_params:,}")
    print(f"  Model size (params only): {total_params * 4 / 1024:.0f} KB")
    
    # Show key weight layers (skip small ones)
    print(f"\n--- LAYER WEIGHTS ---")
    for name, shape, size, mean, std, mn, mx in layers:
        if size > 10:  # Skip biases and scalars
            print(f"  {name:50s} shape={str(shape):20s} params={size:>7,} "
                  f"mean={mean:+.4f} std={std:.4f} range=[{mn:.3f}, {mx:.3f}]")
    
    # 2. Runtime inference test
    print(f"\n--- INFERENCE TEST ---")
    sess = ort.InferenceSession(path)
    
    input_info = sess.get_inputs()
    output_info = sess.get_outputs()
    
    print(f"Runtime inputs:")
    for i in input_info:
        print(f"  {i.name}: {i.shape} ({i.type})")
    print(f"Runtime outputs:")
    for o in output_info:
        print(f"  {o.name}: {o.shape} ({o.type})")
    
    # Create test inputs
    feeds = {}
    for i in input_info:
        shape = []
        for d in i.shape:
            if isinstance(d, str) or d is None or d < 0:
                shape.append(1)  # batch dim
            else:
                shape.append(d)
        feeds[i.name] = np.zeros(shape, dtype=np.float32)
    
    # Test: ball not seen (all zeros)
    results = sess.run(None, feeds)
    print(f"\nTest 1: All zeros (blind):")
    for o, r in zip(output_info, results):
        print(f"  {o.name}: {r.flatten()[:10]}")
    
    # Find the observation input
    obs_name = None
    obs_shape = None
    for i in input_info:
        if "obs" in i.name.lower():
            obs_name = i.name
            shape = []
            for d in i.shape:
                if isinstance(d, str) or d is None or d < 0:
                    shape.append(1)
                else:
                    shape.append(d)
            obs_shape = shape
            break
    
    if obs_name is None:
        # Try first input
        obs_name = input_info[0].name
        shape = []
        for d in input_info[0].shape:
            if isinstance(d, str) or d is None or d < 0:
                shape.append(1)
            else:
                shape.append(d)
        obs_shape = shape
    
    obs_size = obs_shape[-1] if len(obs_shape) > 1 else obs_shape[0]
    print(f"\nObservation input: {obs_name}, obs_size={obs_size}")
    
    # Test scenarios
    scenarios = {
        "Ball center close": None,  # Will fill based on obs structure
        "Ball right close": None,
        "Ball left far": None,
        "No ball": None,
        "Ball center + obstacle close": None,
    }
    
    # Based on RobotBrain.CollectObservations:
    # [0] UZ_normalized (0-1)
    # [1] ballSeen (0/1)  
    # [2] ballAngle (-1 to 1)
    # [3] ballDist (0-1)
    # [4] irLeft (0/1)
    # [5] irRight (0/1)
    # [6] gripperIR (0/1)
    # [7] cameraYaw (-1 to 1)
    # [8] hasBall (0/1)
    # [9] prevGas (-1 to 1)
    # [10] prevSteering (-1 to 1)
    # [11] prevCameraYaw (-1 to 1)
    
    print(f"\n--- BEHAVIORAL ANALYSIS ---")
    test_cases = [
        ("Ball center, close",       [0.5, 1, 0.0, 0.3, 0, 0, 0, 0.0, 0, 0.3, 0.0, 0.0]),
        ("Ball center, far",         [0.5, 1, 0.0, 0.8, 0, 0, 0, 0.0, 0, 0.3, 0.0, 0.0]),
        ("Ball right, close",        [0.5, 1, 0.5, 0.3, 0, 0, 0, 0.0, 0, 0.3, 0.1, 0.0]),
        ("Ball right, far",          [0.5, 1, 0.5, 0.8, 0, 0, 0, 0.0, 0, 0.5, 0.2, 0.0]),
        ("Ball left, close",         [0.5, 1,-0.5, 0.3, 0, 0, 0, 0.0, 0, 0.3,-0.1, 0.0]),
        ("Ball left, far",           [0.5, 1,-0.5, 0.8, 0, 0, 0, 0.0, 0, 0.5,-0.2, 0.0]),
        ("Ball far right",           [0.5, 1, 0.9, 0.7, 0, 0, 0, 0.0, 0, 0.2, 0.3, 0.0]),
        ("Ball very close center",   [0.3, 1, 0.0, 0.1, 0, 0, 0, 0.0, 0, 0.4, 0.0, 0.0]),
        ("No ball, open",            [0.5, 0, 0.0, 0.0, 0, 0, 0, 0.0, 0, 0.0, 0.0, 0.0]),
        ("No ball, wall ahead",      [0.1, 0, 0.0, 0.0, 0, 0, 0, 0.0, 0, 0.3, 0.0, 0.0]),
        ("Ball, obstacle close",     [0.1, 1, 0.0, 0.3, 0, 0, 0, 0.0, 0, 0.3, 0.0, 0.0]),
        ("Has ball in gripper",      [0.5, 0, 0.0, 0.0, 0, 0, 1, 0.0, 1, 0.0, 0.0, 0.0]),
        ("IR left triggered",        [0.3, 1, 0.2, 0.2, 1, 0, 0, 0.0, 0, 0.3, 0.1, 0.0]),
        ("IR right triggered",       [0.3, 1,-0.2, 0.2, 0, 1, 0, 0.0, 0, 0.3,-0.1, 0.0]),
        ("GripIR + ball close",      [0.3, 1, 0.0, 0.1, 0, 0, 1, 0.0, 0, 0.2, 0.0, 0.0]),
    ]
    
    print(f"{'Scenario':30s} | {'gas':>7} {'steer':>7} {'camYaw':>7} | {'gripCmd':>8} | interpretation")
    print("-" * 100)
    
    for name, obs_data in test_cases:
        obs = np.zeros(obs_shape, dtype=np.float32)
        for j, v in enumerate(obs_data):
            if j < obs_size:
                obs[0, j] = v
        
        feeds_test = dict(feeds)
        feeds_test[obs_name] = obs
        
        results = sess.run(None, feeds_test)
        
        # ML-Agents output: continuous actions [gas, steering, cameraYaw], discrete [gripper]
        continuous = None
        discrete = None
        for o, r in zip(output_info, results):
            if "continuous" in o.name.lower() or r.shape[-1] == 3:
                continuous = r.flatten()
            elif "discrete" in o.name.lower() or r.shape[-1] == 1:
                discrete = r.flatten()
            elif r.shape[-1] >= 3:
                continuous = r.flatten()[:3]
        
        if continuous is None:
            # Try to guess
            for r in results:
                if r.size >= 3:
                    continuous = r.flatten()[:3]
                    break
        
        if continuous is not None and len(continuous) >= 3:
            gas, steer, cam = continuous[0], continuous[1], continuous[2]
            grip = int(discrete[0]) if discrete is not None else "?"
            
            # Interpret
            interp = []
            if gas > 0.3: interp.append(f"FWD({gas:.1f})")
            elif gas < -0.3: interp.append(f"REV({gas:.1f})")
            else: interp.append("slow")
            if steer > 0.2: interp.append(f"RIGHT({steer:.1f})")
            elif steer < -0.2: interp.append(f"LEFT({steer:.1f})")
            if abs(cam) > 0.1: interp.append(f"cam{'R' if cam>0 else 'L'}({cam:.1f})")
            
            print(f"{name:30s} | {gas:+7.3f} {steer:+7.3f} {cam:+7.3f} | {grip:>8} | {' '.join(interp)}")
        else:
            vals = [r.flatten()[:5] for r in results]
            print(f"{name:30s} | raw: {vals}")

# Analyze both models
models = [
    ("c:\\Users\\Admin\\Unity\\ROS_test\\Assets\\OnnxSuccessful\\GFSX_Brain 8.onnx", "GFSX_Brain 8 (OnnxSuccessful, 294KB)"),
    ("c:\\Users\\Admin\\Unity\\ROS_test\\Assets\\GFSX_Brain 8.onnx", "GFSX_Brain 8 (Assets, 646KB)"),
]

for path, label in models:
    if os.path.exists(path):
        try:
            analyze_model(path, label)
        except Exception as e:
            print(f"ERROR analyzing {label}: {e}")
            import traceback
            traceback.print_exc()
