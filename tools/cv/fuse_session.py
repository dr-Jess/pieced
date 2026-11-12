#!/usr/bin/env python3
"""Run the phone + camera fusion filter (fusion.py) on a recorded session and score it.

    .venv/bin/python fuse_session.py recordings/session-<timestamp> [--lag 80] [--hfov 70.4] [--shoulder 0.39]

Compares the two camera models ("ortho": fixed metres per pixel; "perspective": pinhole camera,
the default) on the same data. There is no ground truth yet, so the scores are consistency checks,
all measured in camera pixels and reported in cm at the guard spot's distance:
  next frame       how well the filter predicts each camera frame before seeing it
  dropouts         hide the camera for 0.5 s / 1.0 s windows while punching; compare the filter's
                   live output with the hidden frames, vs freezing the last camera position (only
                   frames where the fist moved >8 cm count)
  reach            how far the filter's fist travels across the image vs the camera's, at peaks
  return to guard  depth drift between rests, before the guard anchor corrects it
  latency          how far the filter's output leads the camera
Writes fusion.txt, fusion.png and fused.csv (live estimates at 100 Hz) into the session folder.
"""
import argparse
import csv
import json
import sys
from dataclasses import replace
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from fusion import G, Params, project, run  # noqa: E402
from render_session import SHOULDER_M, heading, load_camera, load_phone  # noqa: E402

ACTIVE = ("sync_start", "calib_slide", "calib_push", "side_punch", "free", "sync_end")
GUARD_IN_FRONT_OF_SHOULDERS = 0.30  # m


def quat_to_matrix(q):
    x, y, z, w = q.T
    return np.stack([
        np.stack([1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)], -1),
        np.stack([2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)], -1),
        np.stack([2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)], -1)], 1)


def load(run_dir: Path, lag: float, prm: Params, hfov_deg=70.4, shoulder_m=0.39):
    """Loads the session and returns (data, params with the camera geometry filled in)."""
    recv, rows = [], []
    for line in open(run_dir / "phone.jsonl"):
        t, payload = line.split("\t", 1)
        m = json.loads(payload)
        if m.get("type") == "motion":
            recv.append(float(t))
            rows.append([m["t"], m["ax"], m["ay"], m["az"], m["gx"], m["gy"], m["gz"]])
    recv, d = np.array(recv), np.array(rows)
    phone_t = d[:, 0] + np.min(recv - d[:, 0])          # laptop clock, no shift

    ph = load_phone(run_dir, lag)                       # attitude with the gravity-checked convention
    cam, frames = load_camera(run_dir, "heavy_gpu")
    steps = json.loads((run_dir / "session.json").read_text())["timeline"]
    side_axis, fwd_axis, heading_source = heading(ph, cam, steps)
    C = np.array([[side_axis[0], side_axis[1], 0], [0, 0, 1], [fwd_axis[0], fwd_axis[1], 0]])
    M = C @ quat_to_matrix(ph["q"])                     # device -> camera frame, per sample
    a_dev = -d[:, 1:4] * G                              # userAcceleration points opposite to motion

    calm = (np.linalg.norm(d[:, 1:4], axis=1) < prm.still_accel) & (np.linalg.norm(d[:, 4:7], axis=1) < prm.still_gyro)
    run_len = np.zeros(len(calm), int)
    for k in range(len(calm)):
        run_len[k] = run_len[k - 1] + 1 if calm[k] and k else int(calm[k])
    still = run_len >= prm.still_samples

    t_frame = cam["t"]
    px = np.array([[float(r["rw_px"]), float(r["rw_py"])] if r["detected"] == "True" else [np.nan] * 2 for r in cam["rows"]])
    guard = np.any([(t_frame >= s["start"]) & (t_frame < s["end"]) for s in steps if s["step"] == "guard"], axis=0)
    origin = np.nanmedian(px[guard], axis=0)
    width, height = 1280, 720
    fx = (width / 2) / np.tan(np.radians(hfov_deg) / 2)
    shoulder_z = fx * shoulder_m / cam["shoulder_px"]   # pinhole: size in px = fx * size / distance
    prm = replace(prm, fx=fx, cx=width / 2, cy=height / 2, guard_px=tuple(origin),
                  guard_z=shoulder_z - GUARD_IN_FRONT_OF_SHOULDERS, m_per_px=SHOULDER_M / cam["shoulder_px"])
    D = {"phone_t": phone_t, "a_dev": a_dev, "M": M, "still": still, "t_frame": t_frame,
         "capture": t_frame - lag, "arrival": t_frame + 0.010,  # +10 ms: live inference time
         "cam_px": px, "steps": steps, "heading_source": heading_source, "shoulder_z": shoulder_z}
    return D, prm


def in_steps(t, steps, names):
    return np.any([(t >= s["start"]) & (t < s["end"]) for s in steps if s["step"] in names] or [np.zeros_like(t, bool)], axis=0)


def go(D, prm):
    return run(D["phone_t"], D["a_dev"], D["M"], D["still"], D["capture"], D["arrival"], D["cam_px"], prm, np.zeros(3))


def at_capture(D, series):
    return np.stack([np.interp(D["capture"], D["phone_t"], series[:, i]) for i in range(series.shape[1])], 1)


def predicted_px(prm, states):
    return np.array([project(prm, x)[0] for x in states])


def fit_leak(D, prm, rounds=2):
    """Lateral camera error that grows with depth beyond what perspective explains, from the filter's residuals."""
    leak = np.array(prm.leak, float)
    ok = np.isfinite(D["cam_px"]).all(1) & in_steps(D["capture"], D["steps"], ACTIVE)
    for _ in range(rounds):
        p = replace(prm, leak=tuple(leak))
        _, final, _ = go(D, p)
        st = at_capture(D, final[:, :3])
        pred = predicted_px(p, st)
        # residual in pixels -> metres of lateral position at the fist's depth (d pixel / d side = -px_per_m)
        scale = np.array([project(p, x)[1][0, 0] for x in st])  # = -px per metre
        resid = (D["cam_px"] - pred) / scale[:, None]
        leak = leak + np.array([np.polyfit(st[ok, 2], resid[ok, i], 1)[0] for i in range(2)])
    return tuple(float(v) for v in np.round(leak, 3))


def score(D, prm, label, out):
    cm_per_px = prm.guard_z / prm.fx if prm.model == "perspective" else prm.m_per_px
    cm_per_px_ref = D["ref_cm_per_px"] / 100  # common reference so models are comparable
    live, final, diag = go(D, prm)
    okc = np.isfinite(D["cam_px"]).all(1)
    act = in_steps(D["capture"], D["steps"], ACTIVE) & okc
    err = np.linalg.norm(D["cam_px"] - diag["pred"], axis=1) * cm_per_px_ref * 100
    ok = act & np.isfinite(err)
    out.append(f"\n[{label}] model={prm.model} leak={prm.leak}")
    out.append(f"  next camera frame: median {np.median(err[ok]):.1f} cm, p90 {np.percentile(err[ok], 90):.1f} cm; "
               f"down-weighted as outliers {100 - diag['accepted'][ok].mean() * 100:.1f}%")

    pred_live = predicted_px(prm, at_capture(D, live[:, :3]))
    rel_cam, rel_pred = D["cam_px"] - np.array(prm.guard_px), pred_live - np.array(prm.guard_px)
    far = act & (np.linalg.norm(rel_cam, axis=1) * cm_per_px_ref > 0.25)
    reach = np.median(np.sum(rel_pred[far] * rel_cam[far], 1) / np.sum(rel_cam[far] ** 2, 1))
    out.append(f"  reach: filter fist travels {reach * 100:.0f}% as far across the image as the camera's, at peaks (>25 cm out)")

    for dur in (0.5, 1.0):
        windows = []
        for s_ in D["steps"]:
            if s_["step"] in ACTIVE:
                t0 = s_["start"] + 0.5
                while t0 + dur < s_["end"]:
                    windows.append((t0, t0 + dur))
                    t0 += dur + 1.5
        hidden, _, _ = go(D, replace(prm, camera_off=windows))
        hidden_px = predicted_px(prm, at_capture(D, hidden[:, :3]))
        ef, eh = [], []
        for a, b in windows:
            before = np.where((D["capture"] < a) & okc)[0]
            if not len(before):
                continue
            for j in np.where((D["capture"] >= a) & (D["capture"] < b) & okc)[0]:
                hold = np.abs(D["cam_px"][j] - D["cam_px"][before[-1]]) * cm_per_px_ref * 100
                if np.linalg.norm(hold) > 8:
                    ef.append(np.abs(D["cam_px"][j] - hidden_px[j]) * cm_per_px_ref * 100)
                    eh.append(hold)
        ef, eh = np.array(ef), np.array(eh)
        out.append(f"  camera hidden {dur:.1f} s ({len(windows)} windows, {len(ef)} frames with the fist >8 cm away): "
                   f"filter {np.median(ef[:, 0]):.1f} / {np.median(ef[:, 1]):.1f} cm (side / up median), "
                   f"freezing {np.median(eh[:, 0]):.1f} / {np.median(eh[:, 1]):.1f} cm")

    still, t = D["still"], D["phone_t"]
    onsets = [k for k in range(1, len(still)) if still[k] and not still[k - 1] and np.hypot(*live[k - 1, :2]) < prm.guard_radius]
    drift = np.array([abs(live[k - 1, 2]) for k in onsets]) * 100
    if len(drift):
        out.append(f"  return to guard ({len(drift)} rests): depth off by {np.median(drift):.1f} cm median, "
                   f"max {drift.max():.1f} cm")
    good = act & np.isfinite(rel_cam).all(1)
    cam_v = np.gradient(D["cam_px"][:, 0], D["t_frame"])
    pred_t = np.array([project(prm, x)[0][0] for x in live[:, :3]])
    fil_v = np.gradient(pred_t, t)
    lags = np.arange(-0.05, 0.2, 0.005)
    corr = [np.corrcoef(np.interp(D["t_frame"][good] - L, t, fil_v), cam_v[good])[0, 1] for L in lags]
    out.append(f"  filter output leads the camera frames by {lags[int(np.argmax(corr))] * 1000:.0f} ms "
               f"(the camera lags real motion by ~{(D['t_frame'][0] - D['capture'][0]) * 1000:.0f} ms)")
    return live, final, diag


def plot(D, prm, live, final, path):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    free = [s for s in D["steps"] if s["step"] == "free"][0]
    lo, hi = free["start"], min(free["start"] + 8, free["end"])
    t, tc = D["phone_t"], D["capture"]
    m, mc = (t >= lo) & (t <= hi), (tc >= lo) & (tc <= hi)
    cm = D["ref_cm_per_px"]
    rel = (D["cam_px"] - np.array(prm.guard_px)) * cm
    pl = (np.array([project(prm, x)[0] for x in live[m, :3]]) - np.array(prm.guard_px)) * cm
    fig, axes = plt.subplots(3, 1, figsize=(15, 9), sharex=True)
    for i, (ax, name) in enumerate(zip(axes[:2], ["image x (cm at guard depth; + = image right = your LEFT)",
                                                 "image y (cm at guard depth; + = DOWN)"])):
        ax.plot(tc[mc] - lo, rel[mc, i], ".", color="tab:purple", ms=5, label="camera (at capture time)")
        ax.plot(t[m] - lo, pl[:, i], color="tab:orange", lw=1.6, label="filter, live, projected into the image")
        ax.set_ylabel(name, fontsize=8)
        ax.legend(loc="upper right", fontsize=8)
        ax.grid(alpha=0.3)
    ax = axes[2]
    ax.fill_between(t[m] - lo, -30, 70, where=D["still"][m], color="0.85", label="phone still")
    ax.plot(t[m] - lo, live[m, 2] * 100, color="tab:orange", lw=1.6, label="filter, live")
    ax.plot(t[m] - lo, final[m, 2] * 100, color="tab:blue", lw=1, alpha=0.7, label="filter, after all camera data")
    ax.set_ylim(-30, 70)
    ax.set_ylabel("toward camera (cm)")
    ax.legend(loc="upper right", fontsize=8)
    ax.grid(alpha=0.3)
    axes[-1].set_xlabel("seconds into free punching")
    fig.suptitle(f"{prm.model} camera model, leak {prm.leak}", fontsize=10)
    fig.tight_layout()
    fig.savefig(path, dpi=80)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("run", type=Path)
    ap.add_argument("--lag", type=float, default=80)
    ap.add_argument("--hfov", type=float, default=70.4, help="camera horizontal field of view, degrees (C920: ~70)")
    ap.add_argument("--shoulder", type=float, default=0.39, help="your shoulder width, m (sets your distance from the camera)")
    args = ap.parse_args()
    D, prm = load(args.run, args.lag / 1000, Params(), args.hfov, args.shoulder)
    D["ref_cm_per_px"] = prm.guard_z / prm.fx * 100
    out = [f"session {args.run.name}: {D['heading_source']}, camera lag {args.lag:.0f} ms",
           f"camera geometry: fx {prm.fx:.0f} px, you're ~{D['shoulder_z']:.2f} m from the camera, guard ~{prm.guard_z:.2f} m; "
           f"1 px at guard depth = {D['ref_cm_per_px'] * 10:.1f} mm"]
    results = {}
    for model in ("ortho", "perspective"):
        p = replace(prm, model=model)
        p = replace(p, leak=fit_leak(D, p))
        results[model] = (p, score(D, p, f"{model}, fitted leak", out))
    print("\n".join(out))
    (args.run / "fusion.txt").write_text("\n".join(out) + "\n")
    p, (live, final, _) = results["perspective"]
    plot(D, p, live, final, args.run / "fusion.png")
    with open(args.run / "fused.csv", "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["t", "side", "up", "fwd", "v_side", "v_up", "v_fwd", "still"])
        for k in range(len(live)):
            w.writerow([f"{D['phone_t'][k]:.4f}"] + [f"{v:.4f}" for v in live[k, :6]] + [int(D["still"][k])])
    print(f"\nWrote fusion.txt, fusion.png, fused.csv (perspective model) to {args.run}")


if __name__ == "__main__":
    main()
