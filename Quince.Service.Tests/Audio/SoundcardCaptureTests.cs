using System.Collections.Generic;
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class SoundcardCaptureTests
{
    private static readonly IReadOnlyList<SoundcardCapture.RecordDeviceInfo> Devices = new[]
    {
        new SoundcardCapture.RecordDeviceInfo(0, "Microphone (Realtek Audio)", "{driver-0-guid}", IsEnabled: true, IsDefault: true),
        new SoundcardCapture.RecordDeviceInfo(1, "Line In (Realtek Audio)", "{driver-1-guid}", IsEnabled: true, IsDefault: false),
        new SoundcardCapture.RecordDeviceInfo(2, "Disabled Device", "{driver-2-guid}", IsEnabled: false, IsDefault: false),
    };

    [Fact]
    public void ResolveDeviceIndex_ExactUidMatch_WinsOverNameAndIndex()
    {
        var result = SoundcardCapture.ResolveDeviceIndex(Devices, "{DRIVER-1-GUID}", "Microphone (Realtek Audio)", 0);
        Assert.Equal(1, result);
    }

    [Fact]
    public void ResolveDeviceIndex_UidNotFound_ReturnsNull()
    {
        var result = SoundcardCapture.ResolveDeviceIndex(Devices, "{does-not-exist}", "Microphone (Realtek Audio)", 0);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveDeviceIndex_ExactNameMatch_WinsOverSubstring()
    {
        // "Line In (Realtek Audio)" would also substring-match "Line In", but the exact match
        // (case-insensitive) should be picked first.
        var result = SoundcardCapture.ResolveDeviceIndex(Devices, "", "line in (realtek audio)", -1);
        Assert.Equal(1, result);
    }

    [Fact]
    public void ResolveDeviceIndex_NameSubstring_FallsBackWhenNoExactMatch()
    {
        var result = SoundcardCapture.ResolveDeviceIndex(Devices, "", "Realtek", -1);
        Assert.Equal(0, result); // first device whose Name contains "Realtek"
    }

    [Fact]
    public void ResolveDeviceIndex_NameNotFound_ReturnsNull()
    {
        var result = SoundcardCapture.ResolveDeviceIndex(Devices, "", "Nonexistent Device", -1);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveDeviceIndex_ExplicitIndex_UsedWhenValidAndEnabled()
    {
        var result = SoundcardCapture.ResolveDeviceIndex(Devices, "", "", 1);
        Assert.Equal(1, result);
    }

    [Fact]
    public void ResolveDeviceIndex_ExplicitIndex_DisabledDevice_FallsThroughToDefault()
    {
        var result = SoundcardCapture.ResolveDeviceIndex(Devices, "", "", 2);
        Assert.Equal(0, result); // device 2 is disabled -> falls through to default-enabled device 0
    }

    [Fact]
    public void ResolveDeviceIndex_ExplicitIndex_OutOfRange_FallsThroughToDefaultInsteadOfThrowing()
    {
        var result = SoundcardCapture.ResolveDeviceIndex(Devices, "", "", 99);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolveDeviceIndex_NothingSpecified_ChoosesDefaultEnabledDevice()
    {
        var result = SoundcardCapture.ResolveDeviceIndex(Devices, "", "", -1);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolveDeviceIndex_NoDefaultDevice_ChoosesFirstEnabledDevice()
    {
        var devices = new[]
        {
            new SoundcardCapture.RecordDeviceInfo(0, "Disabled Device", "{d0}", IsEnabled: false, IsDefault: true),
            new SoundcardCapture.RecordDeviceInfo(1, "Some Mic", "{d1}", IsEnabled: true, IsDefault: false),
        };
        var result = SoundcardCapture.ResolveDeviceIndex(devices, "", "", -1);
        Assert.Equal(1, result);
    }

    [Fact]
    public void ResolveDeviceIndex_AllDevicesDisabled_ReturnsNull()
    {
        var devices = new[]
        {
            new SoundcardCapture.RecordDeviceInfo(0, "Disabled Device", "{d0}", IsEnabled: false, IsDefault: true),
        };
        var result = SoundcardCapture.ResolveDeviceIndex(devices, "", "", -1);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveDeviceIndex_EmptyDeviceList_ReturnsNull()
    {
        var result = SoundcardCapture.ResolveDeviceIndex(Array.Empty<SoundcardCapture.RecordDeviceInfo>(), "", "", -1);
        Assert.Null(result);
    }
}
