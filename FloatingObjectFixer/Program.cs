// FloatingObjectFixer CLI - detects (and optionally corrects) placed
// static/movable-static/tree references whose Position no longer matches
// the terrain height directly beneath them (the classic "landscape fix left
// this rock floating in mid-air" problem). Core logic lives in
// SeamFinder.Core/FloatingObjectFixer.cs - see that file for the full scope
// and reasoning (terrain-only for this first pass, not true 3D collision).
//
// Two ways to run this exe:
//   - "--mo2 <instancePath> <profileName> [gameDataPath] [--threshold=N] [--worldspace=\"Tamriel\"]":
//     detection only - writes FloatingObjectReport.csv, touches nothing.
//   - "--fix <instancePath> <profileName> [gameDataPath] [--threshold=N] [--worldspace=\"Tamriel\"]":
//     generates FloatingObjectFixes.esp correcting every flagged
//     reference's Position.Z (only Z) to match the terrain beneath it.
//
// gameDataPath is optional in both - if omitted, reads gamePath from
// <instancePath>\ModOrganizer.ini. Default threshold is 96 units - see
// FloatingObjectFixer.DefaultThresholdUnits for why.

using SeamFinder.Core;

if (args.Length > 0 && args[0] == "--mo2")
{
    RunDetectMode(args);
}
else if (args.Length > 0 && args[0] == "--fix")
{
    RunFixMode(args);
}
else
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  FloatingObjectFixer.exe --mo2 <instancePath> <profileName> [gameDataPath] [--threshold=N] [--worldspace=\"Tamriel\"]");
    Console.WriteLine("  FloatingObjectFixer.exe --fix <instancePath> <profileName> [gameDataPath] [--threshold=N] [--worldspace=\"Tamriel\"]");
    Pause();
}

void RunDetectMode(string[] detectArgs)
{
    if (detectArgs.Length < 3)
    {
        Console.WriteLine("Usage: FloatingObjectFixer.exe --mo2 <instancePath> <profileName> [gameDataPath] [--threshold=N] [--worldspace=\"Tamriel\"]");
        Console.WriteLine("  Writes FloatingObjectReport.csv - detection only, never touches anything.");
        return;
    }
    var instancePath = detectArgs[1];
    var profileName = detectArgs[2];
    var gameDataPath = detectArgs.Length > 3 && !detectArgs[3].StartsWith("--") ? detectArgs[3] : ReadGamePathFromIni(instancePath);
    var threshold = Threshold(detectArgs);

    Console.WriteLine($"MO2 instance: {instancePath}");
    Console.WriteLine($"Profile: {profileName}");
    Console.WriteLine($"Game Data path: {gameDataPath}");
    Console.WriteLine($"Threshold: {threshold} units");
    Console.WriteLine();

    try
    {
        var resolved = Mo2Resolver.Resolve(instancePath, profileName, gameDataPath);
        Console.WriteLine($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
        if (resolved.MissingPlugins.Count > 0)
        {
            Console.WriteLine($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found:");
            foreach (var m in resolved.MissingPlugins) Console.WriteLine("  " + m);
        }

        var result = FloatingObjectFixer.RunDetectionForResolvedPlugins(resolved.LoadOrder, Console.WriteLine, threshold, WorldspaceFilter(detectArgs), resolved.ResolveDataFile);
        var outPath = Path.Combine(AppContext.BaseDirectory, "FloatingObjectReport.csv");
        File.WriteAllLines(outPath, result.ReportCsvLines);
        Console.WriteLine();
        Console.WriteLine($"Flagged {result.Flagged} reference(s). Report: {outPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine("ERROR: " + ex);
    }

    Pause();
}

void RunFixMode(string[] fixArgs)
{
    if (fixArgs.Length < 3)
    {
        Console.WriteLine("Usage: FloatingObjectFixer.exe --fix <instancePath> <profileName> [gameDataPath] [--threshold=N] [--worldspace=\"Tamriel\"] [--water-depth=N] [--force-correct] [--safety-cap=N]");
        Console.WriteLine("  Generates FloatingObjectFixes.esp correcting every flagged reference's Position.Z.");
        Console.WriteLine("  --force-correct bypasses every auto-fix delta cap - RISKY, off by default. CONFIRMED 2026-09-14");
        Console.WriteLine("  to bury real, legitimately-placed objects (not just decorative clutter) when no safe target can");
        Console.WriteLine("  be confirmed - a vanilla rock near a custom dungeon entrance got shoved underground this way.");
        Console.WriteLine("  Unchecking/omitting this later does NOT undo damage already done (sunk items are never auto-");
        Console.WriteLine("  corrected) - a full reset (disable+delete the old esp, regenerate, restart) is the only way back.");
        Console.WriteLine("  --safety-cap=N is a middle ground: raises (or lowers) just the heightmap-only-guess cap (default");
        Console.WriteLine("  150 units) instead of bypassing every cap like --force-correct does. Still risky above the");
        Console.WriteLine("  default - a heightmap-only target is a GUESS, and a wrong one can end up below the visible ground.");
        return;
    }
    var instancePath = fixArgs[1];
    var profileName = fixArgs[2];
    var gameDataPath = fixArgs.Length > 3 && !fixArgs[3].StartsWith("--") ? fixArgs[3] : ReadGamePathFromIni(instancePath);
    var threshold = Threshold(fixArgs);
    var waterDepth = WaterDepth(fixArgs);
    var forceCorrect = fixArgs.Any(x => x.Equals("--force-correct", StringComparison.OrdinalIgnoreCase));
    var safetyCap = SafetyCap(fixArgs);

    Console.WriteLine($"MO2 instance: {instancePath}");
    Console.WriteLine($"Profile: {profileName}");
    Console.WriteLine($"Game Data path: {gameDataPath}");
    Console.WriteLine($"Threshold: {threshold} units");
    Console.WriteLine($"Water submerge depth: {waterDepth} units");
    if (forceCorrect) Console.WriteLine("Force-correct: ON (RISKY - every auto-fix cap bypassed - see usage note above)");
    else if (Math.Abs(safetyCap - SeamFinder.Core.FloatingObjectFixer.DefaultMaxAutoFixDeltaUnits) > 0.01f)
        Console.WriteLine($"Custom safety cap: {safetyCap} units (RISKY above the {SeamFinder.Core.FloatingObjectFixer.DefaultMaxAutoFixDeltaUnits:F0}-unit default - see usage note above)");
    Console.WriteLine();

    try
    {
        var resolved = Mo2Resolver.Resolve(instancePath, profileName, gameDataPath);
        Console.WriteLine($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
        if (resolved.MissingPlugins.Count > 0)
        {
            Console.WriteLine($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found:");
            foreach (var m in resolved.MissingPlugins) Console.WriteLine("  " + m);
        }

        var fixResult = FloatingObjectFixer.RunFixForResolvedPlugins(
            resolved.LoadOrder, "FloatingObjectFixes.esp", AppContext.BaseDirectory, Console.WriteLine, threshold, WorldspaceFilter(fixArgs), resolved.ResolveDataFile, waterDepth, forceCorrect, safetyCap);
        Console.WriteLine();
        Console.WriteLine($"Corrected {fixResult.RefsCorrected} reference(s).");
        Console.WriteLine($"Output: {fixResult.OutputPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine("ERROR: " + ex);
    }

    Pause();
}

float Threshold(string[] a)
{
    var arg = a.FirstOrDefault(x => x.StartsWith("--threshold=", StringComparison.OrdinalIgnoreCase));
    if (arg is null) return SeamFinder.Core.FloatingObjectFixer.DefaultThresholdUnits;
    return float.TryParse(arg["--threshold=".Length..], out var v) ? v : SeamFinder.Core.FloatingObjectFixer.DefaultThresholdUnits;
}

// --water-depth=N - how far BELOW the water plane a water-surface-fallback
// candidate (Debris/Log/Driftwood only) gets submerged. Default 48 - see
// SeamFinder.Core.FloatingObjectFixer.DefaultWaterSurfaceSubmergeMarginUnits
// for the full three-attempt history behind that default.
float WaterDepth(string[] a)
{
    var arg = a.FirstOrDefault(x => x.StartsWith("--water-depth=", StringComparison.OrdinalIgnoreCase));
    if (arg is null) return SeamFinder.Core.FloatingObjectFixer.DefaultWaterSurfaceSubmergeMarginUnits;
    return float.TryParse(arg["--water-depth=".Length..], out var v) ? v : SeamFinder.Core.FloatingObjectFixer.DefaultWaterSurfaceSubmergeMarginUnits;
}

// --safety-cap=N - a middle ground between the 150-unit default and
// --force-correct's full bypass: raises (or lowers) just the heightmap-
// only-guess tier's cap. See SeamFinder.Core.FloatingObjectFixer.
// DefaultMaxAutoFixDeltaUnits for the full reasoning on why this tier is
// riskier than the collision-confirmed/water-fallback tiers to begin with.
float SafetyCap(string[] a)
{
    var arg = a.FirstOrDefault(x => x.StartsWith("--safety-cap=", StringComparison.OrdinalIgnoreCase));
    if (arg is null) return SeamFinder.Core.FloatingObjectFixer.DefaultMaxAutoFixDeltaUnits;
    return float.TryParse(arg["--safety-cap=".Length..], out var v) ? v : SeamFinder.Core.FloatingObjectFixer.DefaultMaxAutoFixDeltaUnits;
}

// --worldspace="Tamriel" - restricts to one worldspace's EditorID. Strongly
// recommended: large custom-worldspace mods (Arnima, RigmorCyrodiil,
// Vigilant, Wyrmstooth, ...) dominate the flagged set with false positives
// otherwise - see FindCandidates in SeamFinder.Core for why.
string? WorldspaceFilter(string[] a)
{
    var arg = a.FirstOrDefault(x => x.StartsWith("--worldspace=", StringComparison.OrdinalIgnoreCase));
    return arg?[13..].Trim('"');
}

void Pause()
{
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    try { Console.ReadKey(); } catch (InvalidOperationException) { /* no console input (e.g. redirected/piped) - just exit */ }
}

static string ReadGamePathFromIni(string instancePath)
{
    var iniPath = Path.Combine(instancePath, "ModOrganizer.ini");
    if (!File.Exists(iniPath))
        throw new FileNotFoundException("No gameDataPath given and ModOrganizer.ini not found to read it from.", iniPath);

    foreach (var line in File.ReadAllLines(iniPath))
    {
        if (!line.StartsWith("gamePath=")) continue;
        var value = line["gamePath=".Length..].Trim();
        var start = value.IndexOf('(');
        var end = value.LastIndexOf(')');
        if (start >= 0 && end > start)
            value = value[(start + 1)..end];
        value = value.Replace("\\\\", "\\");
        return Path.Combine(value, "Data");
    }

    throw new InvalidOperationException("Could not find gamePath= in ModOrganizer.ini - pass gameDataPath explicitly instead.");
}
