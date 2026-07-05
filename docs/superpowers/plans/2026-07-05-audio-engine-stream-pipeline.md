# Аудио-движок: пайплайн для stream-каналов — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a working, end-to-end audio recording pipeline in `Quince.Service` for `source.type: stream` (Icecast/HLS) channels — capture via ffmpeg, rotated file output, EBU R128 level metering, silence detection, reconnect handling — and wire it into the existing UI (Start/Stop button, status dot, live level bar).

**Architecture:** Direct C# port of the legacy Python pipeline (`../quince/src/audio/*.py`): `StreamCapture` (ffmpeg subprocess → PCM) fans out to `AudioWriter` (PCM → rotated mp3/aac/wav via ffmpeg), `LevelMeter` (True Peak + LUFS), and `SilenceDetector` (RMS state machine), all owned by a `ChannelEngine`. A new `AudioEngineManager` (`IHostedService`) owns one `ChannelEngine` per running channel and pushes live status/level data to the browser through the existing `LevelHub` (SignalR).

**Tech Stack:** .NET 8 (`net8.0-windows`), ASP.NET Core Blazor Server, `System.Diagnostics.Process` (ffmpeg subprocess), `System.Threading.Channels` (fan-out queues), xUnit for tests. No new NuGet packages.

## Global Constraints

- Versioning `X.YY.ZZZ`: bump patch (`ZZZ`) by 1 for this change (see [[quince_dotnet_process_conventions]]). Do not touch major/minor.
- Update `HISTORY.md` (user's request verbatim + brief summary), `CHANGELOG.md` (bullet list), `README.md` (user-facing behavior), and publish a build to `release/<new-version>/` — required for every change per project convention.
- Log line format is fixed: `YYYY-MM-DD HH:MM:SS.mmm [LEVEL] [channel_name] message`, written via `ILogger<T>.BeginScope(new Dictionary<string,object>{["Channel"]=name})` (see [[quince_dotnet_logging_spec]]). Every new component's logger calls must go through this pattern — never format the channel name into the message text itself.
- All new file-system paths (ffmpeg binary) must resolve via the existing `Quince.Service.Configuration.PathResolver.Resolve(configuredValue, defaultRelative)` convention (anchored to `AppContext.BaseDirectory`, not CWD) — same as `ConfigDir`/`LogDir` in `Program.cs`.
- Scope is stream-type channels only. Soundcard capture, ICY/HLS metadata reading, and the meters (▦) window are explicitly out of scope — do not implement them, but do make the boundary visible in the UI (disabled Start button for soundcard channels).
- Spec reference: `docs/superpowers/specs/2026-07-05-audio-engine-stream-pipeline-design.md`.

---

### Task 1: Test project scaffold

**Files:**
- Create: `Quince.Service.Tests/Quince.Service.Tests.csproj`
- Create: `Quince.Service.Tests/Audio/PlaceholderTests.cs`
- Modify: `Quince.Service/Quince.Service.csproj`

**Interfaces:**
- Produces: an xUnit test project (`Quince.Service.Tests`) that can reference `internal` members of `Quince.Service` via `InternalsVisibleTo`, used by every later task's tests.

- [ ] **Step 1: Create the test project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Quince.Service\Quince.Service.csproj" />
  </ItemGroup>

</Project>
```

Save this as `Quince.Service.Tests/Quince.Service.Tests.csproj`.

- [ ] **Step 2: Allow the test project to see `internal` members**

Add to `Quince.Service/Quince.Service.csproj`, inside a new `<ItemGroup>` (before the closing `</Project>`):

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Quince.Service.Tests" />
  </ItemGroup>
```

- [ ] **Step 3: Add a placeholder test so the project builds**

```csharp
using Xunit;

namespace Quince.Service.Tests.Audio;

public class PlaceholderTests
{
    [Fact]
    public void Placeholder_AlwaysPasses()
    {
        Assert.True(true);
    }
}
```

Save as `Quince.Service.Tests/Audio/PlaceholderTests.cs`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 1, Skipped: 0`

- [ ] **Step 5: Commit**

This repo is not under git (confirmed via `git status` → "not a git repository"). Skip the commit step for every task in this plan — there is no VCS to commit to. Proceed to the next task directly.

---

### Task 2: `AudioChunk` and `OutputPathPlanner`

**Files:**
- Create: `Quince.Service/Audio/AudioChunk.cs`
- Create: `Quince.Service/Audio/OutputPathPlanner.cs`
- Test: `Quince.Service.Tests/Audio/OutputPathPlannerTests.cs`

**Interfaces:**
- Produces: `Quince.Service.Audio.AudioChunk` — `readonly struct` with `float[] Samples` (interleaved), `int Channels`, `int FrameCount`. Used by every capture/writer/meter/silence component from Task 6 onward.
- Produces: `Quince.Service.Audio.OutputPathPlanner` — `public static` methods `FormatDate(DateTime, string)`, `FormatTime(DateTime, string)`, `ComputeNextBoundary(DateTime, int)`, `ParseDateFolder(string, string)` (returns `DateOnly?`). Used by `AudioWriter` in Task 9.

- [ ] **Step 1: Write the failing tests**

```csharp
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class OutputPathPlannerTests
{
    [Fact]
    public void FormatDate_ReplacesTokens()
    {
        var dt = new DateTime(2026, 7, 5);
        Assert.Equal("2026-07-05", OutputPathPlanner.FormatDate(dt, "YYYY-MM-DD"));
    }

    [Fact]
    public void FormatTime_ReplacesTokens()
    {
        var dt = new DateTime(2026, 7, 5, 9, 5, 3);
        Assert.Equal("09-05-03", OutputPathPlanner.FormatTime(dt, "hh-mm-ss"));
    }

    [Fact]
    public void ComputeNextBoundary_AlignsToGridFromMidnight()
    {
        var now = new DateTime(2026, 7, 5, 0, 12, 0);
        var boundary = OutputPathPlanner.ComputeNextBoundary(now, 600);
        Assert.Equal(new DateTime(2026, 7, 5, 0, 20, 0), boundary);
    }

    [Fact]
    public void ComputeNextBoundary_ExactlyOnBoundary_ReturnsNextOne()
    {
        var now = new DateTime(2026, 7, 5, 0, 20, 0);
        var boundary = OutputPathPlanner.ComputeNextBoundary(now, 600);
        Assert.Equal(new DateTime(2026, 7, 5, 0, 30, 0), boundary);
    }

    [Fact]
    public void ParseDateFolder_ValidName_ReturnsDate()
    {
        var result = OutputPathPlanner.ParseDateFolder("2026-07-05", "YYYY-MM-DD");
        Assert.Equal(new DateOnly(2026, 7, 5), result);
    }

    [Fact]
    public void ParseDateFolder_InvalidName_ReturnsNull()
    {
        Assert.Null(OutputPathPlanner.ParseDateFolder("not-a-date", "YYYY-MM-DD"));
    }
}
```

Save as `Quince.Service.Tests/Audio/OutputPathPlannerTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: build error — `OutputPathPlanner` does not exist.

- [ ] **Step 3: Implement `AudioChunk`**

```csharp
namespace Quince.Service.Audio;

/// <summary>Interleaved PCM float32 audio, e.g. [L0, R0, L1, R1, ...] for stereo.</summary>
public readonly struct AudioChunk
{
    public AudioChunk(float[] samples, int channels)
    {
        Samples = samples;
        Channels = channels;
    }

    public float[] Samples { get; }
    public int Channels { get; }
    public int FrameCount => Channels > 0 ? Samples.Length / Channels : 0;
}
```

Save as `Quince.Service/Audio/AudioChunk.cs`.

- [ ] **Step 4: Implement `OutputPathPlanner`**

```csharp
using System.Text.RegularExpressions;

namespace Quince.Service.Audio;

public static class OutputPathPlanner
{
    public static string FormatDate(DateTime dt, string format) =>
        format.Replace("YYYY", dt.Year.ToString("D4"))
              .Replace("MM", dt.Month.ToString("D2"))
              .Replace("DD", dt.Day.ToString("D2"));

    public static string FormatTime(DateTime dt, string format) =>
        format.Replace("hh", dt.Hour.ToString("D2"))
              .Replace("mm", dt.Minute.ToString("D2"))
              .Replace("ss", dt.Second.ToString("D2"));

    /// <summary>Next file-rotation boundary after <paramref name="now"/>, aligned to a
    /// <paramref name="durationSeconds"/> grid measured from midnight.</summary>
    public static DateTime ComputeNextBoundary(DateTime now, int durationSeconds)
    {
        var midnight = now.Date;
        var elapsed = (now - midnight).TotalSeconds;
        var nextElapsed = Math.Ceiling((elapsed + 1e-9) / durationSeconds) * durationSeconds;
        return midnight.AddSeconds(nextElapsed);
    }

    /// <summary>Parses a date-folder name (e.g. "2026-07-05") back to a date using the
    /// same YYYY/MM/DD token format used to create it. Returns null if it doesn't match.</summary>
    public static DateOnly? ParseDateFolder(string name, string format)
    {
        var pattern = "^" + Regex.Escape(format)
            .Replace("YYYY", @"(?<year>\d{4})")
            .Replace("MM", @"(?<month>\d{2})")
            .Replace("DD", @"(?<day>\d{2})") + "$";
        var m = Regex.Match(name, pattern);
        if (!m.Success) return null;
        return new DateOnly(int.Parse(m.Groups["year"].Value), int.Parse(m.Groups["month"].Value), int.Parse(m.Groups["day"].Value));
    }
}
```

Save as `Quince.Service/Audio/OutputPathPlanner.cs`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 7, Skipped: 0`

---

### Task 3: `KWeightingFilter`

**Files:**
- Create: `Quince.Service/Audio/KWeightingFilter.cs`
- Test: `Quince.Service.Tests/Audio/KWeightingFilterTests.cs`

**Interfaces:**
- Produces: `Quince.Service.Audio.KWeightingFilter` — constructed with `int sampleRate`; `void Apply(double[][] perChannelSamples)` mutates each channel's array in place with the two-stage EBU R128 K-weighting cascade, keeping per-channel filter state across calls. `void Reset()` clears state. Used by `LevelMeter` in Task 6.

- [ ] **Step 1: Write the failing tests**

```csharp
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class KWeightingFilterTests
{
    [Fact]
    public void Apply_BlocksDcOverTime()
    {
        var filter = new KWeightingFilter(44100);
        var samples = new double[44100]; // 1 second of full-scale DC
        for (int i = 0; i < samples.Length; i++) samples[i] = 1.0;

        filter.Apply(new[] { samples });

        // The RLB high-pass stage rolls off DC/infrasonic content; after 1s
        // of settling the tail should be far smaller than the 1.0 input.
        Assert.True(Math.Abs(samples[^1]) < 0.01);
    }

    [Fact]
    public void Apply_IsLinearAndSymmetricAcrossChannels()
    {
        var filter = new KWeightingFilter(44100);
        var left = new double[100];
        var right = new double[100];
        for (int i = 0; i < 100; i++) { left[i] = 1.0; right[i] = -1.0; }

        filter.Apply(new[] { left, right });

        Assert.Equal(-left[^1], right[^1], precision: 8);
    }
}
```

Save as `Quince.Service.Tests/Audio/KWeightingFilterTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: build error — `KWeightingFilter` does not exist.

- [ ] **Step 3: Implement `KWeightingFilter`**

Port of the coefficient derivation and manual direct-form-II biquad application from `../quince/src/audio/meter.py`'s `_kweight_coeffs`/`_lfilter` (the math there uses only `tan`/`pow`, not scipy internals, so it ports directly without any DSP library):

```csharp
namespace Quince.Service.Audio;

/// <summary>Two-stage K-weighting filter (EBU R128 / ITU-R BS.1770-4), coefficients
/// derived per sample rate via bilinear transform of the EBU Tech 3341 analog prototype.</summary>
public sealed class KWeightingFilter
{
    // Stage 1 - high-shelf pre-filter (acoustic head effect)
    private const double S1F0 = 1681.974450955533;
    private const double S1G = 3.999843853973347;
    private const double S1Q = 0.7071752369554196;
    // Stage 2 - RLB-weighted high-pass
    private const double S2F0 = 38.13547087602444;
    private const double S2Q = 0.5003270373238773;

    private readonly double[] _b1;
    private readonly double[] _a1;
    private readonly double[] _b2;
    private readonly double[] _a2;

    private double[,]? _zi1;
    private double[,]? _zi2;

    public KWeightingFilter(int sampleRate)
    {
        var k1 = Math.Tan(Math.PI * S1F0 / sampleRate);
        var vh = Math.Pow(10.0, S1G / 20.0);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a01 = 1.0 + k1 / S1Q + k1 * k1;
        _b1 = new[]
        {
            (vh + vb * k1 / S1Q + k1 * k1) / a01,
            2.0 * (k1 * k1 - vh) / a01,
            (vh - vb * k1 / S1Q + k1 * k1) / a01,
        };
        _a1 = new[]
        {
            1.0,
            2.0 * (k1 * k1 - 1.0) / a01,
            (1.0 - k1 / S1Q + k1 * k1) / a01,
        };

        var k2 = Math.Tan(Math.PI * S2F0 / sampleRate);
        var a02 = 1.0 + k2 / S2Q + k2 * k2;
        _b2 = new[] { 1.0, -2.0, 1.0 };
        _a2 = new[]
        {
            1.0,
            2.0 * (k2 * k2 - 1.0) / a02,
            (1.0 - k2 / S2Q + k2 * k2) / a02,
        };
    }

    /// <summary>Applies the cascade in place. Each inner array is one channel's samples.</summary>
    public void Apply(double[][] perChannelSamples)
    {
        var channels = perChannelSamples.Length;
        _zi1 ??= new double[2, channels];
        _zi2 ??= new double[2, channels];

        for (var ch = 0; ch < channels; ch++)
        {
            var data = perChannelSamples[ch];
            double z10 = _zi1[0, ch], z11 = _zi1[1, ch];
            double z20 = _zi2[0, ch], z21 = _zi2[1, ch];

            for (var f = 0; f < data.Length; f++)
            {
                var x = data[f];

                var y1 = _b1[0] * x + z10;
                z10 = _b1[1] * x - _a1[1] * y1 + z11;
                z11 = _b1[2] * x - _a1[2] * y1;

                var y2 = _b2[0] * y1 + z20;
                z20 = _b2[1] * y1 - _a2[1] * y2 + z21;
                z21 = _b2[2] * y1 - _a2[2] * y2;

                data[f] = y2;
            }

            _zi1[0, ch] = z10; _zi1[1, ch] = z11;
            _zi2[0, ch] = z20; _zi2[1, ch] = z21;
        }
    }

    public void Reset()
    {
        _zi1 = null;
        _zi2 = null;
    }
}
```

Save as `Quince.Service/Audio/KWeightingFilter.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 9, Skipped: 0`

---

### Task 4: `TruePeakCalculator`

**Files:**
- Create: `Quince.Service/Audio/TruePeakCalculator.cs`
- Test: `Quince.Service.Tests/Audio/TruePeakCalculatorTests.cs`

**Interfaces:**
- Produces: `Quince.Service.Audio.TruePeakCalculator.TruePeakDb(ReadOnlySpan<double> samples)` — `public static double`, 4× oversampled true-peak in dBTP. Used by `LevelMeter` in Task 6.

- [ ] **Step 1: Write the failing tests**

```csharp
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class TruePeakCalculatorTests
{
    [Fact]
    public void TruePeakDb_FullScaleConstant_ReturnsZeroDb()
    {
        var samples = new double[100];
        for (var i = 0; i < samples.Length; i++) samples[i] = 1.0;

        var result = TruePeakCalculator.TruePeakDb(samples);

        Assert.Equal(0.0, result, precision: 6);
    }

    [Fact]
    public void TruePeakDb_Silence_ReturnsVeryLowValue()
    {
        var samples = new double[100];
        var result = TruePeakCalculator.TruePeakDb(samples);
        Assert.True(result < -170.0);
    }

    [Fact]
    public void TruePeakDb_Empty_ReturnsNegativeInfinity()
    {
        Assert.Equal(double.NegativeInfinity, TruePeakCalculator.TruePeakDb(ReadOnlySpan<double>.Empty));
    }
}
```

Save as `Quince.Service.Tests/Audio/TruePeakCalculatorTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: build error — `TruePeakCalculator` does not exist.

- [ ] **Step 3: Implement `TruePeakCalculator`**

Port of `meter.py`'s numpy-only fallback branch of `_true_peak_db` (linear-interpolation 4× oversampling — the only version of this calculation that doesn't depend on scipy):

```csharp
namespace Quince.Service.Audio;

public static class TruePeakCalculator
{
    /// <summary>True Peak (dBTP) of one channel's samples via 4x oversampling
    /// (linear interpolation).</summary>
    public static double TruePeakDb(ReadOnlySpan<double> channelSamples)
    {
        var n = channelSamples.Length;
        if (n == 0) return double.NegativeInfinity;

        var nUp = n * 4;
        var maxAbs = 0.0;
        var denom = nUp > 1 ? nUp - 1 : 1;

        for (var i = 0; i < nUp; i++)
        {
            var pos = (double)i * (n - 1) / denom;
            var lo = (int)Math.Floor(pos);
            var hi = Math.Min(lo + 1, n - 1);
            var frac = pos - lo;
            var value = channelSamples[lo] + frac * (channelSamples[hi] - channelSamples[lo]);
            var abs = Math.Abs(value);
            if (abs > maxAbs) maxAbs = abs;
        }

        return 20.0 * Math.Log10(Math.Max(maxAbs, 1e-9));
    }
}
```

Save as `Quince.Service/Audio/TruePeakCalculator.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 12, Skipped: 0`

---

### Task 5: `LufsCalculator`

**Files:**
- Create: `Quince.Service/Audio/LufsCalculator.cs`
- Test: `Quince.Service.Tests/Audio/LufsCalculatorTests.cs`

**Interfaces:**
- Produces: `Quince.Service.Audio.LufsCalculator.FromMeanSquare(double totalMeanSquare)` — `public static double`. `Integrated(IReadOnlyList<double> blockLufs, IReadOnlyList<double> blockTotalMeanSquare)` — `public static double`, EBU R128 absolute+relative gating. Used by `LevelMeter` in Task 6.

- [ ] **Step 1: Write the failing tests**

```csharp
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class LufsCalculatorTests
{
    [Fact]
    public void FromMeanSquare_BelowNoiseFloor_ReturnsNegativeInfinity()
    {
        Assert.Equal(double.NegativeInfinity, LufsCalculator.FromMeanSquare(1e-12));
    }

    [Fact]
    public void FromMeanSquare_UnityMeanSquare_MatchesReferenceFormula()
    {
        Assert.Equal(-0.691, LufsCalculator.FromMeanSquare(1.0), precision: 6);
    }

    [Fact]
    public void Integrated_NoBlocks_ReturnsNegativeInfinity()
    {
        Assert.Equal(double.NegativeInfinity, LufsCalculator.Integrated(new List<double>(), new List<double>()));
    }

    [Fact]
    public void Integrated_AllBlocksBelowAbsoluteGate_ReturnsNegativeInfinity()
    {
        var lufs = new List<double> { -80.0, -75.0 };
        var ms = new List<double> { 1e-8, 1e-8 };
        Assert.Equal(double.NegativeInfinity, LufsCalculator.Integrated(lufs, ms));
    }

    [Fact]
    public void Integrated_ConsistentLoudBlocks_ReturnsFiniteValue()
    {
        var lufs = new List<double> { -0.691, -0.691, -0.691 };
        var ms = new List<double> { 1.0, 1.0, 1.0 };

        var result = LufsCalculator.Integrated(lufs, ms);

        Assert.Equal(-0.691, result, precision: 6);
    }
}
```

Save as `Quince.Service.Tests/Audio/LufsCalculatorTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: build error — `LufsCalculator` does not exist.

- [ ] **Step 3: Implement `LufsCalculator`**

Port of `meter.py`'s `_lufs_from_mean_square` and `_compute_integrated`:

```csharp
namespace Quince.Service.Audio;

public static class LufsCalculator
{
    private const double AbsoluteGateLufs = -70.0;
    private const double RelativeGateOffsetLu = -10.0;

    /// <summary>Converts a summed-across-channels K-weighted mean-square value to LUFS.</summary>
    public static double FromMeanSquare(double totalMeanSquare)
    {
        // Noise floor: below ~1e-10 (~-100 dBFS RMS) is treated as silence.
        if (totalMeanSquare < 1e-10) return double.NegativeInfinity;
        return -0.691 + 10.0 * Math.Log10(totalMeanSquare);
    }

    /// <summary>Gated integrated loudness (EBU R128) from accumulated 400ms blocks.
    /// <paramref name="blockLufs"/>[i] must be <c>FromMeanSquare(blockTotalMeanSquare[i])</c>.</summary>
    public static double Integrated(IReadOnlyList<double> blockLufs, IReadOnlyList<double> blockTotalMeanSquare)
    {
        if (blockLufs.Count == 0) return double.NegativeInfinity;

        double ungatedSum = 0;
        var ungatedCount = 0;
        for (var i = 0; i < blockLufs.Count; i++)
        {
            if (blockLufs[i] > AbsoluteGateLufs)
            {
                ungatedSum += blockTotalMeanSquare[i];
                ungatedCount++;
            }
        }
        if (ungatedCount == 0) return double.NegativeInfinity;

        var ungatedLufs = FromMeanSquare(ungatedSum / ungatedCount);
        var relThreshold = ungatedLufs + RelativeGateOffsetLu;

        double gatedSum = 0;
        var gatedCount = 0;
        for (var i = 0; i < blockLufs.Count; i++)
        {
            if (blockLufs[i] > AbsoluteGateLufs && blockLufs[i] > relThreshold)
            {
                gatedSum += blockTotalMeanSquare[i];
                gatedCount++;
            }
        }
        if (gatedCount == 0) return double.NegativeInfinity;

        return FromMeanSquare(gatedSum / gatedCount);
    }
}
```

Save as `Quince.Service/Audio/LufsCalculator.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 17, Skipped: 0`

---

### Task 6: `LevelReading` and `LevelMeter`

**Files:**
- Create: `Quince.Service/Audio/LevelReading.cs`
- Create: `Quince.Service/Audio/LevelMeter.cs`
- Test: `Quince.Service.Tests/Audio/LevelMeterTests.cs`

**Interfaces:**
- Consumes: `AudioChunk` (Task 2), `KWeightingFilter` (Task 3), `TruePeakCalculator.TruePeakDb` (Task 4), `LufsCalculator.FromMeanSquare`/`Integrated` (Task 5).
- Produces: `Quince.Service.Audio.LevelReading` — `sealed record` with `double TruePeakDb, TruePeakMaxDb, LoudnessM, LoudnessS, LoudnessI, TruePeakLDb, TruePeakRDb` (all default to `double.NegativeInfinity`). `Quince.Service.Audio.LevelMeter` — public constructor `(ChannelReader<AudioChunk> reader, int sampleRate, int channels, Action<LevelReading> onUpdate, ILogger log)`; public `void Start()`, `void Stop()`, `bool IsRunning`; `internal void ProcessChunk(AudioChunk chunk)` (testable synchronously, called internally by the background read loop). Used by `ChannelEngine` in Task 10.

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Audio;
using System.Threading.Channels;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class LevelMeterTests
{
    [Fact]
    public void ProcessChunk_FullScaleMono_ReportsZeroDbTruePeak()
    {
        LevelReading? received = null;
        var channel = Channel.CreateUnbounded<AudioChunk>();
        var meter = new LevelMeter(channel.Reader, sampleRate: 44100, channels: 1,
            onUpdate: r => received = r, log: NullLogger.Instance);

        // 44100 * 0.1 = 4410 samples needed to cross the ~100ms update threshold.
        var samples = new float[5000];
        for (var i = 0; i < samples.Length; i++) samples[i] = 1.0f;
        var chunk = new AudioChunk(samples, channels: 1);

        meter.ProcessChunk(chunk);

        Assert.NotNull(received);
        Assert.Equal(0.0, received!.TruePeakDb, precision: 6);
        Assert.Equal(0.0, received.TruePeakMaxDb, precision: 6);
    }

    [Fact]
    public void ProcessChunk_BelowUpdateThreshold_DoesNotFireCallback()
    {
        LevelReading? received = null;
        var channel = Channel.CreateUnbounded<AudioChunk>();
        var meter = new LevelMeter(channel.Reader, sampleRate: 44100, channels: 1,
            onUpdate: r => received = r, log: NullLogger.Instance);

        var chunk = new AudioChunk(new float[] { 1.0f, 1.0f, 1.0f }, channels: 1);
        meter.ProcessChunk(chunk);

        Assert.Null(received);
    }
}
```

Save as `Quince.Service.Tests/Audio/LevelMeterTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: build error — `LevelMeter` does not exist.

- [ ] **Step 3: Implement `LevelReading`**

```csharp
namespace Quince.Service.Audio;

public sealed record LevelReading(
    double TruePeakDb = double.NegativeInfinity,
    double TruePeakMaxDb = double.NegativeInfinity,
    double LoudnessM = double.NegativeInfinity,
    double LoudnessS = double.NegativeInfinity,
    double LoudnessI = double.NegativeInfinity,
    double TruePeakLDb = double.NegativeInfinity,
    double TruePeakRDb = double.NegativeInfinity);
```

Save as `Quince.Service/Audio/LevelReading.cs`.

- [ ] **Step 4: Implement `LevelMeter`**

Port of `meter.py`'s `LevelMeter` (ring buffers for momentary/short-term windows, 400ms integrated blocks, ~10Hz update pacing with the same wall-clock burst guard for bursty HLS data):

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

public sealed class LevelMeter
{
    private const double MWindowSeconds = 0.400;
    private const double SWindowSeconds = 3.0;
    private const double UpdateIntervalSeconds = 0.100;

    private readonly ChannelReader<AudioChunk> _reader;
    private readonly int _sampleRate;
    private readonly Action<LevelReading> _onUpdate;
    private readonly ILogger _log;

    private readonly KWeightingFilter _kWeight;
    private readonly double[][] _mBuf;
    private readonly double[][] _sBuf;
    private int _mHead, _sHead;
    private readonly int _blockSize;

    private readonly List<double> _blockLufs = new();
    private readonly List<double> _blockTotalMs = new();
    private readonly double[] _blockAccum;
    private int _blockSamples;

    private int _samplesSinceUpdate;
    private readonly int _updateEvery;
    private double _lastUpdateTime;

    private readonly object _peakLock = new();
    private double _truePeakMaxDb = double.NegativeInfinity;

    private CancellationTokenSource? _cts;
    private Task? _task;

    public LevelMeter(ChannelReader<AudioChunk> reader, int sampleRate, int channels, Action<LevelReading> onUpdate, ILogger log)
    {
        _reader = reader;
        _sampleRate = sampleRate;
        _onUpdate = onUpdate;
        _log = log;

        _kWeight = new KWeightingFilter(sampleRate);
        var mCap = (int)Math.Ceiling(MWindowSeconds * sampleRate);
        var sCap = (int)Math.Ceiling(SWindowSeconds * sampleRate);
        _mBuf = CreateBuffer(mCap, channels);
        _sBuf = CreateBuffer(sCap, channels);
        _blockSize = mCap;
        _blockAccum = new double[channels];
        _updateEvery = (int)(UpdateIntervalSeconds * sampleRate);
    }

    private static double[][] CreateBuffer(int capacity, int channels)
    {
        var buf = new double[channels][];
        for (var c = 0; c < channels; c++) buf[c] = new double[capacity];
        return buf;
    }

    public bool IsRunning => _task is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts = null;
        _task = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var realStart = DateTime.UtcNow;
        var audioSeconds = 0.0;
        try
        {
            await foreach (var chunk in _reader.ReadAllAsync(ct))
            {
                try
                {
                    ProcessChunk(chunk);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Ошибка обработки чанка в LevelMeter");
                    continue;
                }

                audioSeconds += chunk.FrameCount / (double)_sampleRate;
                var ahead = audioSeconds - (DateTime.UtcNow - realStart).TotalSeconds;
                if (ahead > 0.2)
                    await Task.Delay(TimeSpan.FromSeconds(ahead - 0.1), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void ProcessChunk(AudioChunk chunk)
    {
        var frames = chunk.FrameCount;
        if (frames == 0) return;
        var channels = chunk.Channels;

        var perChannel = new double[channels][];
        for (var c = 0; c < channels; c++)
        {
            perChannel[c] = new double[frames];
            for (var f = 0; f < frames; f++)
                perChannel[c][f] = chunk.Samples[f * channels + c];
        }

        // True Peak on the original (un-weighted) PCM.
        var tpL = TruePeakCalculator.TruePeakDb(perChannel[0]);
        var tpR = channels >= 2 ? TruePeakCalculator.TruePeakDb(perChannel[1]) : double.NegativeInfinity;
        var tpOverall = channels >= 2 ? Math.Max(tpL, tpR) : tpL;

        lock (_peakLock)
        {
            if (tpOverall > _truePeakMaxDb) _truePeakMaxDb = tpOverall;
        }

        // K-weight in place (mutates perChannel) for loudness measurement only.
        _kWeight.Apply(perChannel);

        WriteRing(_mBuf, ref _mHead, perChannel, frames);
        WriteRing(_sBuf, ref _sHead, perChannel, frames);
        AccumulateIntegratedBlocks(perChannel, frames);

        _samplesSinceUpdate += frames;
        if (_samplesSinceUpdate < _updateEvery) return;
        _samplesSinceUpdate = 0;

        var now = Environment.TickCount64 / 1000.0;
        if (now - _lastUpdateTime < UpdateIntervalSeconds * 0.5) return;
        _lastUpdateTime = now;

        var msM = RingMeanSquareTotal(_mBuf);
        var msS = RingMeanSquareTotal(_sBuf);
        double peakMax;
        lock (_peakLock) { peakMax = _truePeakMaxDb; }

        var reading = new LevelReading(
            TruePeakDb: tpOverall,
            TruePeakMaxDb: peakMax,
            LoudnessM: LufsCalculator.FromMeanSquare(msM),
            LoudnessS: LufsCalculator.FromMeanSquare(msS),
            LoudnessI: LufsCalculator.Integrated(_blockLufs, _blockTotalMs),
            TruePeakLDb: tpL,
            TruePeakRDb: tpR);

        try { _onUpdate(reading); }
        catch (Exception ex) { _log.LogError(ex, "Колбэк обновления уровня выбросил исключение"); }
    }

    private static void WriteRing(double[][] buf, ref int head, double[][] perChannel, int frames)
    {
        var cap = buf[0].Length;
        if (frames >= cap)
        {
            for (var c = 0; c < buf.Length; c++)
                Array.Copy(perChannel[c], frames - cap, buf[c], 0, cap);
            head = 0;
            return;
        }

        var end = head + frames;
        for (var c = 0; c < buf.Length; c++)
        {
            if (end <= cap)
            {
                Array.Copy(perChannel[c], 0, buf[c], head, frames);
            }
            else
            {
                var first = cap - head;
                Array.Copy(perChannel[c], 0, buf[c], head, first);
                Array.Copy(perChannel[c], first, buf[c], 0, frames - first);
            }
        }
        head = end % cap;
    }

    private static double RingMeanSquareTotal(double[][] buf)
    {
        var total = 0.0;
        foreach (var chBuf in buf)
        {
            var sumSq = 0.0;
            foreach (var v in chBuf) sumSq += v * v;
            total += sumSq / chBuf.Length;
        }
        return total;
    }

    private void AccumulateIntegratedBlocks(double[][] perChannel, int frames)
    {
        var channels = perChannel.Length;
        var offset = 0;
        var remaining = frames;
        while (remaining > 0)
        {
            var space = _blockSize - _blockSamples;
            var take = Math.Min(remaining, space);
            for (var c = 0; c < channels; c++)
            {
                var sumSq = 0.0;
                for (var f = offset; f < offset + take; f++)
                    sumSq += perChannel[c][f] * perChannel[c][f];
                _blockAccum[c] += sumSq;
            }
            _blockSamples += take;
            offset += take;
            remaining -= take;

            if (_blockSamples >= _blockSize)
            {
                var totalMs = 0.0;
                for (var c = 0; c < channels; c++) totalMs += _blockAccum[c] / _blockSize;
                _blockLufs.Add(LufsCalculator.FromMeanSquare(totalMs));
                _blockTotalMs.Add(totalMs);
                Array.Clear(_blockAccum);
                _blockSamples = 0;
            }
        }
    }
}
```

Save as `Quince.Service/Audio/LevelMeter.cs`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 19, Skipped: 0`

---

### Task 7: `SilenceDetector`

**Files:**
- Create: `Quince.Service/Audio/SilenceDetector.cs`
- Test: `Quince.Service.Tests/Audio/SilenceDetectorTests.cs`

**Interfaces:**
- Consumes: `AudioChunk` (Task 2), `Quince.Service.Configuration.SilenceDetectorConfig` (existing, `Quince.Service/Configuration/ChannelConfig.cs`).
- Produces: `Quince.Service.Audio.SilenceDetector` — public constructor `(SilenceDetectorConfig config, ChannelReader<AudioChunk> reader, Action onSilence, Action onSound, ILogger log)`; public `void Start()`, `void Stop()`, `bool IsSilent`, `bool IsRunning`; `internal void ProcessLevel(double levelDb)` (testable synchronously); `internal static double ComputeLevelDb(AudioChunk chunk)`. Used by `ChannelEngine` in Task 10.

- [ ] **Step 1: Write the failing tests**

```csharp
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
```

Save as `Quince.Service.Tests/Audio/SilenceDetectorTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: build error — `SilenceDetector` does not exist.

- [ ] **Step 3: Implement `SilenceDetector`**

Port of `silence.py`:

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Audio;

public sealed class SilenceDetector
{
    private const double ChunkDurationApproxSeconds = 0.1;

    private readonly SilenceDetectorConfig _config;
    private readonly ChannelReader<AudioChunk> _reader;
    private readonly Action _onSilence;
    private readonly Action _onSound;
    private readonly ILogger _log;

    private string _state = "SOUND";
    private double _silenceTimer;
    private double _soundTimer;

    private CancellationTokenSource? _cts;
    private Task? _task;

    public SilenceDetector(SilenceDetectorConfig config, ChannelReader<AudioChunk> reader, Action onSilence, Action onSound, ILogger log)
    {
        _config = config;
        _reader = reader;
        _onSilence = onSilence;
        _onSound = onSound;
        _log = log;
    }

    public bool IsSilent => _state == "SILENT";
    public bool IsRunning => _task is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _state = "SOUND";
        _silenceTimer = 0;
        _soundTimer = 0;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts = null;
        _task = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _reader.ReadAllAsync(ct))
            {
                ProcessLevel(ComputeLevelDb(chunk));
            }
        }
        catch (OperationCanceledException) { }
    }

    internal static double ComputeLevelDb(AudioChunk chunk)
    {
        if (chunk.Samples.Length == 0) return double.NegativeInfinity;
        var sumSq = 0.0;
        foreach (var s in chunk.Samples) sumSq += (double)s * s;
        var rms = Math.Sqrt(sumSq / chunk.Samples.Length);
        return 20.0 * Math.Log10(Math.Max(rms, 1e-9));
    }

    internal void ProcessLevel(double levelDb)
    {
        if (!_config.Enabled) return;
        var isBelow = levelDb < _config.ThresholdDbfs;

        if (_state == "SOUND")
        {
            if (isBelow)
            {
                _silenceTimer += ChunkDurationApproxSeconds;
                if (_silenceTimer >= _config.TriggerSeconds)
                {
                    _state = "SILENT";
                    _silenceTimer = 0;
                    _soundTimer = 0;
                    _onSilence();
                    _log.LogWarning("Тишина обнаружена (уровень {Level:F1} dBFS)", levelDb);
                }
            }
            else
            {
                _silenceTimer = 0;
            }
        }
        else
        {
            if (isBelow)
            {
                _soundTimer = 0;
            }
            else
            {
                _soundTimer += ChunkDurationApproxSeconds;
                if (_soundTimer >= _config.ResumeSeconds)
                {
                    _state = "SOUND";
                    _silenceTimer = 0;
                    _soundTimer = 0;
                    _onSound();
                    _log.LogInformation("Звук возобновился (уровень {Level:F1} dBFS)", levelDb);
                }
            }
        }
    }
}
```

Save as `Quince.Service/Audio/SilenceDetector.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 23, Skipped: 0`

---

### Task 8: `StreamCapture`

**Files:**
- Create: `Quince.Service/Audio/StreamCapture.cs`
- Test: `Quince.Service.Tests/Audio/StreamCaptureTests.cs`

**Interfaces:**
- Consumes: `AudioChunk` (Task 2).
- Produces: `Quince.Service.Audio.StreamStatus` (enum: `Stopped, Connecting, Streaming, Reconnecting, Error`). `Quince.Service.Audio.StreamCapture` — public constructor `(string ffmpegPath, string url, string streamType, bool allowInvalidSsl, int hlsBitrateIndex, int reconnectDelaySeconds, ILogger log)`; `const int SampleRate = 44100`, `const int Channels = 2`; `ChannelReader<AudioChunk> Subscribe(string consumerId)`, `void Unsubscribe(string consumerId)`, `void Start()`, `void Stop()`, `StreamStatus Status`, `int ReconnectAttempt`; `internal static string[] BuildFfmpegArgs(string url, string streamType, bool allowInvalidSsl, int hlsBitrateIndex, string userAgent)`. Used by `ChannelEngine` in Task 10.

- [ ] **Step 1: Write the failing tests**

```csharp
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class StreamCaptureTests
{
    [Fact]
    public void BuildFfmpegArgs_IcecastUrl_DoesNotAddHlsFlags()
    {
        var args = StreamCapture.BuildFfmpegArgs("http://example.com/stream", "icecast",
            allowInvalidSsl: false, hlsBitrateIndex: 0, userAgent: "TestAgent/1.0");

        Assert.DoesNotContain("-allowed_extensions", args);
        Assert.DoesNotContain("-map", args);
        Assert.Contains("-i", args);
        Assert.Contains("http://example.com/stream", args);
        Assert.Contains("pcm_f32le", args);
    }

    [Fact]
    public void BuildFfmpegArgs_Hls_AddsAllowedExtensionsAndMap()
    {
        var args = StreamCapture.BuildFfmpegArgs("https://example.com/playlist.m3u8", "hls",
            allowInvalidSsl: false, hlsBitrateIndex: 2, userAgent: "TestAgent/1.0");

        Assert.Contains("-allowed_extensions", args);
        Assert.Contains("ALL", args);
        Assert.Contains("-map", args);
        Assert.Contains("0:a:2", args);
    }

    [Fact]
    public void BuildFfmpegArgs_AllowInvalidSsl_AddsTlsVerifyOff()
    {
        var args = StreamCapture.BuildFfmpegArgs("https://example.com/stream", "icecast",
            allowInvalidSsl: true, hlsBitrateIndex: 0, userAgent: "TestAgent/1.0");

        Assert.Contains("-tls_verify", args);
        Assert.Contains("0", args);
    }
}
```

Save as `Quince.Service.Tests/Audio/StreamCaptureTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: build error — `StreamCapture` does not exist.

- [ ] **Step 3: Implement `StreamCapture`**

Port of `capture_stream.py` — ffmpeg subprocess decode, per-consumer bounded `Channel<AudioChunk>` fan-out (using the default `BoundedChannelFullMode.Wait` and a non-blocking `TryWrite`, which returns `false` on a full channel exactly like Python's `queue.put_nowait()` raising `Full` — giving the same "drop the incoming frame, log it" behavior), and reconnect loop:

```csharp
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

public enum StreamStatus { Stopped, Connecting, Streaming, Reconnecting, Error }

public sealed class StreamCapture
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    private const int BlockFrames = 4096;
    private const int BytesPerSample = 4;
    private static readonly int ReadBytes = BlockFrames * Channels * BytesPerSample;

    private static readonly string[] DesktopUserAgents =
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:126.0) Gecko/20100101 Firefox/126.0",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:125.0) Gecko/20100101 Firefox/125.0",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0",
    };

    private readonly string _ffmpegPath;
    private readonly string _url;
    private readonly string _streamType;
    private readonly bool _allowInvalidSsl;
    private readonly int _hlsBitrateIndex;
    private readonly int _reconnectDelaySeconds;
    private readonly ILogger _log;

    private readonly object _lock = new();
    private readonly Dictionary<string, ChannelWriter<AudioChunk>> _consumers = new();

    private volatile StreamStatus _status = StreamStatus.Stopped;
    private volatile int _reconnectAttempt;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public StreamCapture(string ffmpegPath, string url, string streamType, bool allowInvalidSsl,
        int hlsBitrateIndex, int reconnectDelaySeconds, ILogger log)
    {
        _ffmpegPath = ffmpegPath;
        _url = url;
        _streamType = streamType;
        _allowInvalidSsl = allowInvalidSsl;
        _hlsBitrateIndex = hlsBitrateIndex;
        _reconnectDelaySeconds = Math.Max(1, reconnectDelaySeconds);
        _log = log;
    }

    public StreamStatus Status => _status;
    public int ReconnectAttempt => _reconnectAttempt;

    public ChannelReader<AudioChunk> Subscribe(string consumerId)
    {
        var channel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        lock (_lock) { _consumers[consumerId] = channel.Writer; }
        return channel.Reader;
    }

    public void Unsubscribe(string consumerId)
    {
        lock (_lock) { _consumers.Remove(consumerId); }
    }

    public void Start()
    {
        if (_task is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _status = StreamStatus.Stopped;
        var proc = _process;
        if (proc != null)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
        }
        try { _task?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        _task = null;
        _process = null;
    }

    internal static string[] BuildFfmpegArgs(string url, string streamType, bool allowInvalidSsl, int hlsBitrateIndex, string userAgent)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (allowInvalidSsl) args.AddRange(new[] { "-tls_verify", "0" });
        args.AddRange(new[] { "-user_agent", userAgent });

        var isHls = streamType == "hls";
        if (isHls) args.AddRange(new[] { "-allowed_extensions", "ALL" });

        args.AddRange(new[] { "-i", url });

        if (isHls) args.AddRange(new[] { "-map", $"0:a:{hlsBitrateIndex}" });

        args.AddRange(new[]
        {
            "-vn",
            "-acodec", "pcm_f32le",
            "-ar", SampleRate.ToString(),
            "-ac", Channels.ToString(),
            "-f", "f32le",
            "pipe:1",
        });
        return args.ToArray();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        _reconnectAttempt = 0;

        while (!ct.IsCancellationRequested)
        {
            _status = StreamStatus.Connecting;
            _log.LogInformation("Подключение к {Url} (попытка {Attempt})", _url, _reconnectAttempt);

            var ua = DesktopUserAgents[Random.Shared.Next(DesktopUserAgents.Length)];
            var args = BuildFfmpegArgs(_url, _streamType, _allowInvalidSsl, _hlsBitrateIndex, ua);

            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo(_ffmpegPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
                _process = process;
                _status = StreamStatus.Streaming;
                if (_reconnectAttempt > 0)
                    _log.LogInformation("Переподключение к {Url} выполнено", _url);
                _reconnectAttempt = 0;

                await ReadLoopAsync(process, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Ошибка ffmpeg");
            }
            finally
            {
                _process = null;
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    process.Dispose();
                }
            }

            if (ct.IsCancellationRequested) break;

            _reconnectAttempt++;
            _status = StreamStatus.Reconnecting;
            _log.LogWarning("Поток отключён. Попытка переподключения {Attempt} через {Delay}с", _reconnectAttempt, _reconnectDelaySeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(_reconnectDelaySeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _status = StreamStatus.Stopped;
    }

    private async Task ReadLoopAsync(Process process, CancellationToken ct)
    {
        var stream = process.StandardOutput.BaseStream;
        var buffer = new byte[ReadBytes];

        while (!ct.IsCancellationRequested)
        {
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
                if (n == 0) break;
                totalRead += n;
            }
            if (totalRead == 0) break; // EOF — process exited

            var nSamples = totalRead / BytesPerSample;
            var nFrames = nSamples / Channels;
            if (nFrames == 0) continue;

            var samples = new float[nFrames * Channels];
            Buffer.BlockCopy(buffer, 0, samples, 0, samples.Length * sizeof(float));
            var chunk = new AudioChunk(samples, Channels);

            List<KeyValuePair<string, ChannelWriter<AudioChunk>>> consumers;
            lock (_lock) { consumers = _consumers.ToList(); }

            foreach (var (consumerId, writer) in consumers)
            {
                if (!writer.TryWrite(chunk))
                    _log.LogDebug("Очередь подписчика '{Consumer}' переполнена — кадр отброшен ({Frames} фреймов)", consumerId, nFrames);
            }
        }

        if (process.HasExited && process.ExitCode != 0)
            _log.LogWarning("FFmpeg завершился с кодом {Code}", process.ExitCode);
    }
}
```

Save as `Quince.Service/Audio/StreamCapture.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 26, Skipped: 0`

---

### Task 9: `AudioWriter`

**Files:**
- Create: `Quince.Service/Audio/AudioWriter.cs`
- Test: `Quince.Service.Tests/Audio/AudioWriterTests.cs`

**Interfaces:**
- Consumes: `AudioChunk` (Task 2), `OutputPathPlanner` (Task 2), `Quince.Service.Configuration.ChannelConfig`/`OutputFormatConfig` (existing).
- Produces: `Quince.Service.Audio.AudioWriter` — public constructor `(ChannelConfig config, ChannelReader<AudioChunk> reader, int inputSampleRate, int inputChannels, string ffmpegPath, ILogger log)`; public `void Start()`, `void Stop()`, `string? CurrentFile`, `bool IsRunning`; `internal static string[] BuildEncodeArgs(OutputFormatConfig fmt, int inputSampleRate, int inputChannels, string outPath)`. Used by `ChannelEngine` in Task 10.

- [ ] **Step 1: Write the failing tests**

```csharp
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
    public void BuildEncodeArgs_UnsupportedFormat_Throws()
    {
        var fmt = new OutputFormatConfig { FileFormat = "flac" };
        Assert.Throws<ArgumentException>(() => AudioWriter.BuildEncodeArgs(fmt, 44100, 2, "out.flac"));
    }
}
```

Save as `Quince.Service.Tests/Audio/AudioWriterTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: build error — `AudioWriter` does not exist.

- [ ] **Step 3: Implement `AudioWriter`**

Port of `writer.py` — time-grid rotation, daily folders, crash cooldown, retention cleanup:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Audio;

public sealed class AudioWriter
{
    private readonly ChannelConfig _config;
    private readonly ChannelReader<AudioChunk> _reader;
    private readonly int _inputSampleRate;
    private readonly int _inputChannels;
    private readonly string _ffmpegPath;
    private readonly ILogger _log;

    private Process? _proc;
    private string? _currentFile;
    private DateTime? _nextBoundary;
    private DateOnly? _openDate;
    private DateTime? _openTime;
    private DateTime? _crashCooldownUntil;

    private CancellationTokenSource? _cts;
    private Task? _task;

    public AudioWriter(ChannelConfig config, ChannelReader<AudioChunk> reader, int inputSampleRate, int inputChannels, string ffmpegPath, ILogger log)
    {
        _config = config;
        _reader = reader;
        _inputSampleRate = inputSampleRate > 0 ? inputSampleRate : config.OutputFormat.SampleRate;
        _inputChannels = inputChannels > 0 ? inputChannels : config.OutputFormat.Channels;
        _ffmpegPath = ffmpegPath;
        _log = log;
    }

    public string? CurrentFile => _currentFile;
    public bool IsRunning => _task is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        CleanupOldFiles();
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
        _log.LogInformation("AudioWriter запущен");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        _cts = null;
        _task = null;
        CloseProc();
        _log.LogInformation("AudioWriter остановлен");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _reader.ReadAllAsync(ct))
            {
                MaybeRotate();

                if (_proc == null)
                {
                    var now = DateTime.Now;
                    if (_crashCooldownUntil.HasValue && now < _crashCooldownUntil.Value)
                        continue;
                    OpenProc(now);
                }

                if (_proc != null)
                {
                    try
                    {
                        var bytes = new byte[chunk.Samples.Length * sizeof(float)];
                        Buffer.BlockCopy(chunk.Samples, 0, bytes, 0, bytes.Length);
                        await _proc.StandardInput.BaseStream.WriteAsync(bytes, ct);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        _log.LogError(ex, "Ошибка записи в stdin ffmpeg");
                        CloseProc(crashed: true);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            CloseProc();
        }
    }

    private void MaybeRotate()
    {
        if (_proc == null || _nextBoundary == null) return;
        var now = DateTime.Now;
        var dateRolled = _openDate.HasValue && DateOnly.FromDateTime(now) > _openDate.Value;
        if (now >= _nextBoundary.Value || dateRolled)
        {
            var oldPath = _currentFile;
            CloseProc();
            OpenProc(now);
            _log.LogInformation("Ротация: {Old} -> {New}", oldPath, _currentFile);
            if (dateRolled) CleanupOldFiles();
        }
    }

    private void OpenProc(DateTime now)
    {
        _crashCooldownUntil = null;
        var outPath = MakeOutputPath(now);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var args = BuildEncodeArgs(_config.OutputFormat, _inputSampleRate, _inputChannels, outPath);

        try
        {
            var psi = new ProcessStartInfo(_ffmpegPath)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            _proc = Process.Start(psi);
            _currentFile = outPath;
            _openDate = DateOnly.FromDateTime(now);
            _openTime = now;
            _nextBoundary = OutputPathPlanner.ComputeNextBoundary(now, _config.FileDurationSeconds);
            _log.LogInformation("Открыт файл вывода: {Path} (следующая граница: {Boundary})", outPath, _nextBoundary);
        }
        catch (Win32Exception)
        {
            _log.LogError("ffmpeg не найден по пути {Path} — не удалось открыть файл {Out}", _ffmpegPath, outPath);
            _proc = null;
        }
    }

    private void CloseProc(bool crashed = false)
    {
        if (_proc == null) return;
        var ageSec = _openTime.HasValue ? (DateTime.Now - _openTime.Value).TotalSeconds : 0.0;

        try
        {
            _proc.StandardInput.Close();
            _proc.WaitForExit(10_000);
            var stderr = _proc.StandardError.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(stderr))
                _log.LogError("FFmpeg stderr: {Stderr}", stderr.Trim());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ошибка закрытия процесса ffmpeg");
            try { _proc.Kill(); } catch { }
        }
        finally
        {
            _proc.Dispose();
            _proc = null;
            _openTime = null;
        }

        if (crashed)
        {
            _crashCooldownUntil = DateTime.Now.AddSeconds(5);
            if (ageSec < 30)
                _log.LogWarning("Процесс вывода ffmpeg завершился через {Age:F1} с — пауза 5 с перед повторным открытием", ageSec);
        }
    }

    private string MakeOutputPath(DateTime dt)
    {
        var dateStr = OutputPathPlanner.FormatDate(dt, _config.DateFolderFormat);
        var timeStr = OutputPathPlanner.FormatTime(dt, _config.FileNameFormat);
        var ext = _config.OutputFormat.FileFormat;
        var folder = Path.Combine(_config.SavePath, dateStr);
        return Path.Combine(folder, $"{timeStr}.{ext}");
    }

    private void CleanupOldFiles()
    {
        if (_config.RetentionDays <= 0) return;
        if (!Directory.Exists(_config.SavePath)) return;

        var cutoff = DateOnly.FromDateTime(DateTime.Now.AddDays(-_config.RetentionDays));
        foreach (var folder in Directory.EnumerateDirectories(_config.SavePath).OrderBy(f => f))
        {
            var name = Path.GetFileName(folder);
            var folderDate = OutputPathPlanner.ParseDateFolder(name, _config.DateFolderFormat);
            if (folderDate is null || folderDate.Value >= cutoff) continue;

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                try { File.Delete(file); _log.LogDebug("Удалён старый файл: {File}", file); }
                catch (IOException ex) { _log.LogWarning(ex, "Не удалось удалить {File}", file); }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                    _log.LogDebug("Удалена пустая папка: {Folder}", folder);
                }
            }
            catch (IOException) { }
        }
    }

    internal static string[] BuildEncodeArgs(OutputFormatConfig fmt, int inputSampleRate, int inputChannels, string outPath)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "f32le",
            "-ar", inputSampleRate.ToString(),
            "-ac", inputChannels.ToString(),
            "-i", "pipe:0",
        };

        switch (fmt.FileFormat.ToLowerInvariant())
        {
            case "wav":
                args.Add("-acodec");
                args.Add(fmt.BitDepth == 24 ? "pcm_s24le" : "pcm_s16le");
                break;
            case "mp3":
                args.AddRange(new[] { "-acodec", "libmp3lame", "-b:a", $"{fmt.BitrateKbps}k" });
                break;
            case "aac":
                args.AddRange(new[] { "-acodec", "aac", "-b:a", $"{fmt.BitrateKbps}k" });
                break;
            default:
                throw new ArgumentException($"Unsupported file format: {fmt.FileFormat}");
        }

        if (fmt.Mode == "custom")
            args.AddRange(new[] { "-ar", fmt.SampleRate.ToString(), "-ac", fmt.Channels.ToString() });

        args.Add(outPath);
        return args.ToArray();
    }
}
```

Save as `Quince.Service/Audio/AudioWriter.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 31, Skipped: 0`

---

### Task 10: `EngineStatus` and `ChannelEngine`

**Files:**
- Create: `Quince.Service/Audio/EngineStatus.cs`
- Create: `Quince.Service/Audio/ChannelEngine.cs`

**Interfaces:**
- Consumes: `StreamCapture` (Task 8), `AudioWriter` (Task 9), `LevelMeter` (Task 6), `SilenceDetector` (Task 7), `Quince.Service.Configuration.ChannelConfig` (existing).
- Produces: `Quince.Service.Audio.EngineStatus` — `sealed record (bool IsRecording = false, int ReconnectAttempt = 0, bool IsSilent = false)`. `Quince.Service.Audio.ChannelEngine` — public constructor `(ChannelConfig config, string ffmpegPath, ILoggerFactory loggerFactory, Action<LevelReading> onLevelUpdate, Action<EngineStatus> onStatusChange)`; public `void Start()`, `void Stop()`, `void UpdateConfig(ChannelConfig newConfig)`, `EngineStatus Status`, `ChannelConfig Config`. Used by `AudioEngineManager` in Task 12.

This task has no dedicated unit tests — `ChannelEngine` is pure orchestration wiring already-tested components together via real ffmpeg subprocesses; it is verified in Task 14's manual end-to-end check. Build success is the acceptance bar for this task.

- [ ] **Step 1: Implement `EngineStatus`**

```csharp
namespace Quince.Service.Audio;

public sealed record EngineStatus(bool IsRecording = false, int ReconnectAttempt = 0, bool IsSilent = false);
```

Save as `Quince.Service/Audio/EngineStatus.cs`.

- [ ] **Step 2: Implement `ChannelEngine`**

Port of the stream half of `channel_engine.py` (soundcard branch and metadata pipeline are out of scope for this increment):

```csharp
using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Audio;

public sealed class ChannelEngine
{
    private readonly string _ffmpegPath;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Action<LevelReading> _onLevelUpdate;
    private readonly Action<EngineStatus> _onStatusChange;

    private ChannelConfig _config;
    private StreamCapture? _capture;
    private AudioWriter? _writer;
    private LevelMeter? _meter;
    private SilenceDetector? _silence;
    private EngineStatus _status = new();
    private bool _started;

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    public ChannelEngine(ChannelConfig config, string ffmpegPath, ILoggerFactory loggerFactory,
        Action<LevelReading> onLevelUpdate, Action<EngineStatus> onStatusChange)
    {
        _config = config;
        _ffmpegPath = ffmpegPath;
        _loggerFactory = loggerFactory;
        _onLevelUpdate = onLevelUpdate;
        _onStatusChange = onStatusChange;
    }

    public EngineStatus Status => _status;
    public ChannelConfig Config => _config;

    public void Start()
    {
        var log = _loggerFactory.CreateLogger("ChannelEngine");
        using var scope = log.BeginScope(new Dictionary<string, object> { ["Channel"] = _config.Name });

        _capture = new StreamCapture(_ffmpegPath, _config.Source.Url, _config.Source.StreamType,
            _config.Source.AllowInvalidSsl, _config.Source.HlsBitrateIndex, _config.Source.ReconnectDelaySeconds,
            _loggerFactory.CreateLogger("StreamCapture"));

        var meterReader = _capture.Subscribe("meter");

        if (_config.RecordAudio)
        {
            var writerReader = _capture.Subscribe("writer");
            _writer = new AudioWriter(_config, writerReader, StreamCapture.SampleRate, StreamCapture.Channels,
                _ffmpegPath, _loggerFactory.CreateLogger("AudioWriter"));
        }

        _meter = new LevelMeter(meterReader, StreamCapture.SampleRate, StreamCapture.Channels, _onLevelUpdate,
            _loggerFactory.CreateLogger("LevelMeter"));

        if (_config.SilenceDetector.Enabled)
        {
            var silenceReader = _capture.Subscribe("silence");
            _silence = new SilenceDetector(_config.SilenceDetector, silenceReader, OnSilence, OnSound,
                _loggerFactory.CreateLogger("SilenceDetector"));
        }

        _capture.Start();
        _writer?.Start();
        _meter.Start();
        _silence?.Start();

        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorAsync(_monitorCts.Token));

        _started = true;
        _status = new EngineStatus(IsRecording: true);
        _onStatusChange(_status);
        log.LogInformation("Запись начата");
    }

    public void Stop()
    {
        var log = _loggerFactory.CreateLogger("ChannelEngine");
        using var scope = log.BeginScope(new Dictionary<string, object> { ["Channel"] = _config.Name });

        _monitorCts?.Cancel();
        try { _monitorTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _monitorCts = null;
        _monitorTask = null;

        _silence?.Stop(); _silence = null;
        _meter?.Stop(); _meter = null;
        _writer?.Stop(); _writer = null;
        _capture?.Stop(); _capture = null;

        _started = false;
        _status = new EngineStatus(IsRecording: false);
        _onStatusChange(_status);
        log.LogInformation("Запись остановлена");
    }

    public void UpdateConfig(ChannelConfig newConfig)
    {
        var wasStarted = _started;
        if (!PipelineChanged(newConfig))
        {
            _config = newConfig;
            return;
        }
        Stop();
        _config = newConfig;
        if (wasStarted) Start();
    }

    private bool PipelineChanged(ChannelConfig newConfig)
    {
        var old = _config;
        return old.Source.Url != newConfig.Source.Url
            || old.Source.StreamType != newConfig.Source.StreamType
            || old.Source.AllowInvalidSsl != newConfig.Source.AllowInvalidSsl
            || old.Source.HlsBitrateIndex != newConfig.Source.HlsBitrateIndex
            || old.OutputFormat.FileFormat != newConfig.OutputFormat.FileFormat
            || old.OutputFormat.Mode != newConfig.OutputFormat.Mode
            || old.OutputFormat.SampleRate != newConfig.OutputFormat.SampleRate
            || old.OutputFormat.Channels != newConfig.OutputFormat.Channels
            || old.OutputFormat.BitrateKbps != newConfig.OutputFormat.BitrateKbps
            || old.OutputFormat.BitDepth != newConfig.OutputFormat.BitDepth
            || old.SavePath != newConfig.SavePath
            || old.FileDurationSeconds != newConfig.FileDurationSeconds
            || old.DateFolderFormat != newConfig.DateFolderFormat
            || old.FileNameFormat != newConfig.FileNameFormat;
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var attempt = _capture?.ReconnectAttempt ?? 0;
                if (attempt != _status.ReconnectAttempt)
                {
                    _status = _status with { IsRecording = _started, ReconnectAttempt = attempt };
                    _onStatusChange(_status);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnSilence()
    {
        _status = _status with { IsSilent = true };
        _onStatusChange(_status);
    }

    private void OnSound()
    {
        _status = _status with { IsSilent = false };
        _onStatusChange(_status);
    }
}
```

Save as `Quince.Service/Audio/ChannelEngine.cs`.

- [ ] **Step 3: Verify the solution builds**

Run: `dotnet build Quince.Service\Quince.Service.csproj`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

---

### Task 11: Bundle ffmpeg.exe

**Files:**
- Create: `Quince.Service/tools/ffmpeg.exe` (binary, fetched — not authored)
- Modify: `Quince.Service/Quince.Service.csproj`

**Interfaces:**
- Produces: `Quince.Service/tools/ffmpeg.exe`, copied to the build output directory. Consumed by `AudioEngineManager` (Task 12) via `PathResolver.Resolve(configuration["FfmpegPath"], "tools/ffmpeg.exe")`.

- [ ] **Step 1: Download the official Windows static build**

Run (PowerShell):

```powershell
New-Item -ItemType Directory -Force -Path "Quince.Service\tools" | Out-Null
Invoke-WebRequest -Uri "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile "$env:TEMP\ffmpeg-essentials.zip"
Expand-Archive -Path "$env:TEMP\ffmpeg-essentials.zip" -DestinationPath "$env:TEMP\ffmpeg-essentials" -Force
$exe = Get-ChildItem -Path "$env:TEMP\ffmpeg-essentials" -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
Copy-Item $exe.FullName "Quince.Service\tools\ffmpeg.exe" -Force
Remove-Item "$env:TEMP\ffmpeg-essentials.zip", "$env:TEMP\ffmpeg-essentials" -Recurse -Force
```

Expected: `Quince.Service\tools\ffmpeg.exe` exists.

If network access is unavailable in the execution environment, stop here and ask the user to supply `ffmpeg.exe` manually at that path — do not substitute an unverified binary from another source.

- [ ] **Step 2: Register the file as build content**

Add to `Quince.Service/Quince.Service.csproj`, in the existing `<ItemGroup>` that has the `config\**` content item:

```xml
    <Content Include="tools\**" CopyToOutputDirectory="PreserveNewest" />
```

(So that `<ItemGroup>` now contains both the `config\**` and `tools\**` lines.)

- [ ] **Step 3: Verify it's copied on build**

Run: `dotnet build Quince.Service\Quince.Service.csproj`
Then check: `Test-Path Quince.Service\bin\Debug\net8.0-windows\tools\ffmpeg.exe`
Expected: `True`

- [ ] **Step 4: Sanity-check the binary runs**

Run: `& "Quince.Service\bin\Debug\net8.0-windows\tools\ffmpeg.exe" -version`
Expected: prints an `ffmpeg version ...` banner and exits 0.

---

### Task 12: `AudioEngineManager` and DI wiring

**Files:**
- Create: `Quince.Service/Services/AudioEngineManager.cs`
- Modify: `Quince.Service/Program.cs`

**Interfaces:**
- Consumes: `ChannelEngine` (Task 10), `Quince.Service.Services.ChannelManager` (existing, `IReadOnlyList<ChannelConfig> Channels`), `Quince.Service.Hubs.LevelHub` (existing), `Quince.Service.Configuration.PathResolver` (existing).
- Produces: `Quince.Service.Services.AudioEngineManager` — `IHostedService`; public `void Start(string channelName)`, `void Stop(string channelName)`, `EngineStatus? GetStatus(string channelName)`. Used by `LevelHub` (Task 13) and `ChannelCard.razor` (Task 13).

- [ ] **Step 1: Implement `AudioEngineManager`**

```csharp
using Microsoft.AspNetCore.SignalR;
using Quince.Service.Audio;
using Quince.Service.Configuration;
using Quince.Service.Hubs;

namespace Quince.Service.Services;

public class AudioEngineManager : IHostedService
{
    private readonly ChannelManager _channelManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHubContext<LevelHub> _hub;
    private readonly string _ffmpegPath;

    private readonly Dictionary<string, ChannelEngine> _engines = new();
    private readonly object _lock = new();

    public AudioEngineManager(ChannelManager channelManager, ILoggerFactory loggerFactory,
        IHubContext<LevelHub> hub, IConfiguration configuration)
    {
        _channelManager = channelManager;
        _loggerFactory = loggerFactory;
        _hub = hub;
        _ffmpegPath = PathResolver.Resolve(configuration["FfmpegPath"], "tools/ffmpeg.exe");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Runs after ChannelManager.StartAsync (registered later in Program.cs — the
        // generic host awaits hosted services' StartAsync in registration order), so
        // _channelManager.Channels is already populated here.
        foreach (var config in _channelManager.Channels)
        {
            if (config.Source.Type == "stream" && config.AutoStart)
                Start(config.Name);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var engine in _engines.Values) engine.Stop();
            _engines.Clear();
        }
        return Task.CompletedTask;
    }

    public EngineStatus? GetStatus(string channelName)
    {
        lock (_lock)
        {
            return _engines.TryGetValue(channelName, out var engine) ? engine.Status : null;
        }
    }

    public void Start(string channelName)
    {
        lock (_lock)
        {
            if (_engines.ContainsKey(channelName)) return;

            var config = _channelManager.Channels.FirstOrDefault(c => c.Name == channelName);
            if (config == null || config.Source.Type != "stream") return;

            var engine = new ChannelEngine(config, _ffmpegPath, _loggerFactory,
                reading => PushLevel(channelName, reading),
                status => PushStatus(channelName, status));

            _engines[channelName] = engine;
            engine.Start();
        }
    }

    public void Stop(string channelName)
    {
        ChannelEngine? engine;
        lock (_lock)
        {
            if (!_engines.TryGetValue(channelName, out engine)) return;
            _engines.Remove(channelName);
        }
        engine.Stop();
    }

    private void PushLevel(string channelName, LevelReading reading)
    {
        _ = _hub.Clients.Group(channelName).SendAsync("LevelUpdate", reading);
    }

    private void PushStatus(string channelName, EngineStatus status)
    {
        _ = _hub.Clients.Group(channelName).SendAsync("StatusUpdate", status);
    }
}
```

Save as `Quince.Service/Services/AudioEngineManager.cs`.

- [ ] **Step 2: Register it in `Program.cs`**

In `Quince.Service/Program.cs`, immediately after the existing lines:

```csharp
builder.Services.AddSingleton<ChannelManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChannelManager>());
```

add:

```csharp
builder.Services.AddSingleton<AudioEngineManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioEngineManager>());
```

`AudioEngineManager` must be registered **after** `ChannelManager` — the generic host starts hosted services in registration order and awaits each `StartAsync` before starting the next, so this ordering guarantees channels are loaded before `AudioEngineManager` reads them.

- [ ] **Step 3: Verify the app builds and starts**

Run: `dotnet build Quince.Service\Quince.Service.csproj`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

Run: `dotnet run --project Quince.Service\Quince.Service.csproj` (then Ctrl+C after confirming startup)
Expected: log output includes `Айва (Quince) запущена` and no unhandled exceptions; no channels with `auto_start: true` and `source.type: stream` exist yet in the shipped configs, so no engines should actually start during this smoke check — confirm via `log\<today>.log` that no `Запись начата` lines appear.

---

### Task 13: UI wiring — `LevelHub`, `ChannelCard.razor`, CSS

**Files:**
- Modify: `Quince.Service/Hubs/LevelHub.cs`
- Modify: `Quince.Service/Pages/Shared/ChannelCard.razor`
- Modify: `Quince.Service/wwwroot/app.css`

**Interfaces:**
- Consumes: `AudioEngineManager` (Task 12), `EngineStatus`/`LevelReading` (Tasks 6, 10).

- [ ] **Step 1: Push current status to newly-subscribed clients in `LevelHub`**

Replace the contents of `Quince.Service/Hubs/LevelHub.cs`:

```csharp
using Microsoft.AspNetCore.SignalR;
using Quince.Service.Services;

namespace Quince.Service.Hubs;

public class LevelHub : Hub
{
    private readonly AudioEngineManager _engineManager;

    public LevelHub(AudioEngineManager engineManager)
    {
        _engineManager = engineManager;
    }

    public async Task Subscribe(string channelId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, channelId);
        var status = _engineManager.GetStatus(channelId);
        if (status is not null)
            await Clients.Caller.SendAsync("StatusUpdate", status);
    }

    public async Task Unsubscribe(string channelId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, channelId);
    }
}
```

(This closes a real gap: without it, a browser opening the page *after* a channel auto-started would show a stale "Старт"/stopped state until the next reconnect/silence event happened to fire.)

- [ ] **Step 2: Wire the Start/Stop button, status dots, and live level bar in `ChannelCard.razor`**

Replace the contents of `Quince.Service/Pages/Shared/ChannelCard.razor`:

```razor
@using Quince.Service.Configuration
@using Quince.Service.Services
@using Quince.Service.Audio
@using Microsoft.AspNetCore.SignalR.Client
@inject AudioEngineManager EngineManager
@inject NavigationManager Nav
@implements IAsyncDisposable

<div class="channel-card">
    <div class="channel-row1">
        <div class="status-dot @StatusDotClass"></div>
        @if (Config.Source.Type == "stream")
        {
            <div class="status-dot @SilenceDotClass"></div>
        }
        <div class="channel-name" title="@Config.Name">@Config.Name</div>
        @if (_status.ReconnectAttempt > 0)
        {
            <div class="reconnect-label">Реконнект #@_status.ReconnectAttempt</div>
        }
        <div class="spacer"></div>
        <button class="btn @(_status.IsRecording ? "btn-stop" : "btn-start")"
                title="@(Config.Source.Type == "stream" ? (_status.IsRecording ? "Остановить запись" : "Начать запись") : "Звуковая карта пока не реализована")"
                disabled="@(Config.Source.Type != "stream")"
                @onclick="ToggleRecording">
            @(_status.IsRecording ? "Стоп" : "Старт")
        </button>
        <button class="btn btn-small" title="Редактировать настройки канала">✎</button>
        <button class="btn btn-small" title="Клонировать канал">⧉</button>
        <button class="btn btn-meters" title="Открыть окно индикаторов уровня">▦ Индикаторы</button>
        <button class="btn btn-delete" title="Удалить канал">✕</button>
    </div>
    <div class="channel-row2">Вход: @ChannelDisplayFormatter.FormatSource(Config)</div>
    <div class="channel-row3">Файл: @ChannelDisplayFormatter.FormatOutput(Config)</div>
    <div class="level-widget">
        <div class="level-row">
            <span class="level-label">TP</span>
            <div class="level-track"><div class="level-fill" style="width: @(LevelFillPercent)%"></div></div>
            <span class="level-value">@LevelValueText</span>
        </div>
    </div>
</div>

@code {
    [Parameter, EditorRequired]
    public ChannelConfig Config { get; set; } = null!;

    private EngineStatus _status = new();
    private LevelReading _level = new();
    private HubConnection? _hub;

    private string StatusDotClass => _status switch
    {
        { IsRecording: false } => "",
        { ReconnectAttempt: > 0 } => "dot-yellow",
        _ => "dot-green",
    };

    private string SilenceDotClass => _status.IsSilent ? "dot-purple" : "";

    private double LevelFillPercent => double.IsNegativeInfinity(_level.TruePeakDb)
        ? 0
        : Math.Clamp((_level.TruePeakDb + 60.0) / 60.0 * 100.0, 0, 100);

    private string LevelValueText => double.IsNegativeInfinity(_level.TruePeakDb) ? "—" : $"{_level.TruePeakDb:F1} dB";

    protected override async Task OnInitializedAsync()
    {
        if (Config.Source.Type != "stream") return;

        _hub = new HubConnectionBuilder()
            .WithUrl(Nav.ToAbsoluteUri("/hubs/level"))
            .Build();

        _hub.On<LevelReading>("LevelUpdate", reading =>
        {
            _level = reading;
            InvokeAsync(StateHasChanged);
        });
        _hub.On<EngineStatus>("StatusUpdate", status =>
        {
            _status = status;
            InvokeAsync(StateHasChanged);
        });

        await _hub.StartAsync();
        await _hub.SendAsync("Subscribe", Config.Name);
    }

    private void ToggleRecording()
    {
        if (Config.Source.Type != "stream") return;
        if (_status.IsRecording)
            EngineManager.Stop(Config.Name);
        else
            EngineManager.Start(Config.Name);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.SendAsync("Unsubscribe", Config.Name);
            await _hub.DisposeAsync();
        }
    }
}
```

- [ ] **Step 3: Add disabled-button and reconnect-pulse styles**

Add to `Quince.Service/wwwroot/app.css`, after the existing `.status-dot.dot-purple { background: #7c3aed; }` rule (around line 299):

```css
@keyframes qc-status-pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.4; }
}

.status-dot.dot-yellow {
    animation: qc-status-pulse 1s ease-in-out infinite;
}
```

Add after the existing `.btn-meters { ... }` rule (around line 356):

```css
.btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
}
```

- [ ] **Step 4: Verify the app builds**

Run: `dotnet build Quince.Service\Quince.Service.csproj`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

---

### Task 14: Manual end-to-end verification

**Files:** none (verification only).

- [ ] **Step 1: Pick a real, currently-live stream channel config**

Run: `Get-ChildItem Quince.Service\config\RP\*.yaml | Select-Object -First 5 -ExpandProperty Name`

Pick one and open it to confirm `source.type: stream`, note its `save_path` (change it to a writable local test folder for this check, e.g. `M:\Users\twelve\Projects\quince-dotnet\_manual-test-recording`, so you don't need production disk paths to exist).

- [ ] **Step 2: Run the app**

Run: `dotnet run --project Quince.Service\Quince.Service.csproj`
Expected: console shows the app listening on `http://localhost:5000`.

- [ ] **Step 3: Start the channel from the browser**

Open `http://localhost:5000`, find the channel card, click **Старт**.
Expected: button switches to **Стоп** (red), status dot turns green within ~1s.

- [ ] **Step 4: Confirm a file is being written**

Wait a few seconds, then run:
Run: `Get-ChildItem "_manual-test-recording" -Recurse`
Expected: a folder named for today's date (per `date_folder_format`) containing a growing audio file matching `output_format.file_format`.

- [ ] **Step 5: Confirm the live level bar moves**

Expected: the TP bar/value on the channel card updates roughly every 100ms and shows a value other than "—" while audio is playing.

- [ ] **Step 6: Stop and confirm clean shutdown**

Click **Стоп**.
Expected: button reverts to **Старт** (green), dot turns grey, the ffmpeg process for that channel is no longer in Task Manager, and the audio file stops growing.

- [ ] **Step 7: Confirm log output matches the required format**

Run: `Get-Content "Quince.Service\log\$(Get-Date -Format yyyy-MM-dd).log" -Tail 30`
Expected: lines matching `YYYY-MM-DD HH:MM:SS.mmm [LEVEL] [<channel name>] message`, including at least "Запись начата", "Открыт файл вывода", "Запись остановлена".

- [ ] **Step 8: Clean up the test recording folder**

Run: `Remove-Item "_manual-test-recording" -Recurse -Force`

---

### Task 15: Docs, version bump, release publish

**Files:**
- Modify: `Quince.Service/VersionInfo.cs`
- Modify: `HISTORY.md`
- Modify: `CHANGELOG.md`
- Modify: `README.md`
- Create: `release/0.00.002/` (published build output)

**Interfaces:** none — documentation and release packaging only.

- [ ] **Step 1: Bump the patch version**

In `Quince.Service/VersionInfo.cs`, change:

```csharp
public const string Version = "0.00.001";
```

to:

```csharp
public const string Version = "0.00.002";
```

- [ ] **Step 2: Append to `HISTORY.md`**

Add a new `## 13` entry at the end of `HISTORY.md`, following the file's existing format (user's request verbatim, then a brief summary of the response) — the request text is whatever the user actually typed to kick off this work in the live conversation, and the summary should describe: implemented the stream-channel audio engine (`StreamCapture`, `AudioWriter`, `LevelMeter`, `SilenceDetector`, `ChannelEngine`, `AudioEngineManager`), wired Start/Stop + live status/level into the UI, bundled `ffmpeg.exe`, added `Quince.Service.Tests` with unit coverage for the pure-logic pieces.

- [ ] **Step 3: Add a `CHANGELOG.md` entry**

Add above the existing `## 0.00.001` section:

```markdown
## 0.00.002 — аудио-движок для stream-каналов

- Реализован полный конвейер записи для каналов `source.type: stream` (Icecast/HLS): `StreamCapture` (ffmpeg-подпроцесс, декодирование, реконнект), `AudioWriter` (ротация по временной сетке, retention), `LevelMeter` (True Peak + LUFS, EBU R128), `SilenceDetector`.
- Новый `AudioEngineManager` (`IHostedService`) управляет движком по каналам, авто-старт для `auto_start: true`.
- UI: кнопка Старт/Стоп теперь реально запускает/останавливает запись, статус-точки и индикатор уровня отражают реальное состояние (через SignalR `LevelHub`). Для каналов `soundcard` кнопка задизейблена — этот тип источника ещё не реализован.
- Забандлен `ffmpeg.exe` в `Quince.Service/tools/` (официальная статическая сборка), путь настраивается через `FfmpegPath`.
- Добавлен проект `Quince.Service.Tests` (xUnit) с покрытием чисто логических частей движка.

**Известные ограничения**: звуковая карта (`source.type: soundcard`) и метаданные потока (ICY/HLS — название трека) всё ещё не реализованы; окно индикаторов (▦) и редактирование/клонирование/удаление канала остаются визуальными заглушками.
```

- [ ] **Step 4: Update `README.md`**

In the "Известные ограничения" section, replace the line about the recording engine not being implemented with the new, narrower limitation (soundcard + metadata only), matching the CHANGELOG wording above. Update the version reference at the top of the file to `0.00.002`.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test Quince.Service.Tests\Quince.Service.Tests.csproj`
Expected: all tests pass (31+ from Tasks 1–9).

- [ ] **Step 6: Publish the release build**

Run: `dotnet publish Quince.Service\Quince.Service.csproj -c Release -o release\0.00.002`
Expected: build succeeds; `release\0.00.002\Quince.Service.exe` and `release\0.00.002\tools\ffmpeg.exe` both exist.

---

## Self-Review

**Spec coverage:** `StreamCapture` (Task 8), `AudioWriter` (Task 9), `LevelMeter`/K-weighting/True-Peak/LUFS (Tasks 3–6), `SilenceDetector` (Task 7), `ChannelEngine`/`AudioEngineManager` orchestration (Tasks 10, 12), logging events (wired inline in Tasks 8–10 via `ILogger.BeginScope`), UI wiring (Task 13), ffmpeg bundling (Task 11), testing (Tasks 1–9, 14), docs/version/release (Task 15) — every spec section maps to a task.

**Placeholder scan:** no TBD/TODO markers; every step has complete, runnable code or an exact command with expected output.

**Type consistency:** `AudioChunk`, `EngineStatus`, `LevelReading` signatures verified identical across every task that consumes them (Tasks 6, 7, 8, 9, 10, 12, 13 all use the same constructor/property names introduced in Tasks 2, 6, 10).

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-05-audio-engine-stream-pipeline.md`. Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration
2. **Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
