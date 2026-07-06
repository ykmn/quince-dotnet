using ManagedBass;
using Xunit;

namespace Quince.Service.Tests.Audio;

/// <summary>
/// Guards the one thing that broke silently before: `bass.dll` must actually be present next to
/// the app (see <c>Quince.Service.csproj</c>'s "native\bass.dll" Content item) — without it,
/// every P/Invoke into ManagedBass throws DllNotFoundException at first use. Bass.Version reads
/// the loaded native library's version and needs no audio device/hardware.
/// </summary>
public class BassNativeLibraryTests
{
    [Fact]
    public void BassDll_IsPresentAndLoadable()
    {
        Assert.True(Bass.Version.Major >= 2);
    }
}
