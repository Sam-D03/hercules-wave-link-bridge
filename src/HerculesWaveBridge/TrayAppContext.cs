using System.Diagnostics;

namespace HerculesWaveBridge;

internal sealed class TrayAppContext : ApplicationContext
{
    private sealed record ColorPreset(string Label, string Hex);

    private sealed record GradientPreset(string Label, string StartHex, string EndHex);

    private static readonly ColorPreset[] ColorPresets =
    [
        new("Orange", "#ff5c00"),
        new("Red", "#ff321c"),
        new("Yellow", "#ffe74a"),
        new("Green", "#80ff00"),
        new("Cyan", "#00f0c8"),
        new("Blue", "#1f45d8"),
        new("Purple", "#a335ff"),
        new("White", "#f5f7fa")
    ];

    private static readonly GradientPreset[] GradientPresets =
    [
        new("Orange to Green", "#ff5c00", "#80ff00"),
        new("Red to Yellow", "#ff321c", "#ffe74a"),
        new("Cyan to Blue", "#00f0c8", "#1f45d8"),
        new("Purple to Pink", "#a335ff", "#ff5ccc"),
        new("White to Blue", "#f5f7fa", "#45a3ff")
    ];

    private readonly BridgeController _controller;
    private readonly BridgeLogger _logger;
    private readonly AtlasLiveSettingsStore _atlasSettings;
    private readonly AtlasLiveDisplayProcess _displayProcess;
    private readonly BackgroundImageManager _backgroundImages;
    private readonly Icon _appIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _vuMenu;
    private readonly ToolStripMenuItem _meterMapMenu;
    private readonly ToolStripMenuItem _encoderMapMenu;
    private readonly ToolStripMenuItem _knobMenu;
    private readonly ToolStripMenuItem _backgroundMenu;
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _keepOpenResetTimer;
    private bool _keepMenuOpenAfterClick;
    private bool _suppressVuMenuRebuild;

    public TrayAppContext(
        BridgeController controller,
        BridgeLogger logger,
        AtlasLiveSettingsStore atlasSettings,
        AtlasLiveDisplayProcess displayProcess,
        BackgroundImageManager backgroundImages)
    {
        _controller = controller;
        _logger = logger;
        _atlasSettings = atlasSettings;
        _displayProcess = displayProcess;
        _backgroundImages = backgroundImages;
        _controller.StateChanged += (_, _) => UpdateStatus();
        _atlasSettings.SettingsChanged += (_, _) =>
        {
            if (!_suppressVuMenuRebuild)
            {
                UpdateVuMenu();
                UpdateMeterMapMenu();
                UpdateEncoderMapMenu();
                UpdateKnobMenu();
            }
        };

        _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        _vuMenu = new ToolStripMenuItem("VU Meter");
        _meterMapMenu = new ToolStripMenuItem("Live Meter Mapping");
        _encoderMapMenu = new ToolStripMenuItem("Encoder Mapping");
        _knobMenu = new ToolStripMenuItem("Knob Sensitivity");
        _backgroundMenu = new ToolStripMenuItem("Background");
        _startupItem = new ToolStripMenuItem("Start on System Startup")
        {
            CheckOnClick = false
        };
        var prerequisiteMenu = new ToolStripMenuItem("Hercules Runtime");
        prerequisiteMenu.DropDownItems.Add("Open Installed SDK Folder", null, (_, _) => OpenInstalledSdk());
        prerequisiteMenu.DropDownItems.Add("Open Official Download Page", null, (_, _) => HerculesRuntimeProvisioner.OpenDownloadPage());
        _startupItem.MouseDown += (_, _) => MarkPersistentMenuClick();
        _startupItem.Click += (_, _) => ToggleStartup();
        _vuMenu.DropDown.Closing += KeepMenuOpenOnPersistentItemClick;
        _meterMapMenu.DropDownOpening += (_, _) => UpdateMeterMapMenu();
        _meterMapMenu.DropDown.Closing += KeepMenuOpenOnPersistentItemClick;
        _encoderMapMenu.DropDownOpening += (_, _) => UpdateEncoderMapMenu();
        _encoderMapMenu.DropDown.Closing += KeepMenuOpenOnPersistentItemClick;
        _knobMenu.DropDown.Closing += KeepMenuOpenOnPersistentItemClick;
        _backgroundMenu.DropDownOpening += (_, _) => UpdateBackgroundMenu();
        _backgroundMenu.DropDown.Closing += KeepMenuOpenOnPersistentItemClick;
        _menu = new ContextMenuStrip();
        _menu.Closing += KeepMenuOpenOnPersistentItemClick;
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Reconnect Hardware", null, async (_, _) => await RunMenuActionAsync(() => _controller.ReconnectHardwareAsync(CancellationToken.None)));
        _menu.Items.Add("Reconnect Wave Link", null, async (_, _) => await RunMenuActionAsync(() => _controller.ReconnectWaveLinkAsync(CancellationToken.None)));
        _menu.Items.Add("Refresh Channels", null, async (_, _) => await RunMenuActionAsync(() => _controller.RefreshChannelsAsync(CancellationToken.None)));
        _menu.Items.Add("Refresh Display", null, async (_, _) => await RunMenuActionAsync(() =>
        {
            _displayProcess.Refresh();
            return Task.CompletedTask;
        }));
        _menu.Items.Add(_vuMenu);
        _menu.Items.Add(_meterMapMenu);
        _menu.Items.Add(_encoderMapMenu);
        _menu.Items.Add(_knobMenu);
        _menu.Items.Add(_backgroundMenu);
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(prerequisiteMenu);
        _menu.Items.Add("Open Logs", null, (_, _) => OpenLogs());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _appIcon = LoadAppIcon();
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _appIcon,
            Text = "Hercules Wave Bridge",
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => OpenLogs();
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => UpdateStatus();
        _keepOpenResetTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _keepOpenResetTimer.Tick += (_, _) =>
        {
            _keepOpenResetTimer.Stop();
            _keepMenuOpenAfterClick = false;
        };
        _timer.Start();
        UpdateVuMenu();
        UpdateMeterMapMenu();
        UpdateEncoderMapMenu();
        UpdateKnobMenu();
        UpdateBackgroundMenu();
        UpdateStartupMenu();
        UpdateStatus();
        _notifyIcon.ShowBalloonTip(3000, "Hercules Wave Bridge", "Bridge is starting. Close Hercules Stream Control before hardware takeover.", ToolTipIcon.Info);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _keepOpenResetTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _appIcon.Dispose();
            _displayProcess.Dispose();
            _controller.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task RunMenuActionAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Tray action failed");
            _notifyIcon.ShowBalloonTip(5000, "Hercules Wave Bridge", ex.Message, ToolTipIcon.Error);
        }
    }

    private void UpdateStatus()
    {
        UpdateStartupMenu();
        var status = _controller.Status;
        var slots = _controller.Slots;
        var text = $"USB:{(status.DeviceConnected ? "OK" : "OFF")} WL:{(status.WaveLinkConnected ? "OK" : "OFF")} {status.TransportMode}";
        _statusItem.Text = text;
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;

        _statusItem.DropDownItems.Clear();
        foreach (var slot in slots)
        {
            var target = _atlasSettings.Current.EncoderTargetFor(slot.Index);
            _statusItem.DropDownItems.Add(slot.IsActive
                ? $"{slot.Index + 1}. {slot.ChannelName} {Math.Round(slot.Volume01 * 100):0}% {(slot.IsMuted ? "muted" : "")} - encoder: {(target == AtlasLiveSettings.PersonalMixOutput1EncoderTarget ? "Personal Mix Output 1" : "channel")}"
                : $"{slot.Index + 1}. Empty");
        }

        var output = _controller.PersonalMixOutput1;
        _statusItem.DropDownItems.Add(output.IsActive
            ? $"Personal Mix Output 1: {output.OutputName} {Math.Round(output.Volume01 * 100):0}% {(output.IsMuted ? "muted" : "")}"
            : "Personal Mix Output 1: not routed");

        _statusItem.DropDownItems.Add($"Display: {_displayProcess.StatusText}");
        _statusItem.DropDownItems.Add($"Background: {_backgroundImages.CurrentLabel}");

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            _statusItem.DropDownItems.Add(status.LastError);
        }
    }

    private void UpdateVuMenu()
    {
        var settings = _atlasSettings.Current;
        _vuMenu.DropDownItems.Clear();
        var settingsPathItem = _vuMenu.DropDownItems.Add($"Settings: {Path.GetFileName(_atlasSettings.SettingsPath)}");
        settingsPathItem.Enabled = false;
        _vuMenu.DropDownItems.Add(new ToolStripSeparator());

        var mode = new ToolStripMenuItem("Mode");
        mode.DropDownItems.Add(MakeCheckedItem("Stereo", settings.MeterMode == "stereo", () => _atlasSettings.Update(s => s with { MeterMode = "stereo" })));
        mode.DropDownItems.Add(MakeCheckedItem("Mono", settings.MeterMode == "mono", () => _atlasSettings.Update(s => s with { MeterMode = "mono" })));
        WirePersistentDropDown(mode);
        _vuMenu.DropDownItems.Add(mode);

        var shape = new ToolStripMenuItem("Shape");
        shape.DropDownItems.Add(MakeCheckedItem("Bars", settings.Shape == "bars", () => _atlasSettings.Update(s => s with { Shape = "bars" })));
        shape.DropDownItems.Add(MakeCheckedItem("Blocks", settings.Shape == "blocks", () => _atlasSettings.Update(s => s with { Shape = "blocks" })));
        shape.DropDownItems.Add(MakeCheckedItem("Pillars", settings.Shape == "pillars", () => _atlasSettings.Update(s => s with { Shape = "pillars" })));
        shape.DropDownItems.Add(MakeCheckedItem("Thin", settings.Shape == "thin", () => _atlasSettings.Update(s => s with { Shape = "thin" })));
        WirePersistentDropDown(shape);
        _vuMenu.DropDownItems.Add(shape);

        var colorMode = new ToolStripMenuItem("Color Mode");
        colorMode.DropDownItems.Add(MakeCheckedItem("Gradient", settings.ColorMode == "gradient", () => _atlasSettings.Update(s => s with { ColorMode = "gradient" })));
        colorMode.DropDownItems.Add(MakeCheckedItem("Solid", settings.ColorMode == "solid", () => _atlasSettings.Update(s => s with { ColorMode = "solid" })));
        WirePersistentDropDown(colorMode);
        _vuMenu.DropDownItems.Add(colorMode);

        var gradient = new ToolStripMenuItem("Gradient");
        foreach (var preset in GradientPresets)
        {
            gradient.DropDownItems.Add(MakeCheckedItem(
                preset.Label,
                settings.GradientStart == preset.StartHex && settings.GradientEnd == preset.EndHex,
                () => _atlasSettings.Update(s => s with
                {
                    ColorMode = "gradient",
                    GradientStart = preset.StartHex,
                    GradientEnd = preset.EndHex
                })));
        }

        WirePersistentDropDown(gradient);
        _vuMenu.DropDownItems.Add(gradient);

        var solid = new ToolStripMenuItem("Solid Color");
        foreach (var preset in ColorPresets)
        {
            solid.DropDownItems.Add(MakeCheckedItem(
                preset.Label,
                settings.SolidColor == preset.Hex,
                () => _atlasSettings.Update(s => s with
                {
                    ColorMode = "solid",
                    SolidColor = preset.Hex
                })));
        }

        WirePersistentDropDown(solid);
        _vuMenu.DropDownItems.Add(solid);

        var gain = new ToolStripMenuItem("Level Gain");
        gain.DropDownItems.Add(MakeCheckedItem("1.00x", Math.Abs(settings.VuGain - 1.00) < 0.001, () => _atlasSettings.Update(s => s with { VuGain = 1.00 })));
        gain.DropDownItems.Add(MakeCheckedItem("1.08x", Math.Abs(settings.VuGain - 1.08) < 0.001, () => _atlasSettings.Update(s => s with { VuGain = 1.08 })));
        gain.DropDownItems.Add(MakeCheckedItem("1.16x", Math.Abs(settings.VuGain - 1.16) < 0.001, () => _atlasSettings.Update(s => s with { VuGain = 1.16 })));
        gain.DropDownItems.Add(MakeCheckedItem("1.25x", Math.Abs(settings.VuGain - 1.25) < 0.001, () => _atlasSettings.Update(s => s with { VuGain = 1.25 })));
        WirePersistentDropDown(gain);
        _vuMenu.DropDownItems.Add(gain);

        var refresh = new ToolStripMenuItem("Refresh Rate");
        refresh.DropDownItems.Add(MakeCheckedItem("50 Hz", Math.Abs(settings.VuRefresh - 0.02) < 0.001, () => _atlasSettings.Update(s => s with { VuRefresh = 0.02 })));
        refresh.DropDownItems.Add(MakeCheckedItem("25 Hz", Math.Abs(settings.VuRefresh - 0.04) < 0.001, () => _atlasSettings.Update(s => s with { VuRefresh = 0.04 })));
        refresh.DropDownItems.Add(MakeCheckedItem("10 Hz", Math.Abs(settings.VuRefresh - 0.10) < 0.001, () => _atlasSettings.Update(s => s with { VuRefresh = 0.10 })));
        WirePersistentDropDown(refresh);
        _vuMenu.DropDownItems.Add(refresh);
    }

    private void UpdateMeterMapMenu()
    {
        var settings = _atlasSettings.Current;
        var slots = _controller.Slots;
        var candidates = LiveAudioMeterService.GetRunningAppCandidates();
        _meterMapMenu.DropDownItems.Clear();

        var note = _meterMapMenu.DropDownItems.Add("Overrides affect live VU meters only");
        note.Enabled = false;
        _meterMapMenu.DropDownItems.Add(new ToolStripSeparator());

        for (var index = 0; index < 4; index++)
        {
            var slot = index < slots.Count ? slots[index] : ChannelSlot.Empty(index);
            var current = settings.MeterOverrideFor(index);
            var channelMenu = new ToolStripMenuItem($"{index + 1}. {slot.ChannelName} - {MeterOverrideLabel(current, candidates)}");
            WirePersistentDropDown(channelMenu);

            var slotIndex = index;
            channelMenu.DropDownItems.Add(MakeCheckedItem(
                slot.ChannelName.Equals("System", StringComparison.OrdinalIgnoreCase) ? "Auto (default Windows output)" : "Auto",
                string.IsNullOrWhiteSpace(current),
                () => _atlasSettings.Update(s => s.WithMeterOverride(slotIndex, string.Empty))));

            if (!string.IsNullOrWhiteSpace(current) && !candidates.Any(candidate => string.Equals(candidate.MatchText, current, StringComparison.OrdinalIgnoreCase)))
            {
                channelMenu.DropDownItems.Add(new ToolStripMenuItem($"Current: {current} (not running)")
                {
                    Checked = true,
                    Enabled = false
                });
            }

            channelMenu.DropDownItems.Add(new ToolStripSeparator());

            var added = 0;
            foreach (var candidate in candidates.Take(80))
            {
                var matchText = candidate.MatchText;
                channelMenu.DropDownItems.Add(MakeCheckedItem(
                    Shorten(candidate.Label, 86),
                    string.Equals(current, matchText, StringComparison.OrdinalIgnoreCase),
                    () => _atlasSettings.Update(s => s.WithMeterOverride(slotIndex, matchText))));
                added++;
            }

            if (added == 0)
            {
                var none = channelMenu.DropDownItems.Add("No running apps found");
                none.Enabled = false;
            }
            else if (candidates.Count > added)
            {
                var more = channelMenu.DropDownItems.Add($"{candidates.Count - added} more app(s) hidden");
                more.Enabled = false;
            }

            _meterMapMenu.DropDownItems.Add(channelMenu);
        }
    }

    private void UpdateKnobMenu()
    {
        var settings = _atlasSettings.Current;
        _knobMenu.DropDownItems.Clear();
        _knobMenu.DropDownItems.Add(MakeCheckedItem(
            "Level 1 - Current",
            settings.KnobSensitivityLevel == 1,
            () => _atlasSettings.Update(s => s with { KnobSensitivityLevel = 1 })));
        _knobMenu.DropDownItems.Add(MakeCheckedItem(
            "Level 2 - 1.5x",
            settings.KnobSensitivityLevel == 2,
            () => _atlasSettings.Update(s => s with { KnobSensitivityLevel = 2 })));
        _knobMenu.DropDownItems.Add(MakeCheckedItem(
            "Level 3 - 2x",
            settings.KnobSensitivityLevel == 3,
            () => _atlasSettings.Update(s => s with { KnobSensitivityLevel = 3 })));
        _knobMenu.DropDownItems.Add(MakeCheckedItem(
            "Level 4 - 3x",
            settings.KnobSensitivityLevel == 4,
            () => _atlasSettings.Update(s => s with { KnobSensitivityLevel = 4 })));
        _knobMenu.DropDownItems.Add(MakeCheckedItem(
            "Level 5 - 4x",
            settings.KnobSensitivityLevel == 5,
            () => _atlasSettings.Update(s => s with { KnobSensitivityLevel = 5 })));
    }

    private void UpdateEncoderMapMenu()
    {
        var settings = _atlasSettings.Current;
        var slots = _controller.Slots;
        var output = _controller.PersonalMixOutput1;
        _encoderMapMenu.DropDownItems.Clear();

        var note = _encoderMapMenu.DropDownItems.Add("Press toggles mute for the selected target");
        note.Enabled = false;
        _encoderMapMenu.DropDownItems.Add(new ToolStripSeparator());

        for (var index = 0; index < 4; index++)
        {
            var slot = index < slots.Count ? slots[index] : ChannelSlot.Empty(index);
            var current = settings.EncoderTargetFor(index);
            var currentLabel = current == AtlasLiveSettings.PersonalMixOutput1EncoderTarget
                ? "Personal Mix Output 1"
                : $"Channel {index + 1}";
            var encoderMenu = new ToolStripMenuItem($"Encoder {index + 1} - {currentLabel}");
            WirePersistentDropDown(encoderMenu);
            var encoderIndex = index;

            encoderMenu.DropDownItems.Add(MakeCheckedItem(
                $"Channel {index + 1}: {slot.ChannelName}",
                current == AtlasLiveSettings.ChannelEncoderTarget,
                () => _atlasSettings.Update(s => s.WithEncoderTarget(encoderIndex, AtlasLiveSettings.ChannelEncoderTarget))));

            var outputLabel = output.IsActive
                ? $"Personal Mix - Audio Output 1: {Shorten(output.OutputName, 64)}"
                : "Personal Mix - Audio Output 1 (not currently routed)";
            encoderMenu.DropDownItems.Add(MakeCheckedItem(
                outputLabel,
                current == AtlasLiveSettings.PersonalMixOutput1EncoderTarget,
                () => _atlasSettings.Update(s => s.WithEncoderTarget(encoderIndex, AtlasLiveSettings.PersonalMixOutput1EncoderTarget))));

            _encoderMapMenu.DropDownItems.Add(encoderMenu);
        }
    }

    private void UpdateBackgroundMenu()
    {
        _backgroundMenu.DropDownItems.Clear();
        var current = _backgroundMenu.DropDownItems.Add(_backgroundImages.CurrentLabel);
        current.Enabled = false;
        _backgroundMenu.DropDownItems.Add(new ToolStripSeparator());
        _backgroundMenu.DropDownItems.Add(MakeCheckedItem(
            "Use Built-in Waves",
            _backgroundImages.Mode == BackgroundMode.BuiltIn,
            () => SelectBackgroundMode(BackgroundMode.BuiltIn)));
        var custom = MakeCheckedItem(
            "Use Custom Image",
            _backgroundImages.Mode == BackgroundMode.Custom,
            () => SelectBackgroundMode(BackgroundMode.Custom));
        custom.Enabled = _backgroundImages.HasCustomBackground;
        _backgroundMenu.DropDownItems.Add(custom);
        _backgroundMenu.DropDownItems.Add(MakeCheckedItem(
            "Use Spotify Album Artwork",
            _backgroundImages.Mode == BackgroundMode.Spotify,
            () => SelectBackgroundMode(BackgroundMode.Spotify)));
        _backgroundMenu.DropDownItems.Add(new ToolStripSeparator());
        _backgroundMenu.DropDownItems.Add("Choose Custom Image...", null, (_, _) => ChooseBackgroundImage());
    }

    private void ChooseBackgroundImage()
    {
        try
        {
            var selectedPath = BackgroundImagePicker.Choose();
            if (selectedPath is null)
            {
                return;
            }

            _backgroundImages.Import(selectedPath);
            UpdateBackgroundMenu();
            _notifyIcon.ShowBalloonTip(
                3000,
                "Hercules Wave Bridge",
                "Custom background applied.",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not choose or import a custom background image");
            MessageBox.Show(
                ex.Message,
                "Could Not Change Background",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void SelectBackgroundMode(BackgroundMode mode)
    {
        try
        {
            _backgroundImages.SetMode(mode);
            UpdateBackgroundMenu();
            _notifyIcon.ShowBalloonTip(
                3000,
                "Hercules Wave Bridge",
                mode switch
                {
                    BackgroundMode.BuiltIn => "Built-in console background applied.",
                    BackgroundMode.Custom => "Custom background applied.",
                    BackgroundMode.Spotify => "Spotify album artwork background enabled.",
                    _ => "Background updated."
                },
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not select the display background mode");
            MessageBox.Show(
                ex.Message,
                "Could Not Change Background",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string MeterOverrideLabel(string current, IReadOnlyList<MeterAppCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return "Auto";
        }

        var candidate = candidates.FirstOrDefault(candidate => string.Equals(candidate.MatchText, current, StringComparison.OrdinalIgnoreCase));
        return candidate is null ? current : Shorten(candidate.Label, 38);
    }

    private static string Shorten(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(1, maxLength - 1)] + ".";
    }

    private void ToggleStartup()
    {
        MarkPersistentMenuClick();
        try
        {
            var enabled = !StartupManager.IsEnabled();
            StartupManager.SetEnabled(enabled);
            _startupItem.Checked = enabled;
            _logger.Info(enabled
                ? "Enabled Windows startup launch."
                : "Disabled Windows startup launch.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not update Windows startup launch setting");
            _notifyIcon.ShowBalloonTip(5000, "Hercules Wave Bridge", $"Could not update startup setting: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void UpdateStartupMenu()
    {
        try
        {
            _startupItem.Checked = StartupManager.IsEnabled();
        }
        catch (Exception ex)
        {
            _startupItem.Checked = false;
            _logger.Warn($"Could not read Windows startup setting: {ex.Message}");
        }
    }

    private ToolStripMenuItem MakeCheckedItem(string text, bool isChecked, Action action)
    {
        var item = new ToolStripMenuItem(text)
        {
            Checked = isChecked,
            CheckOnClick = false
        };

        item.MouseDown += (_, _) => MarkPersistentMenuClick();
        item.Click += (_, _) =>
        {
            MarkPersistentMenuClick();
            if (item.Owner is not null)
            {
                foreach (ToolStripItem sibling in item.Owner.Items)
                {
                    if (sibling is ToolStripMenuItem siblingItem)
                    {
                        siblingItem.Checked = false;
                    }
                }
            }

            item.Checked = true;
            _suppressVuMenuRebuild = true;
            try
            {
                action();
            }
            finally
            {
                _suppressVuMenuRebuild = false;
            }
        };

        return item;
    }

    private void MarkPersistentMenuClick()
    {
        _keepOpenResetTimer.Stop();
        _keepMenuOpenAfterClick = true;
    }

    private void WirePersistentDropDown(ToolStripMenuItem item)
    {
        item.DropDown.Closing += KeepMenuOpenOnPersistentItemClick;
    }

    private void KeepMenuOpenOnPersistentItemClick(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        if (e.CloseReason != ToolStripDropDownCloseReason.ItemClicked || !_keepMenuOpenAfterClick)
        {
            return;
        }

        e.Cancel = true;
        _keepOpenResetTimer.Stop();
        _keepOpenResetTimer.Start();
    }

    private void OpenLogs()
    {
        Directory.CreateDirectory(_controller.LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = _controller.LogDirectory,
            UseShellExecute = true
        });
    }

    private void OpenInstalledSdk()
    {
        try
        {
            var sdkDirectory = HerculesRuntimeProvisioner.TryFindSdkDirectory()
                ?? throw new DirectoryNotFoundException("The Hercules SDK is not installed.");
            Process.Start(new ProcessStartInfo
            {
                FileName = sdkDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not open the installed Hercules SDK folder");
            _notifyIcon.ShowBalloonTip(5000, "Hercules Wave Bridge", $"Could not open the Hercules SDK folder: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is not null)
            {
                return (Icon)icon.Clone();
            }
        }
        catch
        {
            // Fall back to the system icon if Windows cannot read our executable icon.
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
