"""Minimal .env loader (no external deps).

Reads KEY=VALUE lines from the project-root .env file into os.environ.
The .env file is gitignored — copy .env.example to .env and fill it in.
"""
import os
import pathlib

_ENV_PATH = pathlib.Path(__file__).resolve().parent.parent / '.env'


def load_env():
    """Load project-root .env into os.environ (does not override existing vars)."""
    if not _ENV_PATH.exists():
        return
    for line in _ENV_PATH.read_text(encoding='utf-8').splitlines():
        line = line.strip()
        if not line or line.startswith('#') or '=' not in line:
            continue
        key, _, val = line.partition('=')
        os.environ.setdefault(key.strip(), val.strip().strip('"').strip("'"))


def require(key):
    """Return env var `key`, or exit with a helpful message if missing."""
    load_env()
    val = os.environ.get(key)
    if not val:
        raise SystemExit(
            f"Missing {key}. Copy .env.example to .env and fill in your server's values."
        )
    return val
