namespace HerculesWaveBridge;

internal sealed class BridgeController : IDisposable
{
    private const double KnobStep = 0.02;
    private const string WaveLinkAppsFolderTarget = "shell:AppsFolder\\Elgato.WaveLink_g54w8ztgkx496!App";
    private static readonly TimeSpan VolumeWriteCoalesceDelay = TimeSpan.FromMilliseconds(12);

    private readonly BridgeLogger _logger;
    private readonly AtlasLiveSettingsStore _settings;
    private readonly WaveLinkClient _waveLink;
    private readonly IHerculesTransport _transport;
    private readonly DisplayRenderer _displayRenderer = new();
    private readonly object _lock = new();
    private readonly object _volumeWriteLock = new();
    private readonly System.Threading.Timer _refreshTimer;
    private readonly bool _previewRenderDisabled = !IsEnvironmentEnabled("HWB_ENABLE_PREVIEW_RENDER");
    private readonly double?[] _pendingVolumeWrites = new double?[4];
    private readonly bool[] _volumeWriteActive = new bool[4];
    private readonly long[] _lastKnobTurnTicks = new long[4];
    private ChannelSlot[] _slots = Enumerable.Range(0, 4).Select(ChannelSlot.Empty).ToArray();
    private WaveOutputTarget _personalMixOutput1 = WaveOutputTarget.Empty;
    private double? _pendingOutputVolumeWrite;
    private bool _outputVolumeWriteActive;
    private BridgeStatus _status = new(false, false, "Not connected", "Starting");
    private bool _refreshInProgress;
    private bool _hardwareReconnectInProgress;
    private DateTimeOffset _nextHardwareReconnectAttempt = DateTimeOffset.MinValue;

    public BridgeController(BridgeLogger logger, AtlasLiveSettingsStore settings)
    {
        _logger = logger;
        _settings = settings;
        _waveLink = new WaveLinkClient(logger);
        _transport = new HerculesTransport(logger);
        _transport.InputReceived += OnInputReceived;
        _transport.StatusChanged += OnTransportStatusChanged;
        _refreshTimer = new System.Threading.Timer(_ => _ = MaintenanceTickAsync(CancellationToken.None), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler? StateChanged;

    public BridgeStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    public IReadOnlyList<ChannelSlot> Slots
    {
        get
        {
            lock (_lock)
            {
                return _slots.ToArray();
            }
        }
    }

    public IReadOnlyList<ChannelSlot> DisplaySlots
    {
        get
        {
            lock (_lock)
            {
                var slots = _slots.ToArray();
                for (var index = 0; index < slots.Length; index++)
                {
                    if (!IsPersonalMixOutput1Mapped(index))
                    {
                        continue;
                    }

                    slots[index] = _personalMixOutput1.IsActive
                        ? new ChannelSlot(
                            index,
                            _personalMixOutput1.OutputDeviceId,
                            "Personal Out 1",
                            _personalMixOutput1.MixId,
                            _personalMixOutput1.Volume01,
                            _personalMixOutput1.IsMuted,
                            true,
                            AppMatchText: _personalMixOutput1.OutputName,
                            ChannelType: "Output")
                        : ChannelSlot.Empty(index) with { ChannelName = "Personal Out 1" };
                }

                return slots;
            }
        }
    }

    public WaveOutputTarget PersonalMixOutput1
    {
        get
        {
            lock (_lock)
            {
                return _personalMixOutput1;
            }
        }
    }

    public string LogDirectory => _logger.LogDirectory;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.Info("Starting Hercules Wave Bridge.");
        await ReconnectWaveLinkAsync(cancellationToken).ConfigureAwait(false);
        await ReconnectHardwareAsync(cancellationToken).ConfigureAwait(false);
        _refreshTimer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public async Task ReconnectWaveLinkAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await _waveLink.GetApplicationInfoAsync(cancellationToken).ConfigureAwait(false);
            _logger.Info($"Connected to Wave Link {info.Version} build {info.Build}.");
            UpdateStatus(waveLinkConnected: true, lastError: string.Empty);
            await RefreshChannelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Wave Link connection failed");
            UpdateStatus(waveLinkConnected: false, lastError: $"Wave Link: {ex.Message}");
        }
    }

    public async Task ReconnectHardwareAsync(CancellationToken cancellationToken)
    {
        await _transport.StopAsync().ConfigureAwait(false);
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshChannelsAsync(CancellationToken cancellationToken)
    {
        if (_refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            var slots = (await _waveLink.GetChannelSlotsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            var personalMixOutput1 = await _waveLink.GetPersonalMixOutput1Async(cancellationToken).ConfigureAwait(false);
            bool slotsChanged;
            bool outputChanged;
            lock (_lock)
            {
                slotsChanged = !_slots.SequenceEqual(slots);
                outputChanged = _personalMixOutput1 != personalMixOutput1;
                _slots = slots;
                _personalMixOutput1 = personalMixOutput1;
            }

            var statusChanged = UpdateStatus(waveLinkConnected: true, lastError: string.Empty);
            if (slotsChanged)
            {
                LogChannelMapping(slots);
            }


            if (outputChanged)
            {
                LogOutputMapping(personalMixOutput1);
            }

            if (slotsChanged || outputChanged || statusChanged)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
                await RenderDisplayAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Channel refresh failed");
            UpdateStatus(waveLinkConnected: false, lastError: $"Wave Link refresh: {ex.Message}");
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private async Task MaintenanceTickAsync(CancellationToken cancellationToken)
    {
        await RefreshChannelsAsync(cancellationToken).ConfigureAwait(false);

        var status = Status;
        if (status.DeviceConnected || _hardwareReconnectInProgress || DateTimeOffset.Now < _nextHardwareReconnectAttempt)
        {
            return;
        }

        _hardwareReconnectInProgress = true;
        _nextHardwareReconnectAttempt = DateTimeOffset.Now.AddSeconds(8);
        try
        {
            _logger.Info("Attempting automatic Hercules hardware reconnect.");
            await ReconnectHardwareAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Automatic Hercules hardware reconnect failed");
            UpdateStatus(deviceConnected: false, lastError: $"Hardware reconnect: {ex.Message}");
        }
        finally
        {
            _hardwareReconnectInProgress = false;
        }
    }

    public void Dispose()
    {
        _refreshTimer.Dispose();
        _transport.Dispose();
        _waveLink.Dispose();
    }

    private void OnTransportStatusChanged(object? sender, TransportStatus e)
    {
        if (UpdateStatus(deviceConnected: e.Connected, transportMode: e.Mode, lastError: e.Connected ? string.Empty : e.Message))
        {
            _ = RenderDisplayAsync(CancellationToken.None);
        }
    }

    private void OnInputReceived(object? sender, DeviceInputEvent inputEvent)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                switch (inputEvent)
                {
                    case KnobTurn turn:
                        await HandleKnobTurnAsync(turn, CancellationToken.None).ConfigureAwait(false);
                        break;
                    case KnobPress press when press.IsPressed:
                        await HandleKnobPressAsync(press, CancellationToken.None).ConfigureAwait(false);
                        break;
                    case ActionButton button when button.IsPressed:
                        HandleActionButton(button);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Input handling failed for {inputEvent}");
                UpdateStatus(lastError: ex.Message);
            }
        });
    }

    private async Task HandleKnobTurnAsync(KnobTurn turn, CancellationToken cancellationToken)
    {
        var index = Math.Clamp(turn.Index, 0, 3);
        if (IsPersonalMixOutput1Mapped(index))
        {
            WaveOutputTarget nextTarget;
            lock (_lock)
            {
                var target = _personalMixOutput1;
                if (!target.IsActive)
                {
                    return;
                }

                var step = GetAcceleratedKnobStep(index);
                var next = Math.Clamp(target.Volume01 + (turn.Delta * step), 0, 1);
                if (Math.Abs(next - target.Volume01) < 0.0001)
                {
                    return;
                }

                nextTarget = target with { Volume01 = next };
                _personalMixOutput1 = nextTarget;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            QueueOutputVolumeWrite(nextTarget.Volume01);
            return;
        }

        ChannelSlot nextSlot;
        lock (_lock)
        {
            var slot = _slots[index];
            if (!slot.IsActive)
            {
                return;
            }

            var step = GetAcceleratedKnobStep(slot.Index);
            var next = Math.Clamp(slot.Volume01 + (turn.Delta * step), 0, 1);
            if (Math.Abs(next - slot.Volume01) < 0.0001)
            {
                return;
            }

            nextSlot = slot with { Volume01 = next };
            _slots[nextSlot.Index] = nextSlot;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        QueueVolumeWrite(nextSlot);
        await RenderDisplayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleKnobPressAsync(KnobPress press, CancellationToken cancellationToken)
    {
        var index = Math.Clamp(press.Index, 0, 3);
        if (IsPersonalMixOutput1Mapped(index))
        {
            WaveOutputTarget target;
            lock (_lock)
            {
                target = _personalMixOutput1;
                if (!target.IsActive)
                {
                    return;
                }

                _personalMixOutput1 = target with { IsMuted = !target.IsMuted };
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            await _waveLink.SetOutputMuteAsync(target, !target.IsMuted, cancellationToken).ConfigureAwait(false);
            return;
        }

        var slot = GetSlot(index);
        if (!slot.IsActive)
        {
            return;
        }

        var next = !slot.IsMuted;
        ReplaceSlot(slot with { IsMuted = next });
        await _waveLink.SetChannelMixMuteAsync(slot, next, cancellationToken).ConfigureAwait(false);
        await RenderDisplayAsync(cancellationToken).ConfigureAwait(false);
    }

    private void QueueVolumeWrite(ChannelSlot slot)
    {
        var index = Math.Clamp(slot.Index, 0, 3);
        lock (_volumeWriteLock)
        {
            _pendingVolumeWrites[index] = slot.Volume01;
            if (_volumeWriteActive[index])
            {
                return;
            }

            _volumeWriteActive[index] = true;
        }

        _ = Task.Run(() => VolumeWriteLoopAsync(index), CancellationToken.None);
    }

    private async Task VolumeWriteLoopAsync(int index)
    {
        try
        {
            await Task.Delay(VolumeWriteCoalesceDelay).ConfigureAwait(false);

            while (true)
            {
                double volume;
                lock (_volumeWriteLock)
                {
                    if (_pendingVolumeWrites[index] is not { } pending)
                    {
                        _volumeWriteActive[index] = false;
                        return;
                    }

                    volume = pending;
                    _pendingVolumeWrites[index] = null;
                }

                var slot = GetSlot(index);
                if (slot.IsActive)
                {
                    await _waveLink.SetChannelMixVolumeAsync(slot, volume, CancellationToken.None).ConfigureAwait(false);
                }

                await Task.Delay(VolumeWriteCoalesceDelay).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            lock (_volumeWriteLock)
            {
                _volumeWriteActive[index] = false;
            }

            _logger.Error(ex, $"Volume write loop failed for slot {index + 1}");
            UpdateStatus(lastError: $"Volume write: {ex.Message}");
        }
    }

    private void QueueOutputVolumeWrite(double volume01)
    {
        lock (_volumeWriteLock)
        {
            _pendingOutputVolumeWrite = volume01;
            if (_outputVolumeWriteActive)
            {
                return;
            }

            _outputVolumeWriteActive = true;
        }

        _ = Task.Run(OutputVolumeWriteLoopAsync, CancellationToken.None);
    }

    private async Task OutputVolumeWriteLoopAsync()
    {
        try
        {
            await Task.Delay(VolumeWriteCoalesceDelay).ConfigureAwait(false);

            while (true)
            {
                double volume;
                lock (_volumeWriteLock)
                {
                    if (_pendingOutputVolumeWrite is not { } pending)
                    {
                        _outputVolumeWriteActive = false;
                        return;
                    }

                    volume = pending;
                    _pendingOutputVolumeWrite = null;
                }

                var target = PersonalMixOutput1;
                if (target.IsActive)
                {
                    await _waveLink.SetOutputVolumeAsync(target, volume, CancellationToken.None).ConfigureAwait(false);
                }

                await Task.Delay(VolumeWriteCoalesceDelay).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            lock (_volumeWriteLock)
            {
                _outputVolumeWriteActive = false;
            }

            _logger.Error(ex, "Personal Mix Audio Output 1 volume write loop failed");
            UpdateStatus(lastError: $"Output volume write: {ex.Message}");
        }
    }

    private void HandleActionButton(ActionButton button)
    {
        switch (button.Index)
        {
            case 0:
                SendMediaKey(button, MediaKey.PreviousTrack);
                break;
            case 1:
                SendMediaKey(button, MediaKey.PlayPause);
                break;
            case 2:
                SendMediaKey(button, MediaKey.NextTrack);
                break;
            case 3:
                LaunchWaveLink(button);
                break;
            default:
                _logger.Info($"Action button {button.Index + 1} is unbound.");
                break;
        }
    }

    private void SendMediaKey(ActionButton button, MediaKey key)
    {
        var result = MediaKeySender.Send(key);
        if (result.Success)
        {
            _logger.Info($"Action button {button.Index + 1} sent media key {key}.");
        }
        else
        {
            _logger.Warn(
                $"Action button {button.Index + 1} failed to send media key {key}: " +
                $"sentInputs={result.SentInputs}; lastError={result.LastError}; inputSize={result.InputSize}.");
        }
    }

    private void LaunchWaveLink(ActionButton button)
    {
        if (TryStartProcess(WaveLinkAppsFolderTarget, arguments: null, useShellExecute: true, out var firstError) ||
            TryStartProcess("explorer.exe", WaveLinkAppsFolderTarget, useShellExecute: false, out var secondError) ||
            TryStartProcess("wavelink:", arguments: null, useShellExecute: true, out var thirdError))
        {
            _logger.Info($"Action button {button.Index + 1} launched Elgato Wave Link.");
            return;
        }

        _logger.Warn(
            $"Action button {button.Index + 1} could not launch Elgato Wave Link. " +
            $"AppsFolder={firstError}; Explorer={secondError}; protocol={thirdError}");
        UpdateStatus(lastError: "Could not launch Elgato Wave Link.");
    }

    private static bool TryStartProcess(string fileName, string? arguments, bool useShellExecute, out string error)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = useShellExecute
            };

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                startInfo.Arguments = arguments;
            }

            System.Diagnostics.Process.Start(startInfo);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private ChannelSlot GetSlot(int index)
    {
        lock (_lock)
        {
            return _slots[Math.Clamp(index, 0, 3)];
        }
    }

    private void ReplaceSlot(ChannelSlot slot)
    {
        lock (_lock)
        {
            _slots[slot.Index] = slot;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RenderDisplayAsync(CancellationToken cancellationToken)
    {
        if (_previewRenderDisabled)
        {
            return;
        }

        ChannelSlot[] slots;
        BridgeStatus status;
        lock (_lock)
        {
            slots = _slots.ToArray();
            status = _status;
        }

        using var image = _displayRenderer.Render(slots, status);
        await _transport.SendDisplayFrameAsync(image, slots, status, cancellationToken).ConfigureAwait(false);
    }

    private bool UpdateStatus(bool? deviceConnected = null, bool? waveLinkConnected = null, string? transportMode = null, string? lastError = null)
    {
        bool changed;
        lock (_lock)
        {
            var next = _status with
            {
                DeviceConnected = deviceConnected ?? _status.DeviceConnected,
                WaveLinkConnected = waveLinkConnected ?? _status.WaveLinkConnected,
                TransportMode = transportMode ?? _status.TransportMode,
                LastError = lastError ?? _status.LastError
            };

            changed = next != _status;
            _status = next;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    private void LogChannelMapping(IReadOnlyList<ChannelSlot> slots)
    {
        _logger.Info("Mapped Wave Link channels: " + string.Join(", ", slots.Select(slot => slot.IsActive ? $"{slot.Index + 1}:{slot.ChannelName} ({slot.ChannelId}/{slot.MixId})" : $"{slot.Index + 1}:empty")));
    }

    private void LogOutputMapping(WaveOutputTarget target)
    {
        _logger.Info(target.IsActive
            ? $"Mapped Personal Mix Audio Output 1: {target.OutputName} ({target.OutputDeviceId}/{target.OutputId}); level={target.Volume01:0.00}; muted={target.IsMuted}."
            : "Personal Mix Audio Output 1 is not currently routed to an output device.");
    }

    private bool IsPersonalMixOutput1Mapped(int index) =>
        _settings.Current.EncoderTargetFor(index) == AtlasLiveSettings.PersonalMixOutput1EncoderTarget;

    private double GetAcceleratedKnobStep(int index)
    {
        if (!IsEnvironmentEnabled("HWB_KNOB_ACCELERATION", defaultEnabled: true))
        {
            return KnobStep * GetKnobSensitivityMultiplier();
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var last = _lastKnobTurnTicks[index];
        _lastKnobTurnTicks[index] = now;
        if (last == 0)
        {
            return KnobStep * GetKnobSensitivityMultiplier();
        }

        var elapsedMs = (now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var acceleratedStep = elapsedMs switch
        {
            <= 45 => KnobStep * 3.0,
            <= 90 => KnobStep * 2.0,
            _ => KnobStep
        };
        return acceleratedStep * GetKnobSensitivityMultiplier();
    }

    private double GetKnobSensitivityMultiplier()
    {
        return _settings.Current.KnobSensitivityLevel switch
        {
            2 => 1.5,
            3 => 2.0,
            4 => 3.0,
            5 => 4.0,
            _ => 1.0
        };
    }

    private static bool IsEnvironmentEnabled(string name, bool defaultEnabled = false)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            return defaultEnabled;
        }

        return value is not null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
