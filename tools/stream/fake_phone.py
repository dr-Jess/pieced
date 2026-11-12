#!/usr/bin/env python3
"""Pretend to be the phone: stream synthetic IMU samples to the game over TCP.

Useful for testing the game's motion stream without an iPhone. Every
--punch-every seconds it sends a short acceleration spike, which the
game's PhoneMotionPunchDetector turns into a right straight.

    python3 tools/stream/fake_phone.py                 # game on this laptop
    python3 tools/stream/fake_phone.py --host 192.168.1.20 --punch-every 1.5

Standard library only (Python 3.8+).
"""
import argparse
import json
import math
import socket
import time

PROTOCOL = 1


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=7777)
    parser.add_argument("--hz", type=float, default=100.0)
    parser.add_argument("--punch-every", type=float, default=2.0, help="seconds between fake punches (0 = never)")
    parser.add_argument("--seconds", type=float, default=0.0, help="stop after this long (0 = run until Ctrl+C)")
    args = parser.parse_args()

    with socket.create_connection((args.host, args.port), timeout=5) as sock:
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        send = lambda msg: sock.sendall((json.dumps(msg, separators=(",", ":")) + "\n").encode())
        send({"type": "hello", "role": "phone", "name": "fake_phone.py", "protocol": PROTOCOL})
        print(f"connected to {args.host}:{args.port}, streaming at {args.hz:g} Hz (Ctrl+C to stop)")

        period = 1.0 / args.hz
        start = time.monotonic()
        seq = 0
        try:
            while args.seconds <= 0 or time.monotonic() - start < args.seconds:
                seq += 1
                t = time.monotonic() - start
                ax, ay, az = (0.02 * math.sin(t * 3), 0.02 * math.cos(t * 2), 0.01)
                # A ~60 ms spike shaped like a punch's acceleration burst.
                if args.punch_every > 0:
                    phase = t % args.punch_every
                    if phase < 0.06:
                        ay += 3.0 * math.sin(math.pi * phase / 0.06)
                send({"type": "motion", "seq": seq, "t": round(t, 4),
                      "ax": round(ax, 4), "ay": round(ay, 4), "az": round(az, 4),
                      "gx": 0.0, "gy": 0.0, "gz": 0.0,
                      "qx": 0.0, "qy": 0.0, "qz": 0.0, "qw": 1.0,
                      "grx": 0.0, "gry": 0.0, "grz": -1.0})
                time.sleep(max(0.0, start + seq * period - time.monotonic()))
        except KeyboardInterrupt:
            pass
        print(f"sent {seq} samples")


if __name__ == "__main__":
    main()
