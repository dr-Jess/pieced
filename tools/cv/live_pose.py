#!/usr/bin/env python3
"""Stream live MediaPipe arm landmarks from the webcam to the game (role "cv").

Run from your own Terminal window (camera permission), with the game in Play mode:

    cd tools/cv
    .venv/bin/python live_pose.py --camera 0 [--host 127.0.0.1] [--model heavy --delegate GPU] [--no-preview]

Each frame sends one line:
    {"type":"pose","seq":n,"t":<capture time, s>,"model":"heavy_gpu",
     "world":[18 floats],"image":[12 floats],"vis":[6 floats]}
Landmark order: left shoulder, right shoulder, left elbow, right elbow, left wrist, right wrist
(MediaPipe pose 11-16). "world" is MediaPipe's world landmarks in metres (hips origin, +x = your
left, +y = down, -z = toward the camera); "image" is normalized x, y. Frames with no person send
nothing. Stand in guard when it starts: the game takes the first pose as your guard (C recalibrates).
"""
import argparse
import json
import os
import socket
import time

os.environ.setdefault("GLOG_minloglevel", "2")
os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "2")

import cv2
import mediapipe as mp
import numpy as np
from mediapipe.tasks.python import BaseOptions, vision
from pathlib import Path

HERE = Path(__file__).resolve().parent
ARM = [11, 12, 13, 14, 15, 16]


def connect(host, port):
    while True:
        try:
            sock = socket.create_connection((host, port), timeout=2)
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            sock.sendall((json.dumps({"type": "hello", "role": "cv", "name": "live_pose.py", "protocol": 1}) + "\n").encode())
            print(f"connected to the game at {host}:{port}")
            return sock
        except OSError:
            print(f"waiting for the game at {host}:{port} (is it in Play mode?)...")
            time.sleep(1)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--camera", type=int, default=0)
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=7777)
    p.add_argument("--model", default="heavy", choices=["lite", "full", "heavy"])
    p.add_argument("--delegate", default="GPU", choices=["CPU", "GPU"])
    p.add_argument("--no-preview", action="store_true")
    args = p.parse_args()

    landmarker = vision.PoseLandmarker.create_from_options(vision.PoseLandmarkerOptions(
        base_options=BaseOptions(model_asset_path=str(HERE / "models" / f"pose_landmarker_{args.model}.task"),
                                 delegate=getattr(BaseOptions.Delegate, args.delegate)),
        running_mode=vision.RunningMode.VIDEO, num_poses=1))
    cap = cv2.VideoCapture(args.camera, cv2.CAP_AVFOUNDATION)
    if not cap.isOpened():
        raise SystemExit("Can't open the camera. Run this from your own Terminal window, with camera access granted.")
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1280)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
    cap.set(cv2.CAP_PROP_FPS, 30)

    sock = connect(args.host, args.port)
    model_name = f"{args.model}_{args.delegate.lower()}"
    seq, start = 0, time.perf_counter()
    fps, infer_ms, last_report = 0.0, 0.0, time.perf_counter()
    while True:
        ok, frame = cap.read()
        if not ok:
            continue
        t = time.perf_counter()
        fmt, conv = (mp.ImageFormat.SRGBA, cv2.COLOR_BGR2RGBA) if args.delegate == "GPU" else (mp.ImageFormat.SRGB, cv2.COLOR_BGR2RGB)
        image = mp.Image(image_format=fmt, data=cv2.cvtColor(frame, conv))
        t0 = time.perf_counter()
        result = landmarker.detect_for_video(image, int((t - start) * 1000))
        infer_ms = 0.9 * infer_ms + 0.1 * (time.perf_counter() - t0) * 1000
        fps = 0.9 * fps + 0.1 / max(t - last_report, 1e-3)
        last_report = t

        if result.pose_landmarks:
            lm, world = result.pose_landmarks[0], result.pose_world_landmarks[0]
            seq += 1
            msg = {"type": "pose", "seq": seq, "t": round(t, 4), "model": model_name,
                   "world": [round(v, 4) for i in ARM for v in (world[i].x, world[i].y, world[i].z)],
                   "image": [round(v, 4) for i in ARM for v in (lm[i].x, lm[i].y)],
                   "vis": [round(lm[i].visibility, 3) for i in ARM]}
            try:
                sock.sendall((json.dumps(msg, separators=(",", ":")) + "\n").encode())
            except OSError:
                print("lost the game connection")
                sock.close()
                sock = connect(args.host, args.port)

        if not args.no_preview:
            view = frame.copy()
            if result.pose_landmarks:
                h, w = view.shape[:2]
                lm = result.pose_landmarks[0]
                for side, color in ((0, (0, 150, 255)), (1, (255, 220, 0))):  # left orange, right cyan
                    pts = [(int(lm[ARM[j]].x * w), int(lm[ARM[j]].y * h)) for j in (side, side + 2, side + 4)]
                    cv2.polylines(view, [np.array(pts).reshape(-1, 1, 2)], False, color, 4)
            view = cv2.flip(view, 1)
            cv2.putText(view, f"{fps:4.1f} fps   inference {infer_ms:4.1f} ms   {model_name}   sent {seq}   ESC = quit",
                        (20, 40), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (0, 255, 0), 2, cv2.LINE_AA)
            cv2.imshow("Pieced live pose", view)
            if cv2.waitKey(1) & 0xFF == 27:
                break
    cap.release()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
