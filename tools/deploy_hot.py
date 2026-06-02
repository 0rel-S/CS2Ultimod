#!/usr/bin/env python3
"""Hot deploy: build + FTP upload + RCON css_plugins unload/load.
**Players stay connected** — no server downtime.

Use when:
- Pure code changes that don't depend on plugin start-of-life invariants
  (new commands, bug fixes inside event handlers, label/text tweaks, etc.)
- You want feedback during a live session

Caveats:
- css_plugins unload/load leaks AssemblyLoadContexts inside the CS2
  process. Cumulative hot deploys add memory pressure — fall back to
  deploy_hard.py periodically (e.g. once a day) to flush.
- In-memory plugin state is wiped (Superhero _enabled flag, retake AWP
  carriers, _activePlayers, mode manager state). The DB persists, so
  player prefs / admin flags survive.
- The unload→load window is ~1-2s during which !commands and event
  handlers don't fire. Avoid hot-deploying mid-round if a clutch is alive.
"""
import sys, time, pathlib

sys.path.insert(0, str(pathlib.Path(__file__).parent))
from deploy_hard import build, upload, snapshot_log, tail_log, rcon


def hot_reload():
    print('Hot reload via RCON (css_plugins unload/load)...')
    rcon('css_plugins unload CS2Ultimod')
    time.sleep(1.5)
    rcon('css_plugins load CS2Ultimod')


if __name__ == '__main__':
    build()
    seen = snapshot_log()
    upload()
    hot_reload()
    time.sleep(2)
    tail_log(15, seen)
