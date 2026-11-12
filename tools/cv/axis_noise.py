#!/usr/bin/env python3
"""Per-axis wrist noise and "snaps" from benchmark CSVs, using MediaPipe world landmarks (metres).

    .venv/bin/python axis_noise.py recordings/<run> [recordings/<run2> ...]

Axes (world landmarks, origin between the hips):
    x = left/right, y = up/down, z = forward/back (toward/away from the camera)

rest noise  wobble while holding still: std after subtracting a 9-frame moving average (mm)
snap        a short spike: how far a sample sits from the 5-frame running median. Steady motion in
            one direction (most of a punch) survives a median filter; a 1-2 frame jump that comes
            back does not. Reported as the 99th percentile spike (mm) and the % of samples whose
            spike exceeds 30 mm, split into the still and punching phases. Both wrists pooled.
"""
import csv
import sys
from pathlib import Path

import numpy as np

RUNS = ["lite_cpu", "full_cpu", "heavy_cpu", "lite_gpu", "full_gpu", "heavy_gpu"]
AXES = ["x", "y", "z"]
NAMES = {"x": "L/R", "y": "up/dn", "z": "fwd/back"}
SNAP_MM = 30


def series(rows, wrist, axis):
    return np.array([float(r[f"{wrist}_w{axis}"]) for r in rows]) * 1000  # mm


def running_median(v, k=5):
    pad = k // 2
    padded = np.pad(v, pad, mode="edge")
    return np.array([np.median(padded[i:i + k]) for i in range(len(v))])


def rest_noise(v, k=9):
    smooth = np.convolve(v, np.ones(k) / k, mode="same")
    return float(np.std((v - smooth)[k:-k]))


def analyze(run: Path) -> None:
    print(f"\n{run.name}")
    print(f"{'model':10} | {'rest noise (mm)':^24} | {'p99 spike, punching (mm)':^26} | "
          f"{'% samples >30 mm spike, punching':^34} | {'still':^20}")
    sub = " | ".join(" ".join(f"{NAMES[a]:>8}" for a in AXES) for _ in range(3))
    print(f"{'':10} | {sub} | {'  '.join(f'{NAMES[a]:>5}' for a in AXES)}")
    for name in RUNS:
        path = run / f"landmarks_{name}.csv"
        if not path.exists():
            continue
        with open(path) as f:
            rows = [r for r in csv.DictReader(f) if r["detected"] == "True"]
        still = [r for r in rows if r["phase"] == "still"]
        punch = [r for r in rows if r["phase"] == "punch"]
        noise, p99, frac, frac_still = [], [], [], []
        for a in AXES:
            noise.append(np.mean([rest_noise(series(still, w, a)) for w in ("lw", "rw")]))
            spikes_p = np.concatenate([np.abs(series(punch, w, a) - running_median(series(punch, w, a))) for w in ("lw", "rw")])
            spikes_s = np.concatenate([np.abs(series(still, w, a) - running_median(series(still, w, a))) for w in ("lw", "rw")])
            p99.append(np.percentile(spikes_p, 99))
            frac.append(np.mean(spikes_p > SNAP_MM) * 100)
            frac_still.append(np.mean(spikes_s > SNAP_MM) * 100)
        print(f"{name:10} | " + " ".join(f"{v:8.1f}" for v in noise) + " | "
              + " ".join(f"{v:8.0f}" for v in p99) + " | "
              + " ".join(f"{v:7.1f}% " for v in frac) + "   | "
              + "  ".join(f"{v:4.1f}%" for v in frac_still))


if __name__ == "__main__":
    for arg in sys.argv[1:]:
        analyze(Path(arg))
