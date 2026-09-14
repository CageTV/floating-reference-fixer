"""Extract Havok collision geometry from NIFs as flat, NIF-local-space triangle
lists, for use by a caller (FloatingObjectFixer) that owns its own placement
(position/rotation/scale) and Havok-vs-render-mesh business logic.

WHY THIS EXISTS: FloatingObjectFixer's terrain-heightmap-only approach cannot
tell "raw landscape height" from "there's a rock/bridge/wall here the heightmap
doesn't know about" - confirmed via real in-game screenshots 2026-09-12 showing
objects auto-corrected to BELOW visible ground in two unrelated locations. The
only real fix is checking actual collision geometry, per this project's own
`skyrim-nif`-adjacent tooling: `pynifly` (the Python wrapper around the same
`nifly` C++ library behind Outfit Studio/BodySlide) already has full working
Havok shape support, confirmed 2026-09-12 by successfully reading collision from
both a compressed-mesh rock (s3drockm01.nif, 59 verts/103 tris) and a large
bhkRigidBodyT bridge (windhelmbridge.nif, 6877 verts/6064 tris) - two real
meshes from this install. PyFFI, the OTHER NIF library this toolkit uses,
CANNOT do this: it crashed reading bhkRigidBody on the very rock mesh that
motivated this file.

This mirrors the transform math in `nif/collision.py`'s Blender import path
(RigidBodyXF, the shape-type dispatch) since that code is the ACTUAL PROVEN,
shipped implementation used to visually place collision correctly for real
modders - safer to mirror than to re-derive independently.

Scope of shape types handled: bhkListShape (recurse), bhkConvexTransformShape
(apply local transform, recurse), bhkMoppBvTreeShape (recurse into child,
MOPP BVH acceleration structure itself is irrelevant to us - we brute-force
every triangle), bhkCompressedMeshShape + bhkPackedNiTriStripsShape (leaf
triangle mesh - the dominant case for real terrain-adjacent rocks/architecture,
confirmed above), bhkBoxShape (leaf, 8 corners -> 12 tris), bhkConvexVerticesShape
(leaf, scipy ConvexHull - flagged `approx: true` in output since a hull of the
stored vertices is the best available without the shape's own stored face
planes). bhkCapsuleShape/bhkSphereShape are DELIBERATELY SKIPPED (return no
triangles) - real ground-forming rock/architecture collision is essentially
never a bare capsule or sphere in practice (those are used for e.g. tree
trunks, small props), and approximating them wrong is worse than reporting
nothing for this specific "is there a floor here" purpose.

Usage: python extract_collision.py <nif_path> [<nif_path> ...]
   or: python extract_collision.py --stdin   (reads newline-separated paths from stdin)
Output: one JSON object to stdout, keyed by input path:
    {
      "<path>": {
        "ok": true,
        "triangles": [[[x,y,z],[x,y,z],[x,y,z]], ...],   # NIF-local space, engine units
        "shape_types": ["bhkCompressedMeshShape"],        # leaf types actually found
        "approx": false
      },
      "<other path>": {"ok": false, "reason": "no collision object on root node"},
      ...
    }
Never raises for a single bad/missing/no-collision NIF - failures are per-path
in the output so a batch of 50 paths with one bad file still returns 49 good
results, matching the "GUARD INERT" / fail-loud-not-silent discipline this
project uses elsewhere (a caller must be able to tell "checked, no collision"
apart from "didn't check" by looking at `ok` + `reason`, never by absence).
"""
import sys
import os
import json
import math

_pyn_parent = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "pynifly", "io_scene_nifly")
_pyn_parent = os.path.normpath(_pyn_parent)
if _pyn_parent not in sys.path:
    sys.path.insert(0, _pyn_parent)

from pyn.pynifly import NifFile  # noqa: E402

HAVOC_SCALE_FACTOR = 69.99125  # nifconstants.HAVOC_SCALE_FACTOR; SKYRIMSE game_collision_sf is 1.0


def _quat_to_matrix(x, y, z, w):
    """Standard quaternion -> 3x3 rotation matrix, row-major 3-tuples."""
    n = x * x + y * y + z * z + w * w
    if n < 1e-12:
        return ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))
    s = 2.0 / n
    xx, yy, zz = x * x * s, y * y * s, z * z * s
    xy, xz, yz = x * y * s, x * z * s, y * z * s
    wx, wy, wz = w * x * s, w * y * s, w * z * s
    return (
        (1.0 - (yy + zz), xy - wz, xz + wy),
        (xy + wz, 1.0 - (xx + zz), yz - wx),
        (xz - wy, yz + wx, 1.0 - (xx + yy)),
    )


def _mat_apply(m, v):
    return (
        m[0][0] * v[0] + m[0][1] * v[1] + m[0][2] * v[2],
        m[1][0] * v[0] + m[1][1] * v[1] + m[1][2] * v[2],
        m[2][0] * v[0] + m[2][1] * v[1] + m[2][2] * v[2],
    )


def _mat_mul(a, b):
    return tuple(
        tuple(sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3))
        for i in range(3)
    )


IDENTITY3 = ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))


def _rigid_body_transform(body):
    """Mirrors nif/collision.py RigidBodyXF: bhkRigidBodyT carries its own
    translation+rotation (Havok units); plain bhkRigidBody applies none.
    Returns (rot3x3, translation_havok_units)."""
    cls_name = type(body).__name__
    if cls_name != "bhkRigidBodyT":
        return IDENTITY3, (0.0, 0.0, 0.0)
    p = body.properties
    # p.rotation is stored (x, y, z, w) per collision.py's own Quaternion((p.rotation[3], p.rotation[0], p.rotation[1], p.rotation[2]))
    rx, ry, rz, rw = p.rotation[0], p.rotation[1], p.rotation[2], p.rotation[3]
    rot = _quat_to_matrix(rx, ry, rz, rw)
    t = (p.translation[0], p.translation[1], p.translation[2])
    return rot, t


def _box_triangles(dims):
    """bhkBoxShape.properties.bhkDimensions are HALF-extents, Havok units."""
    dx, dy, dz = dims[0], dims[1], dims[2]
    v = [
        (-dx, -dy, -dz), (dx, -dy, -dz), (dx, dy, -dz), (-dx, dy, -dz),
        (-dx, -dy, dz), (dx, -dy, dz), (dx, dy, dz), (-dx, dy, dz),
    ]
    faces = [
        (0, 1, 2), (0, 2, 3),  # bottom
        (4, 6, 5), (4, 7, 6),  # top
        (0, 4, 5), (0, 5, 1),  # front
        (1, 5, 6), (1, 6, 2),  # right
        (2, 6, 7), (2, 7, 3),  # back
        (3, 7, 4), (3, 4, 0),  # left
    ]
    return [(v[a], v[b], v[c]) for a, b, c in faces]


def _convex_hull_triangles(vertices):
    """bhkConvexVerticesShape stores loose vertices (Havok units, x,y,z[,w] per
    entry - only x,y,z used). No stored faces, so the hull is reconstructed -
    flagged approx=True by the caller since floating-point hull triangulation
    can pick a different-but-equivalent triangulation than the original tool,
    though the SURFACE (which is all we raycast against) is the same."""
    from scipy.spatial import ConvexHull
    pts = [(v[0], v[1], v[2]) for v in vertices]
    if len(pts) < 4:
        return []
    hull = ConvexHull(pts)
    return [(pts[a], pts[b], pts[c]) for a, b, c in hull.simplices]


def _resolve_shape(shape, shape_types_out):
    """Returns a list of (v0,v1,v2) triangles in the shape's OWN local space
    (Havok units, not yet scaled), recursing through container/transform
    shapes. shape_types_out collects the leaf type names actually visited."""
    cls_name = type(shape).__name__

    if cls_name == "bhkListShape":
        tris = []
        for child in shape.children:
            tris.extend(_resolve_shape(child, shape_types_out))
        return tris

    if cls_name == "bhkConvexTransformShape":
        child_tris = _resolve_shape(shape.child, shape_types_out)
        # shape.transform is a 4x4 (row-major list of lists) per pynifly.py's
        # bhkConvexTransformShape.transform property.
        xf = shape.transform
        rot = tuple(tuple(xf[r][c] for c in range(3)) for r in range(3))
        trans = (xf[0][3], xf[1][3], xf[2][3])
        out = []
        for a, b, c in child_tris:
            out.append((
                tuple(_mat_apply(rot, a)[i] + trans[i] for i in range(3)),
                tuple(_mat_apply(rot, b)[i] + trans[i] for i in range(3)),
                tuple(_mat_apply(rot, c)[i] + trans[i] for i in range(3)),
            ))
        return out

    if cls_name == "bhkMoppBvTreeShape":
        child = shape.child
        if child is None:
            return []
        return _resolve_shape(child, shape_types_out)

    if cls_name in ("bhkCompressedMeshShape", "bhkPackedNiTriStripsShape"):
        shape_types_out.add(cls_name)
        verts = shape.vertices
        tris_idx = shape.triangles
        if not verts or not tris_idx:
            return []
        return [(verts[a], verts[b], verts[c]) for a, b, c in tris_idx]

    if cls_name == "bhkBoxShape":
        shape_types_out.add(cls_name)
        return _box_triangles(shape.properties.bhkDimensions)

    if cls_name == "bhkConvexVerticesShape":
        shape_types_out.add(cls_name + " (approx: convex hull reconstructed)")
        try:
            return _convex_hull_triangles(shape.vertices)
        except Exception:
            return []

    # bhkCapsuleShape, bhkSphereShape, anything else: deliberately not
    # approximated for ground-surface purposes - see file header.
    shape_types_out.add(cls_name + " (SKIPPED - not ground-relevant)")
    return []


def extract_one(path):
    if not os.path.isfile(path):
        return {"ok": False, "reason": "file not found"}
    try:
        f = NifFile(path)
    except Exception as e:
        return {"ok": False, "reason": f"NIF load failed: {type(e).__name__}: {e}"}

    try:
        co = f.rootNode.collision_object
    except Exception as e:
        return {"ok": False, "reason": f"reading collision_object failed: {type(e).__name__}: {e}"}

    if not co:
        return {"ok": False, "reason": "no collision object on root node"}

    body = getattr(co, "body", None)
    if body is None:
        return {"ok": False, "reason": "collision object has no body (e.g. bhkNPCollisionObject/FO4-style, unsupported here)"}

    shape = getattr(body, "shape", None)
    if shape is None:
        return {"ok": False, "reason": "body has no shape"}

    shape_types = set()
    try:
        local_tris = _resolve_shape(shape, shape_types)
    except Exception as e:
        return {"ok": False, "reason": f"shape resolution failed: {type(e).__name__}: {e}"}

    if not local_tris:
        return {"ok": False, "reason": f"no ground-relevant triangles (shape types seen: {sorted(shape_types)})"}

    rot, trans_havok = _rigid_body_transform(body)
    approx = any("approx" in t for t in shape_types)

    out_tris = []
    for a, b, c in local_tris:
        pts = []
        for v in (a, b, c):
            # Apply body transform (Havok units), THEN scale to NIF-local
            # engine units. RigidBodyXF applies HAVOC_SCALE_FACTOR to the
            # translation only because the ROTATED-VERTEX math there happens
            # in already-scaled (Blender/engine) space; here we scale
            # everything together at the end instead, which is equivalent.
            rv = _mat_apply(rot, v)
            hv = (rv[0] + trans_havok[0], rv[1] + trans_havok[1], rv[2] + trans_havok[2])
            pts.append((hv[0] * HAVOC_SCALE_FACTOR, hv[1] * HAVOC_SCALE_FACTOR, hv[2] * HAVOC_SCALE_FACTOR))
        out_tris.append(pts)

    return {
        "ok": True,
        "triangles": out_tris,
        "shape_types": sorted(shape_types),
        "approx": approx,
    }


def main():
    args = sys.argv[1:]
    if args and args[0] == "--stdin":
        paths = [line.strip() for line in sys.stdin if line.strip()]
    else:
        paths = args

    if not paths:
        print("Usage: python extract_collision.py <nif_path> [<nif_path> ...]", file=sys.stderr)
        print("   or: python extract_collision.py --stdin", file=sys.stderr)
        sys.exit(1)

    result = {}
    for p in paths:
        result[p] = extract_one(p)

    json.dump(result, sys.stdout)


if __name__ == "__main__":
    main()
