# Floating Object Fixer

**Current version: 2.0.0** — see [CHANGELOG.md](CHANGELOG.md) for what's new.

A standalone tool for Skyrim Special Edition / Anniversary Edition that finds
placed trees, rocks, and movable statics whose position no longer matches the
terrain (or water, or real collision geometry) beneath them — the classic "a
landscape mod changed the terrain and left this object floating (or buried)"
bug. Companion to [Landscape Seam Fixer](../Landscape%20Seam%20Fixer) and
[Landscape Texture Fixer](../Landscape%20Texture%20Fixer), sharing the same
MO2-aware resolution and VHGT terrain-decoding logic.

It works entirely offline against your mod manager's own config files — it
does **not** need MO2 or Vortex running.

**Read this before you trust it broadly: this tool gets you MOST of the way
there, not 100%.** Some genuinely floating objects sit in spots where no safe
correction can be confirmed, and the two ways to push further (below) trade
that safety away — they can move a *different*, legitimately-placed object
somewhere you don't want it, not just decorative clutter. Review the CSV
report and the log before trusting a fix broadly.

## What it does

For every placed `TREE`/`STAT`/`MSTT` reference resting on the ground,
compares its position against the best available answer to "what's actually
there":

1. **Terrain height** — the game's own landscape heightmap, interpolated to
   the reference's exact spot (not just the nearest heightmap vertex).
2. **Real collision geometry**, if available — see below. A raw heightmap
   can't tell "this is empty air" from "there's a rock/cliff/bridge here the
   flat landscape grid doesn't know about," which is the single biggest
   source of wrong corrections. Where a Havok collision surface can be found
   and confirmed higher than the raw terrain, it's used instead — a measured
   answer, not a guess.
3. **Water**, for driftwood/log/debris-type objects specifically — these are
   allowed to rest at a cell's water surface instead of the submerged bed
   beneath it, at an adjustable depth (see below).

If the gap exceeds a threshold (96 units by default), it's flagged; fix mode
snaps the reference's `Position.Z` — **only** Z, never X/Y, rotation, or
scale — onto whichever of the above answers is most trustworthy for that
specific reference.

### Real collision awareness (optional, recommended)

Havok collision data lives inside each mesh's own NIF file, not in Mutagen's
data model, so reading it requires an external step: `collision-raycast/
extract_collision.py` (bundled with this tool) shells out to a Python
interpreter and uses [`pynifly`](https://github.com/BadDogSkyrim/PyNifly) (the
same library behind Outfit Studio/BodySlide) to extract real collision
triangles from nearby meshes, which this tool then raycasts against in C#.

**This is entirely optional and degrades gracefully.** If Python or PyNifly
isn't set up, the tool falls back to heightmap-only detection (its original
v1.0 behavior) and says so in the log — nothing breaks, you just lose the
extra accuracy. To enable it:

1. Install Python 3.9+ (the `py` launcher from python.org's installer is
   detected automatically; a plain venv/virtualenv Python on PATH works too).
2. Download the latest `io_scene_nifly.zip` from
   [PyNifly's releases](https://github.com/BadDogSkyrim/PyNifly/releases) and
   extract it so `NiflyDLL.dll` ends up at
   `<this tool's folder>/pynifly/io_scene_nifly/NiflyDLL.dll`.
3. `pip install scipy` (used for one specific collision-shape type).

BSA/BA2-packed meshes are supported too, not just loose files — the resolver
checks loose files first (matching Skyrim's own load-order precedence), then
falls back to opening archives directly.

### Water-surface fallback

Objects that read "Debris," "Log," or "Driftwood" in their EditorID and sit
over a cell's water are allowed to settle *below* the water's surface (real
driftwood sinks, it doesn't float) at an adjustable depth — default 48 units,
tunable per run since a shallow creek and a deep pool aren't the same thing
and one flat per-cell water-height value can't fit every scene. Ivy, moss,
and similar wall/ceiling-clinging flora are excluded from ground-resting
checks entirely — they're meant to hang, not touch the floor.

## Two ways to push past the safe default — both are trade-offs, not free wins

By default, a candidate whose only available answer is a bare heightmap guess
(no confirmed collision surface, not a water case) is only auto-corrected if
it's a *small* guess (150 units) — being wrong about "is there unmodeled
geometry here" sends an object below the visible ground, which is worse than
leaving the original floating bug alone. This is a real, hit trade-off:
some objects with real geometry issues can be found in the CSV report but not
auto-fixed. Two opt-in settings widen that safe zone, both **off by default**
with an explicit warning before running:

- **Custom safety threshold** — raises (or lowers) just that 150-unit cap.
  A genuine middle ground: wider coverage, still capped, still a guess.
- **Force-correct** — bypasses every cap entirely (up to a 3000-unit
  mountainside sanity ceiling) and corrects every flagged reference it can.
  **Confirmed to bury real, legitimately-placed objects, not just decorative
  clutter** — in testing, a vanilla rock next to a custom dungeon entrance
  got shoved underground this way, visibly breaking the scene. If a run with
  this on causes damage, turning it back off does **not** undo it — sunk/
  buried references are never auto-corrected in either mode. A full reset
  (disable and delete the old generated plugin, regenerate, restart the
  game) is the only way back.

## Requirements

- Windows
- To just **run** the pre-built release: nothing extra — it's published
  self-contained (bundles its own .NET runtime). Collision-awareness needs
  the separate Python/PyNifly setup above; everything else works out of the box.
- To **build from source**: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

## Building from source

```
git clone <this repo's URL>
cd "Floating Object Fixer"
dotnet build
```

Publish the desktop UI as a standalone folder:

```
dotnet publish FloatingObjectFixer.UI -c Release -r win-x64 --self-contained true -o FloatingObjectFixer.UI/publish
```

The console CLI (`FloatingObjectFixer/`) runs straight out of
`bin/Release/net10.0/FloatingObjectFixer.exe` — no publish step needed for
local use. Both projects bundle `collision-raycast/extract_collision.py`
into their own output folder automatically as part of the build/publish.

## Usage — desktop app (recommended)

1. Launch `FloatingObjectFixer.UI.exe`.
2. Pick how your mods are managed (Mod Organizer 2 / Vortex / Direct game
   path) — same as the sibling tools.
3. Set **Threshold** (default 96 units) and **Worldspace filter** (default
   `Tamriel` — recommended; large custom worldspaces tend to be heavily rock/
   cliff-decorated and can otherwise dominate the flagged set).
4. Set **Water submerge depth** (default 48 units) if you're seeing driftwood/
   debris land too high or too low relative to a specific scene's water.
5. Click **Run Detection** to generate `FloatingObjectReport.csv` — every
   flagged reference, which plugin owns it, and how far off it sits.
6. Click **Generate Fix Plugin** to produce `FloatingObjectFixes.esp`,
   correcting every flagged reference's `Position.Z`. Independent of step 5.
   Only tick **Force-correct** or a **custom safety threshold** after reading
   their warnings above — leave both off for the safe default behavior.
7. Install/activate the generated plugin like any other mod.
8. **Test in-game before trusting it broadly.** After any regenerate, fully
   exit to desktop and relaunch — Skyrim loads plugin data once at startup
   and never hot-reloads it, so reloading a save after regenerating shows you
   nothing new.

## Usage — command line

```
FloatingObjectFixer.exe --mo2 <instancePath> <profileName> [gameDataPath] [--threshold=N] [--worldspace="Tamriel"]
```
Detection only — writes `FloatingObjectReport.csv` next to the exe.

```
FloatingObjectFixer.exe --fix <instancePath> <profileName> [gameDataPath] [--threshold=N] [--worldspace="Tamriel"] [--water-depth=N] [--force-correct] [--safety-cap=N]
```
Generates `FloatingObjectFixes.esp`. `--force-correct` and `--safety-cap=N`
are the same two opt-in, off-by-default trade-offs described above — read
that section before using either.

`gameDataPath` is optional in both modes — if omitted, it's read from
`<instancePath>\ModOrganizer.ini`. `--threshold` defaults to 96 units;
`--water-depth` defaults to 48; `--worldspace` is unset (checks every
worldspace) by default, but scoping to one is strongly recommended.

## How it works

For every winning `Cell` in the target worldspace, decodes the winning
`Landscape`'s heightmap and, for each placed ground-resting reference: skips
disabled/parked-far-away placeholders, skips anywhere the terrain at that
exact spot hasn't actually been edited by any mod (compared against
Skyrim.esm's own unedited copy — otherwise untouched vanilla mountainous
terrain alone produces false positives that have nothing to do with any
mod), then compares the reference's `Position.Z` against the interpolated
terrain height (adjusted for water where relevant). Flagged candidates then
go through a collision-awareness pass: every other placed static within a
search radius gets its real Havok geometry extracted and raycast against the
candidate's position, refining the target or reclassifying it as not
actually floating when real support is found. Base-object scope is narrowed
to `TREE` unconditionally (excluding wall/ceiling-clinging Ivy/Vine/Hanging
flora) plus `STAT`/`MSTT` only when the base object's EditorID matches a
natural-terrain keyword (Rock, Boulder, Stone, Log, Stump, Root, Bush, Shrub,
Moss, Fern, Debris, Branch, DeadTree) and doesn't match an architectural
exclusion (Wall) — a blind "any static" filter catches far too many
architectural pieces (walls, road chunks) that sit at heights their
structure dictates, never natural ground level.

## Project layout

- `SeamFinder.Core/` — the subset of the shared library this tool needs
  (`Mo2Resolver`, `HeightmapDecoder`, `FloatingObjectFixer`,
  `CollisionRaycaster`) — no trust system here, since this tool only ever
  compares a reference's own position against the terrain/collision/water
  actually present beneath it
- `collision-raycast/` — the Python script that reads real Havok collision
  geometry from NIFs via `pynifly` (see the optional-setup section above)
- `FloatingObjectFixer/` — console CLI
- `FloatingObjectFixer.UI/` — WPF desktop app

`SeamFinder.Core` here is a trimmed copy shared with two sibling tools
(Landscape Seam Fixer, Landscape Texture Fixer) that live in their own
separate repos.

## Contributing

Issues and PRs welcome, especially reports of a specific floating/sunk
object this tool either missed or flagged incorrectly (a
`FloatingObjectReport.csv` row plus an in-game screenshot is the most
useful bug report), and independent confirmation of the collision-raycast
rotation-convention math (see the header comment in `CollisionRaycaster.cs`
for the specific open question).

## License

MIT — see [LICENSE](LICENSE).
