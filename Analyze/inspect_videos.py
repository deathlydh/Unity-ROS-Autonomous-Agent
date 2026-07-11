import cv2
import os

def inspect_video(video_path, output_prefix):
    print(f"\n========================================\nINSPECTING VIDEO: {video_path}\n========================================")
    if not os.path.exists(video_path):
        print(f"Error: {video_path} does not exist!")
        return
        
    cap = cv2.VideoCapture(video_path)
    fps = cap.get(cv2.CAP_PROP_FPS)
    frame_count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    duration = frame_count / fps if fps > 0 else 0
    
    print(f"Resolution: {width}x{height}")
    print(f"FPS: {fps:.2f}")
    print(f"Total Frames: {frame_count}")
    print(f"Duration: {duration:.2f} seconds")
    
    # Save key frames: start, 25%, 50%, 75%, and end
    indices = [0, int(frame_count * 0.25), int(frame_count * 0.50), int(frame_count * 0.75), frame_count - 1]
    
    brain_dir = r"C:\Users\Admin\.gemini\antigravity\brain\9813beb3-7816-40c2-ae0a-72b54e217084"
    temp_dir = os.path.join(brain_dir, ".tempmediaStorage")
    if not os.path.exists(temp_dir):
        os.makedirs(temp_dir)
        
    for i, idx in enumerate(indices):
        cap.set(cv2.CAP_PROP_POS_FRAMES, idx)
        ret, frame = cap.read()
        if ret:
            out_name = f"{output_prefix}_frame_{idx}_{int(idx/fps*1000)}ms.jpg"
            out_path = os.path.join(temp_dir, out_name)
            cv2.imwrite(out_path, frame)
            print(f"Saved keyframe {i}: {out_path} (timestamp: {idx/fps:.2f}s)")
            
    cap.release()

inspect_video(r"c:\Users\Admin\Unity\ROS_test\video_runs\yolo_run_20260702_181408.mp4", "run_181408")
inspect_video(r"c:\Users\Admin\Unity\ROS_test\video_runs\yolo_run_20260702_183715.mp4", "run_183715")
