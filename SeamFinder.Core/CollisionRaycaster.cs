// Real Havok collision-geometry awareness for FloatingObjectFixer, replacing
// its previous terrain-heightmap-only "raw terrainZ" concept with an actual
// "what does the engine's own collision say is here" answer where available.
//
// WHY THIS EXISTS: confirmed via real in-game screenshots 2026-09-12 that the
// heightmap-only approach sends objects BELOW the visible ground whenever
// unmodeled rock/architecture geometry sits above the raw landscape at that
// XY - in two unrelated locations the same night (a RoadMaskMerge-carved pool
// bank, and an unrelated Riverwood stone bridge). No amount of heightmap-side
// gate-tuning can close this: the heightmap genuinely does not contain the
// information needed. The user's explicit direction: "do it right" - real
// collision geometry, not another heuristic.
//
// ARCHITECTURE: this file does NOT parse Havok/NIF binary data itself. NIF
// parsing is delegated to `tools/collision-raycast/extract_collision.py`,
// which wraps `pynifly` (the Python bindings for `nifly`, the same C++
// library behind Outfit Studio/BodySlide). This was a deliberate choice
// after direct testing 2026-09-12: PyFFI (this toolkit's OTHER NIF library)
// crashed reading bhkRigidBody on a real SE rock mesh; pynifly read it (and
// a large bhkRigidBodyT bridge, 6877 verts) cleanly on the first try. Rather
// than hand-roll a Havok binary parser in C# (a multi-week undertaking with
// no working reference to check against), this reuses pynifly's PROVEN,
// shipped parsing and does only the transform + raycast math here, in C#,
// where the rest of this tool's placement logic already lives.
//
// Mutagen itself has NO 3D transform math (confirmed by inspecting the
// Mutagen-Modding/Mutagen source directly - Placement is a pure data record,
// P3Float Position/Rotation with no accompanying matrix logic anywhere in
// the library). The exact Skyrim REFR rotation matrix convention could not
// be independently confirmed via web search before implementation (the
// authoritative CK wiki page blocks automated fetches). Implemented here
// using the standard right-handed Z*Y*X Euler convention, matching NIF's own
// native per-node rotation-matrix representation (which IS unambiguously
// documented) - REQUIRES empirical validation against real, known-problem
// references (position/rotation known, approximate correct world Z inferable
// from context) before this is trusted for auto-correction. See
// ValidateAgainstKnownRefs below and the 2026-09-12 memory notes for the
// specific validation refs used.

using System.Diagnostics;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;

namespace SeamFinder.Core;

public readonly record struct Vec3(float X, float Y, float Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator *(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
}

public readonly record struct Tri3(Vec3 A, Vec3 B, Vec3 C);

// One placed instance whose collision (if any) should be checked: the NIF to
// read, and the world transform to place it with (from its own REFR).
public readonly record struct CollisionCheckInstance(
    string NifPath, Vec3 Position, Vec3 RotationRadians, float Scale, FormKey RefFormKey);

public record ExtractedCollision(bool Ok, string? Reason, List<Tri3> LocalTriangles, List<string> ShapeTypes, bool Approx);

public static class CollisionRaycaster
{
    // Resolved once, lazily. FIXED 2026-09-14 before this project's first
    // public release - the ORIGINAL version here hardcoded both paths to one
    // specific developer machine (`E:\Tabula Rasa\...` and
    // `C:\Users\JulioV\...`), directly contradicting this comment's own
    // stated intent ("rather than hardcoding a path that only works on one
    // machine") - the aspiration was written but never actually implemented.
    // Confirmed via direct testing on this machine that it worked ONLY
    // because both hardcoded paths happened to be correct here; any other
    // user's collision-awareness would have silently no-op'd (the existing
    // "extract_collision.py not found... skipping geometry checks entirely"
    // log line, easy to miss).
    static string? _pythonExe;
    static string? _extractScript;

    static void EnsurePaths()
    {
        if (_extractScript is not null) return;
        // The script is bundled INSIDE this tool's own published output (see
        // FloatingObjectFixer.UI.csproj / FloatingObjectFixer.csproj - a
        // Content item with CopyToOutputDirectory), not resolved against a
        // separate toolkit install - this tool needs to run standalone on
        // any machine that downloads its release, not just this one.
        _extractScript = Path.Combine(AppContext.BaseDirectory, "collision-raycast", "extract_collision.py");

        // Portable Python discovery. VERIFIED absolute paths are tried first
        // (per-user then system-wide install locations, across several recent
        // version numbers, using environment variables rather than a literal
        // username or drive letter) - preferred over a bare PATH command name
        // because Windows ships a fake `python.exe` App Execution Alias stub
        // that just opens the Microsoft Store instead of running anything
        // when no real Python is installed, a well-known gotcha that would
        // otherwise make this silently "succeed" at picking a non-working
        // exe. Bare command names ("py"/"python"/"python3") are the LAST-
        // resort fallback for a real install in a nonstandard location -
        // unverifiable without extra PATH-scanning work, but the existing
        // "failed to start python process" handling below already degrades
        // gracefully if the chosen one doesn't actually work.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var versionsNewestFirst = new[] { "314", "313", "312", "311", "310", "39" };
        var verifiedCandidates = versionsNewestFirst
            .Select(v => Path.Combine(localAppData, "Programs", "Python", $"Python{v}", "python.exe"))
            .Concat(versionsNewestFirst.Select(v => Path.Combine(programFiles, $"Python{v}", "python.exe")));
        _pythonExe = verifiedCandidates.FirstOrDefault(File.Exists) ?? "py";
    }

    // Batches an entire set of unique NIF paths through ONE python process
    // invocation (paths piped via stdin, results read as one JSON object from
    // stdout) rather than one process per file - a real Havok/NIF read isn't
    // free, and the same handful of rock/architecture meshes are reused by
    // hundreds of placed instances across the world, so caching by PATH
    // (never by placed instance) is what keeps this practical at the scale
    // FindCandidates already operates at (100k+ placed objects checked).
    public static Dictionary<string, ExtractedCollision> ExtractCollisionBatch(
        IEnumerable<string> nifPaths, Action<string> log)
    {
        EnsurePaths();
        var uniquePaths = nifPaths.Distinct().ToList();
        var result = new Dictionary<string, ExtractedCollision>();
        if (uniquePaths.Count == 0) return result;

        if (!File.Exists(_extractScript))
        {
            log($"  CollisionRaycaster: extract_collision.py not found at {_extractScript} (should be bundled with " +
                "this tool) - skipping geometry checks entirely. Detection/fix results will use heightmap-only " +
                "targets for every candidate, same as before collision-awareness existed.");
            return result;
        }

        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(_extractScript!);
        psi.ArgumentList.Add("--stdin");

        // Process.Start THROWS (does not return null) when the target exe
        // genuinely can't be found/run - confirmed this was an unhandled-
        // exception bug here (the `is null` check below never actually
        // triggers for that case) while fixing the machine-specific path
        // hardcoding above. Python not being installed at all is an expected,
        // common case for anyone downloading this tool fresh - it must
        // degrade the same way every other "collision-awareness unavailable"
        // path here does, not crash the whole fix/detection run.
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex)
        {
            log($"  CollisionRaycaster: could not launch Python ('{_pythonExe}') - {ex.Message}. Skipping geometry " +
                "checks; see the README for setting up real collision-awareness (Python + PyNifly). Detection/fix " +
                "results will use heightmap-only targets for every candidate, same as before collision-awareness existed.");
            return result;
        }
        using var _ = proc;
        if (proc is null)
        {
            log("  CollisionRaycaster: failed to start python process - skipping geometry checks.");
            return result;
        }

        // FIXED 2026-09-14, after a real user bug report: if the python process
        // exits before we finish writing its input - the common case being
        // `extract_collision.py`'s top-level `from pyn.pynifly import NifFile`
        // throwing because PyNifly isn't set up (Python itself installed per
        // README step 1, but steps 2-3 - the NiflyDLL.dll placement + `pip
        // install scipy` - skipped or wrong) - the OS closes the pipe from the
        // child's side, and WriteLine throws IOException: "The pipe is being
        // closed." That exception was previously unhandled here, so it crashed
        // the ENTIRE detection/fix run instead of degrading - directly
        // contradicting this tool's own README promise ("degrades gracefully...
        // nothing breaks" if Python/PyNifly isn't set up). Catch it and fall
        // back the same way every other collision-awareness-unavailable path in
        // this method already does.
        try
        {
            foreach (var p in uniquePaths) proc.StandardInput.WriteLine(p);
            proc.StandardInput.Close();
        }
        catch (IOException ex)
        {
            var earlyStderr = proc.StandardError.ReadToEnd();
            log($"  CollisionRaycaster: extract_collision.py exited before accepting all input ({ex.Message}). " +
                "This usually means PyNifly isn't fully set up - see the README's collision-awareness section " +
                "(NiflyDLL.dll under pynifly/io_scene_nifly/, plus 'pip install scipy'). " +
                $"Python stderr: {earlyStderr.Trim()}. Skipping geometry checks; detection/fix results will use " +
                "heightmap-only targets for every candidate, same as before collision-awareness existed.");
            return result;
        }

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            log($"  CollisionRaycaster: extract_collision.py failed (exit {proc.ExitCode}): {stderr.Trim()}");
            return result;
        }

        using var doc = JsonDocument.Parse(stdout);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var path = prop.Name;
            var obj = prop.Value;
            var ok = obj.GetProperty("ok").GetBoolean();
            if (!ok)
            {
                var reason = obj.TryGetProperty("reason", out var r) ? r.GetString() : "unknown";
                result[path] = new ExtractedCollision(false, reason, [], [], false);
                continue;
            }
            var tris = new List<Tri3>();
            foreach (var triEl in obj.GetProperty("triangles").EnumerateArray())
            {
                var verts = triEl.EnumerateArray().Select(vEl =>
                {
                    var arr = vEl.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                    return new Vec3(arr[0], arr[1], arr[2]);
                }).ToArray();
                tris.Add(new Tri3(verts[0], verts[1], verts[2]));
            }
            var shapeTypes = obj.GetProperty("shape_types").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            var approx = obj.GetProperty("approx").GetBoolean();
            result[path] = new ExtractedCollision(true, null, tris, shapeTypes, approx);
        }

        return result;
    }

    // Standard right-handed Z*Y*X Euler rotation matrix, matching NIF's own
    // native per-node rotation-matrix convention (see file header on why this
    // was chosen absent independent web confirmation of the REFR-specific
    // convention, and why it MUST be validated empirically before trust).
    static (Vec3 Row0, Vec3 Row1, Vec3 Row2) RotationMatrix(Vec3 eulerRadians)
    {
        var (sx, cx) = (MathF.Sin(eulerRadians.X), MathF.Cos(eulerRadians.X));
        var (sy, cy) = (MathF.Sin(eulerRadians.Y), MathF.Cos(eulerRadians.Y));
        var (sz, cz) = (MathF.Sin(eulerRadians.Z), MathF.Cos(eulerRadians.Z));

        // Rx
        var rx = ((1f, 0f, 0f), (0f, cx, -sx), (0f, sx, cx));
        // Ry
        var ry = ((cy, 0f, sy), (0f, 1f, 0f), (-sy, 0f, cy));
        // Rz
        var rz = ((cz, -sz, 0f), (sz, cz, 0f), (0f, 0f, 1f));

        // R = Rz * Ry * Rx (each a 3x3 tuple-of-tuples; multiply then flatten to rows).
        static (float, float, float) MulRow((float, float, float) rowA, ((float, float, float), (float, float, float), (float, float, float)) b)
        {
            var (a0, a1, a2) = rowA;
            var (b0, b1, b2) = b;
            return (
                a0 * b0.Item1 + a1 * b1.Item1 + a2 * b2.Item1,
                a0 * b0.Item2 + a1 * b1.Item2 + a2 * b2.Item2,
                a0 * b0.Item3 + a1 * b1.Item3 + a2 * b2.Item3
            );
        }
        var ryx = (MulRow(ry.Item1, rx), MulRow(ry.Item2, rx), MulRow(ry.Item3, rx));
        var zyx = (MulRow(rz.Item1, ryx), MulRow(rz.Item2, ryx), MulRow(rz.Item3, ryx));

        return (
            new Vec3(zyx.Item1.Item1, zyx.Item1.Item2, zyx.Item1.Item3),
            new Vec3(zyx.Item2.Item1, zyx.Item2.Item2, zyx.Item2.Item3),
            new Vec3(zyx.Item3.Item1, zyx.Item3.Item2, zyx.Item3.Item3)
        );
    }

    static Vec3 ApplyRotation((Vec3 Row0, Vec3 Row1, Vec3 Row2) r, Vec3 v) => new(
        r.Row0.X * v.X + r.Row0.Y * v.Y + r.Row0.Z * v.Z,
        r.Row1.X * v.X + r.Row1.Y * v.Y + r.Row1.Z * v.Z,
        r.Row2.X * v.X + r.Row2.Y * v.Y + r.Row2.Z * v.Z);

    public static List<Tri3> ToWorldSpace(List<Tri3> local, Vec3 position, Vec3 rotationRadians, float scale)
    {
        var r = RotationMatrix(rotationRadians);
        Vec3 Xform(Vec3 v) => position + ApplyRotation(r, v * scale);
        var outTris = new List<Tri3>(local.Count);
        foreach (var t in local)
            outTris.Add(new Tri3(Xform(t.A), Xform(t.B), Xform(t.C)));
        return outTris;
    }

    // Vertical "raycast": for a fixed (x,y) column, find the HIGHEST Z at
    // which any triangle's XY-projection contains the point, via 2D
    // barycentric test + interpolation. This is a simplification of a true
    // 3D ray-triangle intersection that is exactly equivalent for a
    // perfectly vertical ray and is both simpler and faster - no need for
    // Möller–Trumbore here since two of the three ray-direction components
    // are always zero.
    public static float? HighestSurfaceZ(IEnumerable<Tri3> worldTriangles, float x, float y)
    {
        float? best = null;
        foreach (var t in worldTriangles)
        {
            var z = TryInterpolateZ(t, x, y);
            if (z.HasValue && (best is null || z.Value > best.Value))
                best = z.Value;
        }
        return best;
    }

    // Added 2026-09-13, per the user's own request: a single raycast at the
    // candidate's own origin point can miss real contact that only exists
    // under a DIFFERENT part of the object's footprint (its actual edge, not
    // its center) - "if nothing is touching it, flag it" needs to actually
    // check more than one point first. Materializes worldTriangles to a list
    // once (the single-point caller iterates the same enumerable multiple
    // times too via ToWorldSpace's own cached List<Tri3>, so this doesn't
    // change allocation behavior) and reuses HighestSurfaceZ per sample
    // point, taking the max found across all of them - same "highest
    // relevant surface, not the lowest" reasoning as
    // HeightmapDecoder.GetMaxHeightInFootprint. Skips the extra ring
    // entirely for a near-zero footprint (most small clutter meshes),
    // since a single point is already sufficient there and the ring only
    // adds raycast cost for objects genuinely large enough to matter.
    public static float? HighestSurfaceZInFootprint(IReadOnlyList<Tri3> worldTriangles, float centerX, float centerY, float footprintRadius)
    {
        var best = HighestSurfaceZ(worldTriangles, centerX, centerY);
        if (footprintRadius < 20f) return best;
        for (int deg = 0; deg < 360; deg += 90)
        {
            var rad = deg * Math.PI / 180.0;
            var sx = centerX + (float)(footprintRadius * Math.Cos(rad));
            var sy = centerY + (float)(footprintRadius * Math.Sin(rad));
            var z = HighestSurfaceZ(worldTriangles, sx, sy);
            if (z.HasValue && (best is null || z.Value > best.Value)) best = z.Value;
        }
        return best;
    }

    static float? TryInterpolateZ(Tri3 t, float px, float py)
    {
        // Barycentric coordinates in the XY plane.
        float x1 = t.A.X, y1 = t.A.Y, x2 = t.B.X, y2 = t.B.Y, x3 = t.C.X, y3 = t.C.Y;
        float denom = (y2 - y3) * (x1 - x3) + (x3 - x2) * (y1 - y3);
        if (MathF.Abs(denom) < 1e-6f) return null; // degenerate (near-vertical) triangle, skip

        float w1 = ((y2 - y3) * (px - x3) + (x3 - x2) * (py - y3)) / denom;
        float w2 = ((y3 - y1) * (px - x3) + (x1 - x3) * (py - y3)) / denom;
        float w3 = 1f - w1 - w2;

        const float eps = -1e-4f; // small tolerance for edge/vertex hits
        if (w1 < eps || w2 < eps || w3 < eps) return null;

        return w1 * t.A.Z + w2 * t.B.Z + w3 * t.C.Z;
    }
}
