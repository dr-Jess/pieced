"""Phone + camera fusion for the fist holding the phone (offline prototype of the live filter).

Frame: camera-aligned and fixed in the room. side (+ = your right), up, fwd (toward the camera), in
metres; origin = the right fist's guard position.

State (9): position p (3), velocity v (3), accelerometer bias b (3, device frame, m/s^2; off by default).

Every phone sample (100 Hz):   predict with the phone's acceleration, rotated into the camera frame
                               by its attitude and the heading calibration. Velocity decays toward
                               zero with a 0.5 s time constant: fists don't coast, and it keeps phone
                               integration errors from running away.
When the phone is still:       velocity = 0, and if the fist is at its guard spot, depth = guard depth
Depth limits:                  the fist can't go past arm's reach or far behind guard
Every camera frame (30 Hz):    the wrist's PIXEL position. The filter projects its 3D fist into the
                               image and compares. Two camera models:
                                 "perspective"  pinhole camera: a fist closer to the camera moves more
                                                pixels per cm (the default)
                                 "ortho"        fixed metres-per-pixel at the guard spot (the original)
                               Each frame is applied at its CAPTURE time though it arrives ~90 ms later:
                               the filter rewinds, applies it, and replays the phone samples since,
                               exactly as a live system has to. Implausible readings are down-weighted.

`live` records what the game would have shown at each moment (only data that had arrived by then).
"""
from dataclasses import dataclass, field

import numpy as np

G = 9.80665


@dataclass
class Params:
    accel_noise: float = 0.2          # m/s^2/sqrt(Hz), phone acceleration error (attitude error, vibration)
    estimate_bias: bool = False       # accelerometer bias state; off by default (it absorbed camera error)
    bias_walk: float = 0.02           # m/s^2/sqrt(s)
    velocity_tau: float = 0.5         # s; velocity decays toward zero (0 = off)
    cam_sigma: float = 0.015          # m, camera wrist noise at rest (converted to pixels at the fist's depth)
    cam_sigma_per_speed: float = 0.03  # m per (m/s): blur and tracking error grow with speed
    leak: tuple = (0.0, 0.0)          # camera (side, up) shift per metre toward the camera, beyond perspective
    gate: float = 13.8                # chi-square (2 dof, 99.9%): more surprising readings are down-weighted
    still_accel: float = 0.06         # g
    still_gyro: float = 0.3           # rad/s
    still_samples: int = 8            # 80 ms
    zupt_sigma: float = 0.05          # m/s
    guard_sigma: float = 0.05         # m, depth anchor when resting at guard
    guard_radius: float = 0.10        # m, how close to the guard spot (sideways/up) counts as "at guard"
    reach: float = 0.55               # m in front of guard
    behind: float = 0.20              # m behind guard
    camera_off: list = field(default_factory=list)  # (start, end) windows with the camera hidden (dropout tests)
    # camera geometry (filled in by the loader)
    model: str = "perspective"
    fx: float = 907.0                 # px; C920 at 1280x720 has ~70 deg horizontal field of view
    cx: float = 640.0
    cy: float = 360.0
    guard_px: tuple = (640.0, 360.0)  # pixel where the right wrist sits in guard
    guard_z: float = 2.0              # m from the camera to the guard spot (perspective)
    m_per_px: float = 0.002           # metres per pixel at the guard spot (ortho)


def project(prm: Params, x):
    """Pixel (u, v) of the fist and its 2x3 Jacobian w.r.t. (side, up, fwd)."""
    side, up, fwd = x[0], x[1], x[2]
    ls, lu = prm.leak
    s, u_ = side + ls * fwd, up + lu * fwd      # lateral position as the camera would see it
    if prm.model == "ortho":
        k = 1.0 / prm.m_per_px
        h = np.array([prm.guard_px[0] - s * k, prm.guard_px[1] - u_ * k])
        J = np.array([[-k, 0, -ls * k], [0, -k, -lu * k]])
        return h, J
    # perspective: camera coords X (image right), Y (image down), Z (away from the camera)
    zg = prm.guard_z
    Xg = (prm.guard_px[0] - prm.cx) / prm.fx * zg
    Yg = (prm.guard_px[1] - prm.cy) / prm.fx * zg
    X, Y, Z = Xg - s, Yg - u_, max(zg - fwd, 0.3)  # your right = image left; up = image up; fwd = closer
    h = np.array([prm.cx + prm.fx * X / Z, prm.cy + prm.fx * Y / Z])
    J = np.array([[-prm.fx / Z, 0, prm.fx * (-ls / Z + X / Z ** 2)],
                  [0, -prm.fx / Z, prm.fx * (-lu / Z + Y / Z ** 2)]])
    return h, J


def px_per_m(prm: Params, x) -> float:
    """Pixels per metre of sideways motion at the fist's current depth."""
    if prm.model == "ortho":
        return 1.0 / prm.m_per_px
    return prm.fx / max(prm.guard_z - x[2], 0.3)


class FistFilter:
    def __init__(self, params: Params, p0):
        self.prm = params
        self.x = np.zeros(9)
        self.x[:3] = p0
        bias_var = 0.3 ** 2 if params.estimate_bias else 0.0
        self.P = np.diag([0.02 ** 2] * 2 + [0.05 ** 2] + [0.1 ** 2] * 3 + [bias_var] * 3)

    def predict(self, a_dev, M, dt):
        """a_dev: true acceleration in the device frame (m/s^2); M: device -> camera-frame rotation."""
        x, P, prm = self.x, self.P, self.prm
        a = M @ (a_dev - x[6:9])
        decay = np.exp(-dt / prm.velocity_tau) if prm.velocity_tau > 0 else 1.0
        x[0:3] += x[3:6] * dt + 0.5 * a * dt * dt
        x[3:6] = x[3:6] * decay + a * dt
        F = np.eye(9)
        F[0:3, 3:6] = np.eye(3) * dt
        F[3:6, 3:6] = np.eye(3) * decay
        F[0:3, 6:9] = -0.5 * dt * dt * M
        F[3:6, 6:9] = -dt * M
        Gn = np.vstack([0.5 * dt * dt * np.eye(3), dt * np.eye(3), np.zeros((3, 3))])
        Q = Gn @ Gn.T * prm.accel_noise ** 2 / dt
        if prm.estimate_bias:
            Q[6:9, 6:9] += np.eye(3) * prm.bias_walk ** 2 * dt
        self.P = F @ P @ F.T + Q

    def update(self, y, H, R, gate=None):
        """y = innovation (measurement - prediction)."""
        S = H @ self.P @ H.T + R
        nis = float(y @ np.linalg.solve(S, y))
        accepted = True
        if gate is not None and nis > gate:
            # Soft gate: inflate the measurement noise so the reading only counts as much as it's
            # plausible. A hard reject can lock an overconfident filter out of the camera for good.
            R = R * (nis / gate)
            S = H @ self.P @ H.T + R
            accepted = False
        K = self.P @ H.T @ np.linalg.inv(S)
        self.x = self.x + K @ y
        I_KH = np.eye(9) - K @ H
        self.P = I_KH @ self.P @ I_KH.T + K @ R @ K.T  # Joseph form
        return nis, accepted

    def camera_update(self, z_px):
        prm = self.prm
        h, J = project(prm, self.x)
        H = np.zeros((2, 9))
        H[:, 0:3] = J
        sigma_px = (prm.cam_sigma + prm.cam_sigma_per_speed * np.linalg.norm(self.x[3:6])) * px_per_m(prm, self.x)
        return self.update(z_px - h, H, np.eye(2) * sigma_px ** 2, prm.gate)

    def still_update(self, near_guard):
        prm = self.prm
        H = np.zeros((3, 9)); H[:, 3:6] = np.eye(3)
        self.update(-self.x[3:6], H, np.eye(3) * prm.zupt_sigma ** 2)
        if near_guard:
            H = np.zeros((1, 9)); H[0, 2] = 1
            self.update(np.array([-self.x[2]]), H, np.eye(1) * prm.guard_sigma ** 2)

    def reach_update(self):
        prm, z = self.prm, self.x[2]
        limit = prm.reach if z > prm.reach else -prm.behind if z < -prm.behind else None
        if limit is not None:
            H = np.zeros((1, 9)); H[0, 2] = 1
            self.update(np.array([limit - z]), H, np.eye(1) * 0.03 ** 2)


def run(phone_t, a_dev, M, still, cam_capture_t, cam_arrival_t, cam_px, params: Params, p0):
    """Run the filter live: phone samples in order, camera frames applied when they arrive.

    phone_t (N), a_dev (N,3) true device acceleration m/s^2, M (N,3,3) device->camera rotation,
    still (N) bool; cam_* (K): capture time, arrival time, (K,2) wrist pixel (NaN = none).
    Returns live estimates (N,9), final (replayed) estimates (N,9), and per-frame diagnostics
    (predicted pixel just before each frame was applied, NIS, accepted).
    """
    f = FistFilter(params, p0)
    n = len(phone_t)
    states = [None] * n
    attached = [[] for _ in range(n)]
    live = np.zeros((n, 9))
    order = np.argsort(cam_arrival_t)
    ci = 0
    diag = {"nis": np.full(len(cam_px), np.nan), "accepted": np.zeros(len(cam_px), bool),
            "pred": np.full((len(cam_px), 2), np.nan)}

    def step(k, record):
        if k > 0:
            dt = min(max(phone_t[k] - phone_t[k - 1], 1e-4), 0.05)
            f.predict(a_dev[k], M[k], dt)
        if still[k]:
            f.still_update(np.hypot(f.x[0], f.x[1]) < params.guard_radius)
        f.reach_update()
        for j in attached[k]:
            if record:
                diag["pred"][j] = project(params, f.x)[0]
            nis, ok = f.camera_update(cam_px[j])
            if record:
                diag["nis"][j], diag["accepted"][j] = nis, ok
        states[k] = (f.x.copy(), f.P.copy())

    for k in range(n):
        step(k, False)
        while ci < len(order) and cam_arrival_t[order[ci]] <= phone_t[k]:
            j = order[ci]
            ci += 1
            if np.any(np.isnan(cam_px[j])) or any(a <= cam_capture_t[j] < b for a, b in params.camera_off):
                continue
            kc = int(np.searchsorted(phone_t, cam_capture_t[j], side="right") - 1)
            if kc < 1:
                continue
            attached[kc].append(j)
            f.x, f.P = states[kc - 1][0].copy(), states[kc - 1][1].copy()
            for kk in range(kc, k + 1):
                step(kk, kk == kc)
        live[k] = f.x
    return live, np.array([s[0] for s in states]), diag
