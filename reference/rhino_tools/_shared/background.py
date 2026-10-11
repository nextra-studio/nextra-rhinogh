"""
background - heavy GEOMETRY work on a worker thread while Rhino's main thread
stays alive (repaints, a moving indicator in the prompt, Esc).

Only work that does not change the document may run here: an axis, a
mid-surface, an outline - maths on geometry. Adding, moving and deleting
objects stays on the main thread (RhinoCommon's document is not thread
safe), so a tool splits each object into a pure part (``run`` it here) and
a write part (after, on the main thread).

    work = background.run(lambda: axis_of(geometry), meter, timeout=120)

While the worker runs, the main thread pumps Rhino (``meter.pump``): Esc
raises progress.Stopped, and past ``timeout`` seconds ``TimedOut`` is raised
and the object is given up - a worker cannot be killed, it finishes on its
own and its result is dropped, but Rhino is free again.

Layer A helper - no tool logic here.
"""

import threading
import time


class TimedOut(Exception):
    """The work took longer than its time limit - given up."""


POLL = 0.05         # seconds between pumps of Rhino's messages


def run(func, meter=None, timeout=None, detail=""):
    """``func()`` on a worker thread; the main thread pumps ``meter`` until it
    is done. Its exception is raised here. No ``meter``: run in place."""
    if meter is None:
        return func()
    box = {}

    def work():
        try:
            box["value"] = func()
        except BaseException as exc:            # handed to the main thread
            box["error"] = exc

    worker = threading.Thread(target=work, name="nextra-geometry")
    worker.daemon = True
    start = time.time()
    worker.start()
    while True:
        worker.join(POLL)
        if not worker.is_alive():
            break
        elapsed = time.time() - start
        meter.pump("{} {:.0f} s".format(detail, elapsed).strip())
        if timeout and elapsed > timeout:
            raise TimedOut("{:.0f} s".format(elapsed))
    if "error" in box:
        raise box["error"]
    return box.get("value")


def workers_default():
    """How many objects run at once by default: the cores less two (Rhino's
    own UI and the main thread), at most 8 - past that the Python parts take
    turns on the interpreter lock and nothing more is gained."""
    try:
        import System
        cores = int(System.Environment.ProcessorCount)
    except Exception:
        import os
        cores = os.cpu_count() or 2
    return max(1, min(8, cores - 2))


def run_many(jobs, meter=None, timeout=None, workers=None, on_done=None, detail=""):
    """Every ``(key, func)`` in ``jobs`` on worker threads, up to ``workers``
    at once (default ``workers_default``) - RhinoCommon releases Python's
    lock while it computes, so heavy geometry really runs side by side
    (measured x3.5 on 4 threads; pure Python does not gain).

    The calling (main) thread waits, pumps ``meter`` and calls
    ``on_done(key, value, error)`` for each job AS IT FINISHES - on the main
    thread, so it may write the document. ``error`` is the job's exception,
    or ``TimedOut`` past ``timeout`` seconds (the worker is abandoned: it ends
    on its own, its result dropped, and no longer counts against
    ``workers``). Esc (meter.pump) raises progress.Stopped: jobs not started
    are not run, running ones are abandoned; what on_done wrote stays."""
    workers = max(1, int(workers or workers_default()))
    pending = list(jobs)
    pending.reverse()                               # pop() from the end = in order
    running = {}                                    # key -> (thread, box, start)
    finished = threading.Event()
    total, done = len(pending), 0

    def start(key, func):
        box = {}

        def work():
            try:
                box["value"] = func()
            except BaseException as exc:            # handed to the main thread
                box["error"] = exc
            finally:
                finished.set()

        worker = threading.Thread(target=work, name="nextra-geometry")
        worker.daemon = True
        running[key] = (worker, box, time.time())
        worker.start()

    while pending or running:
        while pending and len(running) < workers:
            key, func = pending.pop()
            start(key, func)
        finished.wait(POLL)                         # sleeps: the workers keep the lock
        finished.clear()
        now = time.time()
        for key in list(running):
            worker, box, began = running[key]
            if worker.is_alive():
                if timeout and now - began > timeout:
                    del running[key]
                    done += 1
                    if on_done is not None:
                        on_done(key, None, TimedOut("{:.0f} s".format(now - began)))
                continue
            del running[key]
            done += 1
            if on_done is not None:
                on_done(key, box.get("value"), box.get("error"))
        if meter is not None:
            meter.pump("{} {} running, {}/{} done".format(detail, len(running), done,
                                                         total).strip())
