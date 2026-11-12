#!/usr/bin/env python3
"""Render a benchmark clip with every model's arm landmarks drawn on it, side by side.

    .venv/bin/python render_overlay.py recordings/<run>              # half speed (default)
    .venv/bin/python render_overlay.py recordings/<run> --speed 1

Writes recordings/<run>/overlay.mp4: a 3x2 grid (lite/full/heavy x CPU/GPU) over the
same frame. Each panel draws shoulder-elbow-wrist for both arms:
  orange = your LEFT arm (appears on the image's right; the camera isn't mirrored)
  cyan   = your RIGHT arm
  filled wrist = model says visible (>0.5); hollow = low visibility
  thin tail = the wrist's last 6 frames, to show lag and jumps
  top-right inset = the same arms seen from ABOVE using MediaPipe's world landmarks (metres,
  origin between the hips): camera at the bottom, so a straight extends DOWN the inset.
  Grid lines every 20 cm; the numbers are each wrist's depth z (negative = toward camera).
In QuickTime, pause and use the arrow keys to step frame by frame.
"""
import argparse
import csv
import json
from pathlib import Path

import cv2
import numpy as np

RUNS = [("lite", "cpu"), ("full", "cpu"), ("heavy", "cpu"), ("lite", "gpu"), ("full", "gpu"), ("heavy", "gpu")]
LEFT, RIGHT = (0, 150, 255), (255, 220, 0)  # BGR: orange, cyan
TAIL = 6


def load(path: Path) -> dict:
    with open(path) as f:
        return {int(r["frame"]): r for r in csv.DictReader(f)}


def point(row, key, scale):
    return int(float(row[f"{key}_px"]) * scale), int(float(row[f"{key}_py"]) * scale)


INSET = 170          # px
INSET_RANGE_X = 0.6  # metres either side of the hip centre
INSET_Z = (-0.9, 0.3)  # metres; negative = toward the camera


def draw_top_down(panel, row, rows, history):
    """Bird's-eye view of world landmarks: x across, z up the inset (camera at the bottom)."""
    x0, y0 = panel.shape[1] - INSET - 6, 36
    roi = panel[y0:y0 + INSET, x0:x0 + INSET]
    roi[:] = (roi * 0.25).astype(np.uint8)

    def to_px(x, z):
        u = (x + INSET_RANGE_X) / (2 * INSET_RANGE_X) * INSET
        v = (z - INSET_Z[0]) / (INSET_Z[1] - INSET_Z[0]) * INSET  # far = top
        return int(x0 + u), int(y0 + INSET - v)

    for m in np.arange(-0.8, 0.41, 0.2):  # depth grid every 20 cm
        y = to_px(0, m)[1]
        if y0 <= y <= y0 + INSET:
            cv2.line(panel, (x0, y), (x0 + INSET, y), (70, 70, 70) if abs(m) > 1e-6 else (140, 140, 140), 1)
    cv2.putText(panel, "camera", (x0 + INSET // 2 - 28, y0 + INSET - 4), cv2.FONT_HERSHEY_SIMPLEX, 0.4, (200, 200, 200), 1)
    cv2.putText(panel, "top view", (x0 + 4, y0 + 13), cv2.FONT_HERSHEY_SIMPLEX, 0.4, (200, 200, 200), 1)

    def world(r, key):
        # World x is positive toward your left, which is the image's right, so the inset matches the image.
        return float(r[f"{key}_wx"]), float(r[f"{key}_wz"])

    ls, rs = to_px(*world(row, "ls")), to_px(*world(row, "rs"))
    cv2.line(panel, ls, rs, (200, 200, 200), 1, cv2.LINE_AA)
    for side, color in (("l", LEFT), ("r", RIGHT)):
        s, e, w = (to_px(*world(row, f"{side}{j}")) for j in "sew")
        cv2.line(panel, s, e, color, 2, cv2.LINE_AA)
        cv2.line(panel, e, w, color, 2, cv2.LINE_AA)
        tail = [to_px(*world(rows[i], f"{side}w")) for i in history if rows.get(i, {}).get("detected") == "True"]
        for a, b in zip(tail, tail[1:]):
            cv2.line(panel, a, b, color, 1, cv2.LINE_AA)
        cv2.circle(panel, w, 4, color, -1, cv2.LINE_AA)
    for k, (side, color) in enumerate((("l", LEFT), ("r", RIGHT))):
        z = float(row[f"{side}w_wz"])
        cv2.putText(panel, f"{side.upper()} z {z:+.2f}m", (x0 + INSET - 84, y0 + 13 + 14 * k),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.45, color, 1, cv2.LINE_AA)


def draw_panel(frame, rows, history, index, label, scale):
    panel = cv2.resize(frame, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    row = rows.get(index)
    if row is None or row["detected"] != "True":
        cv2.putText(panel, "no person", (10, panel.shape[0] // 2), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 0, 255), 2)
    else:
        for side, color, name in (("l", LEFT, "L"), ("r", RIGHT, "R")):
            s, e, w = point(row, f"{side}s", scale), point(row, f"{side}e", scale), point(row, f"{side}w", scale)
            cv2.line(panel, s, e, color, 2, cv2.LINE_AA)
            cv2.line(panel, e, w, color, 2, cv2.LINE_AA)
            tail = [point(rows[i], f"{side}w", scale) for i in history if rows.get(i, {}).get("detected") == "True"]
            for a, b in zip(tail, tail[1:]):
                cv2.line(panel, a, b, color, 1, cv2.LINE_AA)
            visible = float(row[f"{side}w_vis"]) > 0.5
            cv2.circle(panel, w, 7, color, -1 if visible else 2, cv2.LINE_AA)
            cv2.putText(panel, name, (w[0] + 9, w[1] - 6), cv2.FONT_HERSHEY_SIMPLEX, 0.6, color, 2, cv2.LINE_AA)
        draw_top_down(panel, row, rows, history)
    cv2.rectangle(panel, (0, 0), (panel.shape[1], 30), (0, 0, 0), -1)
    cv2.putText(panel, label, (8, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 255), 2, cv2.LINE_AA)
    return panel


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("run", type=Path)
    p.add_argument("--speed", type=float, default=0.5, help="playback speed (0.5 = half speed)")
    p.add_argument("--panel-width", type=int, default=640)
    args = p.parse_args()

    meta = json.loads((args.run / "frames.json").read_text())
    phases = [f["phase"] for f in meta["frames"]]
    runs = [(f"{m} {d.upper()}", load(args.run / f"landmarks_{m}_{d}.csv")) for m, d in RUNS
            if (args.run / f"landmarks_{m}_{d}.csv").exists()]
    cap = cv2.VideoCapture(str(args.run / "clip.avi"))
    scale = args.panel_width / meta["camera"]["width"]
    panel_h = int(meta["camera"]["height"] * scale)
    cols = 3
    rows_n = (len(runs) + cols - 1) // cols
    size = (args.panel_width * cols, panel_h * rows_n + 40)
    fps = meta["camera"]["measured_fps"] * args.speed
    out = args.run / "overlay.mp4"
    writer = cv2.VideoWriter(str(out), cv2.VideoWriter_fourcc(*"avc1"), fps, size)
    if not writer.isOpened():
        writer = cv2.VideoWriter(str(out), cv2.VideoWriter_fourcc(*"mp4v"), fps, size)

    index = 0
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        history = range(max(0, index - TAIL), index + 1)
        panels = [draw_panel(frame, rows, history, index, label, scale) for label, rows in runs]
        while len(panels) % cols:
            panels.append(np.zeros_like(panels[0]))
        grid = np.vstack([np.hstack(panels[r * cols:(r + 1) * cols]) for r in range(rows_n)])
        footer = np.zeros((40, size[0], 3), np.uint8)
        phase = phases[index] if index < len(phases) else "?"
        cv2.putText(footer, f"frame {index}   phase: {phase.upper()}   speed {args.speed:g}x   "
                    "orange = your LEFT arm, cyan = your RIGHT arm, hollow wrist = low visibility",
                    (10, 27), cv2.FONT_HERSHEY_SIMPLEX, 0.65, (255, 255, 255), 1, cv2.LINE_AA)
        writer.write(np.vstack([grid, footer]))
        index += 1
    writer.release()
    print(f"Wrote {index} frames to {out}")


if __name__ == "__main__":
    main()
