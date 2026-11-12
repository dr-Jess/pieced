#!/usr/bin/env python3
"""Benchmark MediaPipe Pose Landmarker (lite / full / heavy, CPU / GPU) on your webcam.

Records one clip (hold still, then throw punches), then runs every model on the
*same* frames so they're compared fairly. Reports camera frame rate, inference
time, detection rate, wrist jitter at rest, tracking loss while punching, and
how far each model's wrists are from the heavy model's.

    .venv/bin/python benchmark_pose.py --list-cameras
    .venv/bin/python benchmark_pose.py --camera 0              # record + analyze
    .venv/bin/python benchmark_pose.py --analyze recordings/<run>   # re-analyze a clip

Not measured here: the camera's own capture latency (exposure + processing +
buffering). That needs an external reference; the phone IMU stream is the plan.
"""
import argparse
import csv
import json
import os
import time
from pathlib import Path

os.environ.setdefault("GLOG_minloglevel", "2")  # quiet MediaPipe's C++ logging
os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "2")

import cv2
import numpy as np
import mediapipe as mp
from mediapipe.tasks.python import BaseOptions, vision

HERE = Path(__file__).resolve().parent
MODELS = ["lite", "full", "heavy"]
DELEGATES = ["CPU", "GPU"]
L_SHOULDER, R_SHOULDER, L_ELBOW, R_ELBOW, L_WRIST, R_WRIST = 11, 12, 13, 14, 15, 16
VISIBLE = 0.5


# ---------------------------------------------------------------- recording

def list_cameras() -> None:
    for index in range(5):
        cap = cv2.VideoCapture(index, cv2.CAP_AVFOUNDATION)
        if cap.isOpened():
            ok, frame = cap.read()
            shape = f"{frame.shape[1]}x{frame.shape[0]}" if ok else "no frame"
            print(f"camera {index}: {shape}, reports {cap.get(cv2.CAP_PROP_FPS):.0f} fps")
        cap.release()


def record(args) -> Path:
    cap = cv2.VideoCapture(args.camera, cv2.CAP_AVFOUNDATION)
    if not cap.isOpened():
        raise SystemExit(f"Can't open camera {args.camera}. Grant camera access to your terminal in "
                         "System Settings > Privacy & Security > Camera, then try again.")
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, args.width)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, args.height)
    cap.set(cv2.CAP_PROP_FPS, args.fps)
    ok, frame = cap.read()
    if not ok:
        raise SystemExit("Camera opened but returned no frames.")
    h, w = frame.shape[:2]

    run = HERE / "recordings" / time.strftime("%Y%m%d-%H%M%S")
    run.mkdir(parents=True)
    writer = cv2.VideoWriter(str(run / "clip.avi"), cv2.VideoWriter_fourcc(*"MJPG"), args.fps, (w, h))
    phases = [("get ready", args.ready), ("HOLD STILL (guard up)", args.still), ("PUNCH!", args.punch)]
    frames = []
    print(f"Recording {w}x{h} from camera {args.camera}. Stand back so your upper body and arms are in frame.")

    for name, duration in phases:
        end = time.perf_counter() + duration
        while (now := time.perf_counter()) < end:
            ok, frame = cap.read()
            if not ok:
                continue
            t = time.perf_counter()
            if name != "get ready":
                writer.write(frame)
                frames.append({"t": t, "phase": "still" if name.startswith("HOLD") else "punch"})
            preview = cv2.flip(frame, 1)
            color = (0, 200, 255) if name == "get ready" else (0, 0, 255) if name == "PUNCH!" else (0, 220, 0)
            cv2.putText(preview, f"{name}  {end - now:4.1f}s", (30, 60), cv2.FONT_HERSHEY_SIMPLEX, 1.4, color, 3)
            cv2.imshow("Pieced pose benchmark (recording)", preview)
            if cv2.waitKey(1) & 0xFF == 27:
                raise SystemExit("Cancelled.")
    writer.release()
    exposure = cap.get(cv2.CAP_PROP_EXPOSURE)
    cap.release()
    cv2.destroyAllWindows()
    cv2.waitKey(1)

    times = np.array([f["t"] for f in frames])
    gaps = np.diff(times) * 1000
    camera = {"width": w, "height": h, "requested_fps": args.fps, "measured_fps": float(1000 / np.mean(gaps)),
              "frame_interval_ms_p50": float(np.median(gaps)), "frame_interval_ms_p95": float(np.percentile(gaps, 95)),
              "frame_interval_ms_max": float(gaps.max()),
              "exposure": exposure}
    (run / "frames.json").write_text(json.dumps({"camera": camera, "frames": frames}, indent=1))
    print(f"Saved {len(frames)} frames to {run}")
    return run


# ---------------------------------------------------------------- analysis

def detrended_std(values: np.ndarray, window: int = 9) -> float:
    """Frame-to-frame jitter: std after removing slow movement with a centered moving average."""
    if len(values) < window * 2:
        return float("nan")
    kernel = np.ones(window) / window
    smooth = np.apply_along_axis(lambda c: np.convolve(c, kernel, mode="same"), 0, values)
    residual = (values - smooth)[window:-window]
    return float(np.sqrt(np.mean(np.sum(residual ** 2, axis=1))))


def run_model(run: Path, model: str, delegate: str, frames: list):
    options = vision.PoseLandmarkerOptions(
        base_options=BaseOptions(model_asset_path=str(HERE / "models" / f"pose_landmarker_{model}.task"),
                                 delegate=getattr(BaseOptions.Delegate, delegate)),
        running_mode=vision.RunningMode.VIDEO, num_poses=1)
    landmarker = vision.PoseLandmarker.create_from_options(options)
    cap = cv2.VideoCapture(str(run / "clip.avi"))
    t0 = frames[0]["t"]
    rows, infer_ms = [], []
    for i, meta in enumerate(frames):
        ok, frame = cap.read()
        if not ok:
            break
        h, w = frame.shape[:2]
        if delegate == "GPU":  # the GPU path only accepts 4-channel images
            image = mp.Image(image_format=mp.ImageFormat.SRGBA, data=cv2.cvtColor(frame, cv2.COLOR_BGR2RGBA))
        else:
            image = mp.Image(image_format=mp.ImageFormat.SRGB, data=cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))
        start = time.perf_counter()
        result = landmarker.detect_for_video(image, int((meta["t"] - t0) * 1000))
        infer_ms.append((time.perf_counter() - start) * 1000)
        row = {"frame": i, "t": meta["t"] - t0, "phase": meta["phase"], "detected": bool(result.pose_landmarks)}
        if result.pose_landmarks:
            lm, world = result.pose_landmarks[0], result.pose_world_landmarks[0]
            for name, idx in [("ls", L_SHOULDER), ("rs", R_SHOULDER), ("le", L_ELBOW), ("re", R_ELBOW),
                              ("lw", L_WRIST), ("rw", R_WRIST)]:
                row.update({f"{name}_px": lm[idx].x * w, f"{name}_py": lm[idx].y * h, f"{name}_vis": lm[idx].visibility,
                            f"{name}_wx": world[idx].x, f"{name}_wy": world[idx].y, f"{name}_wz": world[idx].z})
        rows.append(row)
    cap.release()
    landmarker.close()

    with open(run / f"landmarks_{model}_{delegate.lower()}.csv", "w", newline="") as f:
        keys = sorted({k for r in rows for k in r}, key=lambda k: (k not in ("frame", "t", "phase", "detected"), k))
        writer = csv.DictWriter(f, fieldnames=keys)
        writer.writeheader()
        writer.writerows(rows)
    return rows, np.array(infer_ms[3:])  # skip warm-up frames


def summarize(rows, infer_ms, reference=None) -> dict:
    def phase(p):
        return [r for r in rows if r["phase"] == p]

    def wrists_visible(rs):
        return [r for r in rs if r["detected"] and r["lw_vis"] > VISIBLE and r["rw_vis"] > VISIBLE]

    still, punch = phase("still"), phase("punch")
    s = {"infer_p50": np.median(infer_ms), "infer_p95": np.percentile(infer_ms, 95),
         "detect": np.mean([r["detected"] for r in rows]) * 100,
         "wrists_still": len(wrists_visible(still)) / max(1, len(still)) * 100,
         "wrists_punch": len(wrists_visible(punch)) / max(1, len(punch)) * 100}

    vis = wrists_visible(still)
    if vis:
        shoulder = np.median([np.hypot(r["ls_px"] - r["rs_px"], r["ls_py"] - r["rs_py"]) for r in vis])
        jit_px = np.mean([detrended_std(np.array([[r[f"{s_}_px"], r[f"{s_}_py"]] for r in vis])) for s_ in ("lw", "rw")])
        jit_w = np.mean([detrended_std(np.array([[r[f"{s_}_wx"], r[f"{s_}_wy"], r[f"{s_}_wz"]] for r in vis])) for s_ in ("lw", "rw")])
        jit_z = np.mean([detrended_std(np.array([[r[f"{s_}_wz"]] for r in vis])) for s_ in ("lw", "rw")])
        s.update(shoulder_px=shoulder, jitter_px=jit_px, jitter_pct=jit_px / shoulder * 100,
                 jitter_world_mm=jit_w * 1000, jitter_depth_mm=jit_z * 1000)

    if reference is not None:
        ref = {r["frame"]: r for r in reference}
        d = [np.hypot(r[f"{w}_px"] - ref[r["frame"]][f"{w}_px"], r[f"{w}_py"] - ref[r["frame"]][f"{w}_py"])
             for r in wrists_visible(punch) if r["frame"] in ref and ref[r["frame"]]["detected"] for w in ("lw", "rw")]
        s["vs_heavy_px"] = float(np.median(d)) if d else float("nan")
    return s


def analyze(run: Path, delegates: list) -> None:
    meta = json.loads((run / "frames.json").read_text())
    frames, cam = meta["frames"], meta["camera"]
    print(f"\nCamera: {cam['width']}x{cam['height']}, requested {cam['requested_fps']} fps, measured "
          f"{cam['measured_fps']:.1f} fps (frame interval p50 {cam['frame_interval_ms_p50']:.1f} ms, "
          f"p95 {cam['frame_interval_ms_p95']:.1f} ms, max {cam['frame_interval_ms_max']:.1f} ms)")

    results = {}
    for delegate in delegates:
        for model in reversed(MODELS):  # heavy first: it's the reference
            print(f"  running {model} on {delegate}...", flush=True)
            try:
                results[(model, delegate)] = run_model(run, model, delegate, frames)
            except Exception as e:  # e.g. GPU delegate unavailable
                print(f"    failed: {e}")

    header = (f"{'model':6} {'dev':3} | {'infer p50':>9} {'p95':>6} {'max fps':>7} | {'person':>6} "
              f"{'wrists still':>12} {'punching':>8} | {'jitter px':>9} {'%shoulder':>9} {'world mm':>8} "
              f"{'depth mm':>8} | {'vs heavy':>8}")
    lines = [header, "-" * len(header)]
    for (model, delegate), (rows, infer) in sorted(results.items(), key=lambda kv: (kv[0][1], MODELS.index(kv[0][0]))):
        ref = results.get(("heavy", delegate), (None,))[0]
        s = summarize(rows, infer, ref if model != "heavy" else None)
        lines.append(
            f"{model:6} {delegate:3} | {s['infer_p50']:7.1f}ms {s['infer_p95']:4.1f}ms {1000 / s['infer_p50']:7.0f} | "
            f"{s['detect']:5.0f}% {s['wrists_still']:11.0f}% {s['wrists_punch']:7.0f}% | "
            f"{s.get('jitter_px', float('nan')):9.1f} {s.get('jitter_pct', float('nan')):8.1f}% "
            f"{s.get('jitter_world_mm', float('nan')):8.1f} {s.get('jitter_depth_mm', float('nan')):8.1f} | "
            + (f"{s['vs_heavy_px']:6.1f}px" if "vs_heavy_px" in s else "     ref"))
    notes = """
infer      time inside detect_for_video per frame (no camera latency); max fps = 1000 / p50
person     frames with a detected pose; wrists = both wrists visibility > 0.5 (still / punching phase)
jitter     wrist noise while holding still (slow sway removed): image px, % of shoulder width,
           world-landmark 3D mm, and the depth (toward camera) component alone
vs heavy   median wrist distance from the heavy model's wrist while punching (proxy for accuracy)"""
    table = "\n".join(lines) + "\n" + notes
    print("\n" + table)
    (run / "summary.txt").write_text(json.dumps(cam, indent=1) + "\n\n" + table + "\n")
    print(f"\nPer-frame landmarks: {run}/landmarks_<model>_<device>.csv")


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--list-cameras", action="store_true")
    p.add_argument("--camera", type=int, default=0)
    p.add_argument("--width", type=int, default=1280)
    p.add_argument("--height", type=int, default=720)
    p.add_argument("--fps", type=int, default=30)
    p.add_argument("--ready", type=float, default=4)
    p.add_argument("--still", type=float, default=6)
    p.add_argument("--punch", type=float, default=12)
    p.add_argument("--delegates", nargs="+", default=DELEGATES, choices=DELEGATES)
    p.add_argument("--analyze", type=Path, help="skip recording and analyze an existing recordings/<run> folder")
    args = p.parse_args()

    if args.list_cameras:
        list_cameras()
        return
    run = args.analyze or record(args)
    analyze(run, args.delegates)


if __name__ == "__main__":
    main()
