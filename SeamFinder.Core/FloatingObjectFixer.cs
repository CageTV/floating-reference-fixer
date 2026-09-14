// Detects (and optionally corrects) placed static-ish objects (rocks, trees,
// movable statics) whose authored ground-contact point - the same point CK's
// selection marker sits at, which for almost all meshes IS the reference's
// own Position (mesh authors put the origin at the object's base by
// convention) - no longer matches the terrain height directly beneath it.
// The prompting case: a landscape height fix (SeamFixer) changes a cell's
// terrain, and a reference that was resting on the OLD terrain is left
// floating (or sunk) relative to the NEW terrain.
//
// Deliberately scoped to TERRAIN ONLY for this first pass - snapping to
// "whichever surface is actually closest, whether that's the landscape or
// another placed mesh's own collision shape" would need real 3D raycasting
// against NIF collision geometry, an entirely different (and much larger)
// kind of tool than the pure ESP-data manipulation everything else here
// does. This catches the terrain-height-mismatch case directly, which is
// the one this whole project's own height fixer can actually cause.
//
// Base-type scope: STAT, MSTT (Movable Static), TREE only - the "sits on
// the ground" class. Deliberately excludes everything else (containers,
// activators, doors, lights, flora, NPCs, ...) since those have far too
// many legitimate reasons to sit above/below the raw terrain height (a
// container on a shelf, a hanging light, a door frame, a wall-mounted
// decoration) for a blind height comparison to be trustworthy.

using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

// TerrainZ is always the RAW heightmap value beneath this reference (never
// water-adjusted) - it's also the exact value the fix stage snaps a floating
// reference's Position.Z onto (minus FixEmbedMarginUnits). Delta, by
// contrast, IS computed against the water-adjusted effective surface when
// applicable (see effectiveSurfaceZ in FindCandidates) - it exists purely to
// decide whether something counts as floating/sunk without false-flagging
// objects that are meant to rest at/near a water surface; once something is
// confirmed floating, it corrects toward the real bed, not the water plane.
// GroundConfirmedByCollision: true once ApplyCollisionAwareness has replaced
// TerrainZ/Delta with a target verified against REAL Havok collision
// geometry (not just the raw heightmap guess). See MaxAutoFixDeltaUnits vs.
// MaxAutoFixDeltaUnitsCollisionConfirmed below for why this distinction
// exists - a collision-verified target carries much less "wrong surface"
// risk than a heightmap-only one, so it earns a separate, higher auto-fix
// cap instead of being held to the same conservative limit.
//
// WaterSurfaceFallbackZ / GroundIsWaterSurfaceFallback: added 2026-09-13,
// reviving (narrowly) the water-plane-as-TARGET idea this file's own history
// tried once and reverted (see the "lily pad" comment on effectiveSurfaceZ in
// FindCandidates) - see ApplyCollisionAwareness's fallback branch for the
// full reasoning on why it's safe to bring back now, scoped only to
// WaterSurfaceRestingKeywords-matched bases with no collision answer.
// FootprintRadiusWorld: added 2026-09-13 alongside GetMaxHeightInFootprint -
// the object's own authored horizontal extent (from its base record's
// ObjectBounds), scaled by this specific reference's Scale. Used by
// ApplyCollisionAwareness to raycast across the object's real footprint
// instead of only its single origin point - "if nothing is touching it,
// flag it" needs to actually check more than one point before concluding
// nothing touches, since an off-center edge can rest on a neighbor's
// collision geometry while the origin point itself does not.
public record FloatingCandidate(
    string Worldspace, int CellX, int CellY, string OwnerPlugin,
    FormKey RefFormKey, FormKey BaseFormKey, string? BaseEditorId,
    float WorldX, float WorldY, float RefZ, float TerrainZ, float Delta,
    bool GroundConfirmedByCollision = false,
    float? WaterSurfaceFallbackZ = null,
    bool GroundIsWaterSurfaceFallback = false,
    float FootprintRadiusWorld = 0f);

public record FloatingObjectReportResult(List<string> ReportCsvLines, int CandidatesChecked, int Flagged);

public record FloatingObjectFixResult(int RefsCorrected, string OutputPath);

public static class FloatingObjectFixer
{
    // How far a reference's Z can sit from the terrain directly beneath it
    // before it's worth flagging. One heightmap step is 8 units; this is
    // deliberately many steps above that noise floor so ordinary "half-
    // buried rock" authoring (very common, deliberate) doesn't get flagged -
    // tune via the CLI/UI, there's no universally-right answer since it
    // really depends on each mesh's own bounding box, which this tool can't
    // see without loading the NIF.
    public const float DefaultThresholdUnits = 96f;

    // A quad whose 4 corner vertices span more than this counts as "too
    // rugged to trust a bilinear estimate" - see GetLocalRoughness. 500
    // units is well above ordinary rolling-hill variance (a few heightmap
    // steps of 8 units each) but well below a real alpine cliff face.
    const float MaxRoughnessForReliableCheck = 500f;

    // See HeightmapDecoder.GetNearbyTerrainVariance for the full reasoning -
    // confirmed necessary on a real 2026-09-12 live test where a cliff-edge
    // rock's own immediate quad read as smooth (Roughness=72) while terrain
    // 256 units away dropped by ~198 units, causing the auto-fixer to snap
    // it toward that wrong, much-lower height. A confirmed genuine floating
    // bug on the same profile showed ~114 units of nearby variance at the
    // same radius, so this threshold sits between the two - closer to the
    // confirmed-bad case, on the assumption that a false "fix" that visibly
    // breaks a scene is worse than leaving a real bug unflagged for one more
    // pass.
    const float MaxNearbyVarianceForReliableCheck = 150f;

    // Confirmed necessary on the fourth live-profile test, and the deeper
    // reason the roughness gate above isn't enough on its own: in
    // mountainous regions (confirmed with real Falkreath-area examples,
    // including VANILLA Skyrim.esm's own trees, which rules out "mod bug"
    // entirely), a tree or rock is often resting on a large rock/cliff
    // STATIC MESH sitting on top of the LAND heightmap, not on the raw
    // terrain itself - the exact "closest surface, whether terrain or
    // another mesh's geometry" gap this file's header scopes out of v1. The
    // local 4 corners can read as smooth (low roughness) while still being
    // tens of thousands of units away from the true surface, because that
    // surface simply isn't IN the heightmap data at all. A real "landscape
    // fix left this floating" bug is a few hundred to low thousands of
    // units, never tens of thousands - so delta magnitude itself is turned
    // into a second discriminator: too large means "probably resting on
    // unmodeled geometry, not a real terrain-height bug," and is excluded
    // rather than reported, since blindly "fixing" it would yank the object
    // to a nonsensical position (e.g. a mountainside tree down to the
    // valley floor).
    const float MaxPlausibleDeltaUnits = 3000f;

    // Confirmed necessary on a real 2026-09-12 late-night test: MaxPlausibleDeltaUnits
    // above only excludes the EXTREME version of "resting on unmodeled geometry"
    // (mountainside-scale, thousands of units). A MEDIUM-scale version of the exact
    // same problem is common enough to be unsafe to auto-correct: confirmed in two
    // unrelated places the same night - natural rock/bank ledges around a
    // RoadMaskMerge-carved deep pool (vanilla terrain there was 413-565, genuinely
    // edited down to 100-210, so the vanilla-diff gate correctly does NOT exclude it -
    // but unmodeled bank/rock geometry the heightmap can't see still sits well above
    // that lowered raw terrain), and independently at a Riverwood stone bridge with no
    // connection to RoadMaskMerge at all. Both cases auto-corrected to a position BELOW
    // the actual rendered ground (confirmed via screenshots: camera placed underground
    // looking up at the terrain's underside) - a worse, more visually broken outcome
    // than the original floating bug. Since the tool has no 3D collision awareness (see
    // file header) it cannot tell "the raw terrain here really is the new resting spot"
    // from "there's unmodeled geometry above the raw terrain here" - both this and the
    // "lily pad" water-surface regression are the same underlying gap. Rather than
    // guess at a smarter heuristic that will just find a THIRD unmodeled-geometry class
    // next time, auto-correction is capped to deltas small enough that being wrong
    // about the target is low-consequence: a candidate between this cap and
    // MaxPlausibleDeltaUnits is still flagged and reported (so nothing is hidden), it
    // simply is not auto-written into the fix plugin - left for manual review/placement
    // instead, per explicit user decision after reviewing both regression classes.
    // Made a caller-adjustable PARAMETER (default below), not a fixed
    // constant, from 2026-09-13 onward - user asked for a middle ground
    // between the conservative 150-unit default and full force-correct
    // (which bypasses this cap entirely): a custom threshold, clearly
    // warned, so items in the 150-N unit range get corrected while still
    // capping the riskiest large-delta guesses. Exposed in the UI as a
    // second checkbox + numeric field, off by default (uses this constant).
    public const float DefaultMaxAutoFixDeltaUnits = 150f;

    // Added 2026-09-13 once real Havok collision-awareness went live: the
    // 150-unit cap above exists purely because a heightmap-only target is a
    // GUESS at "is there unmodeled geometry hiding above the raw terrain
    // here" - being wrong about that guess is exactly what pushed objects
    // below the visible ground (see MaxAutoFixDeltaUnits's own comment).
    // Once ApplyCollisionAwareness has actually found and raycast real
    // nearby collision geometry for a candidate (GroundConfirmedByCollision
    // = true), the target Z is no longer a guess - it's a measured surface,
    // so a wrong-magnitude correction is far less likely. The remaining risk
    // for a collision-confirmed candidate is the OTHER unverified piece,
    // CollisionRaycaster's rotation-convention assumption (see that file's
    // header) - a horizontal/orientation risk, not a "how far to move it"
    // risk, so it doesn't argue for keeping the same tight delta cap. Chosen
    // as the same order of magnitude as the confirmed real medium-scale
    // unmodeled-geometry cases from the same night (300-500 units), with
    // headroom, while staying well under MaxPlausibleDeltaUnits's 3000-unit
    // mountainside-scale sanity cutoff - a first cut, not yet confirmed
    // against a real collision-refined large-delta case in-game.
    const float MaxAutoFixDeltaUnitsCollisionConfirmed = 1000f;

    // Cap for the water-surface FALLBACK tier (see WaterSurfaceRestingKeywords
    // / GroundIsWaterSurfaceFallback below) - a real, measured value (the
    // cell's own WaterHeight) but still a HEURISTIC guess about what this
    // specific object should rest on (unlike the collision tier, which is a
    // directly raycast surface), so it sits between the two: looser than the
    // 150-unit pure-heightmap-guess cap, tighter than the 1000-unit
    // collision-confirmed one. 600 comfortably covers the confirmed real
    // driftwood/debris cases from 2026-09-13 (forestdebris06 at 489 units)
    // with headroom, without opening the door to large, likely-wrong
    // corrections.
    const float MaxAutoFixDeltaUnitsWaterSurfaceFallback = 600f;

    // How far BELOW the exact interpolated terrain surface a corrected
    // FLOATING object gets placed, instead of exactly flush onto it (Z =
    // TerrainZ - FixEmbedMarginUnits, not Z = TerrainZ). Keeps the result
    // reading as solidly grounded rather than mathematically-perfect-but-
    // fragile - a hair's-width above the true surface still reads as
    // "floating" in-game (lighting/shadow, or the terrain estimate being a
    // few units off), while a small amount of burial never looks wrong.
    // Small and fixed rather than mesh-aware (no real bounding-box/NIF data
    // available - see file header); deliberately modest so it can't itself
    // bury a small object unreasonably.
    //
    // ONLY applies to solid-ground targets (heightmap-only or collision-
    // confirmed) - see DefaultWaterSurfaceSubmergeMarginUnits below for why
    // the water-surface fallback tier deliberately does NOT reuse this constant.
    const float FixEmbedMarginUnits = 16f;

    // How far BELOW the water plane a water-surface-fallback-corrected
    // object gets placed. History of this constant, both directions tried
    // and rejected in one 2026-09-13 session - see the "REGRESSED" comment
    // in FindCandidates on effectiveSurfaceZ for the original lily-pad
    // revert this all traces back to:
    //   1. Originally reused FixEmbedMarginUnits verbatim (Z = WaterHeight -
    //      16) - confirmed via the live log to be the EXACT numeric target
    //      the already-reverted blanket water-plane lily-pad logic produced,
    //      which is why it looked like "back where we started."
    //   2. Flipped to a small POSITIVE offset (Z = WaterHeight + 8) on the
    //      theory that debris should float ON water, not embed in it like a
    //      rock in dirt - WRONG in practice: the user's in-game retest of
    //      forestdebris05/06 (screenshots, 2026-09-13 ~11:06) showed the log
    //      perched up on the dry mossy bank, disconnected from the stream
    //      entirely - visually worse than either prior state. The user's own
    //      nearby vanilla reference points (rocks/roots in the same rapids,
    //      confirmed sitting PARTLY SUBMERGED, not floating on top) are the
    //      correct vanilla analogue per this project's own core principle -
    //      floating debris in a rapids should look wedged in the bed, mostly
    //      underwater, same as the rocks around it.
    //   3. User's explicit correction: go DOWN, not up, and further than the
    //      original -16 - "given the difference in water heights I'd go 3x
    //      as much downward." A likely contributing factor (not yet
    //      independently confirmed, flagged for later): a rapids/sloped
    //      creek doesn't have one flat surface across a whole cell the way
    //      cell.WaterHeight assumes - the user's own player.getpos check
    //      near this exact spot read Z=658.71, well above the 600 this code
    //      uses, while the object still looked too HIGH, not too low -
    //      consistent with wanting to bury it further below the (single,
    //      approximate) water-plane value this tool has, not just correct
    //      for a slope mismatch.
    // Chosen as 3x FixEmbedMarginUnits (16*3=48) in the SAME negative
    // direction as ground-embedding, not FixEmbedMarginUnits itself, to keep
    // the "how much deeper than plain ground-embedding" scaling explicit
    // rather than a magic new number.
    //
    // Made a caller-adjustable PARAMETER (default below), not a fixed
    // constant, from 2026-09-13 onward - three different values were already
    // tried and rejected in one session (see history above), and river/creek
    // water depth obviously varies scene to scene (a shallow brook vs. a deep
    // pool), so no single hardcoded number can be right everywhere. The UI
    // exposes this as "Water submerge depth," persisted the same way as
    // ThresholdUnits/WorldspaceFilter.
    public const float DefaultWaterSurfaceSubmergeMarginUnits = FixEmbedMarginUnits * 3f;

    public static FloatingObjectReportResult RunDetectionForResolvedPlugins(
        List<Mo2Resolver.ResolvedPlugin> loadOrder, Action<string> log, float thresholdUnits = DefaultThresholdUnits, string? worldspaceFilter = null,
        Func<string, string?>? resolveAssetPath = null)
    {
        var mergedFolder = Path.Combine(Path.GetTempPath(), "FloatingObjectMerged-" + Guid.NewGuid().ToString("N"));
        log($"Staging {loadOrder.Count} plugin files into {mergedFolder} ...");
        Mo2Resolver.MaterializeMergedFolder(loadOrder, mergedFolder);
        try
        {
            var modKeys = loadOrder.Select(p => ModKey.FromFileName(p.FileName)).ToArray();
            using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
                .Create(GameRelease.SkyrimSE)
                .WithLoadOrder(modKeys)
                .WithTargetDataFolder(mergedFolder)
                .Build();
            var priorityIndex = modKeys.Select((k, idx) => (k, idx)).ToDictionary(x => x.k, x => x.idx);
            var candidates = FindCandidates(env.LinkCache, priorityIndex, thresholdUnits, log, worldspaceFilter, resolveAssetPath);
            return BuildReport(candidates);
        }
        finally
        {
            try { Directory.Delete(mergedFolder, recursive: true); }
            catch (Exception ex) { log($"(could not clean up temp folder {mergedFolder}: {ex.Message})"); }
        }
    }

    public static FloatingObjectReportResult RunDetectionForDirectDataFolder(
        string dataFolderPath, Action<string> log, float thresholdUnits = DefaultThresholdUnits, string? worldspaceFilter = null)
    {
        using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
            .Create(GameRelease.SkyrimSE)
            .WithTargetDataFolder(dataFolderPath)
            .Build();
        var priorityIndex = env.LoadOrder.ListedOrder
            .Select((listing, idx) => (listing.ModKey, idx))
            .ToDictionary(x => x.ModKey, x => x.idx);
        Func<string, string?> resolveAssetPath = rel =>
        {
            var p = Path.Combine(dataFolderPath, rel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(p) ? p : null;
        };
        var candidates = FindCandidates(env.LinkCache, priorityIndex, thresholdUnits, log, worldspaceFilter, resolveAssetPath);
        return BuildReport(candidates);
    }

    // Fix mode: writes Position.Z ONLY (never X/Y, never rotation/scale) for
    // every flagged reference, snapping it exactly onto the terrain height
    // beneath it. Always a NEW patch, originals untouched - same pattern as
    // SeamFixer/TextureLayerFixer.
    public static FloatingObjectFixResult RunFixForResolvedPlugins(
        List<Mo2Resolver.ResolvedPlugin> loadOrder, string outputPluginName, string outputDirectory,
        Action<string> log, float thresholdUnits = DefaultThresholdUnits, string? worldspaceFilter = null,
        Func<string, string?>? resolveAssetPath = null,
        float waterSubmergeMarginUnits = DefaultWaterSurfaceSubmergeMarginUnits,
        bool forceCorrectCappedItems = false,
        float heightmapOnlyCapUnits = DefaultMaxAutoFixDeltaUnits)
    {
        var mergedFolder = Path.Combine(Path.GetTempPath(), "FloatingObjectMerged-" + Guid.NewGuid().ToString("N"));
        log($"Staging {loadOrder.Count} plugin files into {mergedFolder} ...");
        Mo2Resolver.MaterializeMergedFolder(loadOrder, mergedFolder);
        try
        {
            var modKeys = loadOrder.Select(p => ModKey.FromFileName(p.FileName)).ToArray();
            using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
                .Create(GameRelease.SkyrimSE)
                .WithLoadOrder(modKeys)
                .WithTargetDataFolder(mergedFolder)
                .Build();
            var priorityIndex = modKeys.Select((k, idx) => (k, idx)).ToDictionary(x => x.k, x => x.idx);
            return GenerateFixPluginCore(env.LinkCache, priorityIndex, mergedFolder, outputPluginName, outputDirectory, log, thresholdUnits, worldspaceFilter, resolveAssetPath, waterSubmergeMarginUnits, forceCorrectCappedItems, heightmapOnlyCapUnits);
        }
        finally
        {
            try { Directory.Delete(mergedFolder, recursive: true); }
            catch (Exception ex) { log($"(could not clean up temp folder {mergedFolder}: {ex.Message})"); }
        }
    }

    public static FloatingObjectFixResult RunFixForDirectDataFolder(
        string dataFolderPath, string outputPluginName, string outputDirectory,
        Action<string> log, float thresholdUnits = DefaultThresholdUnits, string? worldspaceFilter = null,
        float waterSubmergeMarginUnits = DefaultWaterSurfaceSubmergeMarginUnits,
        bool forceCorrectCappedItems = false,
        float heightmapOnlyCapUnits = DefaultMaxAutoFixDeltaUnits)
    {
        using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
            .Create(GameRelease.SkyrimSE)
            .WithTargetDataFolder(dataFolderPath)
            .Build();
        var priorityIndex = env.LoadOrder.ListedOrder
            .Select((listing, idx) => (listing.ModKey, idx))
            .ToDictionary(x => x.ModKey, x => x.idx);
        Func<string, string?> resolveAssetPath = rel =>
        {
            var p = Path.Combine(dataFolderPath, rel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(p) ? p : null;
        };
        return GenerateFixPluginCore(env.LinkCache, priorityIndex, dataFolderPath, outputPluginName, outputDirectory, log, thresholdUnits, worldspaceFilter, resolveAssetPath, waterSubmergeMarginUnits, forceCorrectCappedItems, heightmapOnlyCapUnits);
    }

    static FloatingObjectFixResult GenerateFixPluginCore(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        string dataFolderForWrite,
        string outputPluginName,
        string outputDirectory,
        Action<string> log,
        float thresholdUnits,
        string? worldspaceFilter,
        Func<string, string?>? resolveAssetPath = null,
        float waterSubmergeMarginUnits = DefaultWaterSurfaceSubmergeMarginUnits,
        bool forceCorrectCappedItems = false,
        float heightmapOnlyCapUnits = DefaultMaxAutoFixDeltaUnits)
    {
        var candidates = FindCandidates(linkCache, priorityIndex, thresholdUnits, log, worldspaceFilter, resolveAssetPath);
        if (forceCorrectCappedItems)
            log("  ** Force-correct enabled: every tier's auto-fix cap is bypassed (up to the 3000-unit " +
                "mountainside sanity ceiling). This reproduces the 2026-09-12 pre-cap behavior that pushed " +
                "several objects underground - review the result carefully before trusting it in-game. **");
        else if (Math.Abs(heightmapOnlyCapUnits - DefaultMaxAutoFixDeltaUnits) > 0.01f)
            log($"  ** Custom heightmap-only safety threshold: {heightmapOnlyCapUnits:F0} units (default " +
                $"{DefaultMaxAutoFixDeltaUnits:F0}). Raising this trades safety for coverage - items in the " +
                "widened range get corrected against a raw heightmap GUESS, not a confirmed real surface, " +
                "and CAN end up placed below the visible ground. Review the result carefully. **");

        var outputModKey = ModKey.FromNameAndExtension(outputPluginName);
        var patchMod = new SkyrimMod(outputModKey, SkyrimRelease.SkyrimSE);
        int corrected = 0;
        int leftSunk = 0;
        int leftTooLargeToAutoFix = 0;

        // Only FLOATING candidates (Delta > 0 - the reference sits above the
        // terrain beneath it) get corrected. SUNK candidates (Delta < 0) are
        // reported but deliberately left untouched here - confirmed as a
        // real, in-game-visible bug on the user's first real test
        // (2026-09-11): this used to snap EVERY flagged candidate, floating
        // OR sunk, flush onto the exact terrain surface. Many of the
        // natural-static meshes this tool targets (rocks/boulders
        // especially - see NaturalStaticKeywords) are deliberately authored
        // with a large chunk of their mass buried below their own Position
        // origin, so "this reference sits N units below the terrain" is
        // very often just that mesh's normal, intentional embedding - not a
        // bug this tool can safely second-guess without real mesh geometry
        // (which it deliberately doesn't load - see file header). Raising
        // one of those flush to the bare surface visibly LIFTS it relative
        // to its own authored silhouette - exactly the "placing them in the
        // air" symptom reported. Floating has no equivalent legitimate
        // authoring convention (nothing is MEANT to hover unsupported), so
        // it's the only direction this tool can safely auto-correct.
        foreach (var c in candidates)
        {
            if (c.Delta <= 0f)
            {
                log($"  Left alone (sunk, not floating) [{c.Worldspace}] ({c.CellX},{c.CellY}) ref {c.RefFormKey}: sits {Math.Abs(c.Delta):F0} units below the terrain - likely deliberate embedding, not corrected.");
                leftSunk++;
                continue;
            }

            // See MaxAutoFixDeltaUnits / MaxAutoFixDeltaUnitsCollisionConfirmed /
            // MaxAutoFixDeltaUnitsWaterSurfaceFallback above - a large correction is
            // exactly the case where "resting on unmodeled geometry the heightmap
            // can't see" is most likely, and getting it wrong here means writing a
            // position BELOW the visible ground, which is worse than leaving the
            // original bug alone. Three tiers, most to least trustworthy: a target
            // VERIFIED against real collision geometry, a target from the narrow
            // water-surface fallback (a real measured value, but still a guess about
            // THIS object), or a bare heightmap guess. Still reported (candidates
            // list / CSV is unaffected) either way, just not auto-written into the
            // fix plugin.
            var (effectiveCap, capReason) = (forceCorrectCappedItems, c) switch
            {
                (true, _) => (MaxPlausibleDeltaUnits, "FORCED - cap bypassed"),
                (false, { GroundConfirmedByCollision: true }) => (MaxAutoFixDeltaUnitsCollisionConfirmed, "collision-confirmed target"),
                (false, { GroundIsWaterSurfaceFallback: true }) => (MaxAutoFixDeltaUnitsWaterSurfaceFallback, "water-surface fallback target"),
                (false, _) => (heightmapOnlyCapUnits, "heightmap-only target"),
            };
            if (c.Delta > effectiveCap)
            {
                log($"  Left alone (flagged, not auto-fixed) [{c.Worldspace}] ({c.CellX},{c.CellY}) ref {c.RefFormKey}: floating by {c.Delta:F0} units, over the {effectiveCap:F0}-unit auto-fix cap ({capReason}) - review manually, see report.");
                leftTooLargeToAutoFix++;
                continue;
            }

            if (!linkCache.TryResolveContext<IPlacedObject, IPlacedObjectGetter>(c.RefFormKey, out var refContext))
                continue;
            var writable = refContext.GetOrAddAsOverride(patchMod);
            if (writable.Placement is null) continue;
            var pos = writable.Placement.Position;
            // Water-surface-fallback targets submerge deeper BELOW the water
            // plane (WaterSurfaceSubmergeMarginUnits, 3x the ground margin) -
            // see that constant's comment for the two directions already
            // tried and rejected before this one.
            var correctedZ = c.GroundIsWaterSurfaceFallback
                ? c.TerrainZ - waterSubmergeMarginUnits
                : c.TerrainZ - FixEmbedMarginUnits;
            writable.Placement.Position = new Noggog.P3Float(pos.X, pos.Y, correctedZ);
            log($"  Corrected [{c.Worldspace}] ({c.CellX},{c.CellY}) ref {c.RefFormKey}: Z {c.RefZ:F0} -> {correctedZ:F0} (was floating by {c.Delta:F0} units)");
            corrected++;
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, outputPluginName);
        log($"Writing patch plugin to {outputPath} ...");
        SkyrimMod.WriteBuilder(SkyrimRelease.SkyrimSE)
            .ToPath(outputPath, fileSystem: null)
            .WithNoLoadOrder()
            .WithDataFolder(dataFolderForWrite)
            .WithAllParentMasters()
            .Write(patchMod);

        log($"Corrected {corrected} floating reference(s); left {leftSunk} sunk/buried and {leftTooLargeToAutoFix} over the auto-fix delta cap untouched, out of {candidates.Count} flagged total.");
        return new FloatingObjectFixResult(corrected, outputPath);
    }

    static List<FloatingCandidate> FindCandidates(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        float thresholdUnits,
        Action<string> log,
        string? worldspaceFilter = null,
        Func<string, string?>? resolveAssetPath = null)
    {
        var candidates = new List<FloatingCandidate>();
        int checkedCount = 0;
        // (worldspace, gridX, gridY) -> cell FormKey, built alongside the main
        // loop below so the collision-awareness pass (after this loop) can
        // look up a candidate's neighbor cells without a second full scan.
        var cellGridIndex = new Dictionary<(string Worldspace, int X, int Y), FormKey>();

        foreach (var context in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
        {
            var cell = context.Record;
            if (cell.Grid is null) continue;
            if (!context.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsContext)) continue;
            var wsName = wsContext.Record.EditorID ?? wsContext.Record.FormKey.ToString();
            cellGridIndex[(wsName, cell.Grid.Point.X, cell.Grid.Point.Y)] = cell.FormKey;
            // Confirmed necessary in practice: large custom-worldspace mods
            // (Arnima, RigmorCyrodiil, Vigilant's zCHMolagWorld, Wyrmstooth,
            // ...) are so heavily rock/cliff-mesh-decorated that they
            // dominate the flagged set with the "resting on unmodeled
            // geometry" false-positive class even after every filter above -
            // 77% of one real run's flags came from just 5 such worldspaces,
            // while Tamriel (the worldspace this whole project's height
            // fixer actually targets) had a much more reasonable count.
            // Restricting to one worldspace at a time is the practical way
            // to get a genuinely reviewable report until this tool gains
            // real collision awareness.
            if (worldspaceFilter is not null && !wsName.Equals(worldspaceFilter, StringComparison.OrdinalIgnoreCase)) continue;

            var (landscape, _) = HeightmapDecoder.ResolveWinningLandscape(cell.FormKey, linkCache, priorityIndex);
            if (landscape?.VertexHeightMap is null) continue;
            var heights = HeightmapDecoder.DecodeHeights(landscape.VertexHeightMap);

            // This tool's whole premise (see file header) is that a
            // landscape EDIT stranded a reference that used to rest on the
            // OLD terrain. Vanilla's own copy of this cell's Landscape (null
            // if this cell doesn't originate in Skyrim.esm, e.g. a custom
            // worldspace) lets each reference below be checked against that
            // premise AT ITS OWN EXACT SPOT, not just "did this cell change
            // ANYWHERE" - a cell can have one corner genuinely re-terraformed
            // (a road smoothed by Northern Roads) while a totally unrelated
            // corner has an untouched vanilla mountainside; a whole-cell gate
            // conflates the two. Confirmed necessary in practice: an earlier,
            // whole-cell version of this gate still let through dozens of
            // vanilla Skyrim.esm rocks/trees "corrected" by 1000-3000 units
            // in cells that had some unrelated, tiny genuine edit elsewhere.
            var vanillaHeights = TryGetVanillaHeights(cell.FormKey, linkCache);

            // Confirmed necessary on a real 2026-09-12 live test: a creek's
            // raw VHGT terrain (the actual stream bed) sits well BELOW the
            // water plane above it by design - that's what makes it a creek.
            // Rapids rocks, wet-rocks, reeds, and a bridge's own deck/support
            // pieces are all authored to sit AT OR ABOVE the water surface,
            // never on the submerged bed beneath it, so "terrainZ" is never
            // a valid correction target for anything sitting in that
            // situation. Confirmed on a real cell: WaterHeight=600 while
            // RoadMaskMerger's own (correctly-carved) creek-bed terrain read
            // around -85 to -175 - the auto-fixer had been snapping wet-
            // rocks, a rapids FX prop, and even a genuine tree hundreds of
            // units down into the submerged bed, nowhere near the visible
            // water surface at all. Same "never touch water" principle this
            // project's own SeamFixer already applies to its own corrections
            // (see SeamFixer.cs) - this tool needed the same guard.
            var genuineWaterHeight = (cell.WaterHeight.HasValue && cell.WaterHeight.Value < 1_000_000f)
                ? cell.WaterHeight.Value : (float?)null;

            var cellOriginX = cell.Grid.Point.X * 4096f;
            var cellOriginY = cell.Grid.Point.Y * 4096f;

            foreach (var placed in GetAllPlacedObjectsInCell(cell.FormKey, linkCache))
            {
                if (placed.Placement is null) continue;
                if (!IsGroundRestingBase(placed.Base, linkCache, out var baseFormKey, out var baseEditorId, out var footprintRadiusLocal)) continue;

                var pos = placed.Placement.Position;
                // ObjectBounds is authored in the mesh's own LOCAL space: a
                // reference placed at 2x scale has a footprint twice as wide
                // in the WORLD as its base record's raw bounding box implies.
                var footprintRadiusWorld = footprintRadiusLocal * (placed.Scale ?? 1.0f);
                // Standard Bethesda record-header bit for "initially
                // disabled" (0x800) - a disabled placeholder/reserve
                // reference isn't really floating anywhere; it's not
                // supposed to be visible or physical at all.
                if ((placed.MajorRecordFlagsRaw & 0x800) != 0) continue;

                // Bethesda's own engine convention for "exists but is
                // deliberately stored away from the playable map" (a
                // disabled/reserve/quest-stage-swap reference, common
                // throughout the base game and mods) - confirmed on the very
                // first live-profile test (a road chunk at Z=300,007,072,
                // and a wave of exact/near-exact Z=-30000 refs on the
                // second). Not a real floating-object bug; skip before it
                // even counts toward checkedCount.
                if (Math.Abs(pos.Z) > 25000f) continue;

                checkedCount++;
                var localX = pos.X - cellOriginX;
                var localY = pos.Y - cellOriginY;
                // Only judge references whose (X,Y) actually falls inside
                // this cell's own 4096x4096 footprint - a ref can be listed
                // under a cell while sitting just across the boundary in
                // rare cases, and comparing against the WRONG cell's
                // heightmap would be worse than not checking it at all.
                if (localX is < 0f or > 4096f || localY is < 0f or > 4096f) continue;

                // Skip anywhere the local terrain is too rugged for a
                // bilinear estimate between 4 corners to mean anything - see
                // GetLocalRoughness. A real, deliberately-placed object on
                // steep alpine terrain isn't a bug just because the coarse
                // heightmap grid can't represent that terrain well.
                if (HeightmapDecoder.GetLocalRoughness(heights, localX, localY) > MaxRoughnessForReliableCheck) continue;

                // REMOVED 2026-09-13, same session: this gate (a wider-radius
                // companion to the roughness check above, meant to catch a
                // cliff edge just past the object's own immediate quad) was
                // confirmed via direct diagnostic to be hiding a REAL,
                // massively-floating reference, not just false positives -
                // `angel wing begonia 2` (ref 4F141ECB, cell -12,-15) floats
                // by a genuine 781 units (vanilla terrain 618 -> RoadMaskMerge-
                // edited 235, a real edit the vanilla-diff gate correctly does
                // NOT exclude), but GetNearbyTerrainVariance measured 237 at
                // this exact spot - over the old 150-unit gate - so this
                // candidate was excluded before it was even ADDED to the
                // candidates list, invisible to the report AND to
                // ApplyCollisionAwareness, not just excluded from auto-fix.
                // The RoadMaskMerge-carved deep pool this sits in is exactly
                // the kind of terrain a legitimate, wanted correction target
                // looks like on a heightmap - high nearby variance is not a
                // reliable signal to distinguish "cliff-edge false positive"
                // from "real carved-pool edit" after all. Two remaining
                // safety nets are untouched and still catch the ORIGINAL
                // false-positive class this gate targeted: the vanilla-diff
                // gate above (skips anything the landscape edit didn't touch)
                // and MaxPlausibleDeltaUnits below (3000-unit mountainside
                // sanity ceiling, still excludes true alpine-scale cases).
                // GetLocalRoughness's own narrower, tighter 500-unit gate is
                // NOT touched by this change - it wasn't the cause here
                // (168, well under its gate) and isn't proven unsafe.

                // REVERTED 2026-09-13, same session: tried GetMaxHeightInFootprint
                // here (footprint-aware terrain, ring-sampled at the object's own
                // ObjectBounds radius) per the user's own request, but real-data
                // verification caught a serious regression before deployment -
                // raw flagged-candidate count jumped 3365 -> 28703 on an
                // unrelated test run, checkedCount unchanged, so the ring
                // sampling itself was the cause. Root cause: a typical small
                // object's footprint HALF-EXTENT is already 100-180 units (real
                // measured values: forestdebris05 ~178, rubus01 ~163), and
                // sampling a full ring at THAT radius in all 8 directions
                // frequently reaches terrain that is NOT actually under the
                // object at all (a nearby slope, an unrelated rock's own local
                // rise) - "highest point within a ~350-unit-diameter circle" is a
                // much bigger, noisier area than "the ground actually beneath
                // this object's real footprint," and MAX-only can never correct
                // back down once it picks up a spurious high point. Reverted to
                // plain center-point sampling (GetInterpolatedHeight) rather than
                // ship something proven worse than the baseline. The idea
                // (footprint-aware terrain) may still be worth revisiting with a
                // much smaller/more conservative radius or an averaging (not
                // max-only) approach, but that needs its own careful redesign and
                // verification, not a quick fix - see
                // [[project_floatingobjectfixer_overhaul]] for the full note.
                var terrainZ = HeightmapDecoder.GetInterpolatedHeight(heights, localX, localY);

                // The precise form of the vanilla-vs-edited check described
                // above: if the terrain right HERE (not just somewhere in
                // the cell) is unchanged from vanilla, this reference's
                // apparent float/sink predates any mod's landscape edit
                // entirely and isn't this tool's problem to fix (almost
                // always the "resting on an unmodeled rock/cliff mesh"
                // authoring quirk instead). Deliberately checked against the
                // RAW terrain (not the water-adjusted surface below), since
                // this is about whether the LANDSCAPE changed, not about
                // where an object should rest.
                if (vanillaHeights is not null
                    && Math.Abs(terrainZ - HeightmapDecoder.GetInterpolatedHeight(vanillaHeights, localX, localY)) < 1f)
                    continue;

                // Revision of the original genuineWaterHeight gate, which
                // just `continue`d (skipped entirely) whenever the raw
                // terrain here was submerged. Confirmed too blunt on a real
                // 2026-09-12 retest: it correctly protected reeds/rapids-
                // rocks already resting near the water surface (their delta
                // against raw creek-bed terrain was huge, but they were never
                // actually wrong), but it ALSO left a genuinely floating
                // forestdebris log hanging in mid-air above the same stream,
                // since detection never ran at all. Using the water plane
                // as the effective surface fixes the GATING decision (a
                // rock/reed already sitting near the water now computes a
                // small delta and is correctly left alone) - but a SECOND,
                // later 2026-09-12 retest (after this fix was live and
                // verified working) showed it's the WRONG correction TARGET:
                // a terrestrial plant (angel wing begonia, not an aquatic
                // species) corrected to the water plane visibly floated flat
                // on the surface like a lily pad, and confirmed via a full
                // Wet/rapids scan of the report, NONE of the vanilla objects
                // actually authored to rest at the water surface (RockL02Wet,
                // RockL04Wet, FXrapidsRocks*) ever have a positive delta here
                // in practice - they're always excluded as "sunk" before
                // reaching the correction step regardless of which surface is
                // used as the target. So the water plane is only ever needed
                // for the gating math; once something IS confirmed floating,
                // it should sink toward the real bed like physical debris
                // would (the vanilla precedent this project's own principles
                // call for), not hover at the surface - even when that bed is
                // in a deep, dark pool the player can't fully see, per the
                // user's explicit preference after reviewing both outcomes.
                var effectiveSurfaceZ = (genuineWaterHeight is not null && terrainZ < genuineWaterHeight.Value)
                    ? genuineWaterHeight.Value
                    : terrainZ;

                var delta = pos.Z - effectiveSurfaceZ;
                if (Math.Abs(delta) < thresholdUnits) continue;
                if (Math.Abs(delta) > MaxPlausibleDeltaUnits) continue;

                // The plugin that actually wins THIS reference, not just
                // whichever plugin wins the cell overall - a later plugin
                // can override one specific ref's position without ever
                // touching the rest of the cell.
                var ownerPlugin = linkCache.TryResolveContext<IPlacedObject, IPlacedObjectGetter>(placed.FormKey, out var refCtx)
                    ? refCtx.ModKey.FileName
                    : placed.FormKey.ModKey.FileName;

                // TerrainZ on the candidate is the raw bed - see the comment
                // above on why the correction TARGET deliberately diverges
                // from effectiveSurfaceZ (used only for the delta/gating
                // decision just above). WaterSurfaceFallbackZ carries the
                // water plane forward too, but ONLY for the narrow
                // WaterSurfaceRestingKeywords-matched subset AND only when
                // genuinely underwater - ApplyCollisionAwareness decides
                // whether to actually use it, as a last-resort fallback
                // when it can't find real collision geometry either.
                var waterSurfaceFallbackZ = (genuineWaterHeight is not null && terrainZ < genuineWaterHeight.Value
                    && baseEditorId is not null
                    && WaterSurfaceRestingKeywords.Any(kw => baseEditorId.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                    ? genuineWaterHeight
                    : null;

                candidates.Add(new FloatingCandidate(
                    wsName, cell.Grid.Point.X, cell.Grid.Point.Y, ownerPlugin,
                    placed.FormKey, baseFormKey, baseEditorId,
                    pos.X, pos.Y, pos.Z, terrainZ, delta,
                    WaterSurfaceFallbackZ: waterSurfaceFallbackZ,
                    FootprintRadiusWorld: footprintRadiusWorld));
            }
        }

        log($"Checked {checkedCount} ground-resting placed objects; {candidates.Count} sit more than {thresholdUnits:F0} units from the terrain beneath them.");

        if (resolveAssetPath is not null && candidates.Count > 0)
            candidates = ApplyCollisionAwareness(candidates, cellGridIndex, linkCache, resolveAssetPath, thresholdUnits, log);

        return candidates;
    }

    // Real Havok collision-geometry pass, added 2026-09-12 - see
    // CollisionRaycaster.cs for the full "why" (the heightmap-only approach
    // above sends objects below the visible ground whenever unmodeled rock/
    // architecture geometry sits above the raw terrain; confirmed in two
    // unrelated real locations the same night). This runs AFTER the
    // heightmap pass, only on what it already flagged - it REFINES candidates,
    // it never discovers new ones the heightmap pass excluded.
    //
    // For each candidate: gather every OTHER placed STAT/MSTT reference within
    // COLLISION_SEARCH_RADIUS of its (X,Y) (its own cell + the 8 neighbors, via
    // cellGridIndex), resolve their real collision geometry, and raycast
    // straight down/up through that geometry at the candidate's (X,Y). If real
    // collision is found HIGHER than the raw heightmap terrainZ, that becomes
    // the candidate's new TerrainZ (the correction target) AND is used to
    // recompute Delta (the gating/threshold decision) - both were previously
    // heightmap-only. A candidate whose refined delta drops back under
    // thresholdUnits is REMOVED from the list entirely: it was never actually
    // floating, just resting on geometry the heightmap couldn't see (the exact
    // "resting on unmodeled rock/cliff mesh" false-positive class this file's
    // header has described as out of reach since the very first version).
    static List<FloatingCandidate> ApplyCollisionAwareness(
        List<FloatingCandidate> candidates,
        Dictionary<(string Worldspace, int X, int Y), FormKey> cellGridIndex,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Func<string, string?> resolveAssetPath,
        float thresholdUnits,
        Action<string> log)
    {
        const float CollisionSearchRadius = 700f;

        // Per-candidate: (nifPath, world position/rotation/scale) for every
        // nearby STAT/MSTT instance worth checking. Built in one pass so every
        // unique NIF across the WHOLE candidate set is extracted exactly once
        // (see CollisionRaycaster.ExtractCollisionBatch) rather than once per
        // candidate that happens to be near it.
        var perCandidateInstances = new List<List<CollisionCheckInstance>>();
        // Diagnostic-only counters, parallel to perCandidateInstances - added
        // 2026-09-13 after a confirmed real case (the RoadMaskMerge deep-pool
        // cell, -12,-15) where collision-awareness found NOTHING for a
        // cluster of refs that were the ORIGINAL motivating example for
        // building this whole subsystem. Without these, "no refinement" is
        // indistinguishable between "nothing nearby at all," "nearby but
        // every asset path failed to resolve (likely BSA-packed, not loose -
        // see Mo2Resolver.ResolveDataFile's known gap)," and "resolved and
        // extracted fine, but the raycast just didn't land on any triangle
        // at this exact (X,Y)" - three very different problems with three
        // different fixes.
        var perCandidateNearbyStatMsttCount = new List<int>();
        var perCandidateUnresolvedPathCount = new List<int>();
        var allNifPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            var instances = new List<CollisionCheckInstance>();
            int nearbyStatMstt = 0, unresolvedPath = 0;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (!cellGridIndex.TryGetValue((c.Worldspace, c.CellX + dx, c.CellY + dy), out var neighborCell)) continue;
                foreach (var placed in GetAllPlacedObjectsInCell(neighborCell, linkCache))
                {
                    if (placed.Placement is null) continue;
                    if (placed.FormKey.Equals(c.RefFormKey)) continue; // never check an object's own collision against itself
                    var dist = MathF.Sqrt(MathF.Pow(placed.Placement.Position.X - c.WorldX, 2) + MathF.Pow(placed.Placement.Position.Y - c.WorldY, 2));
                    if (dist > CollisionSearchRadius) continue;

                    if (!linkCache.TryResolve<IPlaceableObjectGetter>(placed.Base.FormKey, out var baseRec)) continue;
                    // Only STAT/MSTT carry static Havok collision worth checking here -
                    // TREE canopies are deliberately walk-through in vanilla Skyrim (see
                    // this project's own vanilla-analogue principle), so a tree is never
                    // a valid "hidden ground" provider.
                    if (baseRec is not (IStaticGetter or IMoveableStaticGetter)) continue;
                    nearbyStatMstt++;
                    // CONFIRMED BUG 2026-09-12: AssetLinkGetter<T>'s IMPLICIT string
                    // conversion strips a "Meshes\" prefix it assumes is a redundant
                    // convention - wrong for at least this install's PGPatcher-rewritten
                    // records, whose Model.File literally stores "Meshes\Landscape\..."
                    // (confirmed: .ToString() = "Meshes\Landscape\Rocks\RockL02.nif",
                    // resolves correctly; the implicit conversion = "Landscape\Rocks\
                    // RockL02.nif", silently resolves to nothing). .ToString() reflects
                    // the RAW literal field content either way, so it is correct
                    // regardless of whether a given record's own Model.File happens to
                    // include that prefix or not - always use it, never the implicit
                    // conversion, for any Model.File on this project.
                    var modelFileLink = (baseRec as IModeledGetter)?.Model?.File;
                    var modelFile = modelFileLink?.ToString();
                    if (string.IsNullOrWhiteSpace(modelFile)) continue;

                    var diskPath = resolveAssetPath(modelFile);
                    if (diskPath is null) { unresolvedPath++; continue; } // loose file not found (e.g. BSA-only asset) - safe miss, see Mo2Resolver.ResolveDataFile

                    allNifPaths.Add(diskPath);
                    instances.Add(new CollisionCheckInstance(
                        diskPath,
                        new Vec3(placed.Placement.Position.X, placed.Placement.Position.Y, placed.Placement.Position.Z),
                        new Vec3(placed.Placement.Rotation.X, placed.Placement.Rotation.Y, placed.Placement.Rotation.Z),
                        placed.Scale ?? 1.0f,
                        placed.FormKey));
                }
            }
            perCandidateInstances.Add(instances);
            perCandidateNearbyStatMsttCount.Add(nearbyStatMstt);
            perCandidateUnresolvedPathCount.Add(unresolvedPath);
        }

        if (allNifPaths.Count == 0) return candidates;

        log($"  CollisionRaycaster: checking real collision geometry for {candidates.Count} candidate(s) against {allNifPaths.Count} unique nearby mesh(es) ...");
        var extracted = CollisionRaycaster.ExtractCollisionBatch(allNifPaths, log);
        var worldTriCache = new Dictionary<FormKey, List<Tri3>>();

        var refined = new List<FloatingCandidate>(candidates.Count);
        int upgraded = 0, noLongerFloating = 0, geometryChecked = 0;

        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var instances = perCandidateInstances[i];
            float? bestGroundZ = null;
            int extractionFailed = 0, raycastMissed = 0, raycastLowerThanTerrain = 0;

            foreach (var inst in instances)
            {
                if (!extracted.TryGetValue(inst.NifPath, out var coll) || !coll.Ok) { extractionFailed++; continue; }
                if (!worldTriCache.TryGetValue(inst.RefFormKey, out var worldTris))
                {
                    worldTris = CollisionRaycaster.ToWorldSpace(coll.LocalTriangles, inst.Position, inst.RotationRadians, inst.Scale);
                    worldTriCache[inst.RefFormKey] = worldTris;
                }
                // REVERTED 2026-09-13, same session as the heightmap footprint
                // revert above - real data raised the same suspicion here:
                // 11E7B7/1562EA got reclassified "not floating" against a
                // collision surface at ~1000-1032, roughly 430-940 units ABOVE
                // this water-adjacent cell's raw terrain/water plane - far more
                // plausible as an unrelated nearby object's geometry (a tall
                // tree, a distant rock spire) caught by the same too-wide
                // ~100-180-unit ring radius than genuine support directly under
                // the candidate. Not independently disproven (unlike the
                // heightmap case, which had a hard checkedCount/flagged-count
                // contradiction), but the risk profile is worse - this is a
                // silent FALSE NEGATIVE class (marks a real floating object as
                // fine) rather than a loud false positive, so it doesn't get
                // the same "ship it, the count doesn't lie" confidence. Reverted
                // to the single center-point raycast pending a proper redesign.
                var z = CollisionRaycaster.HighestSurfaceZ(worldTris, c.WorldX, c.WorldY);
                if (!z.HasValue) { raycastMissed++; continue; }
                if (z.Value <= c.TerrainZ) { raycastLowerThanTerrain++; continue; }
                if (bestGroundZ is null || z.Value > bestGroundZ.Value)
                    bestGroundZ = z.Value;
            }

            if (bestGroundZ is null || bestGroundZ.Value <= c.TerrainZ)
            {
                // No nearby collision found, or it's not higher than the raw
                // heightmap already had - nothing to refine, keep as-is.
                // Diagnostic trace ONLY when the search actually found nearby
                // STAT/MSTT to check (otherwise this is just the ordinary,
                // expected "nothing nearby" case and would flood the log for
                // most of the 3000+ candidates a typical run flags) - added
                // 2026-09-13 after the deep-pool cell (-12,-15) cluster came
                // back with zero refinement despite being the original
                // motivating case for this whole subsystem; without this,
                // "asset path never resolved (likely BSA-packed)" and "raycast
                // never landed on this exact (X,Y)" were indistinguishable.
                var nearbyCount = perCandidateNearbyStatMsttCount[i];
                var unresolvedCount = perCandidateUnresolvedPathCount[i];
                if (nearbyCount > 0)
                {
                    log($"  CollisionRaycaster: ref {c.RefFormKey} - {nearbyCount} nearby STAT/MSTT found, " +
                        $"{unresolvedCount} asset path(s) unresolved (BSA-only?), {instances.Count} usable instance(s), " +
                        $"{extractionFailed} extraction failure(s), {raycastMissed} raycast miss(es) (no triangle at this X,Y), " +
                        $"{raycastLowerThanTerrain} surface(s) found but not higher than raw terrain - no refinement.");
                }

                // Last-resort water-surface fallback, added 2026-09-13 - see
                // FloatingCandidate's header comment and
                // WaterSurfaceRestingKeywords for the full reasoning. Only
                // reached when collision-awareness found nothing usable
                // (the branch above); only exists on the candidate at all
                // when its base matched the narrow keyword list AND it's
                // genuinely over water. Using the water plane here revives
                // exactly what this file's history called "right below the
                // water line" - a real, visually-plausible resting spot for
                // driftwood/debris, without touching non-debris types (the
                // "lily pad" class) which never get a WaterSurfaceFallbackZ
                // in the first place.
                if (c.WaterSurfaceFallbackZ.HasValue && c.WaterSurfaceFallbackZ.Value > c.TerrainZ)
                {
                    var fallbackDelta = c.RefZ - c.WaterSurfaceFallbackZ.Value;
                    if (Math.Abs(fallbackDelta) < thresholdUnits)
                    {
                        log($"  CollisionRaycaster: ref {c.RefFormKey} no longer flagged once the water plane is considered (was {c.Delta:F0} units off raw terrain {c.TerrainZ:F0}; water surface at {c.WaterSurfaceFallbackZ.Value:F0}, only {fallbackDelta:F0} off).");
                        noLongerFloating++;
                        continue;
                    }
                    log($"  CollisionRaycaster: ref {c.RefFormKey} target refined from raw terrain {c.TerrainZ:F0} to water-surface fallback {c.WaterSurfaceFallbackZ.Value:F0} (delta {c.Delta:F0} -> {fallbackDelta:F0}) - no real collision geometry found nearby, driftwood/debris-type base.");
                    refined.Add(c with { TerrainZ = c.WaterSurfaceFallbackZ.Value, Delta = fallbackDelta, GroundIsWaterSurfaceFallback = true });
                    continue;
                }

                refined.Add(c);
                continue;
            }

            geometryChecked++;
            var newDelta = c.RefZ - bestGroundZ.Value;
            if (Math.Abs(newDelta) < thresholdUnits)
            {
                log($"  CollisionRaycaster: ref {c.RefFormKey} no longer flagged once real geometry is considered (was {c.Delta:F0} units off raw terrain {c.TerrainZ:F0}; real nearby collision puts the ground at {bestGroundZ.Value:F0}, only {newDelta:F0} off).");
                noLongerFloating++;
                continue; // drop from the candidate list entirely - it was resting on real geometry the heightmap couldn't see
            }

            upgraded++;
            log($"  CollisionRaycaster: ref {c.RefFormKey} target refined from raw terrain {c.TerrainZ:F0} to real collision surface {bestGroundZ.Value:F0} (delta {c.Delta:F0} -> {newDelta:F0}).");
            refined.Add(c with { TerrainZ = bestGroundZ.Value, Delta = newDelta, GroundConfirmedByCollision = true });
        }

        log($"  CollisionRaycaster: {geometryChecked} candidate(s) had usable nearby collision geometry - {upgraded} target(s) refined, {noLongerFloating} reclassified as not actually floating.");
        return refined;
    }

    // Confirmed necessary on a real 2026-09-12 live test, and a much bigger
    // bug than it first looked: a plugin's own copy of a CELL record only
    // carries whatever Persistent/Temporary reference list it actually needs
    // to declare - a plugin that only touches the cell's Landscape subrecord
    // (this project's own sibling tools included - RoadMaskMerger,
    // LandscapeSeamFixer) commonly ends up as the WINNING cell context while
    // carrying a near-empty children list, because Mutagen's override
    // mechanism copies forward whatever the then-current winner's own list
    // happened to be at generation time, not the true union across the whole
    // load order. Simply reading `winningCell.Persistent.Concat(.Temporary)`
    // (the previous approach) therefore silently hid every reference some
    // EARLIER, non-winning plugin had added - confirmed on a real cell where
    // the winning context (LandscapeSeamFixes.esp) carried only 2 Temporary
    // refs while Forest Fragments.esp's own non-winning copy carried 66,
    // including several "forestdebris" statics floating by 400-1000+ units
    // that were completely invisible to detection as a result. This affects
    // every cell any landscape/texture-fix plugin has touched, not just one
    // mod's naming scheme - far broader than the keyword-allowlist gap this
    // file also fixes. The correct set of references is the UNION of every
    // plugin's own Persistent+Temporary declarations for this cell FormKey,
    // each FormID then resolved through the link cache to its own true
    // winning override (independent of whichever plugin's copy first listed
    // it as a child of this cell).
    static IEnumerable<IPlacedObjectGetter> GetAllPlacedObjectsInCell(
        FormKey cellFormKey, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
    {
        var seen = new HashSet<FormKey>();
        foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            foreach (var link in ctx.Record.Persistent.Concat(ctx.Record.Temporary))
            {
                if (!seen.Add(link.FormKey)) continue;
                if (linkCache.TryResolve<IPlacedObjectGetter>(link.FormKey, out var resolved))
                    yield return resolved;
            }
        }
    }

    // Decodes Skyrim.esm's OWN copy of this cell's Landscape, for comparing
    // per-reference against the (possibly-edited) winning terrain - see the
    // call site above. Null if this cell doesn't originate in Skyrim.esm at
    // all (a purely custom worldspace), in which case the per-reference
    // check is simply skipped and the older roughness/delta gates alone
    // decide, same as before this gate existed.
    static float[,]? TryGetVanillaHeights(FormKey cellFormKey, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
    {
        foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            if (ctx.Record.Landscape?.VertexHeightMap is null) continue;
            string fileName = ctx.ModKey.FileName;
            if (!fileName.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)) continue;
            return HeightmapDecoder.DecodeHeights(ctx.Record.Landscape.VertexHeightMap);
        }
        return null;
    }

    // Confirmed necessary in practice on the very first live-profile test:
    // a blanket "any STAT/MSTT" filter flagged 197,186 of 207,126 checked
    // refs (95%!) - architectural pieces (castle walls, wood beams, road
    // chunks) sit at heights their STRUCTURE dictates, never natural ground
    // level, and there's no way to tell them apart from rocks/boulders by
    // record TYPE alone. Narrowed to: TREE unconditionally (trees rest on
    // ground with very few real exceptions - and it's the user's own
    // original motivating example, Archwood trees floating over a
    // waterfall), plus STAT/MSTT only when the EditorID matches a natural-
    // terrain keyword allowlist. Heuristic and imperfect by nature (an
    // EditorID naming convention, not ground truth) - a real fix needs 3D
    // collision awareness (see file header), which is out of scope for v1.
    // Confirmed necessary on the second live-profile test too: "Snow"/"Ice"/
    // "Mud"/"Dirt"/"Gravel" are near-universal TEXTURE-VARIANT suffixes in
    // Skyrim's naming (e.g. "NorTowerRuinsRamp01Snow" is an architectural
    // ruin piece with a snow-texture variant, not a natural snowdrift) -
    // dropped entirely rather than tightened, they're not discriminating at
    // all. "Mountain"/"Cliff"/"Crag" dropped too: these are large background
    // terrain-ASSEMBLY meshes (a whole mountainside, not a discrete rock)
    // deliberately anchored partway up a slope, not at a simple ground-
    // contact point - the same heuristic that works for a boulder doesn't
    // apply to them, confirmed by real Throat-of-the-World-area cliff/peak
    // pieces showing 20,000+ unit "deltas" that are actually just steep
    // terrain the coarse 128-unit heightmap grid can't capture, not floating
    // bugs. Kept to small, discrete, actually-ground-resting objects only.
    // Confirmed missing on a real 2026-09-11 live test: Forest Fragments.esp
    // ships fallen ground-debris statics under three EditorID conventions
    // this original list didn't cover at all - "forestdebris01" (a literal
    // debris pile, model forest_debris_N.nif), "branch03/04/05" (fallen
    // branches), and "deadtree01/02/03" (a fallen trunk stored as a STAT
    // record, not an actual TREE type, so the unconditional tree rule above
    // doesn't catch it either). All three are exactly the "sits directly on
    // the ground with no legitimate reason to hover" class this tool
    // targets - they were never being considered as candidates at all,
    // regardless of how far they'd drifted from the corrected terrain.
    static readonly string[] NaturalStaticKeywords =
    [
        "Rock", "Boulder", "Stone", "Log", "Stump", "Root", "Bush", "Shrub", "Moss", "Fern",
        "Debris", "Branch", "DeadTree",
    ];

    // Confirmed necessary on a real 2026-09-12 live test: "Hanging Moss"
    // (TreeFloraHangingMoss01/02/03, base type TREE) is authored to hang from
    // a roof beam/archway, NOT rest on the ground beneath it - the tool
    // wrongly treated it like any other TREE (unconditionally ground-resting,
    // see below) and dragged it down onto a stone wall, visibly wrong
    // in-game (confirmed via before/after screenshots of the same ref,
    // 3E0026D0). A full-load-order scan for the same naming pattern turned up
    // a much larger class than just moss: every "Ivy"-named TREE-type climbing
    // decoration across 6+ mods (StonewallIvyAnim, IvyAnim.esp's Ivy01-03Anim,
    // Solitude Walls Ivy's scastlewall*anim, several SKYKLF ivy pieces),
    // Dawnguard's own DLC1PPVineWall/Ceiling/Floor set, TreeDeadVine* and
    // TreeIceVine* decorations, and TreeVineMaple - all wall/ceiling/floor-
    // attached climbing or hanging vegetation, none of it meant to touch
    // raw terrain. "Hanging" and "Vine" are the two keywords that cover this
    // whole class without a single confirmed false exclusion in that scan
    // (the "Divine"-contains-"vine" collision on an unrelated STAT record is
    // harmless - that record never matched NaturalStaticKeywords in the first
    // place, so excluding it changes nothing).
    static readonly string[] AttachedFloraKeywords = ["Hanging", "Vine"];

    // Confirmed necessary on the same 2026-09-12 scan: "Stone" (in
    // NaturalStaticKeywords, meant for natural boulders/stones) also matches
    // the entire "Stonewall*" architectural family (Stonewall01/02,
    // StonewallLong01, StonewallEndL01/R01[Ivy], StonewallTerrace* - ruin wall
    // segments, confirmed via a full base-record scan of every STAT/MSTT
    // EditorID containing "Wall" across the load order). These sit at
    // whatever height the ruin STRUCTURE dictates, never natural ground level
    // - the same "architectural piece, not a discrete rock" reasoning the
    // NaturalStaticKeywords comment above already applies to "Mountain"/
    // "Cliff"/"Crag". None of the ~60 other "Wall"-named STAT/MSTT records
    // found in that same scan (NorPitWall*, NorRmBgWall*, GenKitRmWall*,
    // FarmIntWall01, DweBanner*, the various FX cobweb/mist wall dressings)
    // were ever eligible for ground-resting anyway (none match
    // NaturalStaticKeywords), so this exclusion only removes the one real
    // false-positive class - "Wall" is otherwise a strong, reliable
    // architecture signal.
    const string ArchitecturalExclusionKeyword = "Wall";

    // Added 2026-09-13 for the water-surface fallback (see FloatingCandidate's
    // header comment and ApplyCollisionAwareness). Deliberately a NARROW
    // subset of NaturalStaticKeywords, not the whole list: the physical
    // question here isn't "is this a natural, ground-resting object" (that's
    // already decided by the time this runs) but "would this SPECIFIC kind
    // of object plausibly come to rest right at a water's surface rather than
    // sinking to the real bed beneath it" - true for driftwood/loose debris,
    // false for a rock, a fern, a bush, or a moss clump, all of which sink or
    // grow on solid ground, never float. This is the exact distinction the
    // "lily pad" bug (see FindCandidates) blurred by applying the water
    // plane as a target to EVERY flagged candidate uniformly, terrestrial
    // plants included - scoping to this keyword list is what makes reviving
    // the idea safe this time.
    static readonly string[] WaterSurfaceRestingKeywords = ["Debris", "Log", "Driftwood"];

    // Resolves through the reference's own Base link rather than trusting a
    // naive type check on the placed reference type, since PlacedObject
    // covers all of STAT/MSTT/TREE uniformly.
    static bool IsGroundRestingBase(
        IFormLinkGetter<IPlaceableObjectGetter> baseLink,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        out FormKey baseFormKey, out string? baseEditorId, out float footprintRadiusLocal)
    {
        baseFormKey = baseLink.FormKey;
        baseEditorId = null;
        footprintRadiusLocal = 0f;
        if (!linkCache.TryResolve<IPlaceableObjectGetter>(baseFormKey, out var baseRecord)) return false;
        baseEditorId = (baseRecord as IMajorRecordGetter)?.EditorID;
        var editorId = baseEditorId;
        footprintRadiusLocal = GetFootprintRadiusLocal(baseRecord);

        if (editorId is not null && editorId.Contains(ArchitecturalExclusionKeyword, StringComparison.OrdinalIgnoreCase))
            return false;

        if (baseRecord is ITreeGetter)
        {
            return editorId is null
                || !AttachedFloraKeywords.Any(kw => editorId.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }
        if (baseRecord is IStaticGetter or IMoveableStaticGetter)
        {
            return editorId is not null
                && NaturalStaticKeywords.Any(kw => editorId.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    // Added 2026-09-13, per the user's own request: "compare the location...
    // see highest land height and check well above it" - a real object has
    // horizontal extent, and a single center-point heightmap sample assumes
    // it doesn't. ObjectBounds (OBND) is the record's own AUTHORED local-
    // space bounding box (confirmed via direct reflection against a real
    // Mutagen-loaded Static record: `First`/`Second` are P3Int16 corners,
    // e.g. (-250,-329,-383) to (249,329,377) for a real record) - using the
    // ACTUAL authored footprint instead of a guessed radius means a huge
    // fallen tree and a tiny pebble each get a physically appropriate
    // sampling radius, not the same one. Returns the larger of the X/Y half-
    // extents (a simple bounding RADIUS, not the true rectangular footprint
    // - good enough for an 8-direction ring sample, see
    // HeightmapDecoder.GetMaxHeightInFootprint). Still in the mesh's own
    // LOCAL units - the caller must multiply by the reference's own Scale to
    // get world units, since the same base record can be placed at any size.
    static float GetFootprintRadiusLocal(IPlaceableObjectGetter baseRecord)
    {
        var bounds = (baseRecord as IObjectBoundedGetter)?.ObjectBounds;
        if (bounds is null) return 0f;
        var halfX = Math.Max(Math.Abs((float)bounds.First.X), Math.Abs((float)bounds.Second.X));
        var halfY = Math.Max(Math.Abs((float)bounds.First.Y), Math.Abs((float)bounds.Second.Y));
        return Math.Max(halfX, halfY);
    }

    static FloatingObjectReportResult BuildReport(List<FloatingCandidate> candidates)
    {
        var lines = new List<string> {
            "Worldspace,CellX,CellY,OwnerPlugin,RefFormID,BaseFormID,BaseEditorID,WorldX,WorldY,RefZ,TerrainZ,Delta"
        };
        foreach (var c in candidates.OrderByDescending(c => Math.Abs(c.Delta)))
        {
            lines.Add($"{c.Worldspace},{c.CellX},{c.CellY},{c.OwnerPlugin},{c.RefFormKey},{c.BaseFormKey},{c.BaseEditorId}," +
                $"{c.WorldX:F1},{c.WorldY:F1},{c.RefZ:F1},{c.TerrainZ:F1},{c.Delta:F1}");
        }
        return new FloatingObjectReportResult(lines, candidates.Count, candidates.Count);
    }
}
