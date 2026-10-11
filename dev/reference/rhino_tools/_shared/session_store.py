"""
session_store - conversation-scoped state, on disk, outside the document.

Why this exists
---------------
Object tags ('#12') and named planes ('A1') used to live in
``sc.sticky``. Sticky is scoped to the RHINO PROCESS, not to a
conversation, and the listener runs inside Rhino - so a second chat
against the same open Rhino silently resolved the FIRST chat's tags.
'#12' from a transcript that built a car park would quietly answer a
question about a house, and nothing anywhere said so.

The document is the wrong place for them too: a tag names a thing for
the length of one conversation, and nobody wants '#12' baked into a
.3dm they send to an engineer.

So state lives in a temp directory, one folder per conversation:

    %TEMP%/nextra_sessions/<session_id>/state.json    the tags
    %TEMP%/nextra_sessions/<session_id>/*.png         captures

``begin()`` swaps to a conversation and ``end()`` deletes its folder.
Folders older than a day are swept on every ``begin``, so a crashed run
does not leave state behind for the next one to trip over.

Sticky is still the hot cache - reading a bucket does not touch the disk.
It is keyed by session id, so a stale cache from another conversation is
detected and dropped rather than served.

Buckets
-------
    named_planes   str -> plane dict
    tag_by_guid    str(guid) -> int
    guid_by_tag    int -> System.Guid

The ``tag_counter`` is MONOTONIC within a session and is never rewound,
not even by a clear. A number that has been handed out is spent: a stale
'#12' in a transcript must resolve to nothing rather than to whatever
later took the slot.
"""

import json
import os
import shutil
import tempfile
import time

import System
import scriptcontext as sc


# sticky keys - the cache, not the truth
_ACTIVE = "rg_session_id"       # which conversation sticky currently holds
_CACHE = "rg_session_cache"     # the loaded state dict
_DIRTY = "rg_session_dirty"     # is the cache ahead of the disk

DEFAULT_SESSION = "default"
STATE_FILE = "state.json"
ROOT_NAME = "nextra_sessions"

# How long an abandoned session folder survives before begin() sweeps it.
MAX_AGE_SECONDS = 24 * 60 * 60

_BUCKETS = ("named_planes", "tag_by_guid", "guid_by_tag")
_COUNTERS = ("tag_counter",)

# Buckets whose keys are integers. JSON has no integer keys, so these are
# written as strings and converted back on load.
_INT_KEYED = ("guid_by_tag",)

# Buckets whose values are Guids, written as strings.
_GUID_VALUED = ("guid_by_tag",)


# ---------- Paths ----------

def root_dir():
    """The directory holding every session folder."""
    return os.path.join(tempfile.gettempdir(), ROOT_NAME)


def session_dir(session_id=None):
    """This conversation's folder. Captures belong here too, so that
    ``end`` takes the images away with the handles."""
    sid = session_id or current()
    return os.path.join(root_dir(), _safe(sid))


def state_path(session_id=None):
    return os.path.join(session_dir(session_id), STATE_FILE)


def _safe(session_id):
    """A session id becomes a directory name, so keep it to characters
    that cannot escape the root or upset Windows."""
    text = str(session_id or DEFAULT_SESSION)
    keep = [c if (c.isalnum() or c in "-_.") else "-" for c in text]
    cleaned = "".join(keep).strip("-.") or DEFAULT_SESSION
    return cleaned[:80]


def ensure_dir(session_id=None):
    """Make this session's folder, returning its path."""
    path = session_dir(session_id)
    if not os.path.isdir(path):
        try:
            os.makedirs(path)
        except OSError:
            # A parallel call won the race; that is fine.
            if not os.path.isdir(path):
                raise
    return path


# ---------- Serialisation ----------

def _empty_state():
    state = {}
    for name in _BUCKETS:
        state[name] = {}
    for name in _COUNTERS:
        state[name] = 0
    return state


def _to_guid(text):
    try:
        return System.Guid(str(text))
    except Exception:
        return None


def _encode(state):
    """In-memory state -> JSON-safe dict."""
    out = {}
    for name in _BUCKETS:
        bucket = state.get(name) or {}
        encoded = {}
        for key, value in bucket.items():
            if name in _GUID_VALUED:
                value = str(value)
            encoded[str(key)] = value
        out[name] = encoded
    for name in _COUNTERS:
        out[name] = int(state.get(name, 0))
    return out


def _decode(data):
    """JSON dict -> in-memory state, with int keys and Guids restored.

    Anything unreadable is dropped rather than raising: a corrupt session
    file should cost the handles, not the run."""
    state = _empty_state()
    if not isinstance(data, dict):
        return state

    for name in _BUCKETS:
        raw = data.get(name)
        if not isinstance(raw, dict):
            continue
        bucket = state[name]
        for key, value in raw.items():
            if name in _INT_KEYED:
                try:
                    key = int(key)
                except (TypeError, ValueError):
                    continue
            if name in _GUID_VALUED:
                value = _to_guid(value)
                if value is None:
                    continue
            bucket[key] = value

    for name in _COUNTERS:
        try:
            state[name] = int(data.get(name, 0))
        except (TypeError, ValueError):
            state[name] = 0
    return state


# ---------- The cache ----------

def current():
    """Which conversation is active. Never None."""
    return sc.sticky.get(_ACTIVE) or DEFAULT_SESSION


def _state():
    """The live state dict for the active session, loading it if the
    cache belongs to a different conversation or is not there yet."""
    sid = current()
    cached = sc.sticky.get(_CACHE)
    if cached is not None and cached.get("__session__") == sid:
        return cached

    state = _load(sid)
    state["__session__"] = sid
    sc.sticky[_CACHE] = state
    sc.sticky[_DIRTY] = False
    return state


def _load(session_id):
    path = state_path(session_id)
    if not os.path.isfile(path):
        return _empty_state()
    try:
        handle = open(path, "r")
        try:
            data = json.load(handle)
        finally:
            handle.close()
    except (IOError, OSError, ValueError):
        return _empty_state()
    return _decode(data)


def mark_dirty():
    """Say the cache is ahead of the disk. Mutators call this; the
    listener flushes once per dispatch rather than on every write."""
    sc.sticky[_DIRTY] = True


def flush(force=False):
    """Write the cache to disk if anything changed. True if it wrote."""
    if not force and not sc.sticky.get(_DIRTY, False):
        return False

    state = sc.sticky.get(_CACHE)
    if state is None:
        sc.sticky[_DIRTY] = False
        return False

    sid = state.get("__session__") or current()
    try:
        ensure_dir(sid)
        path = state_path(sid)
        # Write beside, then replace: a half-written state.json read by
        # the next call is worse than no state.json at all.
        temp = path + ".tmp"
        handle = open(temp, "w")
        try:
            json.dump(_encode(state), handle, indent=1)
        finally:
            handle.close()
        if os.path.isfile(path):
            os.remove(path)
        os.rename(temp, path)
    except (IOError, OSError):
        return False

    sc.sticky[_DIRTY] = False
    return True


# ---------- Lifecycle ----------

def begin(session_id, sweep=True):
    """Make this conversation the active one.

    Flushes whatever the cache was holding first, so switching away from
    a session does not lose its handles. Returns the session id actually
    used."""
    sid = _safe(session_id)
    if current() != sid:
        flush()
        sc.sticky.pop(_CACHE, None)
        sc.sticky[_DIRTY] = False
    sc.sticky[_ACTIVE] = sid

    if sweep:
        sweep_old()

    ensure_dir(sid)
    _state()
    return sid


def end(session_id=None):
    """Delete this conversation's folder - handles, captures and all.

    Returns True if a folder was removed. The active session falls back
    to ``default`` so a later call cannot write into the folder that was
    just taken away."""
    sid = _safe(session_id or current())
    path = session_dir(sid)

    if current() == sid:
        sc.sticky.pop(_CACHE, None)
        sc.sticky[_DIRTY] = False
        sc.sticky[_ACTIVE] = DEFAULT_SESSION

    if not os.path.isdir(path):
        return False
    try:
        shutil.rmtree(path, True)
    except (IOError, OSError):
        return False
    return not os.path.isdir(path)


def sweep_old(max_age_seconds=MAX_AGE_SECONDS):
    """Remove session folders nobody ended. Returns how many went.

    A run that crashes leaves its folder behind; without this the temp
    directory grows for ever and a recycled session id could inherit
    handles from a build that failed a week ago."""
    root = root_dir()
    if not os.path.isdir(root):
        return 0

    now = time.time()
    active = current()
    removed = 0
    try:
        names = os.listdir(root)
    except (IOError, OSError):
        return 0

    for name in names:
        if name == active:
            continue
        path = os.path.join(root, name)
        if not os.path.isdir(path):
            continue
        try:
            age = now - os.path.getmtime(path)
        except (IOError, OSError):
            continue
        if age < max_age_seconds:
            continue
        try:
            shutil.rmtree(path, True)
            removed += 1
        except (IOError, OSError):
            continue
    return removed


def sessions():
    """Every session folder on disk, newest first. For diagnostics."""
    root = root_dir()
    if not os.path.isdir(root):
        return []
    out = []
    try:
        names = os.listdir(root)
    except (IOError, OSError):
        return []
    for name in names:
        path = os.path.join(root, name)
        if not os.path.isdir(path):
            continue
        try:
            stamp = os.path.getmtime(path)
        except (IOError, OSError):
            stamp = 0.0
        out.append({"id": name, "path": path, "modified": stamp,
                    "active": name == current()})
    out.sort(key=lambda row: row["modified"], reverse=True)
    return out


# ---------- Buckets and counters ----------

def bucket(name):
    """The live dict for one bucket. Mutate it, then ``mark_dirty()``."""
    state = _state()
    if name not in state or not isinstance(state[name], dict):
        state[name] = {}
    return state[name]


def clear_bucket(name):
    """Empty one bucket. Returns how many entries went.

    Counters are deliberately NOT reset - see the module docstring."""
    state = _state()
    count = len(state.get(name) or {})
    state[name] = {}
    mark_dirty()
    return count


def counter(name):
    state = _state()
    try:
        return int(state.get(name, 0))
    except (TypeError, ValueError):
        return 0


def bump(name):
    """Next number from a monotonic counter."""
    state = _state()
    value = counter(name) + 1
    state[name] = value
    mark_dirty()
    return value


def raise_counter(name, floor):
    """Move a counter UP to at least ``floor``. Never down.

    Used to start a conversation's numbering above what the document
    already carries, so a fresh chat on a saved file cannot mint a tag
    that a persisted one already answers to."""
    state = _state()
    try:
        floor = int(floor)
    except (TypeError, ValueError):
        return counter(name)
    if floor <= counter(name):
        return counter(name)
    state[name] = floor
    mark_dirty()
    return floor


def summary():
    """What this session is holding, for a receipt or a status line."""
    state = _state()
    return {
        "session": current(),
        "path": session_dir(),
        "named_planes": len(state.get("named_planes") or {}),
        "tags": len(state.get("tag_by_guid") or {}),
        "tag_counter": counter("tag_counter"),
    }
