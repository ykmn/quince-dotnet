using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Audio;
using Quince.Service.Configuration;
using System.Threading.Channels;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class SilenceDetectorTests
{
    private static SilenceDetector CreateDetector(SilenceDetectorConfig config, out List<string> events)
    {
        var capturedEvents = new List<string>();
        var channel = Channel.CreateUnbounded<AudioChunk>();
        var detector = new SilenceDetector(config, channel.Reader,
            onSilence: () => capturedEvents.Add("silence"),
            onSound: () => capturedEvents.Add("sound"),
            log: NullLogger.Instance);
        events = capturedEvents;
        return detector;
    }

    [Fact]
    public void ProcessLevel_BelowThresholdPastTrigger_FiresOnSilence()
    {
        var config = new SilenceDetectorConfig { Enabled = true, ThresholdDbfs = -60, TriggerSeconds = 0.3, ResumeSeconds = 0.1 };
        var detector = CreateDetector(config, out var events);

        detector.ProcessLevel(-90);
        detector.ProcessLevel(-90);
        Assert.Empty(events);
        detector.ProcessLevel(-90);

        Assert.Equal(new[] { "silence" }, events);
        Assert.True(detector.IsSilent);
    }

    [Fact]
    public void ProcessLevel_AboveThreshold_NeverTriggersSilence()
    {
        var config = new SilenceDetectorConfig { Enabled = true, ThresholdDbfs = -60, TriggerSeconds = 0.2, ResumeSeconds = 0.1 };
        var detector = CreateDetector(config, out var events);

        for (var i = 0; i < 10; i++) detector.ProcessLevel(-10);

        Assert.Empty(events);
        Assert.False(detector.IsSilent);
    }

    [Fact]
    public void ProcessLevel_ResumesAfterSilence()
    {
        var config = new SilenceDetectorConfig { Enabled = true, ThresholdDbfs = -60, TriggerSeconds = 0.2, ResumeSeconds = 0.2 };
        var detector = CreateDetector(config, out var events);

        detector.ProcessLevel(-90);
        detector.ProcessLevel(-90);
        Assert.True(detector.IsSilent);

        detector.ProcessLevel(-10);
        Assert.True(detector.IsSilent);
        detector.ProcessLevel(-10);

        Assert.Equal(new[] { "silence", "sound" }, events);
        Assert.False(detector.IsSilent);
    }

    [Fact]
    public void ProcessLevel_Disabled_NeverFires()
    {
        var config = new SilenceDetectorConfig { Enabled = false, ThresholdDbfs = -60, TriggerSeconds = 0.1, ResumeSeconds = 0.1 };
        var detector = CreateDetector(config, out var events);

        for (var i = 0; i < 5; i++) detector.ProcessLevel(-90);

        Assert.Empty(events);
    }
}
