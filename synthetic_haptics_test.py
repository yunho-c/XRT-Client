#!/usr/bin/env python3
"""
Send synthetic Inspire-style haptics payloads to Unity Editor over UDP.

Profile:
1) Ramp both hands to a firm grasp.
2) Hold the firm grasp for 5 seconds.
3) Release back to zero.

This script mirrors the same JSON schema used by inspire_hand_haptics_streamer.py:
{
  "type": "haptics",
  "timestamp": <unix-seconds>,
  "left":  {"thumb","index","middle","ring","little","palm"},
  "right": {"thumb","index","middle","ring","little","palm"}
}
"""

from __future__ import annotations

import argparse
import json
import socket
import sys
import time
from typing import Dict


FINGER_KEYS = ("thumb", "index", "middle", "ring", "little", "palm")


def _build_hand_profile(base_level: float) -> Dict[str, float]:
    """
    Slightly different per-finger values so the signal is not perfectly flat.
    Keeps everything inside [0, 1].
    """
    profile = {
        "thumb": min(1.0, base_level * 1),
        "index": min(1.0, base_level * 1.00),
        "middle": min(1.0, base_level * 1),
        "ring": min(1.0, base_level * 12),
        "little": min(1.0, base_level * 1),
        "palm": min(1.0, base_level * 1),
    }
    return profile


def _interpolate(a: float, b: float, t: float) -> float:
    t = max(0.0, min(1.0, t))
    return a + (b - a) * t


def _send_payload(sock: socket.socket, host: str, port: int, left: Dict[str, float], right: Dict[str, float], debug: bool) -> None:
    payload = {
        "type": "haptics",
        "timestamp": time.time(),
        "left": left,
        "right": right,
    }
    raw = json.dumps(payload).encode("utf-8")
    sock.sendto(raw, (host, port))

    if debug:
        print(
            "[haptics->udp] "
            f"{host}:{port} "
            f"L(th={left['thumb']:.3f},ix={left['index']:.3f},md={left['middle']:.3f},"
            f"rg={left['ring']:.3f},lt={left['little']:.3f},pa={left['palm']:.3f}) "
            f"R(th={right['thumb']:.3f},ix={right['index']:.3f},md={right['middle']:.3f},"
            f"rg={right['ring']:.3f},lt={right['little']:.3f},pa={right['palm']:.3f})"
        )


def run_test(
    sock: socket.socket,
    host: str,
    port: int,
    rate_hz: float,
    grasp_level: float,
    ramp_up_s: float,
    hold_s: float,
    release_s: float,
    rest_s: float,
    cycles: int,
    debug: bool,
) -> None:
    dt = 1.0 / rate_hz
    cycle_duration = ramp_up_s + hold_s + release_s + rest_s
    run_mode = "continuous" if cycles <= 0 else f"{cycles} cycle(s)"
    print(
        f"Streaming synthetic haptics to udp://{host}:{port} at {rate_hz:.1f} Hz "
        f"({run_mode}; ramp={ramp_up_s:.2f}s, hold_closed={hold_s:.2f}s, "
        f"release={release_s:.2f}s, rest_open={rest_s:.2f}s, grasp_level={grasp_level:.2f})."
    )

    cycle_index = 0
    while cycles <= 0 or cycle_index < cycles:
        cycle_index += 1
        t0 = time.monotonic()

        while True:
            now = time.monotonic()
            elapsed = now - t0
            if elapsed >= cycle_duration:
                break

            if elapsed < ramp_up_s:
                # Ramp from 0 -> firm grasp.
                base = _interpolate(0.0, grasp_level, elapsed / max(ramp_up_s, 1e-6))
            elif elapsed < ramp_up_s + hold_s:
                # Hold constant pressure.
                base = grasp_level
            elif elapsed < ramp_up_s + hold_s + release_s:
                # Release from firm grasp -> 0.
                t_rel = (elapsed - ramp_up_s - hold_s) / max(release_s, 1e-6)
                base = _interpolate(grasp_level, 0.0, t_rel)
            else:
                # Rest open before next grasp cycle.
                base = 0.0

            left = _build_hand_profile(base)
            right = _build_hand_profile(base)
            _send_payload(sock, host, port, left, right, debug=debug)

            # Keep send cadence stable.
            target_next = now + dt
            sleep_for = target_next - time.monotonic()
            if sleep_for > 0:
                time.sleep(sleep_for)

    # Send a few explicit zero frames so receiver settles cleanly at script exit.
    zeros = {k: 0.0 for k in FINGER_KEYS}
    for _ in range(max(3, int(rate_hz * 0.1))):
        _send_payload(sock, host, port, zeros, zeros, debug=False)
        time.sleep(dt)

    print("Synthetic haptics streaming complete.")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Send synthetic dual-hand haptics payloads directly to Unity Editor UDP debug input."
    )
    parser.add_argument("--host", type=str, default="127.0.0.1", help="UDP target host (Unity machine).")
    parser.add_argument("--port", type=int, default=8765, help="UDP target port (match WebRTCHapticReceiver.udpDebugPort).")
    parser.add_argument("--rate-hz", type=float, default=60.0, help="Payload send rate.")
    parser.add_argument("--grasp-level", type=float, default=0.72, help="Firm grasp level in [0,1].")
    parser.add_argument("--ramp-up-s", type=float, default=1.5, help="Seconds to ramp up to firm grasp.")
    parser.add_argument("--hold-s", type=float, default=5.0, help="Seconds to hold firm grasp.")
    parser.add_argument("--release-s", type=float, default=1.8, help="Seconds to release to zero.")
    parser.add_argument("--rest-s", "--open-hold-s", type=float, default=0.7, help="Seconds to rest open between close/open cycles.")
    parser.add_argument("--cycles", type=int, default=0, help="Number of open/close cycles (0 = run continuously).")
    parser.add_argument("--debug", action="store_true", help="Print outgoing values each frame.")
    args = parser.parse_args()

    if not (0.0 <= args.grasp_level <= 1.0):
        raise ValueError("--grasp-level must be in [0, 1].")
    if args.rate_hz <= 0.0:
        raise ValueError("--rate-hz must be > 0.")
    if args.port <= 0 or args.port > 65535:
        raise ValueError("--port must be in [1, 65535].")

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        run_test(
            sock=sock,
            host=args.host,
            port=args.port,
            rate_hz=args.rate_hz,
            grasp_level=args.grasp_level,
            ramp_up_s=args.ramp_up_s,
            hold_s=args.hold_s,
            release_s=args.release_s,
            rest_s=args.rest_s,
            cycles=args.cycles,
            debug=args.debug,
        )
    except KeyboardInterrupt:
        print("Interrupted by user.")
    finally:
        sock.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
