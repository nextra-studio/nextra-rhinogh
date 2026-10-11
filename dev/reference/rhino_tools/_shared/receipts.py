"""
Structured receipts - R1 to R6 from the tool plan.

Every tool in this package returns a dict, never a bare boolean and
never a bare "success" string. The point is that the caller (usually a
language model) can verify what happened without taking another
screenshot: it gets back the ids, the layer, the bounding box, the plane
that was actually used, and any warnings the tool wants to raise.

The six shapes:

    R1  object receipt      creation / transform / edit
    R2  value receipt       measure / query / analyse
    R3  vision receipt      screenshot / capture
    R4  validation receipt  is_solid / naked_edges / ...
    R5  list receipt        selection / enumeration
    R6  session receipt     document state

Ids are always emitted as short '#12' tags rather than GUIDs - see
``tags`` for why. ``err`` builds the one failure shape shared by all six,
so a caller can branch on ``ok`` before caring which schema it asked for.

Every shape takes ``**extra`` for the op-specific evidence behind its
answer - the gap that failed a check, the views a capture wrote, how
many things were examined. R2 and R5 always did; the others did not, so
tools that needed an extra field built the receipt and then mutated the
dict, and two of them ended up passing a keyword the function silently
could not accept. It is a runtime TypeError that no static check can
see, so the fix is to let all six take it.
"""

from . import coerce
from . import planes
from . import tags as tagmod


def _tag_list(ids, create=True):
    """Ids in, '#12' tags out. Tolerates None and single ids."""
    if ids is None:
        return []
    return tagmod.tags_of(ids, create=create)


def _clean(data):
    """Drop keys whose value is None so receipts stay readable.

    Empty lists are KEPT - "warnings": [] is meaningful information,
    whereas "plane": null is just noise."""
    return {k: v for k, v in data.items() if v is not None}


# ---------- R1: object receipt ----------

def r1(ids, operation, obj_type=None, value=None, layer=None,
       plane=None, bbox=None, warnings=None, hint=None, **extra):
    """Creation / transform / edit.

    ``value`` carries whatever the operation was parameterised by - the
    move vector, the extrusion distance, the rotation angle - so the call
    can be replayed or reversed from the receipt alone.

    ``obj_type`` and ``layer`` are auto-derived from the first id when
    not supplied, which keeps ~50 call sites from repeating themselves.
    """
    guids = coerce.to_guids(ids)

    if obj_type is None and guids:
        obj_type = coerce.object_type_name(guids[0])
    if layer is None and guids:
        layer = coerce.layer_name_of(guids[0])
    if bbox is None and guids:
        bbox = coerce.bbox_dict(coerce.bbox_of(guids))

    plane_data = None
    if plane is not None:
        plane_data = plane if isinstance(plane, dict) else planes.plane_to_dict(plane)

    data = {
        "ok": True,
        "ids": _tag_list(guids),
        "type": obj_type,
        "operation": operation,
        "value": value,
        "layer": layer,
        "plane": plane_data,
        "bbox": bbox,
        "warnings": list(warnings or []),
        "hint": hint,
    }
    data.update(extra)
    return _clean(data)


# ---------- R2: value receipt ----------

def r2(op, value, unit=None, ids=None, warnings=None, hint=None, **extra):
    """Measure / query / analyse.

    ``**extra`` lets a query attach op-specific fields (a parameter, a
    centroid, a domain) without needing a seventh schema."""
    data = {
        "ok": True,
        "op": op,
        "value": value,
        "unit": unit,
        "ids": _tag_list(ids) if ids is not None else None,
        "warnings": list(warnings or []),
        "hint": hint,
    }
    data.update(extra)
    return _clean(data)


# ---------- R3: vision receipt ----------

def r3(image_ref, viewport=None, preview=None, level=0,
       focus_ids=None, focus_mode=None, warnings=None, tag_table=None,
       hint=None, **extra):
    """Screenshot / capture.

    The sidecar ``tag_table`` is the part that matters: it is what lets
    the model read facts off the capture instead of guessing at pixels."""
    focus = None
    if focus_ids is not None or focus_mode is not None:
        focus = _clean({
            "ids": _tag_list(focus_ids) if focus_ids is not None else [],
            "mode": focus_mode,
        })

    data = {
        "ok": True,
        "image_ref": image_ref,
        "viewport": viewport,
        "preview": preview,
        "level": level,
        "focus": focus,
        "tag_table": tag_table,
        "warnings": list(warnings or []),
        "hint": hint,
    }
    data.update(extra)
    return _clean(data)


# ---------- R4: validation receipt ----------

def r4(checks, ids=None, valid=None, warnings=None, hint=None, **extra):
    """Validation.

    ``valid`` defaults to "every boolean check passed", so a caller can
    read one field instead of interpreting the whole check map. Numeric
    checks (naked_edges: 3) are excluded from that roll-up - a count is
    not a pass/fail on its own, and the tool that produced it decides.

    ``**extra`` carries the op-specific evidence behind the verdict - the
    gap that failed a connection check, how many were examined, the list
    of what is broken. R2 and R5 have always taken it; R4 not taking it
    meant every validation tool had to build the receipt and then mutate
    it, which is how one of them ended up passing a field this function
    silently could not accept."""
    if valid is None:
        bools = [v for v in checks.values() if isinstance(v, bool)]
        valid = all(bools) if bools else True

    data = {
        "ok": True,
        "valid": bool(valid),
        "checks": checks,
        "ids": _tag_list(ids) if ids is not None else None,
        "warnings": list(warnings or []),
        "hint": hint,
    }
    data.update(extra)
    return _clean(data)


# ---------- R5: list / selection receipt ----------

def r5(ids, warnings=None, hint=None, **extra):
    """Selection / enumeration. ``count`` is derived, never passed in, so
    it can never disagree with the id list beside it."""
    tag_ids = _tag_list(ids)
    data = {
        "ok": True,
        "count": len(tag_ids),
        "ids": tag_ids,
        "warnings": list(warnings or []),
        "hint": hint,
    }
    data.update(extra)
    return _clean(data)


# ---------- R6: session / context receipt ----------

def r6(units, tolerance, active_layer, active_viewport, selection=None,
       census=None, warnings=None, hint=None, **extra):
    """Document state. The cheap L0 rung of the vision ladder."""
    data = {
        "ok": True,
        "units": units,
        "tolerance": tolerance,
        "active_layer": active_layer,
        "active_viewport": active_viewport or [],
        "selection": _tag_list(selection) if selection is not None else [],
        "census": census or {},
        "warnings": list(warnings or []),
        "hint": hint,
    }
    data.update(extra)
    return _clean(data)


# ---------- Failure ----------

def err(operation, message, ids=None, hint=None, missing=None, **extra):
    """The single failure shape.

    Tools return this instead of raising, because an exception crossing
    the MCP bridge loses the context that makes it actionable. A caller
    checks ``ok`` first and only then reads the schema-specific fields.

    ``missing`` takes the ids the tool was GIVEN, when the reason it is
    failing is that none of them resolved. Any of them that named an
    object which has since been deleted is named in the message - see
    ``tags.explain_missing``. It is the difference between "you passed
    the wrong kind of thing", which sends a model rewriting the call,
    and "that object is gone", which sends it back to rebuild.
    """
    if missing is not None:
        from . import tags as _tags
        try:
            message = message + _tags.explain_missing(missing)
        except Exception:
            pass

    data = {
        "ok": False,
        "operation": operation,
        "error": message,
        "ids": _tag_list(ids, create=False) if ids is not None else [],
        "warnings": [],
        "hint": hint,
    }
    data.update(extra)
    return _clean(data)


# ---------- Console rendering ----------

def to_text(receipt, indent=2):
    """Pretty-print a receipt for the Rhino command line.

    The ``run_*`` wrappers use this so a human running a tool by hand
    sees the same facts the MCP client would receive, rather than a
    silent redraw."""
    import json
    try:
        return json.dumps(receipt, indent=indent, default=str)
    except Exception:
        return str(receipt)


def echo(receipt):
    """Print a receipt and hand it straight back.

    Written as a pass-through so the Rhino wrappers can end with
    ``return receipts.echo(result)`` on one line."""
    print(to_text(receipt))
    return receipt
