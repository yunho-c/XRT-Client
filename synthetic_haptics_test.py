#!/usr/bin/env python3
"""
Interactive synthetic Inspire-style haptics sender for Unity Editor (UDP).

Instead of cycling through a fixed ramp/hold/release profile, this opens a small
GUI with one vertical slider per finger per hand (12 total). The current slider
values are streamed continuously over UDP at a fixed rate, so you can drag any
slider up and down to manually sweep that finger through its range and watch the
effect live in Unity.

Multi-slider sweep:
  - Tick the "link" checkbox under any sliders you want to move together.
  - Dragging ANY linked slider moves all linked sliders to the same value.
  - Or use the "Master (linked)" slider at the top to sweep every linked slider.

Payload schema (matches inspire_hand_haptics_streamer.py):
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
import os
import socket
import sys
import threading
import time
from typing import Dict


import tkinter as tk
from tkinter import ttk


def _fix_tcl_paths() -> None:
    """Some Windows Python installs ship Tcl/Tk under <home>/tcl but don't set
    TCL_LIBRARY/TK_LIBRARY, so Tk() fails with 'Can't find a usable init.tcl'.
    Set the env vars (read by Tcl when Tk() builds its interpreter) before the
    first Tk() call.

    Don't trust sys.prefix / os.__file__ — on broken or relocated installs they
    can resolve to the cwd. The reliable anchor is where the tkinter package
    actually loaded from: tcl/ is a sibling of that Lib/ directory."""
    if os.environ.get("TCL_LIBRARY") and os.path.isfile(
        os.path.join(os.environ["TCL_LIBRARY"], "init.tcl")
    ):
        return

    homes = []
    if getattr(tk, "__file__", None):
        # .../<home>/Lib/tkinter/__init__.py  ->  <home>
        homes.append(os.path.dirname(os.path.dirname(os.path.dirname(tk.__file__))))
    for p in sys.path:
        if os.path.basename(p).lower() in ("lib", "site-packages"):
            homes.append(os.path.dirname(p))
    homes += [sys.base_prefix, sys.prefix, os.path.dirname(sys.executable)]

    for home in homes:
        tcl_root = os.path.join(home, "tcl")
        if not os.path.isdir(tcl_root):
            continue
        tcl_dir = tk_dir = None
        for name in sorted(os.listdir(tcl_root), reverse=True):
            full = os.path.join(tcl_root, name)
            if not os.path.isdir(full):
                continue
            if tcl_dir is None and name.startswith("tcl") and os.path.isfile(
                os.path.join(full, "init.tcl")
            ):
                tcl_dir = full
            if tk_dir is None and name.startswith("tk") and os.path.isdir(full):
                tk_dir = full
        if tcl_dir:
            os.environ["TCL_LIBRARY"] = tcl_dir
            if tk_dir:
                os.environ["TK_LIBRARY"] = tk_dir
            return


_fix_tcl_paths()

FINGER_KEYS = ("thumb", "index", "middle", "ring", "little", "palm")
HANDS = ("left", "right")


class HapticsSender:
    """Background UDP streamer reading a shared value table under a lock."""

    def __init__(self, host: str, port: int, rate_hz: float, debug: bool) -> None:
        self.host = host
        self.port = port
        self.dt = 1.0 / rate_hz
        self.debug = debug
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

        self._lock = threading.Lock()
        self._values: Dict[str, Dict[str, float]] = {
            hand: {k: 0.0 for k in FINGER_KEYS} for hand in HANDS
        }
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        self._thread.join(timeout=1.0)
        # Settle the receiver at zero on exit.
        zeros = {hand: {k: 0.0 for k in FINGER_KEYS} for hand in HANDS}
        for _ in range(3):
            self._send(zeros)
            time.sleep(self.dt)
        self.sock.close()

    def set_value(self, hand: str, finger: str, value: float) -> None:
        with self._lock:
            self._values[hand][finger] = max(0.0, min(1.0, value))

    def _snapshot(self) -> Dict[str, Dict[str, float]]:
        with self._lock:
            return {hand: dict(fingers) for hand, fingers in self._values.items()}

    def _send(self, values: Dict[str, Dict[str, float]]) -> None:
        payload = {
            "type": "haptics",
            "timestamp": time.time(),
            "left": values["left"],
            "right": values["right"],
        }
        self.sock.sendto(json.dumps(payload).encode("utf-8"), (self.host, self.port))
        if self.debug:
            l, r = values["left"], values["right"]
            print(
                f"[haptics->udp] {self.host}:{self.port} "
                f"L({','.join(f'{k[:2]}={l[k]:.2f}' for k in FINGER_KEYS)}) "
                f"R({','.join(f'{k[:2]}={r[k]:.2f}' for k in FINGER_KEYS)})"
            )

    def _run(self) -> None:
        next_t = time.monotonic()
        while not self._stop.is_set():
            self._send(self._snapshot())
            next_t += self.dt
            sleep_for = next_t - time.monotonic()
            if sleep_for > 0:
                time.sleep(sleep_for)
            else:
                next_t = time.monotonic()


class FingerSlider:
    """A single vertical 0..1 slider + a 'link' checkbox for group sweeps."""

    def __init__(self, parent: tk.Widget, app: "HapticsGUI", hand: str, finger: str) -> None:
        self.app = app
        self.hand = hand
        self.finger = finger
        # Last value this slider settled at — used to compute drag deltas so linked
        # sliders translate together while preserving their relative offsets.
        self._last = 0.0

        frame = ttk.Frame(parent)
        frame.pack(side=tk.LEFT, padx=2)

        ttk.Label(frame, text=finger.capitalize()[:3]).pack()

        self.value_var = tk.DoubleVar(value=0.0)
        self.value_label = ttk.Label(frame, text="0.00")
        self.value_label.pack()

        # Vertical scale: top = 1.0, bottom = 0.0.
        self.scale = tk.Scale(
            frame,
            from_=1.0,
            to=0.0,
            resolution=0.01,
            orient=tk.VERTICAL,
            length=140,
            width=12,
            showvalue=False,
            variable=self.value_var,
            command=self._on_drag,
        )
        self.scale.pack()

        self.link_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(frame, text="link", variable=self.link_var).pack()

    def _on_drag(self, raw: str) -> None:
        value = float(raw)
        # If linked, translate the whole linked group by this slider's delta (preserving
        # relative offsets) rather than copying this value onto the others.
        if self.link_var.get():
            self.app.translate_linked(source=self, new_value=value)
        else:
            self.value_label.config(text=f"{value:.2f}")
            self.app.sender.set_value(self.hand, self.finger, value)
            self._last = value

    def set_value(self, value: float, push: bool = True) -> None:
        value = max(0.0, min(1.0, value))
        self.value_var.set(value)
        self.value_label.config(text=f"{value:.2f}")
        self._last = value
        if push:
            self.app.sender.set_value(self.hand, self.finger, value)


class HapticsGUI:
    def __init__(self, root: tk.Tk, sender: HapticsSender) -> None:
        self.root = root
        self.sender = sender
        self._syncing = False
        self._sweep_job = None
        self._master_last = 0.0
        self.sliders: Dict[str, Dict[str, FingerSlider]] = {}

        root.title("Synthetic Haptics — Finger Sliders")

        # --- Master / group controls (one compact row) ---
        top = ttk.Frame(root)
        top.pack(fill=tk.X, padx=8, pady=(6, 2))

        ttk.Label(top, text="Master:").pack(side=tk.LEFT)
        self.master_var = tk.DoubleVar(value=0.0)
        self.master_scale = tk.Scale(
            top,
            from_=0.0,
            to=1.0,
            resolution=0.01,
            orient=tk.HORIZONTAL,
            length=200,
            showvalue=False,
            variable=self.master_var,
            command=self._on_master,
        )
        self.master_scale.pack(side=tk.LEFT, padx=6)

        ttk.Button(top, text="Link All", command=lambda: self._set_all_links(True)).pack(side=tk.LEFT, padx=2)
        ttk.Button(top, text="Unlink All", command=lambda: self._set_all_links(False)).pack(side=tk.LEFT, padx=2)

        # --- Timed sweep controls (one compact row) ---
        # Haptics fire when a value crosses a threshold, so ramp gradually rather
        # than jumping. These buttons glide the targeted sliders to 0 or 1 over
        # 'Sweep time' seconds (the streamer keeps sending the in-between values).
        sweep = ttk.Frame(root)
        sweep.pack(fill=tk.X, padx=8, pady=(0, 4))

        ttk.Label(sweep, text="Sweep s:").pack(side=tk.LEFT)
        self.sweep_time_var = tk.StringVar(value="2.0")
        ttk.Entry(sweep, textvariable=self.sweep_time_var, width=5).pack(side=tk.LEFT, padx=(2, 8))

        ttk.Button(sweep, text="All→0", command=lambda: self._start_sweep(0.0, linked_only=False)).pack(side=tk.LEFT, padx=1)
        ttk.Button(sweep, text="All→1", command=lambda: self._start_sweep(1.0, linked_only=False)).pack(side=tk.LEFT, padx=1)
        ttk.Button(sweep, text="Lnk→0", command=lambda: self._start_sweep(0.0, linked_only=True)).pack(side=tk.LEFT, padx=1)
        ttk.Button(sweep, text="Lnk→1", command=lambda: self._start_sweep(1.0, linked_only=True)).pack(side=tk.LEFT, padx=1)
        ttk.Button(sweep, text="Stop", command=self._cancel_sweep).pack(side=tk.LEFT, padx=1)
        self.sweep_status = ttk.Label(sweep, text="", foreground="#777")
        self.sweep_status.pack(side=tk.LEFT, padx=6)

        # --- Per-hand slider banks, side by side to stay short vertically ---
        hands_row = ttk.Frame(root)
        hands_row.pack(fill=tk.X, padx=8, pady=2)

        for hand in HANDS:
            bank = ttk.LabelFrame(hands_row, text=f"{hand.capitalize()} hand")
            bank.pack(side=tk.LEFT, padx=4, pady=2, anchor=tk.N)

            btns = ttk.Frame(bank)
            btns.pack(fill=tk.X)
            ttk.Button(
                btns, text="Link", width=6, command=lambda h=hand: self._set_hand_links(h, True)
            ).pack(side=tk.LEFT, padx=2, pady=1)
            ttk.Button(
                btns, text="Unlink", width=7, command=lambda h=hand: self._set_hand_links(h, False)
            ).pack(side=tk.LEFT, padx=2, pady=1)

            row = ttk.Frame(bank)
            row.pack(padx=4, pady=(0, 4))

            self.sliders[hand] = {}
            for finger in FINGER_KEYS:
                self.sliders[hand][finger] = FingerSlider(row, self, hand, finger)

        ttk.Label(
            root,
            text="'link' = move sliders together keeping their relative offsets. "
                 "Sweep buttons ramp to 0/1 over 'Sweep s'.",
            foreground="#555",
            wraplength=560,
        ).pack(padx=8, pady=(0, 6))

        root.protocol("WM_DELETE_WINDOW", self._on_close)

    def _iter_sliders(self):
        for hand in HANDS:
            for finger in FINGER_KEYS:
                yield self.sliders[hand][finger]

    def _translate_group(self, linked, before: Dict[FingerSlider, float], desired_delta: float) -> None:
        """Rigidly shift every linked slider by `desired_delta`, clamped so none leaves
        [0, 1] — this preserves the group's relative offsets (no slider clips early)."""
        if not linked:
            return
        lo = min(before.values())
        hi = max(before.values())
        delta = max(-lo, min(1.0 - hi, desired_delta))  # rigid-body clamp
        for sl in linked:
            sl.set_value(before[sl] + delta)

    def translate_linked(self, source: FingerSlider, new_value: float) -> None:
        """Drag handler for a linked slider: move the whole linked group by the source's
        delta while keeping their relative offsets (instead of copying one value to all)."""
        if self._syncing:
            return
        self._syncing = True
        try:
            linked = [sl for sl in self._iter_sliders() if sl.link_var.get()]
            if source not in linked:
                source.set_value(new_value)
                return
            # 'before' state: the source's pre-drag value, others as they stand now.
            before = {sl: (source._last if sl is source else sl.value_var.get()) for sl in linked}
            self._translate_group(linked, before, new_value - source._last)
        finally:
            self._syncing = False

    def _on_master(self, raw: str) -> None:
        """Master slider translates the linked group by its own delta, preserving offsets."""
        value = float(raw)
        if self._syncing:
            self._master_last = value
            return
        self._syncing = True
        try:
            linked = [sl for sl in self._iter_sliders() if sl.link_var.get()]
            before = {sl: sl.value_var.get() for sl in linked}
            self._translate_group(linked, before, value - self._master_last)
        finally:
            self._master_last = value
            self._syncing = False

    def _set_all_links(self, state: bool) -> None:
        for sl in self._iter_sliders():
            sl.link_var.set(state)

    def _set_hand_links(self, hand: str, state: bool) -> None:
        for finger in FINGER_KEYS:
            self.sliders[hand][finger].link_var.set(state)

    def _sweep_duration(self) -> float:
        try:
            return max(0.0, float(self.sweep_time_var.get()))
        except (ValueError, tk.TclError):
            self.sweep_status.config(text="(invalid time — using 0s)")
            return 0.0

    def _cancel_sweep(self) -> None:
        if self._sweep_job is not None:
            self.root.after_cancel(self._sweep_job)
            self._sweep_job = None
            self.sweep_status.config(text="(stopped)")

    def _start_sweep(self, target: float, linked_only: bool) -> None:
        """Linearly ramp the targeted sliders from their current values to
        `target` over the configured sweep time, so thresholds are crossed
        gradually rather than in a single jump."""
        self._cancel_sweep()
        sliders = [sl for sl in self._iter_sliders() if (sl.link_var.get() or not linked_only)]
        if not sliders:
            self.sweep_status.config(text="(no linked sliders selected)")
            return

        duration = self._sweep_duration()
        if duration <= 0.0:
            self._apply_values({sl: target for sl in sliders})
            self.sweep_status.config(text="")
            return

        starts = {sl: sl.value_var.get() for sl in sliders}
        start_t = time.monotonic()
        step_ms = 16  # ~60 fps animation; the UDP streamer runs independently

        def step() -> None:
            frac = (time.monotonic() - start_t) / duration
            if frac >= 1.0:
                self._apply_values({sl: target for sl in sliders})
                self._sweep_job = None
                self.sweep_status.config(text="(done)")
                return
            self._apply_values({sl: starts[sl] + (target - starts[sl]) * frac for sl in sliders})
            self.sweep_status.config(text=f"sweeping → {target:.0f}  ({frac * 100:4.0f}%)")
            self._sweep_job = self.root.after(step_ms, step)

        step()

    def _apply_values(self, values: Dict[FingerSlider, float]) -> None:
        # Bypass link propagation: we drive exactly the sliders we chose.
        self._syncing = True
        try:
            for sl, value in values.items():
                sl.set_value(value)
        finally:
            self._syncing = False

    def _on_close(self) -> None:
        self._cancel_sweep()
        self.sender.stop()
        self.root.destroy()


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Interactive slider GUI to stream synthetic dual-hand haptics to Unity over UDP."
    )
    parser.add_argument("--host", type=str, default="127.0.0.1", help="UDP target host (Unity machine).")
    parser.add_argument("--port", type=int, default=8765, help="UDP target port (match WebRTCHapticReceiver.udpDebugPort).")
    parser.add_argument("--rate-hz", type=float, default=60.0, help="Payload send rate.")
    parser.add_argument("--debug", action="store_true", help="Print outgoing values each frame.")
    args = parser.parse_args()

    if args.rate_hz <= 0.0:
        raise ValueError("--rate-hz must be > 0.")
    if args.port <= 0 or args.port > 65535:
        raise ValueError("--port must be in [1, 65535].")

    sender = HapticsSender(host=args.host, port=args.port, rate_hz=args.rate_hz, debug=args.debug)
    sender.start()
    print(f"Streaming finger-slider haptics to udp://{args.host}:{args.port} at {args.rate_hz:.1f} Hz.")

    root = tk.Tk()
    HapticsGUI(root, sender)
    try:
        root.mainloop()
    except KeyboardInterrupt:
        sender.stop()
    print("Synthetic haptics GUI closed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
