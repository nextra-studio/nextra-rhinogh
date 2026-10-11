"""
Vision core - display conduits, tagging, isolation and capture.

This is a generalisation of ``Confirmed_tool_in_Rhino/view_add_tag_colour.py``.
That script tagged curves with coloured text dots drawn by a display
conduit, kept the tags alive across edits with document event hooks, and
stripped inherited tags off copies. All of that survives here; what
changed is scope:

  * the conduit draws for ANY geometry type, not only curves
  * it can draw SUB-object tags too - face, edge and vertex labels, which
    is what capture levels L3 and L4 need
  * tag numbers come from ``_shared/tags.py`` rather than a private
    counter, so a tag drawn on screen is the same '#12' every receipt
    reports

The event hooks are the part worth keeping carefully. Without them a
tagged object that gets moved, replaced or copied leaves a conduit
pointing at a dead id, or worse, a copy inherits a tag colour it has no
right to - so the model sees two objects both labelled '#12'.

Nothing in this module prompts the user.
"""

import colorsys
import math

import System.Drawing
import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

from .._shared import coerce
from .._shared import tags as tagmod
from .._shared import redraw


# sticky keys
_CONDUITS = "rg_vision_conduits"        # obj_id -> conduit
_ORIGINAL_ATTRS = "rg_vision_orig_attrs"
_HOOKS = "rg_vision_hooks_attached"
_HIDDEN = "rg_vision_hidden"            # ids hidden by isolate
_GHOSTED = "rg_vision_ghosted"          # obj_id -> its previous display-mode override id (or None)

# The marker written into user text so a stray copy can be recognised.
TAG_MARKER = "RG_TAG_ACTIVE"


# ---------- Colour generation ----------

def generate_color(index):
    """Distinct colour per tag number, via golden-ratio hue stepping.

    Stepping the hue by the golden ratio conjugate spreads consecutive
    numbers as far apart on the colour wheel as possible, so #11 and #12
    never look alike. Saturation and value are fixed so every tag is
    equally readable against both light and dark backgrounds."""
    golden_ratio_conjugate = 0.618033988749895
    hue = (index * golden_ratio_conjugate) % 1.0
    r, g, b = colorsys.hsv_to_rgb(hue, 0.65, 0.95)
    return System.Drawing.Color.FromArgb(int(r * 255), int(g * 255), int(b * 255))


# Edges are drawn from a DIFFERENT part of the hue sequence than faces.
#
# Both used generate_color(index), so edge 0 came out the same red as
# face 0 and edge 5 the same orange as face 5 - and since an edge lies
# along the boundary of its own faces, every edge was drawn invisibly on
# top of a face of exactly its colour. The offset is arbitrary; what
# matters is that it is large enough that (i * phi) lands somewhere
# unrelated, so no edge can collide with the face it runs along.
EDGE_HUE_OFFSET = 97


def edge_color(index):
    """Distinct colour per EDGE index, never clashing with a face."""
    return generate_color(index + EDGE_HUE_OFFSET)


# Fixed colours for sub-object roles, where meaning beats distinctness.
START_COLOR = System.Drawing.Color.FromArgb(40, 200, 90)     # green
END_COLOR = System.Drawing.Color.FromArgb(220, 60, 60)       # red
EDGE_COLOR = System.Drawing.Color.FromArgb(80, 140, 240)     # blue
VERTEX_COLOR = System.Drawing.Color.FromArgb(250, 190, 40)   # amber


# ---------- Attribute save/restore ----------

def _original_attrs():
    if _ORIGINAL_ATTRS not in sc.sticky:
        sc.sticky[_ORIGINAL_ATTRS] = {}
    return sc.sticky[_ORIGINAL_ATTRS]


def apply_object_color(obj_id, color, tag_number):
    """Colour an object and remember what it looked like before.

    Storing the original colour source as well as the colour matters:
    an object that was 'by layer' has to go back to 'by layer', not to
    an explicit copy of whatever the layer colour happened to be."""
    rhobj = coerce.find_object(obj_id)
    if rhobj is None:
        return False

    attr = rhobj.Attributes
    originals = _original_attrs()
    key = str(rhobj.Id)
    if key not in originals:
        originals[key] = (attr.ColorSource, attr.ObjectColor)

    attr.ObjectColor = color
    attr.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject
    attr.SetUserString(TAG_MARKER, str(tag_number))
    return sc.doc.Objects.ModifyAttributes(rhobj, attr, True)


def restore_object_color(obj_id):
    """Put an object's colour back and clear the marker."""
    originals = _original_attrs()
    rhobj = coerce.find_object(obj_id)

    if rhobj is not None:
        attr = rhobj.Attributes
        key = str(rhobj.Id)
        if key in originals:
            color_source, color = originals[key]
            attr.ColorSource = color_source
            attr.ObjectColor = color
        attr.SetUserString(TAG_MARKER, None)
        sc.doc.Objects.ModifyAttributes(rhobj, attr, True)
        originals.pop(key, None)
        return True

    return False


def strip_copy_color(rhobj):
    """Clear a tag an object inherited by being copied.

    Without this, copying a tagged object gives you two objects that look
    identically tagged, and a model reading the picture cannot tell which
    one '#12' means.

    BOTH user-text keys have to go. TAG_MARKER is what makes the copy
    LOOK tagged; tags.TAG_KEY is what makes it ANSWER to the tag. Clearing
    only the marker left a copy that was visually clean and still resolved
    as '#12' through ``tags._scan_for_persisted`` - the harder failure of
    the two, because nothing about the picture hinted at it."""
    attr = rhobj.Attributes
    attr.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorByLayer
    attr.SetUserString(TAG_MARKER, None)
    attr.SetUserString(tagmod.TAG_KEY, None)
    sc.doc.Objects.ModifyAttributes(rhobj, attr, True)


# ---------- Display conduit ----------

class TagConduit(Rhino.Display.DisplayConduit):
    """Draws a tag label that follows live geometry.

    Deliberately re-reads the object from the document on every frame
    rather than caching a point: the label then tracks the object when it
    is moved, and disappears cleanly if the object is deleted, without
    any bookkeeping.

    ``level`` decides how much is drawn:

        2  the object tag only
        3  plus curve start/end spheres, or shaded FACES in their own
           colours with face labels
        4  plus EDGES drawn in their own colours with edge labels, and
           vertex spheres

    Edges are level 4, not level 3. The module docstring and the MCP
    schema both used to say level 3, and a model that asked for L3
    expecting edge numbers got faces and no explanation.
    """

    def __init__(self, obj_id, number, color, level=2, indices=None,
                 faces=None, edges=None, vertices=None, colour_parts=True):
        super(TagConduit, self).__init__()
        self.obj_id = obj_id
        self.number = number
        self.color = color
        self.level = level

        # `indices` used to filter faces, edges AND vertices from one
        # set, so indices=[3, 7] could not mean "face 3 and edge 7" - it
        # meant "component 3 and component 7 of every kind at once".
        # The three are separate now; `indices` stays as the shorthand
        # for "these components, whatever kind this level draws".
        self.indices = set(indices) if indices is not None else None
        self.faces = set(faces) if faces is not None else None
        self.edges = set(edges) if edges is not None else None
        self.vertices = set(vertices) if vertices is not None else None

        # Colour the components themselves, not just their labels.
        self.colour_parts = bool(colour_parts)
        self._component_cache = None

    # -- component geometry, built once
    #
    # This used to happen inside DrawForeground: coerce.to_brep, then
    # DuplicateFace and AreaMassProperties PER FACE PER FRAME. On an
    # Extrusion to_brep is a full conversion, and DrawForeground runs
    # several times per capture - which is why a tagged L3 view stalled
    # the viewport. None of it changes between frames, so none of it
    # belongs in the draw call.

    def _components(self):
        """(meshes, centroids, edges, vertices) for this object, cached."""
        if self._component_cache is not None:
            return self._component_cache

        brep = coerce.to_brep(self.obj_id)
        if brep is None:
            self._component_cache = ([], [], [], [])
            return self._component_cache

        # How far to float each coloured face in front of the real one.
        #
        # Without this the overlay sits at EXACTLY the depth of the
        # surface it is colouring, the depth test is a tie, and the
        # capture comes back in one flat colour - the colours were drawn
        # and lost the z-fight, silently.
        #
        # ZBiasMode.TowardsCamera looks like the answer and is not: it
        # biases every face equally, so the far faces beat the near ones
        # too and whichever was drawn LAST covers the object. Measured -
        # a six-colour box came back entirely yellow, face 5.
        #
        # A real offset along each face's own normal keeps the depth test
        # doing its job: near faces still occlude far ones, and the
        # overlay still hides behind anything genuinely in front. Scaled
        # to the object so it is invisible on a building and still
        # effective on a bolt.
        bbox = brep.GetBoundingBox(True)
        lift = ((bbox.Diagonal.Length * 0.001) if bbox.IsValid else 0.1) or 0.1

        meshes, centroids = [], []
        for index in range(brep.Faces.Count):
            face = brep.Faces[index]
            duplicate = face.DuplicateFace(False)

            amp = rg.AreaMassProperties.Compute(duplicate)
            centroids.append(amp.Centroid if amp is not None else None)

            mesh = None
            try:
                made = rg.Mesh.CreateFromBrep(
                    duplicate, rg.MeshingParameters.Default)
                if made:
                    mesh = rg.Mesh()
                    for part in made:
                        if part is not None:
                            mesh.Append(part)
                    self._lift_mesh(mesh, face, lift)
                    self._paint(mesh, generate_color(index))
            except Exception:
                mesh = None
            meshes.append(mesh)

        # Edges have to clear the COLOURED FACES, not just the surface.
        # The face meshes are floated by `lift`, so an edge left on the
        # real surface now sits underneath them and an L4 capture showed
        # edge labels with no edge under them. Twice the lift puts them
        # proud of the colour they are meant to be read against.
        edges = []
        for i in range(brep.Edges.Count):
            curve = brep.Edges[i].DuplicateCurve()
            if curve is not None:
                self._lift_curve(curve, brep, brep.Edges[i], lift * 2.0)
            edges.append(curve)
        vertices = [brep.Vertices[i].Location
                    for i in range(brep.Vertices.Count)]

        self._component_cache = (meshes, centroids, edges, vertices)
        return self._component_cache

    @staticmethod
    def _lift_curve(curve, brep, edge, distance):
        """Float an edge curve off the faces that meet along it.

        Along the average of its adjacent faces' normals, so an edge on
        a convex corner lifts outward and one in a valley lifts into the
        opening - in both cases away from the surfaces it would
        otherwise be buried in."""
        if curve is None or distance <= 0:
            return
        try:
            normal = rg.Vector3d(0, 0, 0)
            for index in edge.AdjacentFaces():
                face = brep.Faces[index]
                domain_u = face.Domain(0)
                domain_v = face.Domain(1)
                normal += face.NormalAt(domain_u.Mid, domain_v.Mid)
            if not normal.Unitize():
                return
            curve.Translate(normal * distance)
        except Exception:
            return

    @staticmethod
    def _paint(mesh, colour):
        """Give every vertex the face's colour, for DrawMeshFalseColors.

        One flat colour per face rather than a gradient: the colour is
        an IDENTIFIER here, not a visualisation of anything, and a
        gradient would make two adjacent faces meet in a shade that is
        neither of them."""
        if mesh is None:
            return
        try:
            mesh.VertexColors.Clear()
            for _ in range(mesh.Vertices.Count):
                mesh.VertexColors.Add(colour.R, colour.G, colour.B)
        except Exception:
            return

    @staticmethod
    def _lift_mesh(mesh, face, distance):
        """Float a face mesh just off the surface it came from.

        Along each vertex's own normal rather than one averaged normal,
        so a curved face lifts evenly instead of tearing away from the
        surface at its edges."""
        if mesh is None or distance <= 0:
            return
        try:
            mesh.Normals.ComputeNormals()
            mesh.Normals.UnitizeNormals()
        except Exception:
            return

        try:
            for i in range(mesh.Vertices.Count):
                normal = mesh.Normals[i]
                vertex = mesh.Vertices[i]
                mesh.Vertices.SetVertex(
                    i,
                    vertex.X + normal.X * distance,
                    vertex.Y + normal.Y * distance,
                    vertex.Z + normal.Z * distance)
        except Exception:
            return

    def invalidate(self):
        """Drop the cached component geometry.

        Called when the object changes. The cache is what makes L3 fast;
        forgetting to drop it is what would make L3 wrong, so the doc
        hooks call this rather than leaving it to a timer."""
        self._component_cache = None

    def _wanted(self, kind, count):
        """Which component indices of one kind to draw.

        Falls back through: the explicit per-kind set, then the shared
        `indices` shorthand, then everything."""
        explicit = {"face": self.faces, "edge": self.edges,
                    "vertex": self.vertices}.get(kind)
        if explicit is not None:
            return [i for i in range(count) if i in explicit]
        if self.indices is not None:
            return [i for i in range(count) if i in self.indices]
        return list(range(count))

    # -- helpers

    def _anchor(self, geometry):
        """Where the object's own tag goes.

        Curves get their midpoint, everything else its bounding box
        centre - the two places a label reads as belonging to the thing
        rather than floating near it."""
        if isinstance(geometry, rg.Curve):
            return geometry.PointAt(geometry.Domain.Mid)
        bbox = geometry.GetBoundingBox(True)
        return bbox.Center if bbox.IsValid else None

    def _dot_radius(self, e, point):
        """Sphere radius that stays constant on screen as you zoom.

        Sub-object markers have to be visible at any zoom or the whole
        point of an L3 capture is lost, so the radius is derived from the
        viewport's pixels-per-unit rather than being a fixed model
        distance."""
        try:
            per_unit = e.Viewport.PixelsPerUnit(point)
            if per_unit and per_unit > 0:
                return 5.0 / per_unit
        except Exception:
            pass
        return 1.0

    # -- label placement
    #
    # Why this is more than DrawDot in a loop: a 400x400 column seen from
    # ten metres puts its six face, twelve edge and eight vertex labels
    # inside about forty pixels. They overlap, hide each other, and hide
    # the object tag underneath - and a capture whose labels cannot be
    # read tells the model nothing, which is the entire point of L3/L4.
    #
    # Tuning pixel constants does not fix that; twenty-six labels do not
    # fit near a slender column at any size. So this does what a drafter
    # does: when a label will not fit beside its component, it moves OUT
    # to a ring around the object and a LEADER LINE connects the two.
    # An offset label with a leader is unambiguous; an offset label
    # without one is a guess.
    #
    # Anything that still cannot be placed is dropped rather than piled
    # up. The sidecar table lists every component by name regardless, so
    # nothing is lost.
    #
    # HOW WELL THIS WORKS, honestly: it reduces overlap, it does not
    # eliminate it. Rhino hands a conduit only the LIVE viewport's
    # dimensions even while rendering a capture at another size, and
    # DrawForeground runs several times per capture with projections
    # that do not all agree. ``capture_to_file`` now forces a uniform
    # scale and publishes the ratio, which fixes the framing and most of
    # the displacement, but dense sub-object views still crowd.
    #
    # The reliable answer is to label fewer things: pass ``indices`` and
    # only the components you are asking about are drawn. ``vision.capture``
    # warns and gives the count when an L3/L4 request would draw more
    # than about a dozen.

    LABEL_CHAR_PX = 11
    LABEL_PAD_PX = 18
    LABEL_HEIGHT_PX = 24
    RING_MARGIN_PX = 46      # how far outside the object the ring sits
    MAX_LABELS = 40

    def _pixels_per_unit(self, e, point):
        try:
            per_unit = e.Viewport.PixelsPerUnit(point)
            return per_unit if per_unit and per_unit > 0 else None
        except Exception:
            return None

    def _screen(self, e, point):
        """World point -> viewport pixel, or None when unavailable."""
        try:
            screen = e.Viewport.WorldToClient(point)
            return float(screen.X), float(screen.Y)
        except Exception:
            return None

    def _nudge(self, e, point, dx_px, dy_px):
        """Move a world point by a SCREEN offset in pixels.

        The camera's X and Y axes are unit vectors along screen right and
        screen up, so a pixel offset converts to world units by dividing
        by pixels-per-unit at that depth. Screen Y runs downward, hence
        the sign on the Y term."""
        per_unit = self._pixels_per_unit(e, point)
        if per_unit is None:
            return point
        try:
            right = e.Viewport.CameraX
            up = e.Viewport.CameraY
        except Exception:
            return point
        return point + right * (dx_px / per_unit) - up * (dy_px / per_unit)

    def _scale(self):
        """Viewport pixels per rendered pixel.

        Rhino only ever hands a conduit the LIVE viewport's dimensions,
        even while rendering a capture at another size. So every pixel
        threshold here - which is sized for what a reader sees in the
        IMAGE - has to be converted into the space the conduit can
        actually measure in. ``capture_to_file`` publishes the ratio
        before it renders and resets it afterwards, so an interactive
        redraw uses 1.0 and behaves exactly as before."""
        try:
            return float(sc.sticky.get("rg_capture_scale", 1.0)) or 1.0
        except Exception:
            return 1.0

    def _half_width(self, text):
        return ((len(text) * self.LABEL_CHAR_PX + self.LABEL_PAD_PX)
                / 2.0) * self._scale()

    def _free(self, placed, target, text):
        half_width = self._half_width(text)
        for other_x, other_y, other_half in placed:
            if (abs(target[0] - other_x) < (half_width + other_half)
                    and abs(target[1] - other_y)
                    < self.LABEL_HEIGHT_PX * self._scale()):
                return False
        return True

    def _screen_bounds(self, e, geometry):
        """The object's extent on screen, for sizing the leader ring."""
        bbox = geometry.GetBoundingBox(True)
        if not bbox.IsValid:
            return None
        points = [self._screen(e, corner) for corner in bbox.GetCorners()]
        points = [p for p in points if p is not None]
        if not points:
            return None
        xs = [p[0] for p in points]
        ys = [p[1] for p in points]
        return min(xs), min(ys), max(xs), max(ys)

    def _place(self, e, placed, point, text, bounds, index, total):
        """Decide where this label goes.

        Beside the component when there is room - that reads best and
        needs no leader. Otherwise out on a ring around the object, at
        an angle spread by index, with a leader drawn back to the
        component. None when neither fits."""
        screen = self._screen(e, point)
        if screen is None:
            return None, False

        if self._free(placed, screen, text):
            placed.append((screen[0], screen[1], self._half_width(text)))
            return self._nudge(e, point, 0, 0), False

        if bounds is None:
            return None, False

        centre_x = (bounds[0] + bounds[2]) / 2.0
        centre_y = (bounds[1] + bounds[3]) / 2.0
        margin = self.RING_MARGIN_PX * self._scale()
        radius_x = (bounds[2] - bounds[0]) / 2.0 + margin
        radius_y = (bounds[3] - bounds[1]) / 2.0 + margin

        # Walk outward in rings so a second and third pass still land.
        for ring in range(3):
            grow = 1.0 + ring * 0.45
            for step in range(total):
                angle = 2.0 * math.pi * (((index + step) % total) / float(total))
                target = (centre_x + math.cos(angle) * radius_x * grow,
                          centre_y + math.sin(angle) * radius_y * grow)
                if self._free(placed, target, text):
                    placed.append((target[0], target[1],
                                   self._half_width(text)))
                    return (self._nudge(e, point, target[0] - screen[0],
                                        target[1] - screen[1]), True)
        return None, False

    def _label(self, e, placed, point, text, color, text_color,
               bounds=None, index=0, total=1):
        """Draw one label, with a leader when it had to move. True if drawn."""
        if len(placed) >= self.MAX_LABELS:
            return False
        position, moved = self._place(e, placed, point, text, bounds,
                                      index, total)
        if position is None:
            return False
        if moved:
            e.Display.DrawLine(rg.Line(point, position), color, 1)
        e.Display.DrawDot(position, text, color, text_color)
        return True

    # -- drawing

    def PostDrawObjects(self, e):
        """The colour pass - depth-tested, so it hides behind geometry.

        PostDrawObjects, not PreDrawObjects: Pre runs BEFORE Rhino draws
        the object, so the object then painted straight over the colours
        and the capture came back in one flat tag colour. Post draws
        after, still against the live depth buffer.

        This is the half that makes a sub-object capture readable. The
        labels alone never could: every face of one object was drawn in
        the SAME colour (the object's), every edge in one fixed blue, and
        the face itself was never drawn at all - so "which face is F3?"
        came down to reading nine-pixel text.

        Drawing here rather than in DrawForeground is what fixes the
        other half of it. DrawForeground paints over everything, so on a
        closed box all six face labels rendered, the three on the far
        side included, with nothing to say which was which. Depth-tested,
        only the faces you can actually see get pixels - which is the
        answer to "which face am I looking at" for free."""
        if not self.colour_parts or self.level < 3:
            return
        if isinstance(coerce.geometry_of(self.obj_id), rg.Curve):
            return

        meshes, _centroids, edges, _vertices = self._components()

        # The meshes were already floated just clear of the surface when
        # they were cached, so ordinary depth testing does the rest: near
        # faces occlude far ones, and anything genuinely in front of the
        # object still hides the overlay.
        for index in self._wanted("face", len(meshes)):
            mesh = meshes[index]
            if mesh is None:
                continue
            # DrawMeshFalseColors, not DrawMeshShaded.
            #
            # Shaded means LIT, and lighting is exactly what breaks the
            # promise the legend makes. Measured: face 5 is published as
            # (242,169,84) and the shaded pixels came out (219,153,76) -
            # same hue, darker, so a reader comparing the colour it sees
            # against the legend finds no match and has to fall back on
            # the nine-pixel text this pass exists to replace.
            #
            # False colours are drawn flat from the mesh's own vertex
            # colours, so the pixel IS the legend entry, exactly.
            e.Display.DrawMeshFalseColors(mesh)

        if self.level < 4:
            return

        for index in self._wanted("edge", len(edges)):
            edge = edges[index]
            if edge is None:
                continue
            e.Display.DrawCurve(edge, edge_color(index), 6)

    def DrawForeground(self, e):
        rhobj = sc.doc.Objects.Find(self.obj_id)
        if rhobj is None:
            return

        geometry = rhobj.Geometry
        if geometry is None:
            return

        tag = "#{}".format(self.number)
        placed = []          # labels drawn this frame, in screen space
        bounds = self._screen_bounds(e, geometry)

        # The object tag goes down FIRST, so when a view is crowded the
        # label that survives is the one naming the whole object - which
        # is what a caller needs to act on it at all.
        anchor = self._anchor(geometry)
        if anchor is not None:
            self._label(e, placed, anchor, tag, self.color,
                        System.Drawing.Color.White, bounds)

        if self.level < 3:
            return

        if isinstance(geometry, rg.Curve):
            self._draw_curve_detail(e, placed, geometry, tag, bounds)
        else:
            self._draw_brep_detail(e, placed, rhobj, tag, bounds)

    def _draw_curve_detail(self, e, placed, curve, tag, bounds=None):
        """Green start, red end - curve direction made visible.

        Direction is not cosmetic: it decides which side an offset goes
        and which way a sweep runs, and it is invisible without this."""
        start = curve.PointAtStart
        end = curve.PointAtEnd
        middle = curve.PointAt(curve.Domain.Mid)

        e.Display.DrawSphere(rg.Sphere(start, self._dot_radius(e, start)),
                             START_COLOR)
        e.Display.DrawSphere(rg.Sphere(end, self._dot_radius(e, end)),
                             END_COLOR)
        self._label(e, placed, start, "{}.S".format(tag), START_COLOR,
                    System.Drawing.Color.White, bounds, 0, 2)
        self._label(e, placed, end, "{}.E".format(tag), END_COLOR,
                    System.Drawing.Color.White, bounds, 1, 2)

    def _draw_brep_detail(self, e, placed, rhobj, tag, bounds=None):
        """Face labels at L3; edge labels and vertex spheres at L4.

        Drawn faces first, then edges, then vertices - so when a
        cluttered view can only fit some of them, what survives is the
        coarsest level, which is also the one most often being asked
        about. Losing a vertex label to keep a face label readable is
        the right trade; the reverse is not."""
        meshes, centroids, edge_curves, vertices = self._components()
        if not centroids and not edge_curves:
            return

        # Only the components asked for. Labelling all 200 edges of a
        # filleted solid to answer a question about two of them is how a
        # capture becomes unreadable.
        faces = self._wanted("face", len(centroids))
        for position, index in enumerate(faces):
            centroid = centroids[index]
            if centroid is None:
                continue
            # The label takes the FACE's colour, not the object's, so it
            # reads as belonging to the patch of colour beside it. Every
            # face label used to be the same colour, which made the dots
            # a set of near-identical stickers on one object.
            colour = (generate_color(index) if self.colour_parts
                      else self.color)
            self._label(e, placed, centroid,
                        "{}.F{}".format(tag, index), colour,
                        System.Drawing.Color.White, bounds,
                        position, len(faces))

        if self.level < 4:
            return

        edges = self._wanted("edge", len(edge_curves))
        for position, index in enumerate(edges):
            edge = edge_curves[index]
            if edge is None:
                continue
            colour = edge_color(index) if self.colour_parts else EDGE_COLOR
            self._label(e, placed, edge.PointAt(edge.Domain.Mid),
                        "{}.E{}".format(tag, index),
                        colour, System.Drawing.Color.White, bounds,
                        position, len(edges))

        picked = self._wanted("vertex", len(vertices))
        for position, index in enumerate(picked):
            location = vertices[index]
            e.Display.DrawSphere(
                rg.Sphere(location, self._dot_radius(e, location)),
                VERTEX_COLOR)
            self._label(e, placed, location, "{}.V{}".format(tag, index),
                        VERTEX_COLOR, System.Drawing.Color.Black, bounds,
                        position, len(picked))


# ---------- Conduit registry ----------

def _conduits():
    if _CONDUITS not in sc.sticky:
        sc.sticky[_CONDUITS] = {}
    return sc.sticky[_CONDUITS]


def number_in_space_order(obj_ids):
    """Give the UNTAGGED objects among ``obj_ids`` their '#' numbers in
    octree order (see ``core_divide.space_order``), so each part holds a
    run of consecutive numbers and neighbours have close numbers.

    Already-tagged objects keep their number - receipts already refer to
    it. Returns how many were numbered now."""
    from . import core_divide
    items = []
    for guid in coerce.to_guids(obj_ids):
        if tagmod.tag_of(guid, create=False) is not None:
            continue
        box = coerce.bbox_of(guid)
        if box is not None and box.IsValid:
            items.append((guid, ((box.Min.X, box.Min.Y, box.Min.Z),
                                 (box.Max.X, box.Max.Y, box.Max.Z))))
        else:
            items.append((guid, None))
    for guid in core_divide.space_order(items):
        tagmod.tag_of(guid, create=True)
    return len(items)


def tag_objects(obj_ids, level=2, persist=False, indices=None,
                faces=None, edges=None, vertices=None, colour_parts=True):
    """Tag and colour objects, enabling a conduit for each.

    Returns ``{tag: guid}`` for everything newly tagged. Objects that are
    already tagged have their level updated rather than being tagged
    twice - re-running a capture at a higher level should escalate the
    existing labels, not duplicate them.

    Narrowing, in order of precision:

        faces=[0, 2]     exactly those faces
        edges=[5]        exactly those edges
        vertices=[...]   exactly those vertices
        indices=[3, 7]   the shorthand - those components of whichever
                         kind this level draws

    A filleted solid has a couple of hundred edges, and labelling all of
    them to answer a question about two is how a capture stops being
    readable.

    ``colour_parts`` shades each requested face in its own colour and
    draws each requested edge in its own, with the label taking the same
    colour. Off, it falls back to text dots in one colour, which is what
    this did before and is still worth having when the object's real
    colour is what is being looked at."""
    ensure_hooks()
    conduits = _conduits()
    result = {}

    for guid in coerce.to_guids(obj_ids):
        tag = tagmod.tag_of(guid, create=True, persist=persist)
        if tag is None:
            continue
        number = int(tag.lstrip("#"))

        key = str(guid)
        if key in conduits:
            existing = conduits[key]
            existing.level = level
            existing.indices = set(indices) if indices is not None else None
            existing.faces = set(faces) if faces is not None else None
            existing.edges = set(edges) if edges is not None else None
            existing.vertices = (set(vertices) if vertices is not None
                                 else None)
            existing.colour_parts = bool(colour_parts)
            result[tag] = guid
            continue

        color = generate_color(number)
        apply_object_color(guid, color, number)

        conduit = TagConduit(guid, number, color, level, indices,
                             faces=faces, edges=edges, vertices=vertices,
                             colour_parts=colour_parts)
        conduit.Enabled = True
        conduits[key] = conduit
        result[tag] = guid

    redraw.request()
    return result


def component_legend(obj_ids, level=3, faces=None, edges=None,
                     indices=None):
    """colour -> component tag, for whatever the capture drew.

    The point of the colour pass is that the model reads a COLOUR off the
    image instead of nine-pixel text. That only works if it can turn the
    colour back into a tag without guessing, so the receipt carries the
    mapping explicitly.

    Returns ``{'#12.F3': [r, g, b], ...}``."""
    legend = {}
    for guid in coerce.to_guids(obj_ids):
        tag = tagmod.tag_of(guid, create=True)
        if tag is None:
            continue
        brep = coerce.to_brep(guid)
        if brep is None:
            continue

        def pick(count, explicit):
            if explicit is not None:
                return [i for i in range(count) if i in set(explicit)]
            if indices is not None:
                return [i for i in range(count) if i in set(indices)]
            return list(range(count))

        if level >= 3:
            for index in pick(brep.Faces.Count, faces):
                colour = generate_color(index)
                legend["{}.F{}".format(tag, index)] = [
                    colour.R, colour.G, colour.B]

        if level >= 4:
            for index in pick(brep.Edges.Count, edges):
                colour = edge_color(index)
                legend["{}.E{}".format(tag, index)] = [
                    colour.R, colour.G, colour.B]

    return legend


def untag_objects(obj_ids=None):
    """Turn off conduits and restore colours. All of them when none are
    named - the 'clear' op."""
    conduits = _conduits()
    targets = ([str(g) for g in coerce.to_guids(obj_ids)] if obj_ids
               else list(conduits.keys()))

    cleared = 0
    for key in targets:
        conduit = conduits.pop(key, None)
        if conduit is not None:
            conduit.Enabled = False
        if restore_object_color(key):
            cleared += 1

    redraw.request()
    return cleared


def tagged_ids():
    """Ids currently carrying a live tag conduit."""
    return list(_conduits().keys())


def set_tag_level(level):
    """Change the detail level on every active conduit at once."""
    for conduit in _conduits().values():
        conduit.level = level
    redraw.request()


# ---------- Document event hooks ----------

def on_delete_object(sender, e):
    """Drop the conduit when its object is deleted."""
    conduits = _conduits()
    key = str(e.ObjectId)
    conduit = conduits.pop(key, None)
    if conduit is not None:
        conduit.Enabled = False
        _original_attrs().pop(key, None)
        redraw.request()


def on_replace_object(sender, e):
    """Follow the object through an edit.

    Rhino replaces rather than mutates on most edits, so without this a
    single move would orphan every tag."""
    conduits = _conduits()
    originals = _original_attrs()
    old_key = str(e.OldRhinoObject.Id)
    new_id = e.NewRhinoObject.Id
    new_key = str(new_id)

    conduit = conduits.pop(old_key, None)
    if conduit is None:
        return

    conduit.obj_id = new_id
    conduits[new_key] = conduit

    # The edit changed the geometry, so the cached face meshes, centroids
    # and edges describe the OLD shape. Caching them is what makes an L3
    # capture fast; dropping them here is what keeps it correct.
    conduit.invalidate()

    if old_key in originals:
        originals[new_key] = originals.pop(old_key)

    apply_object_color(new_id, conduit.color, conduit.number)
    redraw.request()


def on_add_object(sender, e):
    """Strip a tag colour that arrived on a copy.

    Fires on Copy, Paste, Array and anything else that duplicates. An
    untracked object carrying the marker inherited it, so it is stripped
    - otherwise two objects appear identically tagged."""
    conduits = _conduits()
    new_obj = e.TheObject
    if str(new_obj.Id) in conduits:
        return

    if new_obj.Attributes.GetUserString(TAG_MARKER) is not None:
        strip_copy_color(new_obj)
        redraw.request()


def ensure_hooks():
    """Attach the three document hooks once per session."""
    if sc.sticky.get(_HOOKS, False):
        return
    Rhino.RhinoDoc.DeleteRhinoObject += on_delete_object
    Rhino.RhinoDoc.ReplaceRhinoObject += on_replace_object
    Rhino.RhinoDoc.AddRhinoObject += on_add_object
    sc.sticky[_HOOKS] = True


# ---------- Isolate and ghost ----------

def _hidden_store():
    if _HIDDEN not in sc.sticky:
        sc.sticky[_HIDDEN] = []
    return sc.sticky[_HIDDEN]


def isolate(obj_ids):
    """Hide everything except the given objects.

    The hidden ids are remembered so ``restore_visibility`` can put back
    exactly what this hid, rather than unhiding things the user had
    deliberately hidden earlier."""
    keep = set(str(g) for g in coerce.to_guids(obj_ids))
    hidden = _hidden_store()

    for rhobj in sc.doc.Objects:
        if rhobj.IsDeleted or rhobj.IsHidden:
            continue
        if str(rhobj.Id) in keep:
            continue
        if sc.doc.Objects.Hide(rhobj.Id, True):
            hidden.append(str(rhobj.Id))

    redraw.request()
    return len(hidden)


def restore_visibility():
    """Unhide only what ``isolate`` hid."""
    hidden = _hidden_store()
    restored = 0
    for key in hidden:
        guid = coerce.to_guid(key)
        if guid is not None and sc.doc.Objects.Show(guid, True):
            restored += 1
    sc.sticky[_HIDDEN] = []
    redraw.request()
    return restored


def _ghost_store():
    if _GHOSTED not in sc.sticky:
        sc.sticky[_GHOSTED] = {}
    return sc.sticky[_GHOSTED]


def _override_id(attr):
    """The id of an object's current display-mode override, or None.

    Rhino exposes this differently across versions, so this asks and
    shrugs rather than assuming. A None means "no override", which is
    also what a failed read should look like - restoring to 'no
    override' is the status quo ante for the overwhelming majority of
    objects, and guessing wrong in the other direction would pin an
    object to a mode nobody chose."""
    try:
        value = attr.GetDisplayModeOverride()
    except Exception:
        return None
    if value is None:
        return None
    try:
        return value.Id
    except Exception:
        return None


def ghost(keep_opaque_ids, transparency=80):
    """Make everything except the named objects semi-transparent.

    Better than isolating when the surroundings are the point - a wall
    opening only makes sense with the rest of the wall visible. Per-object
    display attributes are used so the effect is per object, not per
    viewport."""
    keep = set(str(g) for g in coerce.to_guids(keep_opaque_ids))
    store = _ghost_store()

    count = 0
    for rhobj in sc.doc.Objects:
        if rhobj.IsDeleted or rhobj.IsHidden:
            continue
        key = str(rhobj.Id)
        if key in keep:
            continue

        try:
            attr = rhobj.Attributes
            if key not in store:
                # Remember the object's EXISTING display-mode override,
                # which is the thing ghosting is about to replace.
                #
                # This used to store ObjectDecoration - an unrelated
                # property, never read back - while unghost called
                # RemoveDisplayModeOverride unconditionally. So ghosting
                # a model where someone had deliberately set one object
                # to Rendered destroyed that setting permanently, and
                # the "restore" step was what destroyed it.
                store[key] = _override_id(attr)
            attr.SetDisplayModeOverride(
                Rhino.Display.DisplayModeDescription.FindByName("Ghosted"))
            sc.doc.Objects.ModifyAttributes(rhobj, attr, True)
            count += 1
        except Exception:
            continue

    redraw.request()
    return count


def unghost():
    """Clear every display override this module applied."""
    store = _ghost_store()
    cleared = 0
    for key in list(store.keys()):
        rhobj = coerce.find_object(key)
        if rhobj is None:
            continue
        try:
            attr = rhobj.Attributes
            previous = store.get(key)
            if previous:
                # It had its own override before we ghosted it. Put that
                # back rather than clearing to "by viewport", which is a
                # different thing and not what we found.
                mode = Rhino.Display.DisplayModeDescription.GetDisplayMode(
                    previous)
                if mode is not None:
                    attr.SetDisplayModeOverride(mode)
                else:
                    attr.RemoveDisplayModeOverride()
            else:
                attr.RemoveDisplayModeOverride()
            sc.doc.Objects.ModifyAttributes(rhobj, attr, True)
            cleared += 1
        except Exception:
            continue
    sc.sticky[_GHOSTED] = {}
    redraw.request()
    return cleared


def clear_all():
    """Reset everything this module changed - tags, isolation, ghosting.

    Worth having as one call: a workflow that escalated through several
    capture levels leaves three kinds of state behind, and remembering to
    undo each one separately is how a document ends up permanently
    coloured."""
    return {
        "untagged": untag_objects(),
        "unhidden": restore_visibility(),
        "unghosted": unghost(),
    }
