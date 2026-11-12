#!/usr/bin/env python3
"""Render a session recording with scrolling, time-aligned phone and camera graphs.

    .venv/bin/python render_session.py recordings/session-<timestamp> [--lag 80] [--speed 0.5]

Left: the camera with both arms drawn (cyan = right, orange = left). At the right wrist (where the
phone is), in the camera's frame:
  yellow arrow  phone velocity, sideways + up (where the fist would be 0.1 s later)
  pink arrow    camera wrist velocity across the image, same scale
  ring          phone velocity toward the camera (green) or away (red); radius = speed
  white outline the phone at true size, rotated by its attitude (thick edge = top of the phone)
The phone's heading relative to the camera comes from the calib_slide step when the session has
one; otherwise it's estimated from punch directions (most punches go toward the camera), which is
labelled on screen.

Right, sharing one time axis (3 s of history, 1 s ahead, white line = now):
  1. Phone acceleration, gravity removed (g): total, plus device x / y / z
  2. Speed: phone (acceleration rotated to world and integrated with a 0.4 s leak, so it shows the
     shape of each punch without drift) vs the camera wrist's speed across the image
  3. Camera right wrist relative to the shoulder midpoint: sideways and up/down (shoulder widths),
     and MediaPipe's depth toward the camera (m)
  4. How late each phone sample arrived over Wi-Fi (ms above the fastest sample)

Phone data is shifted LATER by --lag ms (the camera delay from analyze_session.py) so the same
punch lines up in both. Writes recordings/<session>/review.mp4.
"""
import argparse
import csv
import json
from pathlib import Path

import cv2
import numpy as np

W, H = 1920, 1080
VIDEO_W, VIDEO_H = 960, 540
PAST, FUTURE = 3.0, 1.0
G = 9.80665
TAU = 0.4
SHOULDER_M = 0.41      # measured: phone vs camera vertical motion agreed on a 41 cm shoulder width
ARROW_S = 0.10         # arrows show where the fist would be this many seconds later
PHONE_HALF = np.array([0.0715, 0.1467, 0.00765]) / 2  # iPhone 13, device axes x, y, z
COLORS = {"total": (255, 255, 255), "x": (90, 90, 255), "y": (90, 230, 90), "z": (255, 170, 60),
          "phone": (0, 200, 255), "camera": (255, 120, 255), "delay": (0, 160, 255)}
RIGHT, LEFT = (255, 220, 0), (0, 150, 255)


def rotate(q, v):
    """Rotate vectors v (N,3) by quaternions q (N,4: x,y,z,w)."""
    u, w = q[:, :3], q[:, 3:4]
    t = 2 * np.cross(u, v)
    return v + w * t + np.cross(u, t)


def load_phone(run: Path, lag: float):
    recv, rows = [], []
    for line in open(run / "phone.jsonl"):
        t, payload = line.split("\t", 1)
        m = json.loads(payload)
        if m.get("type") == "motion":
            recv.append(float(t))
            rows.append([m["t"], m["ax"], m["ay"], m["az"], m["qx"], m["qy"], m["qz"], m["qw"],
                         m.get("grx", 0), m.get("gry", 0), m.get("grz", -1)])
    recv, d = np.array(recv), np.array(rows)
    dev = d[:, 0]
    acc, q, grav = d[:, 1:4], d[:, 4:8], d[:, 8:11]
    down = np.array([0, 0, -1.0])  # CoreMotion world: z up
    q_inv = q * np.array([-1, -1, -1, 1])
    err_fwd = np.mean(np.linalg.norm(rotate(q, grav) - down, axis=1))
    err_inv = np.mean(np.linalg.norm(rotate(q_inv, grav) - down, axis=1))
    q = q if err_fwd <= err_inv else q_inv
    # userAcceleration points opposite to the phone's actual acceleration, hence the minus sign.
    world = -rotate(q, acc) * G
    v = np.zeros_like(world)
    for k in range(1, len(dev)):
        dt = min(max(dev[k] - dev[k - 1], 0.0), 0.05)
        v[k] = v[k - 1] * np.exp(-dt / TAU) + world[k] * dt
    delay = (recv - dev) * 1000
    print(f"gravity check: {min(err_fwd, err_inv):.3f} (0 = attitude consistent)")
    return {"t": dev + np.min(recv - dev) + lag, "acc": acc, "total": np.linalg.norm(acc, axis=1),
            "q": q, "v": v, "speed": np.linalg.norm(v, axis=1), "delay": delay - delay.min(), "arrival": recv}


def load_camera(run: Path, model: str):
    frames = json.loads((run / "frames.json").read_text())["frames"]
    rows = list(csv.DictReader(open(run / f"landmarks_{model}.csv")))
    t = np.array([f["t"] for f in frames])[:len(rows)]

    def col(key):
        return np.array([float(r[key]) if r["detected"] == "True" else np.nan for r in rows])

    mid_x, mid_y = (col("ls_px") + col("rs_px")) / 2, (col("ls_py") + col("rs_py")) / 2
    shoulder = np.nanmedian(np.hypot(col("ls_px") - col("rs_px"), col("ls_py") - col("rs_py")))
    side = -(col("rw_px") - mid_x) / shoulder   # + = your right (the image's left)
    up = -(col("rw_py") - mid_y) / shoulder
    px = np.stack([col("rw_px"), col("rw_py")], 1)
    smooth = np.apply_along_axis(lambda c: np.convolve(c, np.ones(3) / 3, "same"), 0, px)
    vel_px = np.gradient(smooth, axis=0) / np.gradient(t)[:, None]  # image px/s
    return {"t": t, "rows": rows, "side": side, "up": up, "depth": -col("rw_wz"),
            "speed": np.linalg.norm(vel_px, axis=1) / shoulder * SHOULDER_M,
            "side_vel": -vel_px[:, 0] / shoulder * SHOULDER_M, "shoulder_px": shoulder}, frames


def heading(phone, cam, steps):
    """Unit vectors (in CoreMotion's horizontal plane) for the camera's sideways (+ = your right) and
    forward (+ = toward the camera) axes."""
    tc = cam["t"]
    vh = np.stack([np.interp(tc, phone["t"], phone["v"][:, i]) for i in range(2)], 1)
    ok = np.isfinite(cam["side_vel"])

    def in_step(name):
        return np.any([(tc >= s["start"]) & (tc < s["end"]) for s in steps if s["step"] == name] or
                      [np.zeros_like(tc, bool)], axis=0)

    slide = in_step("calib_slide") & ok
    if slide.sum() > 30:
        _, vecs = np.linalg.eigh(vh[slide].T @ vh[slide])
        side = vecs[:, -1]
        source = "heading from calib_slide"
    else:  # no calibration: forward = the most common punch direction (axial average)
        hs = np.linalg.norm(vh, axis=1)
        peaks = [k for k in range(1, len(hs) - 1) if hs[k] > 1.5 and hs[k] >= hs[k - 1] and hs[k] >= hs[k + 1]]
        z = np.sum(hs[peaks] * np.exp(2j * np.arctan2(vh[peaks, 1], vh[peaks, 0])))
        a = np.angle(z) / 2
        side = np.array([-np.sin(a), np.cos(a)])
        source = "heading ESTIMATED from punch directions (no calib_slide)"
    if np.nansum((vh @ side)[ok] * cam["side_vel"][ok]) < 0:  # sideways sign: agree with the camera
        side = -side
    forward = np.array([side[1], -side[0]])
    # forward sign: punches start from guard and go toward the camera, so velocity just after leaving
    # rest should mostly be +forward.
    hs = np.linalg.norm(vh, axis=1)
    onsets = [k for k in range(3, len(hs) - 3) if hs[k - 3] < 0.3 and hs[k] > 0.8]
    if onsets and np.median(vh[onsets] @ forward) < 0:
        forward = -forward
    return side, forward, source


def to_camera(vec_world, side, forward):
    """CoreMotion world vectors (N,3) -> (sideways, up, toward camera)."""
    return np.stack([vec_world[:, :2] @ side, vec_world[:, 2], vec_world[:, :2] @ forward], 1)


def draw_arrow(img, start, vec_px, color):
    end = (int(start[0] + vec_px[0]), int(start[1] + vec_px[1]))
    if np.hypot(*vec_px) > 4:
        cv2.arrowedLine(img, start, end, (0, 0, 0), 7, cv2.LINE_AA, tipLength=0.25)
        cv2.arrowedLine(img, start, end, color, 4, cv2.LINE_AA, tipLength=0.25)


def draw_graph(img, rect, title, now, series, lo, hi, unit):
    x0, y0, w, h = rect
    cv2.rectangle(img, (x0, y0), (x0 + w, y0 + h), (30, 30, 30), -1)
    t0, t1 = now - PAST, now + FUTURE

    def X(t):
        return (x0 + (t - t0) / (t1 - t0) * w).astype(np.int32)

    def Y(v):
        return (y0 + h - (np.clip(v, lo, hi) - lo) / (hi - lo) * h).astype(np.int32)

    if lo < 0 < hi:
        y = int(Y(np.array([0.0]))[0])
        cv2.line(img, (x0, y), (x0 + w, y), (80, 80, 80), 1)
    for s in np.arange(np.ceil(t0), t1, 1.0):
        x = int(X(np.array([s]))[0])
        cv2.line(img, (x, y0), (x, y0 + h), (55, 55, 55), 1)
    legend_x = x0 + 8
    for name, (t, v, color) in series.items():
        m = (t >= t0) & (t <= t1) & np.isfinite(v)
        if m.sum() > 1:
            pts = np.stack([X(t[m]), Y(v[m])], 1)
            for seg in np.split(pts, np.where(np.diff(t[m]) > 0.15)[0] + 1):
                if len(seg) > 1:
                    cv2.polylines(img, [seg.reshape(-1, 1, 2)], False, color, 2, cv2.LINE_AA)
        cv2.putText(img, name, (legend_x, y0 + h - 8), cv2.FONT_HERSHEY_SIMPLEX, 0.5, color, 1, cv2.LINE_AA)
        legend_x += 14 + 9 * len(name)
    cv2.putText(img, f"{title}  [{lo:g} .. {hi:g} {unit}]", (x0 + 8, y0 + 20), cv2.FONT_HERSHEY_SIMPLEX, 0.55,
                (230, 230, 230), 1, cv2.LINE_AA)
    xn = int(X(np.array([now]))[0])
    cv2.line(img, (xn, y0), (xn, y0 + h), (255, 255, 255), 1)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("run", type=Path)
    p.add_argument("--lag", type=float, default=80, help="camera delay in ms; phone data is shifted by this")
    p.add_argument("--model", default="heavy_gpu")
    p.add_argument("--speed", type=float, default=0.5)
    args = p.parse_args()

    phone = load_phone(args.run, args.lag / 1000)
    cam, frames = load_camera(args.run, args.model)
    steps = json.loads((args.run / "session.json").read_text())["timeline"]
    side_axis, fwd_axis, heading_source = heading(phone, cam, steps)
    print(heading_source)
    px_per_m = cam["shoulder_px"] / SHOULDER_M
    tc = cam["t"]
    v_cam_frame = to_camera(np.stack([np.interp(tc, phone["t"], phone["v"][:, i]) for i in range(3)], 1),
                            side_axis, fwd_axis)
    q_at = np.stack([np.interp(tc, phone["t"], phone["q"][:, i]) for i in range(4)], 1)
    q_at /= np.linalg.norm(q_at, axis=1, keepdims=True)
    corners = np.array([[sx * PHONE_HALF[0], sy * PHONE_HALF[1], 0] for sx, sy in ((-1, 1), (1, 1), (1, -1), (-1, -1))])
    start = tc[0]

    cap = cv2.VideoCapture(str(args.run / "clip.avi"))
    out = args.run / "review.mp4"
    writer = cv2.VideoWriter(str(out), cv2.VideoWriter_fourcc(*"avc1"), 30 * args.speed, (W, H))
    graph_x, graph_w, gh, gap = VIDEO_W + 20, W - VIDEO_W - 40, 240, 22
    for i, row in enumerate(cam["rows"]):
        ok, frame = cap.read()
        if not ok:
            break
        now = tc[i]
        img = np.zeros((H, W, 3), np.uint8)
        if row["detected"] == "True":
            for side, color in (("l", LEFT), ("r", RIGHT)):
                pts = [(int(float(row[f"{side}{j}_px"])), int(float(row[f"{side}{j}_py"]))) for j in "sew"]
                cv2.polylines(frame, [np.array(pts).reshape(-1, 1, 2)], False, color, 4, cv2.LINE_AA)
                cv2.circle(frame, pts[2], 9, color, -1, cv2.LINE_AA)
            wrist = (int(float(row["rw_px"])), int(float(row["rw_py"])))
            # phone outline, rotated by attitude, orthographic in the camera frame, true size
            c = to_camera(rotate(np.repeat(q_at[i:i + 1], 4, 0), corners), side_axis, fwd_axis)
            poly = np.array([[wrist[0] - x * px_per_m, wrist[1] - y * px_per_m] for x, y, _ in c], np.int32)
            cv2.polylines(frame, [poly.reshape(-1, 1, 2)], True, (255, 255, 255), 2, cv2.LINE_AA)
            cv2.line(frame, tuple(poly[0]), tuple(poly[1]), (255, 255, 255), 5, cv2.LINE_AA)  # top edge
            # velocity: ring for toward/away, arrows for across the image
            vs, vu, vf = v_cam_frame[i]
            radius = int(abs(vf) * ARROW_S * px_per_m)
            if radius > 3:
                cv2.circle(frame, wrist, radius, (0, 220, 0) if vf > 0 else (0, 0, 230), 3, cv2.LINE_AA)
            s = ARROW_S * px_per_m
            draw_arrow(frame, wrist, (-vs * s, -vu * s), (0, 230, 255))
            if i > 0 and i < len(tc) - 1 and cam["rows"][i - 1]["detected"] == "True" and cam["rows"][i + 1]["detected"] == "True":
                prev, nxt = cam["rows"][i - 1], cam["rows"][i + 1]
                dt = tc[i + 1] - tc[i - 1]
                cv_px = ((float(nxt["rw_px"]) - float(prev["rw_px"])) / dt, (float(nxt["rw_py"]) - float(prev["rw_py"])) / dt)
                draw_arrow(frame, wrist, (cv_px[0] * ARROW_S, cv_px[1] * ARROW_S), (255, 100, 255))
        img[:VIDEO_H, :VIDEO_W] = cv2.resize(frame, (VIDEO_W, VIDEO_H))
        step = next((s["step"] for s in steps if s["start"] <= now < s["end"]), "")
        vs, vu, vf = v_cam_frame[i]
        info = [f"t = {now - start:6.2f} s   step: {step}",
                f"phone velocity (camera frame): side {vs:+.1f}  up {vu:+.1f}  toward camera {vf:+.1f} m/s",
                f"phone shifted +{args.lag:.0f} ms   {heading_source}",
                "Arms: cyan = right, orange = left. At the right wrist: yellow = phone velocity,",
                "pink = camera wrist velocity (both: position 0.1 s ahead), ring = toward camera (green)",
                "or away (red), white = phone outline (thick edge = top). 0.5x speed."]
        for k, text in enumerate(info):
            cv2.putText(img, text, (20, VIDEO_H + 40 + 32 * k), cv2.FONT_HERSHEY_SIMPLEX, 0.85 if k == 0 else 0.62,
                        (255, 255, 255), 2 if k == 0 else 1, cv2.LINE_AA)

        a, pt = phone["acc"], phone["t"]
        draw_graph(img, (graph_x, 10, graph_w, gh), "1. phone acceleration, gravity removed", now,
                   {"total": (pt, phone["total"], COLORS["total"]), "x": (pt, a[:, 0], COLORS["x"]),
                    "y": (pt, a[:, 1], COLORS["y"]), "z": (pt, a[:, 2], COLORS["z"])}, -6, 10, "g")
        draw_graph(img, (graph_x, 10 + (gh + gap), graph_w, gh), "2. speed", now,
                   {"phone (integrated)": (pt, phone["speed"], COLORS["phone"]),
                    "camera wrist (image plane)": (tc, cam["speed"], COLORS["camera"])}, 0, 8, "m/s")
        draw_graph(img, (graph_x, 10 + 2 * (gh + gap), graph_w, gh), "3. camera right wrist vs shoulders", now,
                   {"sideways (+ your right)": (tc, cam["side"], COLORS["x"]),
                    "up": (tc, cam["up"], COLORS["y"]),
                    "depth toward camera (m)": (tc, cam["depth"], COLORS["z"])}, -1.5, 1.5, "")
        draw_graph(img, (graph_x, 10 + 3 * (gh + gap), graph_w, gh - 60), "4. phone arrival delay (Wi-Fi)", now,
                   {"ms late": (phone["arrival"], phone["delay"], COLORS["delay"])}, 0, 120, "ms")
        writer.write(img)
    writer.release()
    print(f"Wrote {out}")


if __name__ == "__main__":
    main()
