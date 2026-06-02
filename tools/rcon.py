#!/usr/bin/env python3
"""Usage: python rcon.py "command" [wait_seconds]"""
import socket, struct, time, sys, pathlib

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from _env import require

HOST, PORT, PASSWORD = require('RCON_HOST'), int(require('RCON_PORT')), require('RCON_PASS')

def rcon(command, wait=0.8):
    s = socket.socket()
    s.connect((HOST, PORT))
    s.settimeout(5)

    def send(req_id, req_type, body):
        data = struct.pack('<ii', req_id, req_type) + body.encode('utf-8') + b'\x00\x00'
        s.send(struct.pack('<i', len(data)) + data)

    def recv():
        raw = b''
        while len(raw) < 4:
            raw += s.recv(4096)
        size = struct.unpack('<i', raw[:4])[0]
        data = raw[4:]
        while len(data) < size:
            data += s.recv(4096)
        req_id, req_type = struct.unpack('<ii', data[:8])
        body = data[8:-2].decode('utf-8', errors='replace')
        return req_id, req_type, body

    send(1, 3, PASSWORD)
    time.sleep(0.2)
    recv()
    try: recv()
    except: pass

    send(2, 2, command)
    time.sleep(wait)
    result = ''
    try:
        while True:
            _, _, body = recv()
            result += body
    except: pass
    s.close()
    return result

if __name__ == '__main__':
    cmd = sys.argv[1] if len(sys.argv) > 1 else 'echo ping'
    wait = float(sys.argv[2]) if len(sys.argv) > 2 else 0.8
    print(rcon(cmd, wait))
