using Xunit;
using System.Text.Json;

namespace HerculesWaveBridge.Tests;

public sealed class EncoderMappingTests
{
    [Fact]
    public void EncoderTargetsDefaultToMatchingChannelsAndPersistOverrides()
    {
        var settings = AtlasLiveSettings.Default;

        Assert.All(Enumerable.Range(0, 4), index =>
            Assert.Equal(AtlasLiveSettings.ChannelEncoderTarget, settings.EncoderTargetFor(index)));

        settings = settings.WithEncoderTarget(2, AtlasLiveSettings.PersonalMixOutput1EncoderTarget);
        settings = settings.WithEncoderTarget(0, AtlasLiveSettings.PersonalMixEncoderTarget);

        Assert.Equal(AtlasLiveSettings.PersonalMixOutput1EncoderTarget, settings.EncoderTargetFor(2));
        Assert.Equal(AtlasLiveSettings.PersonalMixEncoderTarget, settings.EncoderTargetFor(0));
        Assert.Equal(AtlasLiveSettings.ChannelEncoderTarget, settings.EncoderTargetFor(1));
        Assert.Equal(AtlasLiveSettings.ChannelEncoderTarget, AtlasLiveSettings.NormalizeEncoderTarget("unknown"));
    }

    [Fact]
    public void PersonalMixUsesFirstWaveLinkMixLevelAndMuteState()
    {
        var mixes = new WaveMixesResult(
        [
            new WaveMix("personal-mix", "Personal Mix", 0.64, true),
            new WaveMix("stream-mix", "Stream Mix", 0.82, false)
        ]);

        var target = WaveLinkClient.SelectPersonalMix(mixes);

        Assert.True(target.IsActive);
        Assert.Equal("personal-mix", target.MixId);
        Assert.Equal("Personal Mix", target.MixName);
        Assert.Equal(0.64, target.Volume01, 3);
        Assert.True(target.IsMuted);
    }

    [Fact]
    public void PersonalMixIsInactiveWhenWaveLinkReturnsNoMixes()
    {
        var target = WaveLinkClient.SelectPersonalMix(new WaveMixesResult([]));

        Assert.False(target.IsActive);
    }

    [Fact]
    public void ExistingSettingsWithoutEncoderMappingsKeepChannelDefaults()
    {
        const string oldSettings = """
            {
              "MeterMode": "mono",
              "Shape": "bars",
              "ColorMode": "gradient",
              "SolidColor": "#80ff00",
              "GradientStart": "#a335ff",
              "GradientEnd": "#ff5ccc",
              "VuGain": 1.16,
              "VuRefresh": 0.04,
              "KnobSensitivityLevel": 4,
              "MeterOverride0": "chrome",
              "MeterOverride1": "spotify",
              "MeterOverride2": "elitedangerous64",
              "MeterOverride3": ""
            }
            """;

        var settings = JsonSerializer.Deserialize<AtlasLiveSettings>(oldSettings);

        Assert.NotNull(settings);
        Assert.All(Enumerable.Range(0, 4), index =>
            Assert.Equal(AtlasLiveSettings.ChannelEncoderTarget, settings.EncoderTargetFor(index)));
    }

    [Fact]
    public void PersonalMixOutputOneUsesFirstOutputRoutedToFirstMix()
    {
        var mixes = new WaveMixesResult(
        [
            new WaveMix("personal-mix", "Personal Mix", 1, false),
            new WaveMix("stream-mix", "Stream Mix", 1, false)
        ]);
        var outputs = new WaveOutputDevicesResult(
            null,
            [
                new WaveOutputDevice(
                    "device-a",
                    "Speakers",
                    "thirdParty",
                    [new WaveOutput("output-a", "Speakers", false, 0.42, "")]),
                new WaveOutputDevice(
                    "device-b",
                    "Headphones",
                    "thirdParty",
                    [new WaveOutput("output-b", "Headphones", true, 0.73, "personal-mix")])
            ]);

        var target = WaveLinkClient.SelectPersonalMixOutput1(mixes, outputs);

        Assert.True(target.IsActive);
        Assert.Equal("device-b", target.OutputDeviceId);
        Assert.Equal("output-b", target.OutputId);
        Assert.Equal("Headphones", target.OutputName);
        Assert.Equal("personal-mix", target.MixId);
        Assert.Equal(0.73, target.Volume01, 3);
        Assert.True(target.IsMuted);
    }

    [Fact]
    public void PersonalMixOutputOneIsInactiveWhenFirstMixIsNotRouted()
    {
        var mixes = new WaveMixesResult([new WaveMix("personal-mix", "Personal Mix", 1, false)]);
        var outputs = new WaveOutputDevicesResult(
            null,
            [new WaveOutputDevice(
                "device-a",
                "Speakers",
                "thirdParty",
                [new WaveOutput("output-a", "Speakers", false, 0.5, "")])]);

        var target = WaveLinkClient.SelectPersonalMixOutput1(mixes, outputs);

        Assert.False(target.IsActive);
        Assert.Equal("personal-mix", target.MixId);
        Assert.Equal("Personal Mix", target.MixName);
    }
}
