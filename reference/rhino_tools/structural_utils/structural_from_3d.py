"""
structural.from_3d - a 3D solid model into an analysis model, in ONE call.

The whole solid-model workflow, in order, with the grid <-> snap loop run
here until it settles - nothing to remember, no step to skip:

    0. clear the last run             every axis / plate / surface an earlier
                                      run made from a solid still here (user
                                      text structural_source) is deleted -
                                      each call starts clean, so a second
                                      call REPLACES the first. Members whose
                                      solid is gone stay (nothing to remake
                                      them from) - counted in a warning
    1. structural.convert(layers)     axes + plate boundaries, contacts mapped
       structural.clean               duplicates out: copied lines, plates on
                                      one plane overlapping (the smaller one
                                      deleted, or trimmed back if it overlaps less)
    2. structural.grid                GRID FIRST - levels at the converted
                                      beam axes (replaces the last run's grid)
    3. structural.snap(lines)         line ends onto it - most joints close here
    4. structural.connect             lines only: the small gaps left, then
                                      by contacts (decide once, then solve)
    5. structural.grid                again - replaces its old grid
       structural.snap(lines)
       structural.snap(plates)        wall / slab boundaries onto it, split
                                      into grid panels
       ... 5 again while the grid changed or points would still snap,
       at most ``max_rounds`` rounds
       structural.clean               again - snapping can land two beams on one axis
    6. structural.intersect           real nodes where members cross
    7. structural.convert(finish)     plate boundaries -> their surfaces
    8. structural.report              the check

The one decision left to the model is ``layers`` - what each layer IS
(line / surface / skip). Without it this returns convert's layer table and
changes nothing. The single tools stay for repairs and odd cases.

Layer B only: it calls the tools and keeps the numbers that matter.
"""

import os
import sys

try:
    _HERE = os.path.dirname(os.path.abspath(__file__))
except NameError:
    _HERE = os.getcwd()
_PARENT = os.path.dirname(os.path.dirname(_HERE))
if _PARENT not in sys.path:
    sys.path.insert(0, _PARENT)

from rhino_tools._shared import coerce, progress, receipts
from rhino_tools.structural_utils import structural_convert
from rhino_tools.structural_utils import structural_grid
from rhino_tools.structural_utils import structural_snap
from rhino_tools.structural_utils import structural_tidy


MAX_ROUNDS = 3


def _value(receipt):
    value = receipt.get("value")
    return value if isinstance(value, dict) else {}


def _brief(receipt, *keys):
    """The receipt's ok / error and the few numbers named in ``keys`` (from the
    receipt or its value)."""
    if not receipt.get("ok"):
        return {"ok": False, "error": receipt.get("error"), "hint": receipt.get("hint")}
    value = _value(receipt)
    out = {"ok": True}
    for key in keys:
        found = receipt.get(key, value.get(key))
        if found not in (None, [], {}):
            out[key] = found
    warnings = receipt.get("warnings") or []
    if warnings:
        out["warnings"] = warnings[:4]
    return out


def _settled(grid_receipt):
    """True when the grid did not change and nothing would snap any more."""
    value = _value(grid_receipt)
    changed = (value.get("changed_from_previous") or {}).get("changed", True)
    return not changed and not value.get("would_snap")


def _clean():
    return _brief(structural_tidy.tool_structural_clean(), "deleted",
                  "lines_and_surfaces_deleted", "plates_deleted", "plates_trimmed",
                  "plates_collapsed", "plates_collapsed_deleted")


def tool_structural_from_3d(layers=None, contacts=False, max_rounds=MAX_ROUNDS, intersect=True,
                            selected=False, parallel=False):
    """Solids -> a joined analysis model on its grid, in one call. See the
    module docstring. ``layers`` = convert's decision per layer; left out, the
    layer table only (nothing deleted). With ``layers``, what earlier runs
    made from the solids is deleted first. ``max_rounds`` caps the snap <->
    grid loop. ``selected=True``: only the solids selected in Rhino are
    converted (and what they made before replaced); the joining steps after
    that still run on the whole analysis model. ``parallel`` goes to convert:
    False (default) one solid at a time, True / a number several at once."""
    ids, error = coerce.only_selected("structural.from_3d", None, selected)
    if error:
        return error
    if layers is None:
        table = structural_convert.tool_structural_convert(ids=ids)
        if table.get("ok"):
            table["hint"] = ("Nothing changed. Decide per layer from this table, then "
                             "structural.from_3d(layers={'<layer>': 'line' | 'surface' | 'skip'}) "
                             "does the whole model (and replaces any earlier run's).")
        return table

    error = structural_convert.read_decisions(layers)[1]
    if error:
        return receipts.err("structural.from_3d", error)
    steps = {}
    # Rhino stays alive: status-bar meter, the step in the prompt, Esc stops
    # between steps (progress). 10 steps + 3 per snap / grid round.
    with progress.Meter("structural.from_3d", 10 + 3 * max(1, int(max_rounds))) as meter:
        try:
            return _build(meter, steps, layers, ids, contacts, max_rounds, intersect, parallel)
        except progress.Stopped as stop:
            return receipts.err("structural.from_3d", "Stopped with Esc at '{}' - the model is "
                                "as that step left it; run again to rebuild.".format(stop),
                                steps=steps)


def _build(meter, steps, layers, ids, contacts, max_rounds, intersect, parallel=False):
    meter.step("clear the last run")
    solids = set(str(g) for g in structural_convert.all_solids())
    chosen = solids & set(ids) if ids else solids
    if ids and not chosen:
        return receipts.err("structural.from_3d", "None of the selected objects is a solid or "
                                                 "closed mesh.")
    removed, stuck = structural_convert.remove_made(chosen)
    orphans = sum(len(c) for p, c in structural_convert.converted_sources().items()
                  if p not in solids)
    steps["clear_previous"] = {"ok": not stuck, "removed": removed}
    if stuck:
        steps["clear_previous"]["could_not_delete"] = stuck[:12]
    meter.step("convert")
    convert = structural_convert.tool_structural_convert(ids=sorted(chosen) if ids else None,
                                                         layers=layers, contacts=contacts,
                                                         parallel=parallel)
    steps["convert"] = _brief(convert, "made", "failed", "skipped", "curved", "contacts")
    if not convert.get("ok") and "Nothing was made" not in str(convert.get("error")):
        return receipts.err("structural.from_3d", "convert failed - nothing else was run.",
                            steps=steps, hint=convert.get("hint"))

    meter.step("clean")
    steps["clean"] = _clean()

    meter.step("grid")
    grid = structural_grid.tool_structural_grid()
    steps["grid"] = _brief(grid, "grid", "levels_merged_by_contact", "would_snap")
    if not grid.get("ok"):
        return receipts.err("structural.from_3d", "grid failed - stopped after convert.",
                            steps=steps, hint=grid.get("hint"))
    meter.step("snap lines")
    steps["snap_lines"] = _brief(structural_snap.tool_structural_snap(members="lines"),
                                 "moved_points", "worst_move", "stays_off_grid")

    meter.step("connect")
    connect = structural_tidy.tool_structural_connect(contacts=contacts)
    steps["connect"] = _brief(connect, "joints", "solve_rounds", "solve_residual",
                              "ends_unmet_before", "ends_unmet_after", "joined_by_contact",
                              "contact_pairs_left", "hosts_split")

    # Each round: grid again, then everything onto it. A round whose grid did
    # not change and had nothing left to snap is the last.
    rounds = []
    for n in range(max(1, int(max_rounds))):
        meter.step("round {}: grid".format(n + 1))
        grid = structural_grid.tool_structural_grid()
        if not grid.get("ok"):
            rounds.append({"round": n + 1, "grid": _brief(grid)})
            break
        meter.step("round {}: snap lines".format(n + 1))
        lines = structural_snap.tool_structural_snap(members="lines")
        meter.step("round {}: snap plates".format(n + 1))
        plates = structural_snap.tool_structural_snap(members="plates")
        rounds.append({
            "round": n + 1,
            "grid": _brief(grid, "changed_from_previous", "would_snap",
                           "levels_merged_by_contact"),
            "lines": _brief(lines, "moved_points", "worst_move", "stays_off_grid"),
            "plates": _brief(plates, "plates_snapped", "plates_left", "panels_added",
                             "plate_notes", "lines_split"),
        })
        if _settled(grid):
            break
    steps["snap_grid_rounds"] = rounds
    settled = grid.get("ok") and _settled(grid)

    meter.step("clean after snap")
    steps["clean_after_snap"] = _clean()

    meter.step("intersect")
    if intersect:
        steps["intersect"] = _brief(structural_tidy.tool_structural_intersect(),
                                    "split_members", "plates_split", "created_count")
    meter.step("finish surfaces")
    finish = structural_convert.tool_structural_convert(finish=True)
    # every plate it could not finish, with why, its size and its solid's layer
    steps["finish"] = _brief(finish, "finished", "failed", "patched", "failures")
    meter.step("report")
    report = structural_tidy.tool_structural_report()
    steps["report"] = _brief(report, "valid", "checks", "advisories", "free_ends",
                             "contacts_not_joined", "plates_left_out")

    warnings = []
    if stuck:
        warnings.append("{} object(s) from the last run could not be deleted (locked?) - "
                        "now doubled: {}".format(len(stuck), ", ".join(stuck[:12])))
    if orphans:
        warnings.append("{} member(s) from an earlier run have no solid any more - kept, not "
                        "rebuilt. Stale? object.delete them.".format(orphans))
    if not settled:
        warnings.append("The grid had not settled after {} round(s) - look at the last round's "
                        "changed_from_previous / would_snap.".format(len(rounds)))
    hint = ("Built. Check it: vision.capture (lines and surfaces only). Report warnings such as "
            "'joints meant but not closed' or contacts_not_joined are to be FIXED with the single "
            "tools (structural.connect / structural.snap, move=[{'level': ...}] for a whole "
            "floor), never waved through.")
    return receipts.r2("from_3d", {"valid": bool(report.get("valid")), "settled": bool(settled),
                                 "rounds": len(rounds), "replaced_previous": removed},
                       warnings=warnings + (report.get("warnings") or [])[:6], hint=hint,
                       steps=steps)


# ---------- Rhino execution wrapper ----------

def run_structural_from_3d():
    return receipts.echo(tool_structural_from_3d())


if __name__ == "__main__":
    run_structural_from_3d()
