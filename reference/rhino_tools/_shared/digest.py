"""
Large results - what to send back when the honest answer is enormous.

The problem
-----------
``point.query.get_coordinates`` on a 4,000-point cloud returns 4,000
coordinate triples. That is roughly 90,000 characters, about 25,000
tokens, and it crosses the socket, lands in a receipt, and - if the
caller is a language model - fills a context window with numbers nobody
asked to read. The same shape of failure sits in every sub-object query:
the vertices of a filleted solid, the control points of a rebuilt curve,
the face list of a Brep after a boolean.

Nobody wants four thousand coordinates. What they want is one of:

  - a specific one ("where is #12?")               -> narrow by id
  - a specific few ("faces 3 and 7")               -> narrow by index
  - a shape of the whole ("how big is this?")      -> a digest
  - genuinely all of them, in chunks               -> offset and limit

The rule
--------
Never truncate silently. A list that has been cut says so, says how many
there were, and says exactly how to get the rest. A caller that receives
40 of 4,000 and is not told is worse off than one that receives all
4,000 - it will do arithmetic on a tenth of the data and believe it.

So ``cap`` returns the first slice plus a ``_more`` block naming the
total and the call that continues it. And where the values are points,
``extent`` adds the thing a reader usually wanted anyway: the bounding
box and the count, which answers "how big and how many" in twenty
tokens instead of twenty-five thousand.
"""


# Above this many entries, a collection is capped rather than sent whole.
#
# 40 is chosen against what a caller does with the result. One to a few
# is the common case and is untouched. Forty is enough to see the shape
# of a list, spot an outlier, and confirm an order. Past that, a reader
# is skimming and a digest serves them better than the rows.
DEFAULT_LIMIT = 40


def cap(values, limit=DEFAULT_LIMIT, offset=0, tool=None, hint=None):
    """Slice a large collection and say what was left out.

    Works on a list or a dict. Returns ``(kept, more)`` where ``more``
    is None when nothing was cut - so the common small case adds no
    noise to the receipt at all.

    ``tool`` names the call to repeat with a new offset, which is what
    turns "we cut this" into something a caller can act on."""
    if values is None:
        return None, None

    offset = max(0, int(offset or 0))
    limit = max(1, int(limit or DEFAULT_LIMIT))

    total = len(values)
    if offset == 0 and total <= limit:
        return values, None

    if isinstance(values, dict):
        items = list(values.items())[offset:offset + limit]
        kept = dict(items)
    else:
        kept = list(values)[offset:offset + limit]

    shown = len(kept)
    remaining = max(0, total - offset - shown)

    more = {
        "total": total,
        "shown": shown,
        "offset": offset,
        "remaining": remaining,
    }
    if remaining and tool:
        more["next"] = "{}(offset={}, limit={})".format(
            tool, offset + shown, limit)
    more["note"] = hint or (
        "{} of {} shown. Narrow by ids or indices rather than paging "
        "through all of them - the whole list is rarely what you want."
        .format(shown, total))

    return kept, more


def extent(points):
    """The shape of a point set, instead of the point set.

    Count, bounding box, size and centre. This answers "how big is it
    and how many are there" - which is what most queries against a large
    set are actually asking - for a fixed twenty tokens no matter how
    many points there are.

    Accepts anything shaped like ``[x, y, z]`` or ``{"world": [x,y,z]}``,
    because the coordinate tools emit both."""
    coordinates = []
    for entry in (points or []):
        vector = _as_xyz(entry)
        if vector is not None:
            coordinates.append(vector)

    if not coordinates:
        return None

    lows = [min(c[axis] for c in coordinates) for axis in range(3)]
    highs = [max(c[axis] for c in coordinates) for axis in range(3)]

    return {
        "count": len(coordinates),
        "min": [round(v, 3) for v in lows],
        "max": [round(v, 3) for v in highs],
        "size": [round(highs[a] - lows[a], 3) for a in range(3)],
        "center": [round((highs[a] + lows[a]) / 2.0, 3) for a in range(3)],
    }


def _as_xyz(entry):
    """Pull an [x, y, z] out of whatever shape the caller had."""
    if isinstance(entry, dict):
        for key in ("world", "point", "position", "origin"):
            if key in entry:
                return _as_xyz(entry[key])
        return None
    if isinstance(entry, (list, tuple)) and len(entry) >= 3:
        try:
            return [float(entry[0]), float(entry[1]), float(entry[2])]
        except (TypeError, ValueError):
            return None
    return None


def apply(receipt, key, limit=DEFAULT_LIMIT, offset=0, tool=None,
          summarise_points=True):
    """Cap one field of a finished receipt, in place.

    The way tools use this: build the receipt as normal, then hand it
    here. Keeping it out of the receipt constructors is deliberate -
    a receipt should not silently reshape what a tool decided to return,
    and the tool that knows the field is unbounded is the one that should
    say so.

    ``summarise_points`` adds the bounding box when the values look like
    coordinates, so a capped result still answers the size question that
    the full list would have."""
    if not isinstance(receipt, dict) or key not in receipt:
        return receipt

    values = receipt[key]
    if not isinstance(values, (list, dict)):
        return receipt

    if summarise_points:
        sample = (list(values.values()) if isinstance(values, dict)
                  else values)
        box = extent(sample)
        if box and box["count"] > limit:
            receipt.setdefault("extent", box)

    kept, more = cap(values, limit=limit, offset=offset, tool=tool)
    receipt[key] = kept
    if more:
        receipt["_more"] = more
        receipt.setdefault(
            "hint",
            "Only part of '{}' is shown - see _more. Narrowing by ids or "
            "indices is almost always better than paging.".format(key))
    return receipt
