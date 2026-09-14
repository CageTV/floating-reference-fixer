# Third-party component: PyNifly

The `io_scene_nifly/` folder in this directory is a **trimmed** copy of
[PyNifly](https://github.com/BadDogSkyrim/PyNifly) by **BadDogSkyrim** - just
`NiflyDLL.dll` (the compiled `nifly` library) and the `pyn/` Python wrapper
package it depends on. The full PyNifly download is a Blender addon (~43MB,
Blender-specific import/export UI, animation/rigging tooling, bundled `.blend`
assets); none of that is needed here, so it isn't included. `NiflyDLL.dll` is
used unmodified, exactly as PyNifly's own release ships it.

This tool (`FloatingObjectFixer`) uses it for exactly one thing:
`collision-raycast/extract_collision.py` calls `pyn.pynifly.NifFile` to read
a NIF's real Havok collision geometry, so floating/sunk-object detection can
check actual collision shapes instead of only the terrain heightmap.

## License: GPL-3.0

PyNifly is licensed GNU GPL v3.0 (full text: `LICENSE` in this same folder).
`FloatingObjectFixer` itself is MIT-licensed (see the repo root `LICENSE`) -
these are compatible here because of how the two pieces actually interact:

- `extract_collision.py` (Python, GPL-3.0-covered - it imports PyNifly
  directly) runs as its own **separate OS process**, launched by the .NET
  executable via `Process.Start` and exchanging plain JSON over stdin/stdout.
- The .NET code and the Python code are never compiled or linked together
  into one program - no shared address space, no shared library linkage,
  just two independent processes talking over a pipe.

This is the standard "mere aggregation" case GPL's own FAQ describes: placing
an independent GPL program alongside a differently-licensed one, invoked as
its own process, does not bring the other program under the GPL. So the .NET
side of this tool stays MIT; only the PyNifly files themselves (this folder)
carry the GPL-3.0 obligations - which is exactly why this NOTICE.md and the
full `LICENSE` text travel alongside them. (Not formal legal advice - if
you're redistributing this tool further and want certainty for your own
situation, the mere-aggregation FAQ entry on gnu.org is the primary source,
or consult your own counsel.)

## What GPL-3.0 requires of a redistributor (i.e., what this satisfies)

- Keep this attribution and the copyright/license notices intact - done via
  this file plus the unmodified `LICENSE` text.
- Make the corresponding source available for anything GPL-covered that's
  redistributed - the `pyn/` folder here already IS that source (unmodified
  `.py` files, not compiled), and `NiflyDLL.dll` is PyNifly's own official
  prebuilt release binary, whose source is the upstream `nifly`/PyNifly
  repositories themselves (linked above).
- Don't add further restrictions on top of the GPL terms for this component -
  none are added; use it exactly as PyNifly itself permits.
