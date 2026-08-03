using System.Reflection;

namespace HerculesWaveBridge;

internal sealed class AtlasLiveDisplayProcess : IDisposable
{
    private const int ActionCount = 4;
    private static readonly TimeSpan SilentVuKeepAlive = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GraphicsKeepAlive = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MeterBindingRefresh = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();
    private readonly BridgeLogger _logger;
    private readonly AtlasLiveSettingsStore _settings;
    private readonly BridgeController _controller;
    private readonly BackgroundImageManager _backgroundImages;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private string _statusText = "Stopped";
    private int _refreshVersion;

    public AtlasLiveDisplayProcess(
        BridgeLogger logger,
        AtlasLiveSettingsStore settings,
        BridgeController controller,
        BackgroundImageManager backgroundImages)
    {
        _logger = logger;
        _settings = settings;
        _controller = controller;
        _backgroundImages = backgroundImages;
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _worker is { IsCompleted: false };
            }
        }
    }

    public string StatusText
    {
        get
        {
            lock (_lock)
            {
                return _statusText;
            }
        }
    }

    public void Start()
    {
        if (IsEnvironmentEnabled("HWB_DISABLE_ATLAS_DISPLAY"))
        {
            SetStatus("Disabled by HWB_DISABLE_ATLAS_DISPLAY.");
            _logger.Warn("Atlas Live native display is disabled by HWB_DISABLE_ATLAS_DISPLAY.");
            return;
        }

        lock (_lock)
        {
            if (_worker is { IsCompleted: false })
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => WorkerLoopAsync(_cts.Token), CancellationToken.None);
            _statusText = "Starting native display loop";
        }
    }

    public void Refresh()
    {
        Interlocked.Increment(ref _refreshVersion);
        if (!IsRunning)
        {
            Start();
            return;
        }

        SetStatus("Display refresh requested");
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_lock)
        {
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
            _statusText = "Stopped";
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
                worker?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The worker may already be unwinding through native SDK cleanup.
            }
            finally
            {
                cts.Dispose();
            }
        }
    }

    public void Dispose() => Stop();

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        var resources = ExtractDisplayResources();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunDisplaySessionAsync(resources, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetStatus($"Display error: {ex.Message}");
                _logger.Error(ex, "Atlas Live native display session failed");
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunDisplaySessionAsync(DisplayResources resources, CancellationToken cancellationToken)
    {
        using var sdk = new NativeHsmSdk(resources.DisplayDirectory, _logger);
        using var liveMeters = new LiveAudioMeterService(_logger);
        var count = sdk.InitWait(TimeSpan.FromSeconds(8), cancellationToken);
        _logger.Info($"Atlas Live native SDK device count: {count}.");
        if (count <= 0)
        {
            SetStatus("No Stream 100 display found");
            await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            return;
        }

        var openResult = sdk.Open();
        _logger.Info($"Atlas Live native SDK open result: ret={openResult}; handle=0x{sdk.Handle:x}.");
        if (sdk.Handle == 0)
        {
            throw new InvalidOperationException($"HSM_OpenDevice returned handle 0, ret={openResult}.");
        }

        sdk.SetScreenConfiguration();
        sdk.SetDisplay();

        var activeBackgroundPath = _backgroundImages.GetActiveBackgroundPath(resources.BuiltInBackgroundPath);
        var renderer = new AtlasLivePanelRenderer(activeBackgroundPath, resources.WaveLinkIconPath);
        var cache = new Dictionary<string, PreparedGraphic>(StringComparer.Ordinal);
        var backgroundId = PrepareCached(sdk, cache, "background", 0, NativeHsmSdk.ScreenWidth, NativeHsmSdk.ScreenHeight, renderer.RenderBackground());
        int? staleBackgroundId = null;
        var appliedRefreshVersion = Volatile.Read(ref _refreshVersion);
        var actionIconIds = new int[ActionCount];
        var actionStripIds = new int[ActionCount];
        for (var index = 0; index < ActionCount; index++)
        {
            actionIconIds[index] = PrepareCached(sdk, cache, $"action-icon-{index}", 1, AtlasLivePanelRenderer.IconWidth, AtlasLivePanelRenderer.IconHeight, renderer.RenderActionIcon(index));
            actionStripIds[index] = PrepareCached(sdk, cache, $"action-strip-{index}", 2, NativeHsmSdk.StripWidth, NativeHsmSdk.StripHeight, renderer.RenderActionStrip(active: true));
        }

        var currentSettings = _settings.Current;
        ApplyVuStyles(sdk, renderer, currentSettings);
        SetStatus("Running native display loop");

        var lastGraphics = DateTimeOffset.MinValue;
        var lastVu = DateTimeOffset.MinValue;
        var lastMeterBindingRefresh = DateTimeOffset.MinValue;
        var frame = 0;
        var vuFrame = 0;
        var lastLoggedFrame = 0;
        var lastGraphicsFingerprint = 0;
        var hasRenderedGraphics = false;
        var lastVuValues = new double[4][];
        var lastVuSent = Enumerable.Repeat(DateTimeOffset.MinValue, 4).ToArray();
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var requestedRefreshVersion = Volatile.Read(ref _refreshVersion);
            if (requestedRefreshVersion != appliedRefreshVersion)
            {
                activeBackgroundPath = _backgroundImages.GetActiveBackgroundPath(resources.BuiltInBackgroundPath);
                renderer.ReloadBackground(activeBackgroundPath);
                var previousBackgroundId = backgroundId;
                backgroundId = PrepareCached(
                    sdk,
                    cache,
                    "background",
                    0,
                    NativeHsmSdk.ScreenWidth,
                    NativeHsmSdk.ScreenHeight,
                    renderer.RenderBackground());
                if (backgroundId != previousBackgroundId)
                {
                    staleBackgroundId = previousBackgroundId;
                }
                appliedRefreshVersion = requestedRefreshVersion;
                hasRenderedGraphics = false;
                SetStatus("Running native display loop");
                _logger.Info($"Atlas Live display refreshed in place; background={activeBackgroundPath}.");
            }

            var settings = _settings.Current;
            if (settings != currentSettings)
            {
                currentSettings = settings;
                ApplyVuStyles(sdk, renderer, currentSettings);
                _logger.Info($"Atlas Live native VU settings changed: mode={settings.MeterMode}, shape={settings.Shape}, color={settings.ColorMode}, vuRefresh={settings.VuRefresh:0.###}s.");
            }

            var slots = NormalizeSlots(_controller.DisplaySlots);
            if (now - lastMeterBindingRefresh >= MeterBindingRefresh)
            {
                liveMeters.RefreshBindings(slots, settings);
                lastMeterBindingRefresh = now;
            }

            var graphicsFingerprint = BuildGraphicsFingerprint(slots);
            if (!hasRenderedGraphics ||
                graphicsFingerprint != lastGraphicsFingerprint ||
                now - lastGraphics >= GraphicsKeepAlive)
            {
                RenderGraphicsFrame(sdk, renderer, cache, slots, backgroundId, actionIconIds, actionStripIds);
                if (staleBackgroundId is int preparedId)
                {
                    var releaseResult = sdk.UnprepareGraphicElement(preparedId);
                    if (releaseResult != 0)
                    {
                        _logger.Warn($"HSM_UnPrepareGraphicElement({preparedId}) returned {releaseResult}.");
                    }
                    else
                    {
                        _logger.Info($"Released stale prepared display background {preparedId}.");
                    }

                    staleBackgroundId = null;
                }
                lastGraphics = now;
                lastGraphicsFingerprint = graphicsFingerprint;
                hasRenderedGraphics = true;
                frame++;
            }

            var vuRefresh = TimeSpan.FromSeconds(Math.Clamp(currentSettings.VuRefresh, 0.01, 0.2));
            if (now - lastVu >= vuRefresh)
            {
                var meterSnapshots = liveMeters.ReadSnapshots(slots, currentSettings.VuGain);
                RenderVuFrame(sdk, slots, meterSnapshots, currentSettings, lastVuValues, lastVuSent, now);
                lastVu = now;
                vuFrame++;
            }

            if (frame > 0 && frame % 150 == 0 && frame != lastLoggedFrame)
            {
                lastLoggedFrame = frame;
                _logger.Info($"Atlas Live native display heartbeat: graphics={frame}; vu={vuFrame}; slots={string.Join(" | ", slots.Select(slot => slot.IsActive ? $"{slot.ChannelName[..Math.Min(8, slot.ChannelName.Length)]}:{Math.Round(slot.Volume01 * 100):0}%" : "empty"))}.");
            }

            var nextGraphics = lastGraphics + GraphicsKeepAlive;
            var nextVu = lastVu + vuRefresh;
            var sleep = Min(nextGraphics, nextVu) - DateTimeOffset.Now;
            await Task.Delay(ClampDelay(sleep), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ChannelSlot[] NormalizeSlots(IReadOnlyList<ChannelSlot> source)
    {
        var slots = new ChannelSlot[4];
        for (var index = 0; index < slots.Length; index++)
        {
            slots[index] = index < source.Count ? source[index] : ChannelSlot.Empty(index);
        }

        return slots;
    }

    private static int BuildGraphicsFingerprint(ChannelSlot[] slots)
    {
        var hash = new HashCode();
        foreach (var slot in slots)
        {
            hash.Add(slot.Index);
            hash.Add(slot.ChannelId, StringComparer.Ordinal);
            hash.Add(slot.ChannelName, StringComparer.Ordinal);
            hash.Add(slot.IsMuted);
            hash.Add(slot.IsActive);
            hash.Add(slot.IconData, StringComparer.Ordinal);
            hash.Add(slot.IsAppIcon);
        }

        return hash.ToHashCode();
    }

    private void RenderGraphicsFrame(
        NativeHsmSdk sdk,
        AtlasLivePanelRenderer renderer,
        Dictionary<string, PreparedGraphic> cache,
        ChannelSlot[] slots,
        int backgroundId,
        int[] actionIconIds,
        int[] actionStripIds)
    {
        sdk.SetCustomBackground(backgroundId);
        for (var index = 0; index < 4; index++)
        {
            var slot = slots[index];
            var iconId = PrepareCached(sdk, cache, $"top-icon-{index}", 1, AtlasLivePanelRenderer.IconWidth, AtlasLivePanelRenderer.IconHeight, renderer.RenderChannelIcon(slot));
            var stripId = PrepareCached(sdk, cache, $"top-strip-{index}", 2, NativeHsmSdk.StripWidth, NativeHsmSdk.StripHeight, renderer.RenderTopStrip(slot));
            sdk.SetGraphicElement((uint)index, iconId, slot.IsMuted || !slot.IsActive ? 4 : 0);
            sdk.SetGraphicElement((uint)index, stripId, 0);
            sdk.SetGraphicElement(AtlasLivePanelRenderer.BottomTargetBase + (uint)index, actionIconIds[index], 0);
            sdk.SetGraphicElement(AtlasLivePanelRenderer.BottomTargetBase + (uint)index, actionStripIds[index], 0);
            sdk.SetLedStyle(index, 1);
        }

        sdk.SetDisplay();
    }

    private static void RenderVuFrame(
        NativeHsmSdk sdk,
        ChannelSlot[] slots,
        LiveMeterSnapshot[] meterSnapshots,
        AtlasLiveSettings settings,
        double[][] lastValues,
        DateTimeOffset[] lastSent,
        DateTimeOffset now)
    {
        for (var index = 0; index < 4; index++)
        {
            var slot = slots[index];
            var liveLevel = index < meterSnapshots.Length ? meterSnapshots[index].PostFaderPeak01 : 0;
            var markerLevel = slot.IsActive ? slot.Volume01 : 0;
            var values = AtlasLivePanelRenderer.NativeMeterValues(liveLevel, markerLevel, settings);
            if (CanSkipSilentVuUpdate(values, lastValues[index], now - lastSent[index]))
            {
                continue;
            }

            sdk.SetAudioLevels6(index, values[0], values[1], values[2], values[3], values[4], values[5]);
            lastValues[index] = values;
            lastSent[index] = now;
        }
    }

    private static bool CanSkipSilentVuUpdate(double[] values, double[]? previous, TimeSpan elapsed)
    {
        if (previous is null || elapsed >= SilentVuKeepAlive)
        {
            return false;
        }

        var liveBarIsSilent =
            values[1] <= 0.001 &&
            values[2] <= 0.001 &&
            values[4] <= 0.001 &&
            values[5] <= 0.001;
        return liveBarIsSilent && previous.AsSpan().SequenceEqual(values);
    }

    private void ApplyVuStyles(NativeHsmSdk sdk, AtlasLivePanelRenderer renderer, AtlasLiveSettings settings)
    {
        var styles = renderer.BuildVuStyles(settings);
        var results = new int[styles.Length];
        for (var index = 0; index < styles.Length; index++)
        {
            results[index] = sdk.SetAudioChannelStyle(index, styles[index]);
        }

        _logger.Info($"Atlas Live native VU style results: {string.Join(", ", results)}.");
    }

    private static int PrepareCached(NativeHsmSdk sdk, Dictionary<string, PreparedGraphic> cache, string key, int kind, int width, int height, byte[] pixels)
    {
        if (cache.TryGetValue(key, out var old) && old.Pixels.AsSpan().SequenceEqual(pixels))
        {
            return old.Id;
        }

        var id = sdk.PrepareGraphicElement(kind, width, height, pixels);
        cache[key] = new PreparedGraphic(id, pixels);
        return id;
    }

    private DisplayResources ExtractDisplayResources()
    {
        var displayDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerculesWaveBridge",
            "display");
        Directory.CreateDirectory(displayDir);
        DeleteStaleResource(Path.Combine(displayDir, "wave_draft.py"));
        DeleteStaleResource(Path.Combine(displayDir, "hsm_sdk.py"));
        var builtInBackgroundPath = Path.Combine(displayDir, "background.png");
        ExtractResource("AtlasLive.background.png", builtInBackgroundPath);
        if (!HerculesRuntimeProvisioner.TryProvision(displayDir, _logger, out var runtimeError))
        {
            throw new InvalidOperationException(runtimeError);
        }

        var waveLinkIconPath = HerculesRuntimeProvisioner.TryExtractWaveLinkIcon(
            Path.Combine(displayDir, "wave-link-local-icon.png"),
            _logger);
        return new DisplayResources(
            displayDir,
            builtInBackgroundPath,
            waveLinkIconPath);
    }

    private void DeleteStaleResource(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not delete stale display resource {path}: {ex.Message}");
        }
    }

    private static void ExtractResource(string resourceName, string targetPath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var input = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        input.CopyTo(output);
    }

    private void SetStatus(string message)
    {
        lock (_lock)
        {
            _statusText = message;
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static TimeSpan ClampDelay(TimeSpan value)
    {
        if (value < TimeSpan.FromMilliseconds(5))
        {
            return TimeSpan.FromMilliseconds(5);
        }

        if (value > TimeSpan.FromMilliseconds(50))
        {
            return TimeSpan.FromMilliseconds(50);
        }

        return value;
    }

    private static bool IsEnvironmentEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is not null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record DisplayResources(string DisplayDirectory, string BuiltInBackgroundPath, string? WaveLinkIconPath);

    private sealed record PreparedGraphic(int Id, byte[] Pixels);
}
