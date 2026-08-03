using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HerculesWaveBridge;

internal sealed class WaveLinkClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly BridgeLogger _logger;
    private readonly SemaphoreSlim _socketLock = new(1, 1);
    private ClientWebSocket? _socket;
    private int _socketPort;
    private int _nextRequestId;

    public WaveLinkClient(BridgeLogger logger)
    {
        _logger = logger;
    }

    public async Task<WaveApplicationInfo> GetApplicationInfoAsync(CancellationToken cancellationToken)
    {
        return await InvokeAsync<WaveApplicationInfo>("getApplicationInfo", null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChannelSlot>> GetChannelSlotsAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync<WaveChannelsResult>("getChannels", null, cancellationToken)
            .ConfigureAwait(false);

        var slots = result.Channels
            .Where(channel => channel.Mixes is { Count: > 0 })
            .Take(4)
            .Select((channel, index) =>
            {
                var mix = channel.Mixes![0];
                var name = string.IsNullOrWhiteSpace(channel.Name) ? channel.Id : channel.Name;
                return new ChannelSlot(
                    index,
                    channel.Id,
                    name,
                    mix.MixId ?? string.Empty,
                    Clamp01(mix.Level),
                    mix.IsMuted ?? channel.IsMuted ?? false,
                    true,
                    channel.Image?.ImgData,
                    channel.Image?.IsAppIcon ?? false,
                    BuildAppMatchText(channel, name),
                    channel.Type ?? string.Empty);
            })
            .ToList();

        while (slots.Count < 4)
        {
            slots.Add(ChannelSlot.Empty(slots.Count));
        }

        return slots;
    }

    public async Task<WaveOutputTarget> GetPersonalMixOutput1Async(CancellationToken cancellationToken)
    {
        var mixes = await InvokeAsync<WaveMixesResult>("getMixes", null, cancellationToken)
            .ConfigureAwait(false);
        var outputs = await InvokeAsync<WaveOutputDevicesResult>("getOutputDevices", null, cancellationToken)
            .ConfigureAwait(false);

        return SelectPersonalMixOutput1(mixes, outputs);
    }

    public async Task SetChannelMixVolumeAsync(ChannelSlot slot, double volume01, CancellationToken cancellationToken)
    {
        if (!slot.IsActive || string.IsNullOrWhiteSpace(slot.ChannelId))
        {
            return;
        }

        var payload = new
        {
            id = slot.ChannelId,
            mixes = new[]
            {
                new
                {
                    id = slot.MixId,
                    level = Clamp01(volume01)
                }
            }
        };

        await InvokeAsync<JsonElement>("setChannel", payload, cancellationToken).ConfigureAwait(false);
        _logger.Info($"Wave Link set volume: slot={slot.Index + 1}; channel={slot.ChannelName}; id={slot.ChannelId}; mix={slot.MixId}; level={Clamp01(volume01):0.00}");
    }

    public async Task SetChannelMixMuteAsync(ChannelSlot slot, bool isMuted, CancellationToken cancellationToken)
    {
        if (!slot.IsActive || string.IsNullOrWhiteSpace(slot.ChannelId))
        {
            return;
        }

        var payload = new
        {
            id = slot.ChannelId,
            mixes = new[]
            {
                new
                {
                    id = slot.MixId,
                    isMuted
                }
            }
        };

        await InvokeAsync<JsonElement>("setChannel", payload, cancellationToken).ConfigureAwait(false);
        _logger.Info($"Wave Link set mute: slot={slot.Index + 1}; channel={slot.ChannelName}; id={slot.ChannelId}; mix={slot.MixId}; muted={isMuted}");
    }

    public async Task SetOutputVolumeAsync(WaveOutputTarget target, double volume01, CancellationToken cancellationToken)
    {
        if (!target.IsActive)
        {
            return;
        }

        var payload = new
        {
            outputDevice = new
            {
                id = target.OutputDeviceId,
                outputs = new[]
                {
                    new
                    {
                        id = target.OutputId,
                        level = Clamp01(volume01)
                    }
                }
            }
        };

        await InvokeAsync<JsonElement>("setOutputDevice", payload, cancellationToken).ConfigureAwait(false);
        _logger.Info($"Wave Link set Personal Mix Audio Output 1 volume: output={target.OutputName}; device={target.OutputDeviceId}; id={target.OutputId}; level={Clamp01(volume01):0.00}");
    }

    public async Task SetOutputMuteAsync(WaveOutputTarget target, bool isMuted, CancellationToken cancellationToken)
    {
        if (!target.IsActive)
        {
            return;
        }

        var payload = new
        {
            outputDevice = new
            {
                id = target.OutputDeviceId,
                outputs = new[]
                {
                    new
                    {
                        id = target.OutputId,
                        isMuted
                    }
                }
            }
        };

        await InvokeAsync<JsonElement>("setOutputDevice", payload, cancellationToken).ConfigureAwait(false);
        _logger.Info($"Wave Link set Personal Mix Audio Output 1 mute: output={target.OutputName}; device={target.OutputDeviceId}; id={target.OutputId}; muted={isMuted}");
    }

    internal static WaveOutputTarget SelectPersonalMixOutput1(
        WaveMixesResult mixesResult,
        WaveOutputDevicesResult outputsResult)
    {
        var mix = mixesResult.Mixes?.FirstOrDefault();
        if (mix is null)
        {
            return WaveOutputTarget.Empty;
        }

        var match = outputsResult.OutputDevices?
            .SelectMany(device => (device.Outputs ?? [])
                .Select(output => new { Device = device, Output = output }))
            .FirstOrDefault(item => string.Equals(item.Output.MixId, mix.Id, StringComparison.Ordinal));

        if (match is null)
        {
            return WaveOutputTarget.Empty with
            {
                MixId = mix.Id,
                MixName = string.IsNullOrWhiteSpace(mix.Name) ? "Personal Mix" : mix.Name
            };
        }

        return new WaveOutputTarget(
            match.Device.Id,
            match.Output.Id,
            string.IsNullOrWhiteSpace(match.Output.Name) ? match.Device.Name : match.Output.Name,
            mix.Id,
            string.IsNullOrWhiteSpace(mix.Name) ? "Personal Mix" : mix.Name,
            Clamp01(match.Output.Level),
            match.Output.IsMuted,
            true);
    }

    public void Dispose()
    {
        ResetSocket();
        _socketLock.Dispose();
    }

    private async Task<T> InvokeAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        await _socketLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var socket = await GetConnectedSocketAsync(timeout.Token).ConfigureAwait(false);
            return await InvokeOnConnectedSocketAsync<T>(socket, method, parameters, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            ResetSocket();
            throw;
        }
        finally
        {
            _socketLock.Release();
        }
    }

    private async Task<ClientWebSocket> GetConnectedSocketAsync(CancellationToken cancellationToken)
    {
        var port = FindWaveLinkPort();
        if (_socket is { State: WebSocketState.Open } && _socketPort == port)
        {
            return _socket;
        }

        ResetSocket();
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "streamdeck://");
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), cancellationToken).ConfigureAwait(false);
        _socket = socket;
        _socketPort = port;
        return socket;
    }

    private async Task<T> InvokeOnConnectedSocketAsync<T>(
        ClientWebSocket socket,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var request = JsonSerializer.Serialize(new
        {
            id,
            jsonrpc = "2.0",
            method,
            @params = parameters
        }, JsonOptions);

        var requestBytes = Encoding.UTF8.GetBytes(request);
        await socket.SendAsync(
            new ArraySegment<byte>(requestBytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var json = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<JsonRpcEnvelope<T>>(json, JsonOptions)
                ?? throw new InvalidOperationException("Wave Link returned an empty JSON-RPC response.");

            if (envelope.Id != id)
            {
                continue;
            }

            if (envelope.Error is not null)
            {
                throw new InvalidOperationException($"Wave Link RPC {method} failed: {envelope.Error.Code} {envelope.Error.Message}");
            }

            if (envelope.Result is null)
            {
                if (typeof(T) == typeof(JsonElement))
                {
                    return (T)(object)default(JsonElement);
                }

                throw new InvalidOperationException($"Wave Link RPC {method} returned no result.");
            }

            return envelope.Result;
        }
    }

    private void ResetSocket()
    {
        try
        {
            _socket?.Dispose();
        }
        catch
        {
            // Best-effort cleanup; the next request will create a fresh socket.
        }

        _socket = null;
        _socketPort = 0;
    }

    private static async Task<string> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("Wave Link closed the WebSocket connection.");
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return builder.ToString();
            }
        }
    }

    private int FindWaveLinkPort()
    {
        var packagesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        var candidates = new List<string>
        {
            Path.Combine(packagesDir, "Elgato.WaveLink_g54w8ztgkx496", "LocalState", "ws-info.json")
        };

        if (Directory.Exists(packagesDir))
        {
            candidates.AddRange(Directory.EnumerateDirectories(packagesDir, "Elgato.WaveLink*")
                .Select(path => Path.Combine(path, "LocalState", "ws-info.json")));
        }

        foreach (var file in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("port", out var port) && port.TryGetInt32(out var value))
                {
                    return value;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Could not read Wave Link ws-info file {file}: {ex.Message}");
            }
        }

        throw new FileNotFoundException("Could not find Wave Link ws-info.json. Start Wave Link and try again.");
    }

    private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));

    private static string BuildAppMatchText(WaveChannel channel, string fallbackName)
    {
        var names = new List<string>
        {
            fallbackName
        };

        if (!string.IsNullOrWhiteSpace(channel.Id))
        {
            names.Add(channel.Id);
        }

        if (!string.IsNullOrWhiteSpace(channel.Type))
        {
            names.Add(channel.Type);
        }

        if (channel.Apps is not null)
        {
            foreach (var app in channel.Apps)
            {
                if (!string.IsNullOrWhiteSpace(app.Name))
                {
                    names.Add(app.Name);
                }

                if (!string.IsNullOrWhiteSpace(app.Id))
                {
                    names.Add(app.Id);
                }
            }
        }

        return string.Join("|", names
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }
}

internal sealed record WaveApplicationInfo(
    string AppID,
    string Name,
    string Version,
    int Build,
    int InterfaceRevision);

internal sealed record WaveChannelsResult(List<WaveChannel> Channels);

internal sealed record WaveChannel(
    string Id,
    string Name,
    string? Type,
    bool? IsMuted,
    double Level,
    List<WaveChannelMix>? Mixes,
    WaveChannelImage? Image,
    List<WaveChannelApp>? Apps);

internal sealed record WaveChannelImage(
    string? ImgData,
    bool IsAppIcon);

internal sealed record WaveChannelApp(
    string? Id,
    string? Name);

internal sealed record WaveChannelMix(
    [property: JsonPropertyName("id")]
    string? MixId,
    bool? IsMuted,
    double Level);

internal sealed record WaveMixesResult(List<WaveMix>? Mixes);

internal sealed record WaveMix(
    string Id,
    string Name,
    double Level,
    bool IsMuted);

internal sealed record WaveOutputDevicesResult(
    WaveMainOutput? MainOutput,
    List<WaveOutputDevice>? OutputDevices);

internal sealed record WaveMainOutput(
    string OutputDeviceId,
    string OutputId);

internal sealed record WaveOutputDevice(
    string Id,
    string Name,
    string? DeviceType,
    List<WaveOutput>? Outputs);

internal sealed record WaveOutput(
    string Id,
    string Name,
    bool IsMuted,
    double Level,
    string? MixId);

internal sealed record JsonRpcEnvelope<T>(
    string Jsonrpc,
    int Id,
    T? Result,
    JsonRpcError? Error);

internal sealed record JsonRpcError(int Code, string Message);
