using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Skyrim;
using SeamFinder.Core;

namespace FloatingObjectFixer.UI;

public partial class MainWindow : Window
{
    string? _lastReportPath;
    string? _lastFixPluginPath;
    string? _lastOutputFolder;

    // Path AppendLog also mirrors every line to, alongside the LogBox -
    // set fresh at the start of each run so the log ends up sitting right
    // next to that run's own esp/csv instead of only living in the UI
    // (which resets on every relaunch, unlike the output folder's files).
    string? _currentLogFilePath;

    // Whether OutputFolderBox's current text was set by RefreshOutputFolderDefault
    // rather than typed by the user - stays true (keep auto-updating the default)
    // until the user actually edits the field themselves.
    bool _outputFolderAutoSet = true;
    bool _suppressOutputTextChanged;

    public MainWindow()
    {
        InitializeComponent();
        LoadPersistedSettings();
        RefreshOutputFolderDefault();
    }

    // --- Settings persistence ---
    //
    // Remembers everything typed into the form (paths, threshold, worldspace
    // filter) across app launches, same pattern/reasoning as SeamFinder.UI -
    // kept in a stable per-user location rather than next to the exe, since
    // this app gets rebuilt/republished in place during development, which
    // would otherwise silently wipe saved settings on every update.
    static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatingObjectFixer", "settings.json");

    // ForceCorrectCappedItems (the "bypass every cap" checkbox) is
    // DELIBERATELY not a member here - it never persists a user-set value
    // across relaunches; it always starts UNCHECKED (the XAML default, no
    // IsChecked attribute) rather than remembering whatever the user last
    // toggled it to. Briefly flipped to default-ON on 2026-09-13, then
    // reverted the very next day once real evidence showed it burying
    // legitimately-placed objects (a vanilla rock near a custom dungeon
    // entrance), not just decorative clutter - back to off-by-default,
    // opt-in-with-a-loud-warning for good. It lives only in RunSettings
    // (this run) and gets written to the per-run settings-used.json snapshot
    // for audit purposes.
    record PersistedSettings(
        bool IsMo2Mode, bool IsVortexMode,
        string Mo2InstancePath, string Mo2GameDataPath, string Mo2PluginsTxt, string Mo2LoadOrderTxt, string Mo2ModlistTxt,
        string VortexGameDataPath, string DirectGameDataPath, string OutputFolder,
        float ThresholdUnits, string? WorldspaceFilter,
        // Defaults to 0 (not SeamFinder.Core's real default) when deserializing an
        // older settings.json written before this field existed - LoadPersistedSettings
        // below treats 0-or-less as "not actually set" and falls back to the real
        // default rather than silently writing 0 into WaterDepthBox.
        float WaterSubmergeMarginUnits = 0f);

    void LoadPersistedSettings()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            var s = System.Text.Json.JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(SettingsFilePath));
            if (s is null) return;

            (ModeMo2.IsChecked, ModeVortex.IsChecked, ModeDirect.IsChecked) = s switch
            {
                { IsMo2Mode: true } => (true, false, false),
                { IsVortexMode: true } => (false, true, false),
                _ => (false, false, true),
            };
            Mo2InstancePathBox.Text = s.Mo2InstancePath;
            Mo2GameDataPathBox.Text = s.Mo2GameDataPath;
            Mo2PluginsTxtBox.Text = s.Mo2PluginsTxt;
            Mo2LoadOrderTxtBox.Text = s.Mo2LoadOrderTxt;
            Mo2ModlistTxtBox.Text = s.Mo2ModlistTxt;
            VortexGameDataPathBox.Text = s.VortexGameDataPath;
            DirectGameDataPathBox.Text = s.DirectGameDataPath;
            ThresholdBox.Text = s.ThresholdUnits.ToString(System.Globalization.CultureInfo.InvariantCulture);
            WorldspaceFilterBox.Text = s.WorldspaceFilter ?? "";
            WaterDepthBox.Text = (s.WaterSubmergeMarginUnits > 0f
                ? s.WaterSubmergeMarginUnits
                : SeamFinder.Core.FloatingObjectFixer.DefaultWaterSurfaceSubmergeMarginUnits)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(s.OutputFolder)) OutputFolderBox.Text = s.OutputFolder; // marks _outputFolderAutoSet false via its own TextChanged handler
        }
        catch
        {
            // Corrupt or unreadable settings file - start fresh rather than
            // block the app from opening at all.
        }
    }

    void SavePersistedSettings(RunSettings s)
    {
        try
        {
            var persisted = new PersistedSettings(
                s.IsMo2Mode, s.IsVortexMode,
                s.Mo2InstancePath, s.Mo2GameDataPath, s.Mo2PluginsTxt, s.Mo2LoadOrderTxt, s.Mo2ModlistTxt,
                s.VortexGameDataPath, s.DirectGameDataPath, OutputFolderBox.Text.Trim(),
                s.ThresholdUnits, s.WorldspaceFilter, s.WaterSubmergeMarginUnits);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            File.WriteAllText(SettingsFilePath, System.Text.Json.JsonSerializer.Serialize(persisted, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - a locked/inaccessible AppData shouldn't stop the run itself.
        }
    }

    // Writes the exact settings a run used into that run's own output
    // folder too (alongside log.txt/esp/csv), separate from the
    // always-on-launch copy above - a record of what config produced this
    // particular output, portable with it if the folder is shared/moved.
    void SaveSettingsSnapshotToOutputFolder(RunSettings s, string outputFolder)
    {
        try
        {
            Directory.CreateDirectory(outputFolder);
            var snapshot = new
            {
                OutputFolder = outputFolder,
                s.IsMo2Mode, s.IsVortexMode, s.Mo2InstancePath, s.Mo2GameDataPath,
                s.VortexGameDataPath, s.DirectGameDataPath, s.ThresholdUnits, s.WorldspaceFilter,
                s.WaterSubmergeMarginUnits, s.ForceCorrectCappedItems, s.HeightmapOnlyCapUnits, s.UsesCustomSafetyCap,
            };
            File.WriteAllText(Path.Combine(outputFolder, "settings-used.json"),
                System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort, same reasoning as SavePersistedSettings.
        }
    }

    // --- Log file ---

    void StartLogFile(string outputFolder)
    {
        if (string.IsNullOrEmpty(outputFolder)) { _currentLogFilePath = null; return; }
        try
        {
            Directory.CreateDirectory(outputFolder);
            _currentLogFilePath = Path.Combine(outputFolder, "log.txt");
            File.WriteAllText(_currentLogFilePath, "");
        }
        catch
        {
            // Best-effort - a locked/inaccessible output folder shouldn't
            // stop the run itself, just the file mirror of its log.
            _currentLogFilePath = null;
        }
    }

    // --- Mode switching ---

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (Mo2Panel is null) return;

        Mo2Panel.Visibility = ModeMo2.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        VortexPanel.Visibility = ModeVortex.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DirectPanel.Visibility = ModeDirect.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshOutputFolderDefault();
    }

    // --- Output folder: smart per-mode default, stays editable ---

    void ModeDataPathBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshOutputFolderDefault();

    void OutputFolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressOutputTextChanged) return;
        _outputFolderAutoSet = false;
    }

    void RefreshOutputFolderDefault()
    {
        if (OutputFolderBox is null) return;

        string? defaultPath = null;
        string hint = "";

        if (ModeMo2?.IsChecked == true)
        {
            var instancePath = Mo2InstancePathBox?.Text.Trim();
            if (!string.IsNullOrEmpty(instancePath))
            {
                defaultPath = Path.Combine(instancePath, "mods", "Floating Object Fixes");
                hint = "Writes into your MO2 instance's mods folder, so it shows up as an installable mod (a meta.ini is added automatically).";
            }
        }
        else if (ModeVortex?.IsChecked == true)
        {
            defaultPath = VortexGameDataPathBox?.Text.Trim();
            hint = "Writes directly into your game's Data folder, matching where Vortex deploys mods by default.";
        }
        else if (ModeDirect?.IsChecked == true)
        {
            defaultPath = DirectGameDataPathBox?.Text.Trim();
            hint = "Writes directly into your game's Data folder.";
        }

        OutputHintText.Text = hint + " You can change this to any folder you like.";

        if (_outputFolderAutoSet && !string.IsNullOrEmpty(defaultPath))
        {
            _suppressOutputTextChanged = true;
            OutputFolderBox.Text = defaultPath;
            _suppressOutputTextChanged = false;
        }
    }

    // --- MO2 panel ---

    void Mo2BrowseInstance_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select your MO2 instance folder (where ModOrganizer.exe lives)" };
        if (dlg.ShowDialog() == true)
            Mo2InstancePathBox.Text = dlg.FolderName;
    }

    void Mo2BrowseGameData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dlg.ShowDialog() == true)
            Mo2GameDataPathBox.Text = dlg.FolderName;
    }

    void Mo2BrowsePluginsTxt_Click(object sender, RoutedEventArgs e) => BrowseForFile(Mo2PluginsTxtBox, "plugins.txt");
    void Mo2BrowseLoadOrderTxt_Click(object sender, RoutedEventArgs e) => BrowseForFile(Mo2LoadOrderTxtBox, "loadorder.txt");
    void Mo2BrowseModlistTxt_Click(object sender, RoutedEventArgs e) => BrowseForFile(Mo2ModlistTxtBox, "modlist.txt");

    static void BrowseForFile(TextBox target, string suggestedName)
    {
        var dlg = new OpenFileDialog { FileName = suggestedName, Filter = "Text files|*.txt|All files|*.*" };
        if (dlg.ShowDialog() == true)
            target.Text = dlg.FileName;
    }

    void Mo2InstancePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshMo2Instance();
        RefreshOutputFolderDefault();
    }

    void RefreshMo2Instance()
    {
        var instancePath = Mo2InstancePathBox.Text.Trim();
        if (string.IsNullOrEmpty(instancePath) || !Directory.Exists(instancePath)) return;

        var profilesDir = Path.Combine(instancePath, "profiles");
        Mo2ProfileCombo.Items.Clear();
        if (Directory.Exists(profilesDir))
        {
            foreach (var dir in Directory.GetDirectories(profilesDir))
                Mo2ProfileCombo.Items.Add(Path.GetFileName(dir));
        }

        var iniPath = Path.Combine(instancePath, "ModOrganizer.ini");
        if (File.Exists(iniPath))
        {
            var gamePath = ReadIniGamePath(iniPath);
            if (gamePath != null)
                Mo2GameDataPathBox.Text = Path.Combine(gamePath, "Data");

            var selectedProfile = ReadIniSelectedProfile(iniPath);
            if (selectedProfile != null && Mo2ProfileCombo.Items.Contains(selectedProfile))
                Mo2ProfileCombo.SelectedItem = selectedProfile;
        }

        if (Mo2ProfileCombo.SelectedItem is null && Mo2ProfileCombo.Items.Count > 0)
            Mo2ProfileCombo.SelectedIndex = 0;

        RefreshMo2ProfileFiles();
    }

    void RefreshMo2ProfileFiles()
    {
        var instancePath = Mo2InstancePathBox.Text.Trim();
        var profile = Mo2ProfileCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(instancePath) || string.IsNullOrEmpty(profile)) return;

        var profileDir = Path.Combine(instancePath, "profiles", profile);
        Mo2PluginsTxtBox.Text = Path.Combine(profileDir, "plugins.txt");
        Mo2LoadOrderTxtBox.Text = Path.Combine(profileDir, "loadorder.txt");
        Mo2ModlistTxtBox.Text = Path.Combine(profileDir, "modlist.txt");
    }

    static string? ReadIniGamePath(string iniPath)
    {
        foreach (var line in File.ReadAllLines(iniPath))
        {
            if (!line.StartsWith("gamePath=")) continue;
            var value = line["gamePath=".Length..].Trim();
            var start = value.IndexOf('(');
            var end = value.LastIndexOf(')');
            if (start >= 0 && end > start) value = value[(start + 1)..end];
            return value.Replace("\\\\", "\\");
        }
        return null;
    }

    static string? ReadIniSelectedProfile(string iniPath)
    {
        foreach (var line in File.ReadAllLines(iniPath))
        {
            if (!line.StartsWith("selected_profile=")) continue;
            var value = line["selected_profile=".Length..].Trim();
            var start = value.IndexOf('(');
            var end = value.LastIndexOf(')');
            if (start >= 0 && end > start) value = value[(start + 1)..end];
            return value;
        }
        return null;
    }

    // --- Vortex panel ---

    void VortexBrowseGameData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dlg.ShowDialog() == true)
            VortexGameDataPathBox.Text = dlg.FolderName;
    }

    void VortexAutoDetect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var env = GameEnvironment.Typical.Construct<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE);
            VortexGameDataPathBox.Text = env.DataFolderPath.Path;
            AppendLog($"Auto-detected game Data folder: {env.DataFolderPath.Path}");
            AppendLog("(This finds your Skyrim SE install via Steam/GOG/registry - correct as long as");
            AppendLog(" Vortex is using its default deployment method, which links mods directly into it.)");
        }
        catch (Exception ex)
        {
            AppendLog("Auto-detect failed: " + ex.Message);
            MessageBox.Show(this, "Could not auto-detect your game install. Please browse to your Data folder manually.",
                "Auto-detect failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- Direct panel ---

    void DirectBrowseGameData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dlg.ShowDialog() == true)
            DirectGameDataPathBox.Text = dlg.FolderName;
    }

    // --- Output ---

    void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select an output folder" };
        if (dlg.ShowDialog() == true)
            OutputFolderBox.Text = dlg.FolderName;
    }

    // --- Run ---

    record RunSettings(
        bool IsMo2Mode, bool IsVortexMode,
        string Mo2InstancePath, string Mo2GameDataPath, string Mo2PluginsTxt, string Mo2LoadOrderTxt, string Mo2ModlistTxt,
        string VortexGameDataPath, string DirectGameDataPath,
        float ThresholdUnits, string? WorldspaceFilter, float WaterSubmergeMarginUnits,
        bool ForceCorrectCappedItems, float HeightmapOnlyCapUnits, bool UsesCustomSafetyCap);

    void SetBusy(bool busy)
    {
        RunButton.IsEnabled = !busy;
        FixButton.IsEnabled = !busy;
        RunProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // CustomSafetyCapBox only makes sense while its own checkbox is ticked -
    // greyed out otherwise so it's visually obvious the number isn't in effect.
    void CustomSafetyCapCheck_Toggled(object sender, RoutedEventArgs e)
    {
        CustomSafetyCapBox.IsEnabled = CustomSafetyCapCheck.IsChecked == true;
    }

    RunSettings SnapshotSettings()
    {
        var thresholdText = ThresholdBox.Text.Trim();
        var threshold = float.TryParse(thresholdText, out var t) ? t : SeamFinder.Core.FloatingObjectFixer.DefaultThresholdUnits;
        var worldspace = WorldspaceFilterBox.Text.Trim();
        var waterDepthText = WaterDepthBox.Text.Trim();
        var waterDepth = float.TryParse(waterDepthText, out var wd) ? wd : SeamFinder.Core.FloatingObjectFixer.DefaultWaterSurfaceSubmergeMarginUnits;
        var usesCustomCap = CustomSafetyCapCheck.IsChecked == true;
        var safetyCapText = CustomSafetyCapBox.Text.Trim();
        var safetyCap = usesCustomCap && float.TryParse(safetyCapText, out var sc)
            ? sc
            : SeamFinder.Core.FloatingObjectFixer.DefaultMaxAutoFixDeltaUnits;

        return new(
            ModeMo2.IsChecked == true, ModeVortex.IsChecked == true,
            Mo2InstancePathBox.Text.Trim(), Mo2GameDataPathBox.Text.Trim(), Mo2PluginsTxtBox.Text.Trim(),
            Mo2LoadOrderTxtBox.Text.Trim(), Mo2ModlistTxtBox.Text.Trim(),
            VortexGameDataPathBox.Text.Trim(), DirectGameDataPathBox.Text.Trim(),
            threshold, string.IsNullOrEmpty(worldspace) ? null : worldspace, waterDepth,
            ForceCorrectCheck.IsChecked == true, safetyCap, usesCustomCap);
    }

    async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        ResultText.Text = "";
        OpenReportButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
        SetBusy(true);

        StartLogFile(OutputFolderBox.Text.Trim());

        var settings = SnapshotSettings();
        SavePersistedSettings(settings);
        SaveSettingsSnapshotToOutputFolder(settings, OutputFolderBox.Text.Trim());

        try
        {
            var result = await Task.Run(() => RunDetectionForSelectedMode(settings));
            if (result is null) return;

            var outputFolder = OutputFolderBox.Text.Trim();
            Directory.CreateDirectory(outputFolder);
            EnsureMo2MetaIni(outputFolder);

            var outPath = Path.Combine(outputFolder, "FloatingObjectReport.csv");
            File.WriteAllLines(outPath, result.ReportCsvLines);
            AppendLog($"Report written to: {outPath}");

            _lastReportPath = outPath;
            _lastOutputFolder = outputFolder;
            ResultText.Text = $"Flagged {result.Flagged} reference(s) out of {result.CandidatesChecked} checked.";
            OpenReportButton.IsEnabled = true;
            OpenFolderButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppendLog("");
            AppendLog("ERROR: " + ex);
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async void FixButton_Click(object sender, RoutedEventArgs e)
    {
        // Custom WarningDialog, not MessageBox, added 2026-09-13 - the user
        // specifically asked for a LARGE bold "WARNING" header, which a plain
        // MessageBox cannot render (no rich text/font-size control).
        if (ForceCorrectCheck.IsChecked == true)
        {
            var confirm = WarningDialog.Show(this,
                "Force-correct bypasses every auto-fix cap and corrects EVERY flagged floating reference, " +
                "including ones this tool could NOT confirm a safe target for. Some corrections WILL be " +
                "wrong instead of just left floating - this pushed objects underground on 2026-09-12, and " +
                "CONFIRMED 2026-09-14 to bury real, legitimately-placed objects too, not just decorative " +
                "clutter (a vanilla rock near a custom dungeon entrance got shoved underground, visibly " +
                "breaking the scene).\n\nIf this run causes damage, unchecking this box does NOT undo it - " +
                "sunk/buried items are never auto-corrected in either mode. A full reset (disable+delete " +
                "the old esp, regenerate, restart) is the only way back.");
            if (!confirm) return;
        }
        else if (CustomSafetyCapCheck.IsChecked == true)
        {
            var confirm = WarningDialog.Show(this,
                "A custom heightmap-only safety threshold is a MIDDLE GROUND, not a safe one - it still " +
                "corrects items against a raw heightmap GUESS, not a confirmed real surface, just for a " +
                "wider range than the default 150 units. A wrong guess CAN place an object below the " +
                "visible ground, same risk class as Force-correct (which CONFIRMED this on 2026-09-14 - a " +
                "vanilla rock near a custom dungeon entrance got shoved underground), just narrower in " +
                "scope.\n\nIf this run causes damage, changing this setting back does NOT undo it - " +
                "sunk/buried items are never auto-corrected regardless. A full reset (disable+delete " +
                "the old esp, regenerate, restart) is the only way back.");
            if (!confirm) return;
        }

        LogBox.Clear();
        ResultText.Text = "";
        OpenFixPluginButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
        SetBusy(true);

        var settings = SnapshotSettings();
        var outputFolder = OutputFolderBox.Text.Trim();
        StartLogFile(outputFolder);
        SavePersistedSettings(settings);
        SaveSettingsSnapshotToOutputFolder(settings, outputFolder);

        try
        {
            if (string.IsNullOrEmpty(outputFolder))
            {
                ShowValidation("Please choose an output folder.");
                return;
            }

            var result = await Task.Run(() => GenerateFixForSelectedMode(settings, outputFolder));
            if (result is null) return;

            Dispatcher.Invoke(() => EnsureMo2MetaIni(outputFolder));

            _lastFixPluginPath = result.OutputPath;
            _lastOutputFolder = outputFolder;
            ResultText.Text = $"Corrected {result.RefsCorrected} reference(s). Test in-game before relying on this - see the log above for details.";
            OpenFixPluginButton.IsEnabled = true;
            OpenFolderButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppendLog("");
            AppendLog("ERROR: " + ex);
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    FloatingObjectFixResult? GenerateFixForSelectedMode(RunSettings s, string outputFolder)
    {
        void Log(string line) => Dispatcher.Invoke(() => AppendLog(line));
        const string pluginName = "FloatingObjectFixes.esp";

        if (s.IsMo2Mode)
        {
            if (string.IsNullOrEmpty(s.Mo2InstancePath) || string.IsNullOrEmpty(s.Mo2GameDataPath))
            {
                ShowValidation("Please fill in the MO2 instance folder and game Data folder.");
                return null;
            }

            Log($"MO2 instance: {s.Mo2InstancePath}");
            Log($"Game Data path: {s.Mo2GameDataPath}");
            Log($"Threshold: {s.ThresholdUnits} units");
            Log($"Worldspace filter: {s.WorldspaceFilter ?? "(none - all worldspaces)"}");
            Log($"Water submerge depth: {s.WaterSubmergeMarginUnits} units");
            if (s.ForceCorrectCappedItems) Log("Force-correct: ON (every auto-fix cap bypassed - risky, see checkbox tooltip)");
            else if (s.UsesCustomSafetyCap) Log($"Custom heightmap-only safety threshold: {s.HeightmapOnlyCapUnits} units (default 150 - risky, see checkbox tooltip)");
            Log("");

            var resolved = Mo2Resolver.ResolveFromExplicitPaths(
                s.Mo2PluginsTxt, s.Mo2LoadOrderTxt, s.Mo2ModlistTxt, s.Mo2InstancePath, s.Mo2GameDataPath);
            Log($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
            if (resolved.MissingPlugins.Count > 0)
            {
                Log($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found:");
                foreach (var m in resolved.MissingPlugins) Log("  " + m);
            }

            return SeamFinder.Core.FloatingObjectFixer.RunFixForResolvedPlugins(
                resolved.LoadOrder, pluginName, outputFolder, Log, s.ThresholdUnits, s.WorldspaceFilter, resolved.ResolveDataFile, s.WaterSubmergeMarginUnits, s.ForceCorrectCappedItems, s.HeightmapOnlyCapUnits);
        }
        else
        {
            var dataFolder = s.IsVortexMode ? s.VortexGameDataPath : s.DirectGameDataPath;

            if (string.IsNullOrEmpty(dataFolder))
            {
                ShowValidation("Please fill in the game Data folder.");
                return null;
            }

            Log($"Game Data path: {dataFolder}");
            Log($"Threshold: {s.ThresholdUnits} units");
            Log($"Worldspace filter: {s.WorldspaceFilter ?? "(none - all worldspaces)"}");
            Log($"Water submerge depth: {s.WaterSubmergeMarginUnits} units");
            if (s.ForceCorrectCappedItems) Log("Force-correct: ON (every auto-fix cap bypassed - risky, see checkbox tooltip)");
            else if (s.UsesCustomSafetyCap) Log($"Custom heightmap-only safety threshold: {s.HeightmapOnlyCapUnits} units (default 150 - risky, see checkbox tooltip)");
            Log("");

            return SeamFinder.Core.FloatingObjectFixer.RunFixForDirectDataFolder(
                dataFolder, pluginName, outputFolder, Log, s.ThresholdUnits, s.WorldspaceFilter, s.WaterSubmergeMarginUnits, s.ForceCorrectCappedItems, s.HeightmapOnlyCapUnits);
        }
    }

    FloatingObjectReportResult? RunDetectionForSelectedMode(RunSettings s)
    {
        void Log(string line) => Dispatcher.Invoke(() => AppendLog(line));

        if (s.IsMo2Mode)
        {
            if (string.IsNullOrEmpty(s.Mo2InstancePath) || string.IsNullOrEmpty(s.Mo2GameDataPath))
            {
                ShowValidation("Please fill in the MO2 instance folder and game Data folder.");
                return null;
            }

            Log($"MO2 instance: {s.Mo2InstancePath}");
            Log($"plugins.txt: {s.Mo2PluginsTxt}");
            Log($"loadorder.txt: {s.Mo2LoadOrderTxt}");
            Log($"modlist.txt: {s.Mo2ModlistTxt}");
            Log($"Game Data path: {s.Mo2GameDataPath}");
            Log($"Threshold: {s.ThresholdUnits} units");
            Log($"Worldspace filter: {s.WorldspaceFilter ?? "(none - all worldspaces)"}");
            Log("");

            var resolved = Mo2Resolver.ResolveFromExplicitPaths(
                s.Mo2PluginsTxt, s.Mo2LoadOrderTxt, s.Mo2ModlistTxt, s.Mo2InstancePath, s.Mo2GameDataPath);
            Log($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
            if (resolved.MissingPlugins.Count > 0)
            {
                Log($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found in any enabled mod folder or the game Data folder:");
                foreach (var m in resolved.MissingPlugins) Log("  " + m);
            }

            return SeamFinder.Core.FloatingObjectFixer.RunDetectionForResolvedPlugins(
                resolved.LoadOrder, Log, s.ThresholdUnits, s.WorldspaceFilter, resolved.ResolveDataFile);
        }
        else
        {
            var dataFolder = s.IsVortexMode ? s.VortexGameDataPath : s.DirectGameDataPath;

            if (string.IsNullOrEmpty(dataFolder))
            {
                ShowValidation("Please fill in the game Data folder.");
                return null;
            }

            Log($"Game Data path: {dataFolder}");
            Log($"Threshold: {s.ThresholdUnits} units");
            Log($"Worldspace filter: {s.WorldspaceFilter ?? "(none - all worldspaces)"}");
            Log("");

            return SeamFinder.Core.FloatingObjectFixer.RunDetectionForDirectDataFolder(
                dataFolder, Log, s.ThresholdUnits, s.WorldspaceFilter);
        }
    }

    void ShowValidation(string message)
    {
        Dispatcher.Invoke(() => MessageBox.Show(this, message, "Missing information", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    // If we're in MO2 mode and the chosen output folder is actually inside
    // this instance's mods folder (the default, but the user may have
    // redirected elsewhere), add a meta.ini so MO2 recognizes it as an
    // installable mod. Skipped entirely if they pointed output somewhere
    // else - no meta.ini dropped into an unrelated folder. Shared by both
    // the detection report and the fix plugin, since either can land there.
    void EnsureMo2MetaIni(string outputFolder)
    {
        if (ModeMo2.IsChecked != true) return;

        var instancePath = Mo2InstancePathBox.Text.Trim();
        if (string.IsNullOrEmpty(instancePath)) return;

        var modsDir = Path.Combine(instancePath, "mods") + Path.DirectorySeparatorChar;
        var fullOutput = Path.GetFullPath(outputFolder) + Path.DirectorySeparatorChar;
        if (!fullOutput.StartsWith(Path.GetFullPath(modsDir), StringComparison.OrdinalIgnoreCase)) return;

        var metaPath = Path.Combine(outputFolder, "meta.ini");
        if (!File.Exists(metaPath))
        {
            File.WriteAllText(metaPath, "[General]\r\ngameName=SkyrimSE\r\nmodid=0\r\nversion=1.0.0\r\ninstalled=true\r\n");
            AppendLog($"Wrote meta.ini so this shows up as an MO2 mod: {metaPath}");
        }
    }

    void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        if (_currentLogFilePath is not null)
        {
            try { File.AppendAllText(_currentLogFilePath, line + Environment.NewLine); }
            catch { /* best-effort, see StartLogFile */ }
        }
    }

    // --- Result bar ---

    void OpenReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReportPath is null) return;
        Process.Start(new ProcessStartInfo(_lastReportPath) { UseShellExecute = true });
    }

    void OpenFixPluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFixPluginPath is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastFixPluginPath}\"") { UseShellExecute = true });
    }

    void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputFolder is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutputFolder}\"") { UseShellExecute = true });
    }
}
