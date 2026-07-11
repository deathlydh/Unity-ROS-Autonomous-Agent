import cv2
import numpy as np
import os

def track_ball_in_video(video_path, output_dir, run_name):
    print(f"\nTracking ball in: {video_path}")
    cap = cv2.VideoCapture(video_path)
    fps = cap.get(cv2.CAP_PROP_FPS)
    frame_count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    
    # We will detect the green bounding boxes drawn by YOLO
    # Green in BGR is (0, 255, 0). We can threshold the image for high green content.
    
    last_w = 0
    moving_frame = -1
    disappeared_frame = -1
    
    frames_with_detections = []
    
    for f_idx in range(frame_count):
        ret, frame = cap.read()
        if not ret:
            break
            
        # Look for green bounding boxes
        # OpenCV BGR: green is around H=60 in HSV.
        # Alternatively, find pixels where G > 200 and R < 50 and B < 50
        green_mask = (frame[:, :, 1] > 200) & (frame[:, :, 2] < 50) & (frame[:, :, 0] < 50)
        green_pixels = np.argwhere(green_mask)
        
        if len(green_pixels) > 20: # Bounding box present
            y_coords = green_pixels[:, 0]
            x_coords = green_pixels[:, 1]
            ymin, ymax = y_coords.min(), y_coords.max()
            xmin, xmax = x_coords.min(), x_coords.max()
            w = xmax - xmin
            h = ymax - ymin
            
            # Filter out the top-left FPS text if it gets caught (unlikely with our color check, but just in case)
            if xmin < 100 and ymin < 50:
                continue
                
            frames_with_detections.append((f_idx, w, h, (xmin+xmax)/2, (ymin+ymax)/2))
            
            if last_w > 0 and w - last_w > 3 and moving_frame == -1:
                # Bounding box grew significantly -> robot started moving!
                moving_frame = f_idx
                
            last_w = w
        else:
            if last_w > 0 and disappeared_frame == -1 and f_idx > 50:
                # Ball was seen but now disappeared!
                disappeared_frame = f_idx
                
    print(f"Total frames processed: {frame_count}")
    print(f"Frames with ball detected: {len(frames_with_detections)}")
    
    if len(frames_with_detections) > 0:
        first_det = frames_with_detections[0][0]
        last_det = frames_with_detections[-1][0]
        print(f"First detection: frame {first_det} ({first_det/fps:.2f}s)")
        print(f"Last detection: frame {last_det} ({last_det/fps:.2f}s)")
        
        # Save first detection frame
        cap.set(cv2.CAP_PROP_POS_FRAMES, first_det)
        _, img = cap.read()
        cv2.imwrite(os.path.join(output_dir, f"{run_name}_first_det.jpg"), img)
        
        # Save middle detection frame
        mid_idx = frames_with_detections[len(frames_with_detections)//2][0]
        cap.set(cv2.CAP_PROP_POS_FRAMES, mid_idx)
        _, img = cap.read()
        cv2.imwrite(os.path.join(output_dir, f"{run_name}_mid_det.jpg"), img)
        
        # Save last detection frame
        cap.set(cv2.CAP_PROP_POS_FRAMES, last_det)
        _, img = cap.read()
        cv2.imwrite(os.path.join(output_dir, f"{run_name}_last_det.jpg"), img)
        
        # Save 10 frames after last detection to see the claw action
        claw_idx = min(last_det + 15, frame_count - 1)
        cap.set(cv2.CAP_PROP_POS_FRAMES, claw_idx)
        _, img = cap.read()
        cv2.imwrite(os.path.join(output_dir, f"{run_name}_claw_action.jpg"), img)
        print(f"Saved claw action frame: frame {claw_idx} ({(claw_idx)/fps:.2f}s)")

    cap.release()

brain_dir = r"C:\Users\Admin\.gemini\antigravity\brain\9813beb3-7816-40c2-ae0a-72b54e217084"
temp_dir = os.path.join(brain_dir, ".tempmediaStorage")

track_ball_in_video(r"c:\Users\Admin\Unity\ROS_test\video_runs\yolo_run_20260702_181408.mp4", temp_dir, "run_181408")
track_ball_in_video(r"c:\Users\Admin\Unity\ROS_test\video_runs\yolo_run_20260702_183715.mp4", temp_dir, "run_183715")
