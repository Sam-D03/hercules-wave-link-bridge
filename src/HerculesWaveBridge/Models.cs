namespace HerculesWaveBridge;

internal sealed record ChannelSlot(
    int Index,
    string? ChannelId,
    string ChannelName,
    string MixId,
    double Volume01,
    bool IsMuted,
    bool IsActive,
    string? IconData = null,
    bool IsAppIcon = false,
    string AppMatchText = "",
    string ChannelType = "")
{
    public static ChannelSlot Empty(int index) => new(index, null, "Empty", string.Empty, 0, false, false);
}

internal sealed record WaveOutputTarget(
    string OutputDeviceId,
    string OutputId,
    string OutputName,
    string MixId,
    string MixName,
    double Volume01,
    bool IsMuted,
    bool IsActive)
{
    public static WaveOutputTarget Empty { get; } = new(
        string.Empty,
        string.Empty,
        "Audio Output 1 unavailable",
        string.Empty,
        "Personal Mix",
        0,
        false,
        false);
}

internal sealed record WaveMixTarget(
    string MixId,
    string MixName,
    double Volume01,
    bool IsMuted,
    bool IsActive)
{
    public static WaveMixTarget Empty { get; } = new(
        string.Empty,
        "Personal Mix unavailable",
        0,
        false,
        false);
}

internal sealed record LiveMeterSnapshot(
    int SlotIndex,
    double SourcePeak01,
    double PostFaderPeak01,
    bool IsMatched,
    string MatchedSourceName)
{
    public static LiveMeterSnapshot Empty(int slotIndex) => new(slotIndex, 0, 0, false, string.Empty);
}

internal sealed record MeterAppCandidate(
    string Label,
    string MatchText);

internal sealed record BridgeStatus(
    bool DeviceConnected,
    bool WaveLinkConnected,
    string TransportMode,
    string LastError);

internal abstract record DeviceInputEvent;

internal sealed record KnobTurn(int Index, int Delta) : DeviceInputEvent;

internal sealed record KnobPress(int Index, bool IsPressed) : DeviceInputEvent;

internal sealed record ActionButton(int Index, bool IsPressed) : DeviceInputEvent;

internal sealed record TransportStatus(bool Connected, string Mode, string Message);
