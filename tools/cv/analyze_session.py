#!/usr/bin/env python3
"""Timing analysis of a record_session.py recording.

    .venv/bin/python analyze_session.py recordings/session-<timestamp>

1. Network: how late phone samples arrive (relative to the fastest one) and how often Wi-Fi stalls.
2. Camera delay: how far the camera's view of the right wrist lags the phone, found by
   cross-correlating phone |acceleration| with the right wrist's 2D image acceleration over a range
   of lags. Done per step (sync_start, free, sync_end) as a consistency check.

Interpreting the lag: phone samples are placed on the laptop clock using the fastest-arriving
sample, so they appear late by the phone's *minimum* sensor-to-laptop latency (unknown, likely
~5-15 ms). The camera timestamp is when OpenCV returned the frame. So:
    camera capture-to-laptop delay ~= reported lag + phone minimum latency
MediaPipe's own smoothing lag is included (it's in the landmarks); live inference time isn't.
"""
import csv
import json
import sys
from pathlib import Path

import numpy as np


def load_phone(run: Path):
    recv, dev, acc = [], [], []
    for line in open(run / "phone.jsonl"):
        t, payload = line.split("\t", 1)
        m = json.loads(payload)
        if m.get("type") == "motion":
            recv.append(float(t)); dev.append(m["t"]); acc.append(np.linalg.norm([m["ax"], m["ay"], m["az"]]))
    return map(np.array, (recv, dev, acc))


def network(recv, dev):
    extra = (recv - dev) * 1000
    extra -= extra.min()
    gaps = np.diff(recv) * 1000
    stalls = np.where(gaps > 30)[0]
    span = recv[-1] - recv[0]
    print("Network")
    print(f"  arrival delay above fastest: p50 {np.percentile(extra, 50):.1f} ms, p95 {np.percentile(extra, 95):.1f}, "
          f"p99 {np.percentile(extra, 99):.1f}, max {extra.max():.1f}")
    print(f"  samples >30 ms late: {np.mean(extra > 30) * 100:.1f}%;  stalls (gap >30 ms): {len(stalls)} "
          f"= {len(stalls) / span:.2f}/s, longest {gaps.max():.0f} ms")
    if len(stalls) > 2:
        print(f"  time between stalls: median {np.median(np.diff(recv[stalls])):.2f} s")


def camera_lag(run: Path, model: str, t_phone, accel):
    path = run / f"landmarks_{model}.csv"
    if not path.exists():
        return
    rows = list(csv.DictReader(open(path)))
    t_cam = np.array([f["t"] for f in json.loads((run / "frames.json").read_text())["frames"]])[:len(rows)]
    detected = np.array([r["detected"] == "True" for r in rows])
    shoulder = np.median([np.hypot(float(r["ls_px"]) - float(r["rs_px"]), float(r["ls_py"]) - float(r["rs_py"]))
                          for r in rows if r["detected"] == "True"])
    wrist = np.array([[float(r["rw_px"]), float(r["rw_py"])] if r["detected"] == "True" else [np.nan, np.nan]
                      for r in rows]) / shoulder
    wrist = np.apply_along_axis(lambda c: np.convolve(c, np.ones(3) / 3, "same"), 0, wrist)
    dt = np.gradient(t_cam)
    cam_acc = np.linalg.norm(np.gradient(np.gradient(wrist, axis=0), axis=0), axis=1) / dt ** 2
    envelope = np.convolve(accel, np.ones(5) / 5, "same")
    lags = np.arange(-0.15, 0.30, 0.002)

    def best(mask):
        scores = []
        for lag in lags:
            phone = np.interp(t_cam[mask] - lag, t_phone, envelope)
            cam = cam_acc[mask]
            good = np.isfinite(cam)
            scores.append(np.corrcoef(phone[good], cam[good])[0, 1])
        scores = np.array(scores)
        i = scores.argmax()
        near = lags[scores > scores[i] - 0.05] * 1000
        return lags[i] * 1000, scores[i], near.min(), near.max()

    print(f"Camera lag behind phone, right wrist, {model}")
    timeline = json.loads((run / "session.json").read_text())["timeline"]
    for step in ("sync_start", "free", "sync_end"):
        for seg in (s for s in timeline if s["step"] == step):
            mask = (t_cam >= seg["start"]) & (t_cam < seg["end"]) & detected
            lag, corr, lo, hi = best(mask)
            print(f"  {step:10} {lag:5.0f} ms   correlation {corr:.2f}   (within 0.05 of best: {lo:.0f} to {hi:.0f} ms)")
    lag, corr, lo, hi = best(detected)
    print(f"  {'whole':10} {lag:5.0f} ms   correlation {corr:.2f}   ({lo:.0f} to {hi:.0f} ms)")


def main():
    run = Path(sys.argv[1])
    recv, dev, accel = load_phone(run)
    network(recv, dev)
    t_phone = dev + np.min(recv - dev)  # phone sample times on the laptop clock (+ min latency)
    for model in ("heavy_gpu", "full_cpu"):
        camera_lag(run, model, t_phone, accel)


if __name__ == "__main__":
    main()
