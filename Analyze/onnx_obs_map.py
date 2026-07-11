"""
Корректный ONNX behavioral test для модели GFSX_Brain 8
Observation map (40 inputs, stacked 2x по 20):
  [0]  uz_normalized (0-1)
  [1]  leftIR (0/1) 
  [2]  rightIR (0/1)
  [3]  gripperIR (0/1)
  [4]  ballAngle (-1 to 1, 0 if not seen)
  [5]  ballDist (0-1, 1 if not seen)
  [6]  lastKnownBallDirection (-1/0/1)
  [7]  ballSeen (0/1)
  [8]  cameraYaw (-1 to 1)
  [9]  hasBall (0/1)
  [10] displacementX (-1 to 1)
  [11] displacementZ (-1 to 1)
  [12] heading (0-1, yaw/360)
  [13] speed (0-1)
  [14] timeSinceSeen (0-1)
  
  --- Maybe more fields OR stacked [15-19]=prev frame? ---
  
  With 40 total and 15 observations, it could be:
  - Stacked 2x: [0-14] = current, [15-29] = previous, then what's [30-39]?
  - OR the model has 20 observations (some I missed) x 2 stacked = 40
  - OR the model has prevGas/prevSteering/prevCameraYaw that I missed
  
  Actually from the code CollectObservations adds:
  4 (UZ, leftIR, rightIR, gripperIR) +
  5 (ballAngle, ballDist, lastKnownBallDir, ballSeen, cameraYaw) +
  1 (hasBall) +
  2 (displacementX, displacementZ) +
  1 (heading) +
  1 (speed) +
  1 (timeSinceSeen) = 15 total
  
  BUT the running_mean has 40 values, and the normalizer sees 40.
  40/15 is not integer. Could be stacked but also there might be
  prevGas, prevSteering, prevCameraYaw (3 more) = 18.
  
  Actually, I see prevGas, prevSteering, prevCameraYaw in the diagnostics log header.
  Let me check if they're added as observations too.
  
  If 18 + 2 padding = 20, stacked x2 = 40? Or 20 observations stacked 2x.
"""

import onnxruntime as ort
import numpy as np

# Use the model from Assets (646KB, has discrete actions)
path = r"c:\Users\Admin\Unity\ROS_test\Assets\GFSX_Brain 8.onnx"
sess = ort.InferenceSession(path)

inputs = sess.get_inputs()
outputs = sess.get_outputs()

# Check normalizer running_mean to understand what each observation LOOKS like
import onnx
model = onnx.load(path)
for init in model.graph.initializer:
    if "running_mean" in init.name:
        arr = onnx.numpy_helper.to_array(init)
        print(f"Running mean ({len(arr)} values):")
        for i, v in enumerate(arr):
            print(f"  [{i:2d}] mean={v:+.4f}")
    if "Div_110" in init.name:  # This is usually running_variance or std
        arr = onnx.numpy_helper.to_array(init)
        print(f"\nRunning std ({len(arr)} values):")
        for i, v in enumerate(arr):
            print(f"  [{i:2d}] std={v:.4f}")

# The normalizer means tell us what each observation "typically" looks like:
# If mean ≈ 0.5 → probably UZ or heading
# If mean ≈ 0 → probably angle or displacement
# If mean ≈ 1 → probably a constant or rarely-seen flag
# Two identical patterns = stacked observations
