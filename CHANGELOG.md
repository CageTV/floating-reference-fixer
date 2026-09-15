# Floating Object Fixer — Changelog

## v2.0.3 — 2026-09-15

**New: automatic ESL flagging.** The generated fix plugin is now checked for ESL eligibility every run
(same logic as SSEEdit's own "Find ESP plugins which could be turned into ESL" script) and automatically
flagged as an ESL if it qualifies — this tool's output only ever overrides existing references, so it's
eligible essentially every time. The log reports whether the flag was set and why.

## v2.0.2 — 2026-09-14

**Collision-awareness now works out of the box — no manual setup at all.** Real user feedback after
v2.0.1: even with Python installed, users still had to manually download PyNifly and run
`pip install scipy` before collision-awareness would actually engage — exactly the two steps v2.0.1's
own README called "optional setup." Fixed properly instead of just documenting it better:

- **PyNifly now ships with the tool.** A trimmed copy (`NiflyDLL.dll` + its `pyn/` Python wrapper only
  — about 6MB, not the full ~43MB Blender addon) is bundled directly in the release. PyNifly is
  GPL-3.0; this tool is MIT — see `pynifly/NOTICE.md` for why that's fine (PyNifly runs as its own
  separate process, invoked via `Process.Start` and JSON over stdin/stdout, never linked into the .NET
  binary — "mere aggregation" under GPL's own FAQ). Full GPL-3.0 text travels alongside it in
  `pynifly/LICENSE`.
- **The SciPy dependency is gone entirely**, not just bundled — it was only used for one specific
  collision-shape type (`bhkConvexVerticesShape`). Replaced with a small, dependency-free pure-Python
  convex-hull implementation, verified against known shapes (cube, tetrahedron) and a real raycast
  query before shipping.
- **The only remaining requirement is Python itself** (3.9+) — no `pip install`, no manual downloads.
  If Python isn't found at all, the tool still degrades gracefully to heightmap-only, same as before.

Verified end-to-end against a real 1283-plugin profile with a completely fresh build (no external
PyNifly/scipy present anywhere the tool would normally look) — collision-awareness engaged
immediately: 4348 candidates checked against 1170 unique meshes, 519 targets refined, 773 correctly
reclassified as not actually floating, clean completion.

## v2.0.1 — 2026-09-14

**Fixed a crash reported by real users**: running detection/fix with Python
installed but PyNifly not (fully) set up — i.e. step 1 of the optional
collision-awareness setup done, steps 2–3 skipped or wrong — crashed the
whole run with `IOException: The pipe is being closed`, instead of degrading
to heightmap-only like the README promises. Root cause: the bundled
`extract_collision.py` fails to import PyNifly and exits immediately, before
reading any of the NIF paths piped to its stdin; the .NET side was writing
that input in an unguarded loop, so the moment the child process exited, the
next write threw and crashed the whole detection/fix run instead of falling
back. Now caught and handled the same way every other "collision-awareness
unavailable" case already was — the log names the likely cause and includes
the actual Python error, and results fall back to heightmap-only for that
run, exactly as v2.0.0 already did for "no Python installed at all."

## v2.0.0 — 2026-09-14

A large feature update built over several days of live testing against a real,
800+ mod load order. Every item below was verified against real in-game
results before being kept, including two changes that were built, tested,
found to cause real regressions, and reverted the same day.

**Real Havok collision awareness** — the single biggest gap in v1.0.0. A
raw heightmap can't tell "genuinely empty air" from "there's a rock/cliff/
bridge here the flat landscape grid doesn't represent," which was the direct
cause of the worst v1.0.0-era failure mode: auto-correcting an object to a
position *below* the visible ground because the heightmap had no way to know
better. Nearby placed statics now have their real collision geometry
extracted (via a bundled Python script using `pynifly`) and raycast against
each flagged candidate; a confirmed real surface is trusted far more than a
heightmap guess, and gets a correspondingly higher auto-fix cap. Entirely
optional and gracefully degrading — no Python/PyNifly, no problem, the tool
just falls back to v1.0.0's heightmap-only behavior.

**BSA/BA2 archive support for collision geometry.** Loose-file-only
resolution was silently blind to any mod that packs its meshes into an
archive with no loose-file fallback — confirmed on one real mod, this
alone raised real collision-geometry coverage by ~78% project-wide.

**Water-surface handling** for driftwood/log/debris-type objects, at an
adjustable depth (default 48 units) — previously, an object floating over a
creek would either get shoved into the submerged bed or, in an earlier
attempt, made to hover unnaturally at the exact water line regardless of
object type. Ivy/vine/hanging flora are now excluded from ground-resting
checks entirely (they're meant to hang from a ceiling or wall, not touch a
floor), and an architectural "Wall" exclusion stops ruin/structure pieces
from being treated as natural terrain features.

**Two opt-in, off-by-default trade-offs** for cases the safe default leaves
un-auto-fixed: a custom heightmap-only safety threshold (a real middle
ground — wider coverage, still capped), and a full force-correct mode
(bypasses every cap). Both ship with an explicit, specific warning before
running, after force-correct was confirmed in testing to bury a real,
legitimately-placed object (not just decorative clutter) when enabled by
default — it's opt-in for good reason, and the warning names the exact
incident that proved it.

**Fixed a real detection-hiding bug**: a terrain-ruggedness reliability
gate, added to protect against a cliff-edge false positive, was found to
also be hiding a genuinely, massively floating reference (700+ units) in a
legitimately re-terraformed area — "how rugged is the nearby terrain"
doesn't actually distinguish "unreliable cliff-edge estimate" from "a real,
large landscape edit," which is exactly the case this tool exists to catch.
Removed; the two other, more targeted safety checks are unaffected.

**UI**: an honest disclaimer that this tool gets you most of the way there,
not 100%; short plain-language explanations under both the Threshold and
Water submerge depth fields (not just tooltips — buried in a tooltip is easy
to miss); a proper bold "⚠ WARNING" confirmation dialog (not a plain message
box) before either risky opt-in runs.

**Fixed a packaging bug found while preparing this release**: both the
Python script location and the Python interpreter path were hardcoded to
one specific developer machine, which would have silently disabled
collision-awareness for anyone else who downloaded this tool. Both are now
resolved relative to the tool's own install location / standard install
paths, and a missing Python installation now degrades gracefully instead of
throwing an unhandled exception.

## v1.0.0 — 2026-09-10 (first release)

A new tool: finds placed trees, rocks, and movable statics whose position
no longer matches the terrain height directly beneath them — the classic
"a landscape mod changed the terrain and left this object floating (or
sunk) in mid-air" bug.

**Terrain-only for this release.** It compares each reference's position
against the game's own landscape heightmap, interpolated to the reference's
exact spot. It does **not** know about rock/cliff mesh collision, so an
object legitimately resting on a large rock or cliff formation (common in
mountainous terrain) can still misreport as "floating" — review the report
before trusting a fix broadly, especially outside a 96–500 unit deviation.

**Vanilla-terrain gate.** A reference is only flagged if the terrain at its
*exact* position was actually edited by a mod, verified against Skyrim.esm's
own unedited copy — not just "this cell has some edit somewhere in it."
Without this check, mountainous vanilla terrain alone (completely untouched
by any mod) produced enormous false-positive counts on its own, unrelated
to any real bug.

**Desktop app included**, matching the look and workflow of Landscape Seam
Fixer / Landscape Texture Fixer: pick your mod manager, run detection to
get a CSV report, or generate a fix plugin directly. A Threshold and
Worldspace filter replace those tools' trust checkboxes (this tool doesn't
use the trust system — it only ever compares a reference's own position
against the terrain actually winning beneath it).

**Verified** against a real load order scoped to Tamriel: 46 genuine
floating/sunk references corrected, down from 1476 raw candidates before
the vanilla-terrain gate above was added (most of that difference was
vanilla Skyrim.esm objects in never-edited mountainous cells — not bugs).
