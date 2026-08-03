using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HerculesWaveBridge;

internal sealed class PacketMapDecoder
{
    private const int EncoderDetentThreshold = 48;
    private static readonly int[] EncoderOffsets = [3, 5, 7, 9];
    private readonly BridgeLogger _logger;
    private readonly List<PacketRule> _rules;
    private readonly short[] _lastEncoderValues = new short[4];
    private readonly int[] _encoderRemainders = new int[4];
    private bool _hasStream100State;
    private byte _lastButtonMask;
    private int _unmatchedPacketLogs;

    private PacketMapDecoder(BridgeLogger logger, List<PacketRule> rules)
    {
        _logger = logger;
        _rules = rules;
    }

    public static PacketMapDecoder Load(BridgeLogger logger)
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerculesWaveBridge");
        Directory.CreateDirectory(configDir);

        var file = Path.Combine(configDir, "packet-map.json");
        if (!File.Exists(file))
        {
            File.WriteAllText(file, """
            {
              "notes": [
                "Map captured Stream 100 packets here after USBPcap/Wireshark discovery.",
                "Patterns are hex bytes separated by spaces. Use ?? as a wildcard.",
                "eventType: knobTurn, knobPress, actionButton."
              ],
              "rules": []
            }
            """);
            logger.Warn($"Created empty packet map at {file}. Raw packets will be logged until this is populated.");
        }

        try
        {
            var model = JsonSerializer.Deserialize<PacketMapFile>(File.ReadAllText(file), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            var rules = model?.Rules?
                .Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern))
                .ToList() ?? [];
            logger.Info($"Loaded {rules.Count} packet map rule(s) from {file}.");
            return new PacketMapDecoder(logger, rules);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Could not load packet map {file}");
            return new PacketMapDecoder(logger, []);
        }
    }

    public IReadOnlyList<DeviceInputEvent> Decode(byte[] packet)
    {
        var events = new List<DeviceInputEvent>();
        var recognizedStream100 = TryGetStream100Frame(packet, out var stream100Frame);
        if (recognizedStream100)
        {
            DecodeBuiltInStream100Packet(stream100Frame, events);
        }

        foreach (var rule in _rules)
        {
            if (!Matches(rule.Pattern, packet))
            {
                continue;
            }

            var index = Math.Clamp(rule.Index ?? 0, 0, 3);
            switch (rule.EventType?.Trim().ToLowerInvariant())
            {
                case "knobturn":
                    events.Add(new KnobTurn(index, rule.Delta ?? 0));
                    break;
                case "knobpress":
                    events.Add(new KnobPress(index, rule.IsPressed ?? true));
                    break;
                case "actionbutton":
                    events.Add(new ActionButton(index, rule.IsPressed ?? true));
                    break;
                default:
                    _logger.Warn($"Unknown packet rule eventType '{rule.EventType}'.");
                    break;
            }
        }

        if (events.Count == 0 && !recognizedStream100)
        {
            LogUnmatchedPacket(packet);
        }

        return events;
    }

    private void DecodeBuiltInStream100Packet(ReadOnlySpan<byte> packet, List<DeviceInputEvent> events)
    {
        var buttonMask = packet[1];
        if (_hasStream100State)
        {
            var changedButtons = (byte)(buttonMask ^ _lastButtonMask);
            for (var bit = 0; bit < 8; bit++)
            {
                var bitMask = (byte)(1 << bit);
                if ((changedButtons & bitMask) == 0)
                {
                    continue;
                }

                var isPressed = (buttonMask & bitMask) != 0;
                if (bit < 4)
                {
                    events.Add(new KnobPress(bit, isPressed));
                }
                else
                {
                    events.Add(new ActionButton(bit - 4, isPressed));
                }
            }
        }

        for (var index = 0; index < EncoderOffsets.Length; index++)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(EncoderOffsets[index], sizeof(short)));
            if (_hasStream100State)
            {
                var delta = unchecked((short)(value - _lastEncoderValues[index]));
                if (delta != 0)
                {
                    _encoderRemainders[index] += delta;
                    while (Math.Abs(_encoderRemainders[index]) >= EncoderDetentThreshold)
                    {
                        var direction = Math.Sign(_encoderRemainders[index]);
                        events.Add(new KnobTurn(index, direction));
                        _encoderRemainders[index] -= direction * EncoderDetentThreshold;
                    }
                }
            }

            _lastEncoderValues[index] = value;
        }

        _lastButtonMask = buttonMask;
            _hasStream100State = true;
    }

    private void LogUnmatchedPacket(byte[] packet)
    {
        _unmatchedPacketLogs++;
        if (_unmatchedPacketLogs > 20 && _unmatchedPacketLogs % 100 != 0)
        {
            return;
        }

        var sampleLength = Math.Min(packet.Length, 96);
        _logger.Info($"No packet-map rule matched packet len={packet.Length} sample={Convert.ToHexString(packet.AsSpan(0, sampleLength))}");
    }

    private static bool TryGetStream100Frame(byte[] packet, out ReadOnlySpan<byte> frame)
    {
        for (var offset = 0; offset <= packet.Length - 64; offset++)
        {
            if (packet[offset] == 0x0C &&
                packet[offset + 11] == 0xBC &&
                packet[offset + 12] == 0x73 &&
                packet[offset + 13] == 0x6D &&
                (packet[offset + 14] == 0x21 || packet[offset + 14] == 0x32))
            {
                frame = packet.AsSpan(offset, 64);
                return true;
            }
        }

        frame = default;
        return false;
    }

    private static bool Matches(string pattern, byte[] packet)
    {
        var tokens = pattern.Split([' ', '-', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length > packet.Length)
        {
            return false;
        }

        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "??")
            {
                continue;
            }

            if (!byte.TryParse(tokens[i], System.Globalization.NumberStyles.HexNumber, null, out var expected) ||
                packet[i] != expected)
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record PacketMapFile(List<string>? Notes, List<PacketRule>? Rules);

internal sealed record PacketRule(
    string Pattern,
    string EventType,
    int? Index,
    int? Delta,
    bool? IsPressed);
