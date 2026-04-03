import cv2
import socket
import json
import time
import threading
from ultralytics import YOLO

# --- НАСТРОЙКИ ---
STREAM_URL = "http://192.168.1.1:8080/"
MODEL_NAME = "yolov8s.pt"  # Меняем Nano(n) на Small(s). На RTX 4060Ti будет летать, но точность в разы выше!
CONFIDENCE = 0.20  # Чуть снизили для более стабильного захвата
TARGET_CLASSES = [32, 49]  # 32='sports ball', 49='orange'. YOLO часто путает оранжевый пластиковый мяч с апельсином!


# --- UDP ---
UDP_IP = "127.0.0.1"
UDP_PORT = 5005

# Shared frame buffer (capture thread -> inference thread)
latest_frame = None
frame_lock = threading.Lock()

def capture_thread(cap):
    """Pulls frames into buffer as fast as possible, independent of inference."""
    global latest_frame
    while True:
        ret, frame = cap.read()
        if ret:
            with frame_lock:
                latest_frame = frame
        else:
            time.sleep(0.01)

def run_vision():
    print(f"Loading model {MODEL_NAME}...")
    model = YOLO(MODEL_NAME)
    print("Model loaded. Connecting to stream...")

    cap = cv2.VideoCapture(STREAM_URL)
    if not cap.isOpened():
        print(f"ERROR: Could not connect to stream: {STREAM_URL}")
        return

    # Start capture in separate thread
    t = threading.Thread(target=capture_thread, args=(cap,), daemon=True)
    t.start()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print(f"--- YOLO (UDP) STARTED -> {UDP_IP}:{UDP_PORT} ---")

    while True:
        with frame_lock:
            frame = latest_frame.copy() if latest_frame is not None else None

        if frame is None:
            time.sleep(0.01)
            continue

        start = time.time()
        results = model.predict(frame, classes=TARGET_CLASSES, conf=CONFIDENCE,
                                verbose=False)

        x_norm, y_norm, sees = 0.0, 1.0, 0.0

        if results and len(results[0].boxes) > 0:
            # Ищем самый крупный объект (ближайший мячик), если их несколько
            best_box = None
            max_area = 0
            for box in results[0].boxes:
                x1_, y1_, x2_, y2_ = box.xyxy[0].cpu().numpy()
                area = (x2_ - x1_) * (y2_ - y1_)
                if area > max_area:
                    max_area = area
                    best_box = box

            if best_box is not None:
                x1, y1, x2, y2 = best_box.xyxy[0].cpu().numpy()
                conf = float(best_box.conf[0])
                cls_id = int(best_box.cls[0])
                cls_name = "Ball" if cls_id == 32 else "Orange!" # Для отладки
                
                h, w = frame.shape[:2]
                center_x = (x1 + x2) / 2.0
                x_norm = (center_x - w / 2.0) / (w / 2.0)
                
                # РАЗМЕР МЯЧА: (высота рамки / высота кадра). 
                # Не зависит от наклона камеры! Чем больше мяч, тем он ближе.
                ball_h = y2 - y1
                y_norm = max(0.0, min(1.0, ball_h / h))
                
                sees = 1.0
                cv2.rectangle(frame, (int(x1), int(y1)), (int(x2), int(y2)), (0, 255, 0), 2)
                cv2.putText(frame, f"{cls_name} {conf:.2f}", (int(x1), int(y1) - 10),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 0), 2)

        data = {"angle": float(x_norm), "distance": float(y_norm), "sees": float(sees)}
        try:
            sock.sendto(json.dumps(data).encode(), (UDP_IP, UDP_PORT))
        except Exception:
            pass

        fps = 1.0 / (time.time() - start)
        cv2.putText(frame, f"FPS: {int(fps)}", (10, 30),
                    cv2.FONT_HERSHEY_SIMPLEX, 1, (0, 0, 255), 2)
        cv2.imshow("YOLO Vision (UDP)", frame)
        if cv2.waitKey(1) & 0xFF == ord('q'):
            break

    cap.release()
    cv2.destroyAllWindows()

if __name__ == '__main__':
    run_vision()
