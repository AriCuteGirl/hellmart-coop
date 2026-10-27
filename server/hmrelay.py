#!/usr/bin/env python3
"""Hellmart Coop relay: room codes over UDP, so nobody has to forward ports.

One player hosts the game as usual; host and friends all connect *out* to this relay. The host
opens a room and gets a short code; friends join with the code. The relay only forwards the mod's
packets between the host and each friend (it doesn't look inside them).

Wire format (all little-endian), every packet starts with b"HMR1" and a type byte:
  1 CREATE   client->relay  name(str)                   open a room as its host
  2 CREATED  relay->host    code(str)                   the room's code
  3 JOIN     client->relay  code(str) name(str)         join a room
  4 JOINED   relay->peer    peer(u16)                   you're in
  5 ERROR    relay->client  message(str)
  6 DATA     host->relay    peer(u16) payload           to that friend
             peer->relay    payload                     to the host
             relay->host    peer(u16) payload           from that friend
             relay->peer    payload                     from the host
  7 PING     client->relay  (keeps the mapping alive; any packet does)
  8 LEFT     relay->host    peer(u16)                   a friend timed out
Health check: b"HMR1?" -> b"HMR1!".
str = u8 length + UTF-8 bytes.

State for Server Manager: data/status.json (rooms, players, uptime), rewritten every 2 s.
Closing a room from Server Manager: manage.py writes data/close/<CODE>; the relay picks it up.

Settings (environment): HMRELAY_PORT (27360), HMRELAY_DATA (./data), HMRELAY_MAX_ROOMS (200).
"""
import asyncio
import json
import os
import secrets
import struct
import sys
import time

MAGIC = b"HMR1"
CREATE, CREATED, JOIN, JOINED, ERROR, DATA, PING, LEFT = 1, 2, 3, 4, 5, 6, 7, 8
PORT = int(os.environ.get("HMRELAY_PORT", "27360"))
DATA_DIR = os.environ.get("HMRELAY_DATA", os.path.join(os.path.dirname(os.path.abspath(__file__)), "data"))
MAX_ROOMS = int(os.environ.get("HMRELAY_MAX_ROOMS", "200"))
MAX_PEERS = 8
PEER_TIMEOUT = 15.0
HOST_TIMEOUT = 30.0
CODE_CHARS = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"  # no 0/O, 1/I


def pstr(s):
    b = s.encode("utf-8")[:64]
    return bytes([len(b)]) + b


def rstr(buf, pos):
    n = buf[pos]
    return buf[pos + 1:pos + 1 + n].decode("utf-8", "replace"), pos + 1 + n


def clean_name(name):
    name = "".join(c for c in name if c.isprintable()).strip()
    return name[:24] or "Player"


class Room:
    def __init__(self, code, host, name):
        self.code = code
        self.host = host
        self.host_name = name
        self.host_seen = time.time()
        self.created = time.time()
        self.peers = {}  # id -> {"addr", "name", "seen", "since"}
        self.by_addr = {}  # addr -> id
        self.next_id = 1
        self.bytes = 0


class Relay(asyncio.DatagramProtocol):
    def __init__(self):
        self.rooms = {}  # code -> Room
        self.host_rooms = {}  # host addr -> Room
        self.peer_rooms = {}  # peer addr -> Room
        self.transport = None
        self.started = time.time()

    def connection_made(self, transport):
        self.transport = transport
        print(f"hmrelay: listening on UDP {PORT}", flush=True)

    def send(self, addr, ptype, body=b""):
        self.transport.sendto(MAGIC + bytes([ptype]) + body, addr)

    def new_code(self):
        while True:
            code = "".join(secrets.choice(CODE_CHARS) for _ in range(4))
            if code not in self.rooms:
                return code

    def datagram_received(self, data, addr):
        # Health check for Server Manager.
        if data == b"HMR1?":
            self.transport.sendto(b"HMR1!", addr)
            return
        if len(data) < 5 or data[:4] != MAGIC:
            return
        ptype = data[4]
        try:
            if ptype == DATA:
                self.on_data(data, addr)
            elif ptype == CREATE:
                self.on_create(data, addr)
            elif ptype == JOIN:
                self.on_join(data, addr)
            elif ptype == PING:
                self.touch(addr)
        except (IndexError, struct.error, UnicodeDecodeError):
            pass

    def touch(self, addr):
        room = self.host_rooms.get(addr)
        if room:
            room.host_seen = time.time()
            return room, 0
        room = self.peer_rooms.get(addr)
        if room:
            pid = room.by_addr.get(addr)
            if pid in room.peers:
                room.peers[pid]["seen"] = time.time()
                return room, pid
        return None, None

    def on_create(self, data, addr):
        name, _ = rstr(data, 5)
        room = self.host_rooms.get(addr)
        if room is None:
            if len(self.rooms) >= MAX_ROOMS:
                self.send(addr, ERROR, pstr("the relay is full"))
                return
            room = Room(self.new_code(), addr, clean_name(name))
            self.rooms[room.code] = room
            self.host_rooms[addr] = room
            print(f"hmrelay: room {room.code} opened by {room.host_name} {addr[0]}", flush=True)
        room.host_seen = time.time()
        self.send(addr, CREATED, pstr(room.code))

    def on_join(self, data, addr):
        code, pos = rstr(data, 5)
        name, _ = rstr(data, pos)
        room = self.rooms.get(code.strip().upper())
        if room is None:
            self.send(addr, ERROR, pstr("no room with that code"))
            return
        pid = room.by_addr.get(addr)
        if pid is None:
            if len(room.peers) >= MAX_PEERS:
                self.send(addr, ERROR, pstr("the room is full"))
                return
            pid = room.next_id
            room.next_id += 1
            room.peers[pid] = {"addr": addr, "name": clean_name(name), "seen": time.time(), "since": time.time()}
            room.by_addr[addr] = pid
            self.peer_rooms[addr] = room
            print(f"hmrelay: {room.peers[pid]['name']} joined {room.code} as {pid}", flush=True)
        room.peers[pid]["seen"] = time.time()
        self.send(addr, JOINED, struct.pack("<H", pid))

    def on_data(self, data, addr):
        room, pid = self.touch(addr)
        if room is None:
            return
        room.bytes += len(data)
        if pid == 0:
            target = struct.unpack_from("<H", data, 5)[0]
            peer = room.peers.get(target)
            if peer:
                self.send(peer["addr"], DATA, data[7:])
        else:
            self.send(room.host, DATA, struct.pack("<H", pid) + data[5:])

    def close_room(self, room, why):
        for peer in room.peers.values():
            self.peer_rooms.pop(peer["addr"], None)
            self.send(peer["addr"], ERROR, pstr(f"room closed ({why})"))
        self.host_rooms.pop(room.host, None)
        self.rooms.pop(room.code, None)
        print(f"hmrelay: room {room.code} closed ({why})", flush=True)

    def sweep(self):
        now = time.time()
        close_dir = os.path.join(DATA_DIR, "close")
        if os.path.isdir(close_dir):
            for code in os.listdir(close_dir):
                path = os.path.join(close_dir, code)
                try:
                    os.remove(path)
                except OSError:
                    pass
                room = self.rooms.get(code.upper())
                if room:
                    self.close_room(room, "closed by the server admin")
        for room in list(self.rooms.values()):
            if now - room.host_seen > HOST_TIMEOUT:
                self.close_room(room, "host left")
                continue
            for pid, peer in list(room.peers.items()):
                if now - peer["seen"] > PEER_TIMEOUT:
                    room.peers.pop(pid)
                    room.by_addr.pop(peer["addr"], None)
                    self.peer_rooms.pop(peer["addr"], None)
                    self.send(room.host, LEFT, struct.pack("<H", pid))
                    print(f"hmrelay: {peer['name']} left {room.code}", flush=True)

    def status(self):
        now = time.time()
        return {
            "version": 1,
            "updated": int(now),
            "uptime": int(now - self.started),
            "port": PORT,
            "rooms": [
                {
                    "code": r.code,
                    "host": r.host_name,
                    "since": int(r.created),
                    "bytes": r.bytes,
                    "players": [{"id": 0, "name": r.host_name, "role": "host", "since": int(r.created)}]
                    + [{"id": pid, "name": p["name"], "role": "player", "since": int(p["since"])} for pid, p in r.peers.items()],
                }
                for r in self.rooms.values()
            ],
        }


def write_status(relay):
    os.makedirs(DATA_DIR, exist_ok=True)
    path = os.path.join(DATA_DIR, "status.json")
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        json.dump(relay.status(), f, indent=1)
    os.replace(tmp, path)


async def main():
    os.makedirs(os.path.join(DATA_DIR, "close"), exist_ok=True)
    loop = asyncio.get_running_loop()
    relay = Relay()
    await loop.create_datagram_endpoint(lambda: relay, local_addr=("0.0.0.0", PORT))
    while True:
        await asyncio.sleep(2)
        relay.sweep()
        try:
            write_status(relay)
        except OSError as e:
            print(f"hmrelay: could not write status: {e}", file=sys.stderr, flush=True)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
