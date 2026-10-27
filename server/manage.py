#!/usr/bin/env python3
"""Command-line admin tool for the Hellmart Coop relay (used by Server Manager).

Every command prints one JSON object. Errors: {"ok": false, "error": "..."}, exit code 1.
The relay keeps rooms in memory and writes data/status.json every 2 s; this tool reads that.

usage: manage.py [--root DIR] <command> [args]

  info                 game, version, rooms and players online, uptime
  rooms                open rooms with their players
  players              everyone online (one row per player, with the room code)
  player <id>          one row from "players" (id = CODE:peer, e.g. K7Q2:1)
  close <code>         close a room (its players get "room closed"); the relay does it within 2 s
  settings             this relay has no settings file: {}
  backup|backups|restore   nothing to back up (rooms live in memory): ok
  logs [lines]         the relay logs to the system journal only: []

--root is the folder with hmrelay.py (default: the folder of this file). Server Manager passes it
because it runs this script through "python3 -" over SSH.
"""
import json
import os
import sys
import time

VERSION = "1.0.0"
GAME = "hellmart-coop"
STALE = 10  # status older than this many seconds: the relay isn't running


def out(obj, code=0):
    print(json.dumps(obj, separators=(",", ":")))
    sys.exit(code)


def status(root):
    path = os.path.join(root, "data", "status.json")
    try:
        with open(path) as f:
            st = json.load(f)
    except FileNotFoundError:
        return {"running": False, "rooms": []}
    st["running"] = time.time() - st.get("updated", 0) < STALE
    if not st["running"]:
        st["rooms"] = []
    return st


def players(st):
    rows = []
    for room in st.get("rooms", []):
        for p in room.get("players", []):
            rows.append({
                "id": f"{room['code']}:{p['id']}",
                "name": p["name"],
                "room": room["code"],
                "role": p["role"],
                "since": p["since"],
                "minutes": max(0, int((time.time() - p["since"]) // 60)),
            })
    return rows


def main(argv):
    root = os.path.dirname(os.path.abspath(__file__))
    if len(argv) >= 2 and argv[0] == "--root":
        root = os.path.expanduser(argv[1])
        argv = argv[2:]
    if not argv:
        out({"ok": False, "error": "no command"}, 1)
    cmd, args = argv[0], argv[1:]
    st = status(root)
    if cmd == "info":
        rows = players(st)
        out({"ok": True, "game": GAME, "version": VERSION, "running": st["running"],
             "rooms": len(st.get("rooms", [])), "players": len(rows), "uptime": st.get("uptime", 0)})
    if cmd == "rooms":
        # "now" = the server's clock, so "since" times read right even if the app's clock differs.
        out({"ok": True, "now": int(time.time()), "rooms": st.get("rooms", [])})
    if cmd == "players":
        out({"ok": True, "players": players(st)})
    if cmd == "player":
        if not args:
            out({"ok": False, "error": "usage: player <CODE:peer>"}, 1)
        for row in players(st):
            if row["id"] == args[0]:
                out({"ok": True, "player": row})
        out({"ok": False, "error": "no such player online"}, 1)
    if cmd == "close":
        if not args:
            out({"ok": False, "error": "usage: close <code>"}, 1)
        code = "".join(c for c in args[0].upper() if c.isalnum())[:8]
        if not any(r["code"] == code for r in st.get("rooms", [])):
            out({"ok": False, "error": f"no open room {code}"}, 1)
        close_dir = os.path.join(root, "data", "close")
        os.makedirs(close_dir, exist_ok=True)
        open(os.path.join(close_dir, code), "w").close()
        out({"ok": True, "closing": code})
    if cmd == "settings":
        out({"ok": True, "settings": {}})
    if cmd in ("backup", "backups", "restore"):
        out({"ok": True, "backups": [], "note": "rooms live in memory, nothing to back up"})
    if cmd == "logs":
        out({"ok": True, "lines": []})
    out({"ok": False, "error": f"unknown command {cmd}"}, 1)


if __name__ == "__main__":
    main(sys.argv[1:])
