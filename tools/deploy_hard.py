#!/usr/bin/env python3
"""Hard deploy: build + upload + dathost stop+start + tail log.
**Disconnects all players** during the ~30s downtime window.

Use when:
- Plugin runtime state needs a clean reset (Superhero session, retake AWP
  queue, mode manager, etc.)
- Changes touch plugin lifecycle / event subscriptions that hot reload
  cannot safely re-wire
- After many hot deploys (CSS unload/load leaks AssemblyLoadContexts —
  long-running servers benefit from a periodic full restart)

For incremental code-only changes that don't break in-flight state, prefer
deploy_hot.py (no disconnect).
"""
import subprocess, sys, ftplib, time, os, pathlib, socket, struct
import urllib.request, urllib.error, base64, json

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from _env import require, load_env

FTP_HOST   = require('FTP_HOST')
FTP_USER   = require('FTP_USER')
FTP_PASS   = require('FTP_PASS')
RCON_HOST  = require('RCON_HOST')
RCON_PORT  = int(require('RCON_PORT'))
RCON_PASS  = require('RCON_PASS')

# Optional dathost API creds (from .env) — enables a clean stop/start restart.
# Leave them blank to fall back to a soft css_plugins reload.
load_env()
_email, _password, _server_id = (os.environ.get('DATHOST_EMAIL'),
                                 os.environ.get('DATHOST_PASSWORD'),
                                 os.environ.get('DATHOST_SERVER_ID'))
DATHOST_API = (_email, _password, _server_id) if (_email and _password and _server_id) else None
if DATHOST_API is None:
    print('  (no dathost API creds in .env) — using RCON reload fallback')
REMOTE_DLL     = '/addons/counterstrikesharp/plugins/CS2Ultimod/CS2Ultimod.dll'
REMOTE_DEPS    = '/addons/counterstrikesharp/plugins/CS2Ultimod/CS2Ultimod.deps.json'
REMOTE_CONFIGS = '/addons/configs'
REMOTE_LOG     = '/addons/counterstrikesharp/logs/'
LOCAL_DLL      = pathlib.Path(__file__).parent.parent / 'src/bin/Release/net8.0/CS2Ultimod.dll'
LOCAL_DEPS     = pathlib.Path(__file__).parent.parent / 'src/bin/Release/net8.0/CS2Ultimod.deps.json'
LOCAL_CONFIGS  = pathlib.Path(__file__).parent.parent / 'configs'
PROJECT        = pathlib.Path(__file__).parent.parent / 'src/CS2Ultimod.csproj'

def rcon(command):
    """Send a single RCON command via Source RCON protocol."""
    def _packet(pid, ptype, body):
        body_enc = body.encode('utf-8') + b'\x00\x00'
        size = 4 + 4 + len(body_enc)
        return struct.pack('<iii', size, pid, ptype) + body_enc

    try:
        s = socket.create_connection((RCON_HOST, RCON_PORT), timeout=10)
        s.sendall(_packet(1, 3, RCON_PASS))   # SERVERDATA_AUTH
        s.recv(4096)                            # auth response
        s.sendall(_packet(2, 2, command))       # SERVERDATA_EXECCOMMAND
        time.sleep(0.5)
        resp = s.recv(4096)
        s.close()
        # body starts at byte 12, strip trailing null bytes
        body = resp[12:].rstrip(b'\x00').decode('utf-8', errors='replace')
        print(f'  RCON: {body.strip() or "(ok)"}')
        return True
    except Exception as e:
        print(f'  RCON failed: {e}')
        return False

def build():
    print('Building...')
    r = subprocess.run(['dotnet', 'build', str(PROJECT), '-c', 'Release'], capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-2000:])
        sys.exit(1)
    print('Build OK')

def ftp_makedirs(ftp, remote_path):
    parts = [p for p in remote_path.split('/') if p]
    current = ''
    for part in parts:
        current += '/' + part
        try:
            ftp.mkd(current)
        except ftplib.error_perm:
            pass  # already exists

def upload():
    print('Uploading DLL + configs...')
    ftp = ftplib.FTP()
    ftp.connect(FTP_HOST, 21, timeout=15)
    ftp.login(FTP_USER, FTP_PASS)
    ftp.set_pasv(True)

    # Upload configs FIRST so they're present when DLL reload fires
    # Upload configs/ recursively → /addons/configs/
    count = 0
    for local_file in sorted(LOCAL_CONFIGS.rglob('*')):
        if local_file.is_file() and local_file.suffix not in ('.gitkeep',):
            rel = local_file.relative_to(LOCAL_CONFIGS)
            remote_file = REMOTE_CONFIGS + '/' + '/'.join(rel.parts)
            remote_dir  = REMOTE_CONFIGS + '/' + '/'.join(rel.parts[:-1]) if len(rel.parts) > 1 else REMOTE_CONFIGS
            ftp_makedirs(ftp, remote_dir)
            with open(local_file, 'rb') as f:
                ftp.storbinary('STOR ' + remote_file, f)
            count += 1
    print(f'  {count} config file(s) uploaded')

    # .deps.json drives CSS's native-asset resolution (e.g. runtimes/linux-x64/
    # native/libe_sqlite3.so). Without it SQLite fails to load → plugin self-
    # unloads. MUST ship alongside the DLL. Upload before the DLL so it's in
    # place when the reload fires.
    with open(LOCAL_DEPS, 'rb') as f:
        ftp.storbinary('STOR ' + REMOTE_DEPS, f)
    print('  deps.json uploaded')

    # Upload DLL last so CSS reload fires after configs + deps are in place
    with open(LOCAL_DLL, 'rb') as f:
        ftp.storbinary('STOR ' + REMOTE_DLL, f)
    print('  DLL uploaded')

    ftp.quit()
    print('Upload done')

def reload_plugin():
    """Soft reload — leaks AssemblyLoadContexts. Kept as fallback."""
    print('Reloading plugin via RCON (soft, leaks ALCs)...')
    rcon('css_plugins unload CS2Ultimod')
    time.sleep(1)
    rcon('css_plugins load CS2Ultimod')

def _dathost_request(method, path):
    email, password, server_id = DATHOST_API
    url = f'https://dathost.net/api/0.1/game-servers/{server_id}{path}'
    auth = base64.b64encode(f'{email}:{password}'.encode()).decode()
    req = urllib.request.Request(url, method=method,
                                  headers={'Authorization': f'Basic {auth}'})
    with urllib.request.urlopen(req, timeout=20) as resp:
        return resp.status, resp.read().decode('utf-8', errors='replace')

def _wait_rcon(timeout=120):
    """Poll RCON until the server accepts auth (or timeout)."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            s = socket.create_connection((RCON_HOST, RCON_PORT), timeout=4)
            s.close()
            return True
        except Exception:
            time.sleep(3)
    return False

def restart_server():
    """Hard restart via dathost API: stop → start. Clean ALC, no leak."""
    if DATHOST_API is None:
        print('No dathost API creds — falling back to plugin reload.')
        reload_plugin()
        return
    print('Stopping server (dathost API)...')
    try:
        # /stop: graceful shutdown of the cs2 process
        status, body = _dathost_request('POST', '/stop')
        print(f'  stop: HTTP {status}')
    except urllib.error.HTTPError as e:
        # 409 if already stopped — fine
        print(f'  stop: HTTP {e.code} ({e.reason})')
    except Exception as e:
        print(f'  stop failed: {e} — aborting; click Restart in panel.')
        return

    time.sleep(3)

    print('Starting server (dathost API)...')
    try:
        status, body = _dathost_request('POST', '/start')
        print(f'  start: HTTP {status}')
    except urllib.error.HTTPError as e:
        print(f'  start FAILED: HTTP {e.code} {e.reason} — click Start in panel.')
        return
    except Exception as e:
        print(f'  start failed: {e} — click Start in panel.')
        return

    print('Waiting for RCON to come back online...')
    if _wait_rcon(180):
        print('  RCON online — server up.')
    else:
        print('  RCON still down after 3 min — check panel.')

def _logfile():
    import datetime
    today = datetime.date.today().strftime('%Y%m%d')
    return f'/addons/counterstrikesharp/logs/log-CS2Ultimod{today}.txt'

def snapshot_log():
    ftp = ftplib.FTP()
    ftp.connect(FTP_HOST, 21, timeout=15)
    ftp.login(FTP_USER, FTP_PASS)
    ftp.set_pasv(True)
    try:
        buf = []
        ftp.retrbinary('RETR ' + _logfile(), buf.append)
        seen = set(b''.join(buf).decode('utf-8', errors='replace').splitlines())
    except Exception:
        seen = set()
    ftp.quit()
    return seen

def tail_log(seconds=15, seen=None):
    if seen is None: seen = set()
    print(f'Watching log for {seconds}s...')
    ftp = ftplib.FTP()
    ftp.connect(FTP_HOST, 21, timeout=15)
    ftp.login(FTP_USER, FTP_PASS)
    ftp.set_pasv(True)
    deadline = time.time() + seconds
    while time.time() < deadline:
        time.sleep(2)
        try:
            buf = []
            ftp.retrbinary('RETR ' + _logfile(), buf.append)
            lines = b''.join(buf).decode('utf-8', errors='replace').splitlines()
            for l in lines:
                if l not in seen:
                    print(l)
                    seen.add(l)
        except Exception:
            pass
    ftp.quit()

if __name__ == '__main__':
    build()
    seen = snapshot_log()   # capture existing lines before deploy
    upload()
    restart_server()
    time.sleep(3)
    tail_log(20, seen)
