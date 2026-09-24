using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace HerculesWaveBridge;

internal sealed class LiveAudioMeterService : IDisposable
{
    private static readonly TimeSpan MissingMatchLogInterval = TimeSpan.FromSeconds(30);
    private static readonly string[] GenericNeedles =
    [
        "software",
        "hardware",
        "input",
        "output",
        "render",
        "capture",
        "wave",
        "link"
    ];

    private readonly BridgeLogger _logger;
    private readonly object _lock = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dictionary<int, DateTimeOffset> _lastMissingLog = new();
    private MeterBindings _bindings = MeterBindings.Empty;
    private string _lastBindingSummary = string.Empty;
    private bool _disposed;

    public LiveAudioMeterService(BridgeLogger logger)
    {
        _logger = logger;
    }

    public static IReadOnlyList<MeterAppCandidate> GetRunningAppCandidates()
    {
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    AudioSessionManager? manager = null;
                    SessionCollection? sessions;
                    try
                    {
                        manager = device.AudioSessionManager;
                        sessions = manager.Sessions;
                    }
                    catch
                    {
                        manager?.Dispose();
                        continue;
                    }

                    try
                    {
                        for (var index = 0; index < sessions.Count; index++)
                        {
                            AudioSessionControl? session = null;
                            try
                            {
                                session = sessions[index];
                                var pid = session.GetProcessID;
                                if (pid != 0)
                                {
                                    AddProcessCandidate(candidates, checked((int)pid));
                                }
                            }
                            catch
                            {
                                // Sessions can disappear while the menu is opening.
                            }
                            finally
                            {
                                session?.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        manager.Dispose();
                    }
                }
            }
        }
        catch
        {
            // Fall back to visible processes below if Core Audio is unavailable.
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(process.MainWindowTitle))
                {
                    AddProcessCandidate(candidates, process);
                }
            }
            catch
            {
                // Process may exit while building the menu.
            }
            finally
            {
                process.Dispose();
            }
        }

        return candidates
            .Select(candidate => new MeterAppCandidate(candidate.Value, candidate.Key))
            .OrderBy(candidate => candidate.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public void RefreshBindings(IReadOnlyList<ChannelSlot> slots, AtlasLiveSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        MeterBindings? oldBindings = null;
        try
        {
            var next = BuildBindings(slots, settings);
            lock (_lock)
            {
                oldBindings = _bindings;
                _bindings = next;
            }

            if (!string.Equals(next.Summary, _lastBindingSummary, StringComparison.Ordinal))
            {
                _lastBindingSummary = next.Summary;
                _logger.Info($"Live meter bindings: {next.Summary}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Live meter refresh failed: {ex.Message}");
        }
        finally
        {
            oldBindings?.Dispose();
        }
    }

    public LiveMeterSnapshot[] ReadSnapshots(IReadOnlyList<ChannelSlot> slots, double vuGain)
    {
        MeterBindings bindings;
        lock (_lock)
        {
            bindings = _bindings;
        }

        var gain = Math.Clamp(vuGain, 0.50, 2.00);
        var snapshots = new LiveMeterSnapshot[4];
        for (var index = 0; index < snapshots.Length; index++)
        {
            var slot = index < slots.Count ? slots[index] : ChannelSlot.Empty(index);
            if (!slot.IsActive)
            {
                snapshots[index] = LiveMeterSnapshot.Empty(index);
                continue;
            }

            var binding = bindings.ForSlot(index);
            if (binding is null)
            {
                snapshots[index] = LiveMeterSnapshot.Empty(index);
                continue;
            }

            var sourcePeak = binding.ReadPeak();
            var postFaderPeak = slot.IsMuted ? 0 : Math.Clamp(sourcePeak * slot.Volume01 * gain, 0, 1);
            snapshots[index] = new LiveMeterSnapshot(index, sourcePeak, postFaderPeak, true, binding.Name);
        }

        return snapshots;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        MeterBindings oldBindings;
        lock (_lock)
        {
            oldBindings = _bindings;
            _bindings = MeterBindings.Empty;
        }

        oldBindings.Dispose();
        _enumerator.Dispose();
    }

    private MeterBindings BuildBindings(IReadOnlyList<ChannelSlot> slots, AtlasLiveSettings settings)
    {
        var disposables = new List<IDisposable>();
        var candidates = EnumerateSessionCandidates(disposables).ToArray();
        var slotBindings = new MeterBinding?[4];
        var summaries = new string[4];

        for (var index = 0; index < slotBindings.Length; index++)
        {
            var slot = index < slots.Count ? slots[index] : ChannelSlot.Empty(index);
            if (!slot.IsActive)
            {
                summaries[index] = $"{index + 1}:empty";
                continue;
            }

            if (IsOutputSlot(slot) || IsMixSlot(slot))
            {
                var outputBinding = TryCreateEndpointBinding(slot.ChannelId, disposables);
                slotBindings[index] = outputBinding;
                summaries[index] = outputBinding is null
                    ? $"{index + 1}:{slot.ChannelName}=output-missing"
                    : $"{index + 1}:{slot.ChannelName}={outputBinding.Name}";
                continue;
            }

            var manualOverride = settings.MeterOverrideFor(index);
            if (!string.IsNullOrWhiteSpace(manualOverride))
            {
                var manualMatches = candidates
                    .Where(candidate => candidate.HasProcessKey(manualOverride))
                    .ToArray();

                if (manualMatches.Length == 0)
                {
                    LogMissingManualMatch(slot, manualOverride);
                    summaries[index] = $"{index + 1}:{slot.ChannelName}=manual:{manualOverride}:unmatched";
                    continue;
                }

                slotBindings[index] = CreateBinding(manualMatches);
                summaries[index] = $"{index + 1}:{slot.ChannelName}=manual:{manualOverride}:{manualMatches.Length} session(s)";
                continue;
            }

            if (IsSystemSlot(slot))
            {
                var systemBinding = TryCreateDefaultEndpointBinding(disposables);
                slotBindings[index] = systemBinding;
                summaries[index] = systemBinding is null
                    ? $"{index + 1}:{slot.ChannelName}=default-output-missing"
                    : $"{index + 1}:{slot.ChannelName}={systemBinding.Name}";
                continue;
            }

            var matches = candidates
                .Where(candidate => MatchesSlot(slot, candidate))
                .ToArray();

            if (matches.Length == 0)
            {
                LogMissingMatch(slot);
                summaries[index] = $"{index + 1}:{slot.ChannelName}=unmatched";
                continue;
            }

            slotBindings[index] = CreateBinding(matches);
            summaries[index] = $"{index + 1}:{slot.ChannelName}={matches.Length} session(s)";
        }

        return new MeterBindings(slotBindings, disposables, string.Join("; ", summaries));
    }

    private static MeterBinding CreateBinding(IReadOnlyList<SessionCandidate> matches) =>
        new(
            string.Join(" + ", matches.Select(match => match.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(3)),
            () => matches.Max(match => SafePeak(match.Meter)));

    private IEnumerable<SessionCandidate> EnumerateSessionCandidates(List<IDisposable> disposables)
    {
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            disposables.Add(device);
            AudioSessionManager? manager;
            SessionCollection? sessions;
            try
            {
                manager = device.AudioSessionManager;
                disposables.Add(new CleanupAction(manager.Dispose));
                sessions = manager.Sessions;
            }
            catch
            {
                continue;
            }

            for (var index = 0; index < sessions.Count; index++)
            {
                AudioSessionControl session;
                try
                {
                    session = sessions[index];
                    disposables.Add(session);
                }
                catch
                {
                    continue;
                }

                if (session.State != AudioSessionState.AudioSessionStateActive)
                {
                    continue;
                }

                var processNames = CollectProcessNames(session.GetProcessID);
                var names = CollectSessionNames(device, session, processNames).ToArray();
                if (names.Length == 0)
                {
                    continue;
                }

                var meter = session.AudioMeterInformation;
                yield return new SessionCandidate(
                    string.Join(" / ", names.Distinct(StringComparer.OrdinalIgnoreCase).Take(4)),
                    BuildSearchBlob(names),
                    processNames.Select(Compact).Where(IsUsefulNeedle).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    meter);
            }
        }
    }

    private MeterBinding? TryCreateDefaultEndpointBinding(List<IDisposable> disposables)
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                return null;
            }

            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            disposables.Add(device);
            var meter = device.AudioMeterInformation;
            return new MeterBinding(
                $"Default output: {device.FriendlyName}",
                () => SafePeak(meter));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not bind live System meter to default Windows output: {ex.Message}");
            return null;
        }
    }

    private MeterBinding? TryCreateEndpointBinding(string? deviceId, List<IDisposable> disposables)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        try
        {
            var device = _enumerator.GetDevice(deviceId);
            disposables.Add(device);
            var meter = device.AudioMeterInformation;
            return new MeterBinding(
                $"Output: {device.FriendlyName}",
                () => SafePeak(meter));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not bind live output meter to {deviceId}: {ex.Message}");
            return null;
        }
    }

    private void LogMissingMatch(ChannelSlot slot)
    {
        var now = DateTimeOffset.Now;
        if (_lastMissingLog.TryGetValue(slot.Index, out var last) && now - last < MissingMatchLogInterval)
        {
            return;
        }

        _lastMissingLog[slot.Index] = now;
        _logger.Warn($"No live Windows audio session matched slot {slot.Index + 1} ({slot.ChannelName}). Match text: {slot.AppMatchText}");
    }

    private void LogMissingManualMatch(ChannelSlot slot, string manualOverride)
    {
        var now = DateTimeOffset.Now;
        if (_lastMissingLog.TryGetValue(slot.Index, out var last) && now - last < MissingMatchLogInterval)
        {
            return;
        }

        _lastMissingLog[slot.Index] = now;
        _logger.Warn($"Manual live meter override for slot {slot.Index + 1} ({slot.ChannelName}) did not match an active Windows audio session: {manualOverride}");
    }

    private static IEnumerable<string> CollectSessionNames(
        MMDevice device,
        AudioSessionControl session,
        IReadOnlyList<string> processNames)
    {
        yield return device.FriendlyName;
        yield return device.DeviceFriendlyName;
        yield return session.DisplayName;
        yield return session.GetSessionIdentifier;
        yield return session.GetSessionInstanceIdentifier;
        yield return session.IconPath;

        if (session.IsSystemSoundsSession)
        {
            yield return "System Sounds";
        }

        foreach (var name in processNames)
        {
            yield return name;
        }
    }

    private static IReadOnlyList<string> CollectProcessNames(uint pid)
    {
        var names = new List<string>();
        Process? process = null;
        try
        {
            process = Process.GetProcessById(checked((int)pid));
            AddName(names, process.ProcessName);
            AddName(names, process.MainWindowTitle);

            try
            {
                var module = process.MainModule;
                if (module is not null)
                {
                    AddName(names, Path.GetFileNameWithoutExtension(module.FileName));
                    AddName(names, module.FileVersionInfo.FileDescription);
                    AddName(names, module.FileVersionInfo.ProductName);
                    AddName(names, module.FileVersionInfo.OriginalFilename);
                }
            }
            catch
            {
                // Some protected processes hide module metadata; process name is enough when available.
            }
        }
        catch
        {
            // Process can exit between session enumeration and metadata lookup.
        }
        finally
        {
            process?.Dispose();
        }

        return names;
    }

    private static void AddName(List<string> names, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            names.Add(value);
        }
    }

    private static void AddProcessCandidate(Dictionary<string, string> candidates, int pid)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            AddProcessCandidate(candidates, process);
        }
        catch
        {
            // Process can exit between session enumeration and lookup.
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void AddProcessCandidate(Dictionary<string, string> candidates, Process process)
    {
        var matchText = AtlasLiveSettings.NormalizeOverride(process.ProcessName);
        if (string.IsNullOrWhiteSpace(matchText) || candidates.ContainsKey(matchText))
        {
            return;
        }

        var label = BuildProcessLabel(process);
        candidates[matchText] = label;
    }

    private static string BuildProcessLabel(Process process)
    {
        var processName = process.ProcessName;
        var names = new List<string>();
        AddName(names, null);

        try
        {
            var module = process.MainModule;
            if (module is not null)
            {
                AddName(names, module.FileVersionInfo.FileDescription);
                AddName(names, module.FileVersionInfo.ProductName);
            }
        }
        catch
        {
            // Protected processes may hide module metadata.
        }

        AddName(names, process.MainWindowTitle);
        AddName(names, processName);

        var display = names
            .Select(name => name.Trim())
            .FirstOrDefault(name => !string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
            ?? processName;

        return string.Equals(display, processName, StringComparison.OrdinalIgnoreCase)
            ? processName
            : $"{display} ({processName})";
    }

    private static bool IsSystemSlot(ChannelSlot slot) =>
        slot.ChannelName.Equals("System", StringComparison.OrdinalIgnoreCase);

    private static bool IsOutputSlot(ChannelSlot slot) =>
        slot.ChannelType.Equals("Output", StringComparison.OrdinalIgnoreCase);

    private static bool IsMixSlot(ChannelSlot slot) =>
        slot.ChannelType.Equals("Mix", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesSlot(ChannelSlot slot, SessionCandidate candidate)
    {
        foreach (var needle in BuildNeedles(slot))
        {
            if (candidate.Contains(needle))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> BuildNeedles(ChannelSlot slot)
    {
        foreach (var part in SplitMatchText(slot.AppMatchText))
        {
            foreach (var needle in BuildNeedleVariants(part))
            {
                yield return needle;
            }
        }

        foreach (var needle in BuildNeedleVariants(slot.ChannelName))
        {
            yield return needle;
        }
    }

    private static IEnumerable<string> SplitMatchText(string value) =>
        value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> BuildNeedleVariants(string value)
    {
        var normalized = NormalizeWords(value);
        if (IsUsefulNeedle(normalized))
        {
            yield return normalized;
        }

        var compact = Compact(value);
        if (IsUsefulNeedle(compact))
        {
            yield return compact;
        }

        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsUsefulNeedle(word))
            {
                yield return word;
            }
        }
    }

    private static bool IsUsefulNeedle(string value)
    {
        if (value.Length < 3)
        {
            return false;
        }

        if (value.StartsWith("pcm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !GenericNeedles.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private static SearchBlob BuildSearchBlob(IEnumerable<string> values)
    {
        var builder = new StringBuilder();
        var compactBuilder = new StringBuilder();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            builder.Append(' ');
            builder.Append(NormalizeWords(value));
            compactBuilder.Append(Compact(value));
        }

        return new SearchBlob(builder.ToString(), compactBuilder.ToString());
    }

    private static string NormalizeWords(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static string Compact(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static double SafePeak(AudioMeterInformation meter)
    {
        try
        {
            return Math.Clamp(meter.MasterPeakValue, 0, 1);
        }
        catch
        {
            return 0;
        }
    }

    private sealed record SessionCandidate(
        string Name,
        SearchBlob Search,
        IReadOnlySet<string> ProcessKeys,
        AudioMeterInformation Meter)
    {
        public bool HasProcessKey(string processName) =>
            ProcessKeys.Contains(Compact(processName));

        public bool Contains(string needle) =>
            Search.Words.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            Search.Compact.Contains(Compact(needle), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SearchBlob(string Words, string Compact);

    private sealed record MeterBinding(string Name, Func<double> ReadPeak);

    private sealed class MeterBindings : IDisposable
    {
        public static MeterBindings Empty { get; } = new(new MeterBinding?[4], [], "empty");

        private readonly MeterBinding?[] _slots;
        private readonly IReadOnlyList<IDisposable> _disposables;

        public MeterBindings(MeterBinding?[] slots, IReadOnlyList<IDisposable> disposables, string summary)
        {
            _slots = slots;
            _disposables = disposables;
            Summary = summary;
        }

        public string Summary { get; }

        public MeterBinding? ForSlot(int index) =>
            index >= 0 && index < _slots.Length ? _slots[index] : null;

        public void Dispose()
        {
            foreach (var disposable in _disposables.Reverse())
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // COM wrappers are best-effort cleanup.
                }
            }
        }
    }

    private sealed class CleanupAction : IDisposable
    {
        private Action? _cleanup;

        public CleanupAction(Action cleanup)
        {
            _cleanup = cleanup;
        }

        public void Dispose()
        {
            var cleanup = Interlocked.Exchange(ref _cleanup, null);
            cleanup?.Invoke();
        }
    }
}
