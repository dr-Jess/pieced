#!/usr/bin/env python3
"""Record a synchronized phone + camera session for building the fusion filter.

Run from your own Terminal window (macOS only grants camera access there), with Unity's
Play mode stopped (this script takes over port 7777), then connect the phone app to the
IP shown in the preview window:

    cd tools/cv
    .venv/bin/python record_session.py --camera 0 [--targets targets.json]

Hold the phone gripped in your RIGHT fist. The preview walks you through:
    ready      get in position, guard up
    sync_start 5 sharp right jabs, about 1 s apart (aligns the phone and camera clocks)
    guard      hold your guard still
    calib_slide  slide your right fist left and right, PARALLEL to the camera, ~1 sweep/s
                 (defines the camera's sideways axis in the phone's frame; fist size shouldn't change)
    calib_push   push your right fist straight AT THE LENS and back, ~1/s
                 (the camera's blind direction: checks how much image motion it still causes)
    side_punch   turned 90 deg to your left (right shoulder toward the camera), throw right straights:
                 they now cross the camera's view, so the camera can check the phone's punch distance
    touch_<n>  touch each tape target with your right fist and hold (only with --targets)
    free       punch freely, mixing straights, hooks and uppercuts with both hands
    sync_end   5 sharp right jabs again (measures clock drift over the session)

Output, in recordings/session-<timestamp>/:
    clip.avi      camera video
    frames.json   per-frame laptop timestamp and step
    phone.jsonl   every phone message, prefixed with its laptop arrival time
    session.json  step timeline, targets, phone hello, settings
    report.txt    camera rate, phone rate and drops, network jitter
    landmarks_<model>_<device>.csv   MediaPipe results (heavy GPU + full CPU by default)

All timestamps are time.perf_counter() seconds on the laptop, so both streams share one clock.
"""
import argparse
import json
import socket
import subprocess
import threading
import time
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent


# ---------------------------------------------------------------- phone server

class PhoneServer(threading.Thread):
    """Accepts the phone app's TCP stream and logs each line with its laptop arrival time."""

    def __init__(self, port: int):
        super().__init__(daemon=True)
        self.port = port
        self.running = True
        self.log = None            # file handle; lines are only written while recording
        self.lock = threading.Lock()
        self.hello = None
        self.connected = False
        self.samples = 0
        self.latest_accel = 0.0
        self.rate = 0.0
        self._rate_window = (time.perf_counter(), 0)
        self.server = socket.create_server(("", port), reuse_port=False)
        self.server.settimeout(0.5)

    def run(self):
        while self.running:
            try:
                client, address = self.server.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            threading.Thread(target=self._read, args=(client,), daemon=True).start()

    def _read(self, client):
        self.connected = True
        try:
            for raw in client.makefile("rb"):
                t = time.perf_counter()
                line = raw.decode("utf-8", "replace").strip()
                if not line:
                    continue
                if line.startswith(("GET ", "HEAD ")):  # a browser checking reachability
                    client.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: 22\r\n\r\nPieced recorder is up\n")
                    return
                try:
                    msg = json.loads(line)
                except json.JSONDecodeError:
                    continue
                with self.lock:
                    if self.log is not None:
                        self.log.write(f"{t:.6f}\t{line}\n")
                if msg.get("type") == "hello":
                    self.hello = msg
                elif msg.get("type") == "motion":
                    self.samples += 1
                    self.latest_accel = float(np.linalg.norm([msg.get("ax", 0), msg.get("ay", 0), msg.get("az", 0)]))
                    start, count = self._rate_window
                    if t - start >= 1.0:
                        self.rate = (count + 1) / (t - start)
                        self._rate_window = (t, 0)
                    else:
                        self._rate_window = (start, count + 1)
        except OSError:
            pass
        finally:
            self.connected = False
            self.rate = 0.0
            client.close()

    def start_logging(self, path: Path):
        with self.lock:
            self.log = open(path, "w")

    def stop(self):
        self.running = False
        with self.lock:
            if self.log:
                self.log.close()
                self.log = None
        self.server.close()


def awdl_active() -> bool:
    """AWDL (AirDrop/Continuity radio) causes ~100 ms Wi-Fi stalls twice a second."""
    try:
        out = subprocess.run(["ifconfig", "awdl0"], capture_output=True, text=True, timeout=2).stdout
        return "status: active" in out or ("<UP" in out and "RUNNING" in out)
    except (OSError, subprocess.TimeoutExpired):
        return False


def lan_ip() -> str:
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(("8.8.8.8", 53))  # no packets sent; just picks the outgoing interface
            return s.getsockname()[0]
    except OSError:
        return "<laptop IP>"


# ---------------------------------------------------------------- session script

def build_steps(args, targets):
    steps = [("ready", args.ready, "Get in position, guard up"),
             ("sync_start", 8, "SYNC: 5 sharp RIGHT jabs, ~1 s apart"),
             ("guard", 5, "Hold your guard STILL")]
    if not args.no_calibration:
        steps += [("calib_slide", 8, "Slide RIGHT fist left-right PARALLEL to camera, ~1 sweep/s"),
                  ("guard", 2.5, "Back to guard"),
                  ("calib_push", 8, "Push RIGHT fist straight AT THE LENS and back, ~1/s"),
                  ("guard", 2.5, "Back to guard")]
    if not args.no_side:
        steps += [("turn", 3, "Turn to YOUR LEFT 90 deg: right shoulder toward the camera"),
                  ("side_punch", 10, "Side-on: throw RIGHT straights across the camera's view"),
                  ("turn", 3, "Turn back to face the camera, guard up"),
                  ("guard", 2.5, "Hold guard")]
    for target in targets:
        steps.append((f"touch_{target['name']}", 4, f"Touch target {target['name']} with RIGHT fist and HOLD"))
        steps.append(("guard", 2.5, "Back to guard"))
    steps += [("free", args.free, "Punch freely: straights, hooks, uppercuts, both hands"),
              ("guard", 3, "Guard, catch your breath"),
              ("sync_end", 8, "SYNC: 5 sharp RIGHT jabs, ~1 s apart")]
    return steps


def overlay(frame, lines, accel, phone_ok):
    view = cv2.flip(frame, 1)  # mirror, so it behaves like a mirror
    cv2.rectangle(view, (0, 0), (view.shape[1], 40 + 38 * len(lines)), (0, 0, 0), -1)
    for i, (text, color, scale) in enumerate(lines):
        cv2.putText(view, text, (20, 40 + 38 * i), cv2.FONT_HERSHEY_SIMPLEX, scale, color, 2, cv2.LINE_AA)
    # phone acceleration meter (0-4 g): jabs should spike it
    h = view.shape[0]
    x0, y0, width = 20, h - 40, 300
    cv2.rectangle(view, (x0, y0), (x0 + width, y0 + 20), (60, 60, 60), -1)
    fill = int(min(accel / 4.0, 1.0) * width)
    cv2.rectangle(view, (x0, y0), (x0 + fill, y0 + 20), (0, 200, 0) if phone_ok else (0, 0, 200), -1)
    cv2.putText(view, f"phone |accel| {accel:.1f} g", (x0 + width + 10, y0 + 17), cv2.FONT_HERSHEY_SIMPLEX, 0.6,
                (255, 255, 255), 1, cv2.LINE_AA)
    return view


def record(args) -> Path:
    targets = json.loads(Path(args.targets).read_text())["targets"] if args.targets else []
    try:
        phone = PhoneServer(args.port)
    except OSError as e:
        raise SystemExit(f"Can't listen on port {args.port} ({e}). Stop Unity's Play mode (it uses the same port).")
    phone.start()

    cap = cv2.VideoCapture(args.camera, cv2.CAP_AVFOUNDATION)
    if not cap.isOpened():
        raise SystemExit("Can't open the camera. Run this from your own Terminal window, with camera access granted.")
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1280)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
    cap.set(cv2.CAP_PROP_FPS, 30)
    window = "Pieced session recorder"
    ip = lan_ip()
    awdl_at_start = awdl_active()
    if awdl_at_start:
        print("WARNING: AWDL is on; expect ~100 ms Wi-Fi stalls. Fix: sudo ifconfig awdl0 down "
              "(and turn off AirDrop/Handoff so macOS doesn't turn it back on).")

    # Wait until the phone is streaming (or the user presses S to record camera-only).
    while True:
        ok, frame = cap.read()
        if not ok:
            continue
        phone_ok = phone.connected and phone.rate > 50
        lines = [(f"Connect the phone app to {ip} : {args.port}", (0, 220, 255), 1.0),
                 (f"phone: {'streaming %.0f Hz' % phone.rate if phone.connected else 'not connected'}",
                  (0, 220, 0) if phone_ok else (0, 0, 255), 0.9),
                 ("Phone in RIGHT fist. SPACE = start, S = start without phone, ESC = quit", (255, 255, 255), 0.8)]
        if awdl_at_start:
            lines.append(("AWDL is ON: Wi-Fi will stall. Run: sudo ifconfig awdl0 down", (0, 0, 255), 0.8))
        cv2.imshow(window, overlay(frame, lines, phone.latest_accel, phone_ok))
        key = cv2.waitKey(1) & 0xFF
        if key == 27:
            raise SystemExit("Cancelled.")
        if (key == 32 and phone_ok) or key in (ord("s"), ord("S")):
            break

    run = HERE / "recordings" / time.strftime("session-%Y%m%d-%H%M%S")
    run.mkdir(parents=True)
    writer = None
    frames, timeline = [], []
    phone.start_logging(run / "phone.jsonl")
    steps = build_steps(args, targets)

    for name, duration, prompt in steps:
        start = time.perf_counter()
        end = start + duration
        timeline.append({"step": name, "start": start, "end": end, "prompt": prompt})
        while (now := time.perf_counter()) < end:
            ok, frame = cap.read()
            if not ok:
                continue
            t = time.perf_counter()
            if writer is None:
                h, w = frame.shape[:2]
                writer = cv2.VideoWriter(str(run / "clip.avi"), cv2.VideoWriter_fourcc(*"MJPG"), 30, (w, h))
            writer.write(frame)
            frames.append({"t": t, "phase": name})
            color = (0, 0, 255) if name.startswith(("sync", "free")) else (0, 220, 0) if name == "guard" else (0, 220, 255)
            phone_ok = phone.connected and phone.rate > 50
            lines = [(f"{prompt}", color, 1.0),
                     (f"{end - now:4.1f} s   step {len(timeline)}/{len(steps)}   phone "
                      f"{'%.0f Hz' % phone.rate if phone.connected else 'DISCONNECTED'}",
                      (255, 255, 255) if phone_ok else (0, 0, 255), 0.8)]
            cv2.imshow(window, overlay(frame, lines, phone.latest_accel, phone_ok))
            if cv2.waitKey(1) & 0xFF == 27:
                print("Stopped early; keeping what was recorded.")
                break
        else:
            continue
        break

    phone.stop()
    if writer:
        writer.release()
    cap.release()
    cv2.destroyAllWindows()
    cv2.waitKey(1)

    gaps = np.diff([f["t"] for f in frames]) * 1000
    camera = {"width": 1280, "height": 720, "requested_fps": 30, "measured_fps": float(1000 / np.mean(gaps)),
              "frame_interval_ms_p50": float(np.median(gaps)), "frame_interval_ms_p95": float(np.percentile(gaps, 95)),
              "frame_interval_ms_max": float(gaps.max())}
    (run / "frames.json").write_text(json.dumps({"camera": camera, "frames": frames}, indent=1))
    (run / "session.json").write_text(json.dumps({
        "hand": "right", "camera_index": args.camera, "timeline": timeline, "targets": targets,
        "targets_file": args.targets, "phone_hello": phone.hello, "clock": "time.perf_counter() seconds",
        "awdl_active_at_start": awdl_at_start, "awdl_active_at_end": awdl_active()}, indent=1))
    print(f"Saved {len(frames)} frames and {phone.samples} phone samples to {run}")
    return run


# ---------------------------------------------------------------- report + landmarks

def report(run: Path) -> str:
    meta = json.loads((run / "frames.json").read_text())
    cam = meta["camera"]
    lines = [f"Camera: {cam['measured_fps']:.1f} fps, frame interval p50 {cam['frame_interval_ms_p50']:.1f} ms, "
             f"p95 {cam['frame_interval_ms_p95']:.1f} ms, max {cam['frame_interval_ms_max']:.1f} ms"]
    recv, dev, seq = [], [], []
    for line in open(run / "phone.jsonl"):
        t, payload = line.split("\t", 1)
        msg = json.loads(payload)
        if msg.get("type") == "motion":
            recv.append(float(t)); dev.append(msg["t"]); seq.append(msg["seq"])
    if len(recv) < 10:
        lines.append("Phone: no motion data recorded.")
    else:
        recv, dev, seq = map(np.array, (recv, dev, seq))
        span = recv[-1] - recv[0]
        dropped = int(np.sum(np.diff(seq) - 1))
        # Arrival time minus send time, up to an unknown constant clock offset. Its spread is the
        # network + app jitter; the fastest packet sets the baseline.
        delay = (recv - dev) * 1000
        extra = delay - delay.min()
        drift = np.polyfit(dev - dev[0], delay, 1)[0]  # delay is in ms, x in s: ms of drift per second
        lines += [f"Phone: {len(recv)} samples over {span:.1f} s = {len(recv) / span:.1f} Hz, "
                  f"{dropped} dropped by sequence number",
                  f"Network/app jitter (arrival delay above the fastest sample): p50 {np.percentile(extra, 50):.1f} ms, "
                  f"p95 {np.percentile(extra, 95):.1f} ms, p99 {np.percentile(extra, 99):.1f} ms, max {extra.max():.1f} ms",
                  f"Phone vs laptop clock drift: {drift:.3f} ms per second (from a line fit; jitter makes this rough)"]
    session = json.loads((run / "session.json").read_text())
    if "awdl_active_at_start" in session:
        lines.append(f"AWDL (AirDrop radio, causes Wi-Fi stalls): {'ON' if session['awdl_active_at_start'] else 'off'} "
                     f"at start, {'ON' if session['awdl_active_at_end'] else 'off'} at end")
    text = "\n".join(lines)
    (run / "report.txt").write_text(text + "\n")
    return text


def run_landmarks(run: Path, specs: list) -> None:
    import sys
    sys.path.insert(0, str(HERE))
    from benchmark_pose import run_model  # reuse the benchmark's MediaPipe runner
    frames = json.loads((run / "frames.json").read_text())["frames"]
    for spec in specs:
        model, delegate = spec.split(":")
        print(f"  MediaPipe {model} on {delegate}...", flush=True)
        try:
            run_model(run, model, delegate.upper(), frames)
        except Exception as e:
            print(f"    failed: {e}")


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--camera", type=int, default=0)
    p.add_argument("--port", type=int, default=7777)
    p.add_argument("--targets", help="JSON file of tape targets (see targets.example.json)")
    p.add_argument("--ready", type=float, default=4)
    p.add_argument("--free", type=float, default=15, help="seconds of free punching")
    p.add_argument("--no-calibration", action="store_true", help="skip the slide/push calibration moves")
    p.add_argument("--no-side", action="store_true", help="skip the side-on punching step")
    p.add_argument("--models", nargs="+", default=["heavy:GPU", "full:CPU"])
    p.add_argument("--skip-landmarks", action="store_true")
    p.add_argument("--reprocess", type=Path, help="redo the report and landmarks for an existing session folder")
    args = p.parse_args()

    run = args.reprocess or record(args)
    print(report(run))
    if not args.skip_landmarks:
        run_landmarks(run, args.models)
    print(f"\nDone: {run}")


if __name__ == "__main__":
    main()
