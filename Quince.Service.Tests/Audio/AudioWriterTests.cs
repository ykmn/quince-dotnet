using Quince.Service.Audio;
using Quince.Service.Configuration;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class AudioWriterTests
{
    [Fact]
    public void BuildEncodeArgs_Mp3_UsesLibmp3lameAndBitrate()
    {
        var fmt = new OutputFormatConfig { FileFormat = "mp3", BitrateKbps = 96, Mode = "original" };
        var args = AudioWriter.BuildEncodeArgs(fmt, inputSampleRate: 44100, inputChannels: 2, outPath: "out.mp3");

        Assert.Contains("libmp3lame", args);
        Assert.Contains("96k", args);
        Assert.Equal("out.mp3", args[^1]);
    }

    [Fact]
    public void BuildEncodeArgs_Wav24Bit_UsesPcmS24le()
    {
        var fmt = new OutputFormatConfig { FileFormat = "wav", BitDepth = 24, Mode = "original" };
        var args = AudioWriter.BuildEncodeArgs(fmt, 44100, 2, "out.wav");

        Assert.Contains("pcm_s24le", args);
    }

    [Fact]
    public void BuildEncodeArgs_Wav16Bit_UsesPcmS16le()
    {
        var fmt = new OutputFormatConfig { FileFormat = "wav", BitDepth = 16, Mode = "original" };
        var args = AudioWriter.BuildEncodeArgs(fmt, 44100, 2, "out.wav");

        Assert.Contains("pcm_s16le", args);
    }

    [Fact]
    public void BuildEncodeArgs_CustomMode_AddsResampleFlags()
    {
        var fmt = new OutputFormatConfig { FileFormat = "aac", BitrateKbps = 128, Mode = "custom", SampleRate = 22050, Channels = 1 };
        var args = AudioWriter.BuildEncodeArgs(fmt, 44100, 2, "out.aac");

        Assert.Contains("22050", args);
        Assert.Contains("1", args);
    }

    [Fact]
    public void BuildEncodeArgs_AlwaysPassesOverwriteFlag()
    {
        // docs/HISTORY.md #136: without -y, ffmpeg can't prompt for overwrite on a non-interactive
        // stdin (piped PCM here) and just exits immediately if the output path already exists —
        // dropping several seconds of a 24/7 recording every time a same-second filename collides
        // with a leftover file (e.g. an orphaned ffmpeg from an unclean restart).
        var fmt = new OutputFormatConfig { FileFormat = "mp3", BitrateKbps = 96, Mode = "original" };
        var args = AudioWriter.BuildEncodeArgs(fmt, 44100, 2, "out.mp3");

        Assert.Contains("-y", args);
    }

    [Fact]
    public void BuildEncodeArgs_UnsupportedFormat_Throws()
    {
        var fmt = new OutputFormatConfig { FileFormat = "flac" };
        Assert.Throws<ArgumentException>(() => AudioWriter.BuildEncodeArgs(fmt, 44100, 2, "out.flac"));
    }

    [Fact]
    public void ResolveEffectiveFormat_OriginalMode_SoundcardUsesWav()
    {
        var config = new ChannelConfig { Source = { Type = "soundcard" }, OutputFormat = { Mode = "original", FileFormat = "mp3" } };
        Assert.Equal("wav", AudioWriter.ResolveEffectiveFormat(config).FileFormat);
    }

    [Fact]
    public void ResolveEffectiveFormat_OriginalMode_HlsUsesAac()
    {
        var config = new ChannelConfig { Source = { Type = "stream", StreamType = "hls" }, OutputFormat = { Mode = "original", FileFormat = "mp3" } };
        Assert.Equal("aac", AudioWriter.ResolveEffectiveFormat(config).FileFormat);
    }

    [Theory]
    [InlineData("icecast")]
    [InlineData("icecast_mp3")]
    public void ResolveEffectiveFormat_OriginalMode_IcecastUsesMp3(string streamType)
    {
        var config = new ChannelConfig { Source = { Type = "stream", StreamType = streamType }, OutputFormat = { Mode = "original", FileFormat = "aac" } };
        Assert.Equal("mp3", AudioWriter.ResolveEffectiveFormat(config).FileFormat);
    }

    [Fact]
    public void ResolveEffectiveFormat_CustomMode_KeepsConfiguredFileFormat()
    {
        var config = new ChannelConfig { Source = { Type = "stream", StreamType = "hls" }, OutputFormat = { Mode = "custom", FileFormat = "wav" } };
        Assert.Equal("wav", AudioWriter.ResolveEffectiveFormat(config).FileFormat);
    }
}
