#!/usr/bin/env python3
"""Render the fusion filter's output over a session: camera view + overhead view + graphs.

    .venv/bin/python render_fusion.py recordings/session-<timestamp> [--lag 80] [--speed 0.5]

Camera (top left): cyan = right arm as MediaPipe sees it, orange = left arm. Yellow dot = the
  filter's fused right fist (live: only data that had arrived by then), drawn at its sideways/up
  position; it grows as the fist comes toward the camera.
Overhead (top right): looking down, camera at the bottom, so punching toward the camera moves DOWN.
  Yellow = fused fist + its last second of trail. Dashed purple = the camera line: everywhere the
  fist could be given what the camera sees: a ray from the lens through the wrist's pixel (it fixes
  direction, not distance; bent slightly by the fitted leak). Grey = guard spot, reach limits, and a torso outline for scale.
Graphs (bottom): sideways and up, camera vs filter; depth, filter only. 3 s history, 1 s ahead.
Writes recordings/<session>/fusion_review.mp4.
"""
import argparse
import sys
from dataclasses import replace
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from fuse_session import Params, fit_leak, go, load  # noqa: E402
from fusion import project  # noqa: E402
from render_session import SHOULDER_M, draw_graph, load_camera  # noqa: E402

W, H = 1920, 1080
CAM_W, CAM_H = 1152, 648
TOP_X, TOP_W = CAM_W, W - CAM_W
TOP_SCALE = 520            # px per metre in the overhead view
TOP_ORIGIN = (TOP_X + TOP_W // 2, 200)  # guard spot; depth toward the camera goes down the screen
YELLOW, CYAN, ORANGE, PURPLE, GREY = (0, 230, 255), (255, 220, 0), (0, 150, 255), (255, 110, 230), (150, 150, 150)


def top(side, depth):
    """Overhead pixel: sideways + = your right, which is the image's LEFT (matches the camera view)."""
    return int(TOP_ORIGIN[0] - side * TOP_SCALE), int(TOP_ORIGIN[1] + depth * TOP_SCALE)


def camera_ray(prm, u_px, depth):
    """Sideways position (m) at a given depth that projects to image column u_px."""
    Z = np.maximum(prm.guard_z - depth, 0.3)
    Xg = (prm.guard_px[0] - prm.cx) / prm.fx * prm.guard_z
    return Xg - (u_px - prm.cx) * Z / prm.fx - prm.leak[0] * depth


def draw_overhead(img, now_idx, live_at, cam_u, shoulder_side, prm):
    cv2.rectangle(img, (TOP_X, 0), (W, CAM_H), (25, 25, 25), -1)
    for d in np.arange(-0.4, 0.9, 0.1):  # depth grid every 10 cm
        y = top(0, d)[1]
        if 0 < y < CAM_H:
            cv2.line(img, (TOP_X, y), (W, y), (60, 60, 60) if abs(d) > 1e-6 else (110, 110, 110), 1)
    for s in np.arange(-0.6, 0.61, 0.1):
        x = top(s, 0)[0]
        cv2.line(img, (x, 0), (x, CAM_H), (45, 45, 45), 1)
    # torso (shoulders ~41 cm wide, ~30 cm behind the guard spot) and reach limits
    cv2.ellipse(img, top(shoulder_side, -0.32), (int(SHOULDER_M / 2 * TOP_SCALE), int(0.10 * TOP_SCALE)), 0, 0, 360, GREY, 2)
    cv2.putText(img, "you", (top(shoulder_side, -0.32)[0] - 18, top(shoulder_side, -0.32)[1] + 6),
                cv2.FONT_HERSHEY_SIMPLEX, 0.6, GREY, 1, cv2.LINE_AA)
    for d, label in ((prm.reach, "reach limit"), (-prm.behind, "behind-guard limit")):
        y = top(0, d)[1]
        cv2.line(img, (TOP_X + 10, y), (W - 10, y), (90, 90, 160), 1)
        cv2.putText(img, label, (TOP_X + 14, y - 6), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (120, 120, 200), 1, cv2.LINE_AA)
    cv2.drawMarker(img, TOP_ORIGIN, GREY, cv2.MARKER_CROSS, 18, 2)
    cv2.putText(img, "guard", (TOP_ORIGIN[0] + 12, TOP_ORIGIN[1] - 8), cv2.FONT_HERSHEY_SIMPLEX, 0.5, GREY, 1, cv2.LINE_AA)
    cv2.putText(img, "camera  v", (TOP_X + TOP_W // 2 - 45, CAM_H - 12), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (220, 220, 220), 1, cv2.LINE_AA)
    cv2.putText(img, "OVERHEAD (camera at the bottom)", (TOP_X + 12, 24), cv2.FONT_HERSHEY_SIMPLEX, 0.65, (230, 230, 230), 1, cv2.LINE_AA)
    # camera ray: every (side, depth) that projects onto the wrist's pixel column
    if np.isfinite(cam_u):
        pts = [top(camera_ray(prm, cam_u, d), d) for d in np.linspace(-0.4, 0.9, 27)]
        for a, b in zip(pts[::2], pts[1::2]):
            cv2.line(img, a, b, PURPLE, 2, cv2.LINE_AA)
    trail = live_at[max(0, now_idx - 30):now_idx + 1]
    for a, b in zip(trail[:-1], trail[1:]):
        cv2.line(img, top(a[0], a[2]), top(b[0], b[2]), (0, 150, 170), 2, cv2.LINE_AA)
    p = live_at[now_idx]
    cv2.circle(img, top(p[0], p[2]), 11, YELLOW, -1, cv2.LINE_AA)
    cv2.putText(img, f"fused fist: side {p[0] * 100:+.0f}  up {p[1] * 100:+.0f}  toward camera {p[2] * 100:+.0f} cm",
                (TOP_X + 12, 52), cv2.FONT_HERSHEY_SIMPLEX, 0.6, YELLOW, 1, cv2.LINE_AA)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("run", type=Path)
    ap.add_argument("--lag", type=float, default=80)
    ap.add_argument("--speed", type=float, default=0.5)
    args = ap.parse_args()

    D, prm = load(args.run, args.lag / 1000, Params())
    prm = replace(prm, leak=fit_leak(D, prm))
    cm_per_px = prm.guard_z / prm.fx * 100
    live, _, _ = go(D, prm)
    cam, _ = load_camera(args.run, "heavy_gpu")
    rows = cam["rows"]
    tc = D["capture"]                   # each frame shows the moment it was captured
    live_at = np.stack([np.interp(tc, D["phone_t"], live[:, i]) for i in range(3)], 1)
    scale = prm.guard_z / prm.fx  # metres per pixel at guard depth (for the torso marker)
    origin = np.array(prm.guard_px)
    shoulder_mid = np.array([[(float(r["ls_px"]) + float(r["rs_px"])) / 2] if r["detected"] == "True" else [np.nan] for r in rows])[:, 0]
    start = tc[0]
    # image-plane positions relative to guard, in cm at guard depth (+x = your right, +y = up)
    cam_rel = -(D["cam_px"] - origin) * cm_per_px
    fil_rel = -(np.array([project(prm, x)[0] for x in live_at]) - origin) * cm_per_px

    cap = cv2.VideoCapture(str(args.run / "clip.avi"))
    out = args.run / "fusion_review.mp4"
    writer = cv2.VideoWriter(str(out), cv2.VideoWriter_fourcc(*"avc1"), 30 * args.speed, (W, H))
    for i, row in enumerate(rows):
        ok, frame = cap.read()
        if not ok:
            break
        img = np.zeros((H, W, 3), np.uint8)
        if row["detected"] == "True":
            for side, color in (("l", ORANGE), ("r", CYAN)):
                pts = [(int(float(row[f"{side}{j}_px"])), int(float(row[f"{side}{j}_py"]))) for j in "sew"]
                cv2.polylines(frame, [np.array(pts).reshape(-1, 1, 2)], False, color, 4, cv2.LINE_AA)
                cv2.circle(frame, pts[2], 8, color, -1, cv2.LINE_AA)
        p = live_at[i]
        fx, fy = (int(v) for v in project(prm, p)[0])
        radius = int(np.clip(12 + p[2] * 40, 6, 40))
        cv2.circle(frame, (fx, fy), radius + 3, (0, 0, 0), 3, cv2.LINE_AA)
        cv2.circle(frame, (fx, fy), radius, YELLOW, 4, cv2.LINE_AA)
        img[:CAM_H, :CAM_W] = cv2.resize(frame, (CAM_W, CAM_H))
        step = next((s["step"] for s in D["steps"] if s["start"] <= D["t_frame"][i] < s["end"]), "")
        cv2.rectangle(img, (0, 0), (560, 44), (0, 0, 0), -1)
        cv2.rectangle(img, (0, CAM_H - 36), (CAM_W, CAM_H), (0, 0, 0), -1)
        cv2.putText(img, f"t = {D['t_frame'][i] - D['t_frame'][0]:6.2f} s   step: {step}   {args.speed:g}x", (14, 30),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.8, (255, 255, 255), 2, cv2.LINE_AA)
        cv2.putText(img, "cyan = camera's right wrist   yellow ring = filter's fist (bigger = closer to camera)",
                    (14, CAM_H - 14), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2, cv2.LINE_AA)

        shoulder_side = -(shoulder_mid[i] - origin[0]) * scale if np.isfinite(shoulder_mid[i]) else 0.0
        draw_overhead(img, i, live_at, D["cam_px"][i, 0], shoulder_side, prm)

        now = tc[i]
        gw = (W - 60) // 3
        y0, gh = CAM_H + 30, H - CAM_H - 70
        draw_graph(img, (20, y0, gw, gh), "image x: + = your right (cm at guard depth)", now,
                   {"camera": (tc, cam_rel[:, 0], PURPLE), "filter (projected)": (tc, fil_rel[:, 0], YELLOW)}, -60, 60, "")
        draw_graph(img, (40 + gw, y0, gw, gh), "image y: + = up (cm at guard depth)", now,
                   {"camera": (tc, cam_rel[:, 1], PURPLE), "filter (projected)": (tc, fil_rel[:, 1], YELLOW)}, -60, 80, "")
        draw_graph(img, (60 + 2 * gw, y0, gw, gh), "toward camera (cm)", now,
                   {"filter": (D["phone_t"], live[:, 2] * 100, YELLOW)}, -30, 70, "")
        cv2.putText(img, f"perspective camera ~{prm.guard_z:.2f} m to guard; leak beyond perspective {prm.leak[0] * 10:+.1f} cm per 10 cm toward camera   "
                    f"{D['heading_source']}", (20, H - 14), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (200, 200, 200), 1, cv2.LINE_AA)
        writer.write(img)
    writer.release()
    print(f"Wrote {out}")


if __name__ == "__main__":
    main()
