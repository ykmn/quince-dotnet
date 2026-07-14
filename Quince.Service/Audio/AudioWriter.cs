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
    private readonly System.Text.StringBuilder _stderrBuffer = new();
    private readonly object _stderrLock = new();
    private string? _currentFile;
    private DateTime? _nextBoundary;
    private DateOnly? _openDate;
    private DateTime? _openTime;
    private DateTime? _crashCooldownUntil;

    // Silence-detector-driven stop/resume (docs/HISTORY.md #142, redesigned #144): on confirmed
    // silence the current file is closed/saved right away rather than left open-but-idle (an
    // idle-open ffmpeg file was the #144 bug — production channels never produced a finished file
    // across a silence period). While stopped, incoming chunks are diverted into _backfillBuffer.
    // On confirmed resume a NEW file is opened, backdated by 2*ResumeSeconds so its content (and
    // name) start resume_seconds before the actual physical return of sound, then the buffered
    // window is flushed into it before live chunks. _wasRecordingActive is read/written only from
    // RunAsync's own loop (never touched by SetRecordingActive, called off the SilenceDetector's
    // thread) so no lock is needed to detect the stop<->active edges.
    private volatile bool _recordingActive = true;
    private bool _wasRecordingActive = true;
    private readonly BackfillBuffer _backfillBuffer;

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
        // 2x resume_seconds so recording backfills from resume_seconds before the actual physical
        // return of sound, not just from the (later) confirmed-recovered instant — see BackfillBuffer.
        _backfillBuffer = new BackfillBuffer(2 * config.SilenceDetector.ResumeSeconds, _inputSampleRate);
    }

    public string? CurrentFile => _currentFile;
    public bool IsRunning => _task is { IsCompleted: false };

    /// <summary>OS process ID of the currently-open output ffmpeg process, for the admin "Монитор
    /// ресурсов" dialog (<see cref="Services.ProcessMonitorService"/>) — null between files (rotation)
    /// or while stopped. Guards against the narrow race in <see cref="CloseProc"/> where <c>_proc</c>
    /// is disposed a moment before the field itself is nulled out (unlike <see cref="FfmpegPipedCapture"/>,
    /// which nulls its field first) — this property can be read from the monitor's own polling thread
    /// at any time, not just from this writer's single-threaded run loop.</summary>
    public int? ProcessId
    {
        get
        {
            try { return _proc?.Id; }
            catch (InvalidOperationException) { return null; }
        }
    }

    /// <summary>Called by <see cref="ChannelEngine"/> when its <see cref="SilenceDetector"/> fires
    /// onSilence/onSound. Going inactive closes/saves the current file on the next chunk; while
    /// inactive, chunks are buffered (not written) so a subsequent resume can open a new file that
    /// backfills from just before sound actually returned. A no-op if the channel has no silence
    /// detector (never called in that case, so recording just always stays active).</summary>
    public void SetRecordingActive(bool active) => _recordingActive = active;

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
                if (!_recordingActive)
                {
                    if (_wasRecordingActive)
                    {
                        // Falling edge: silence confirmed — close/save the current file right away
                        // instead of leaving it open-but-idle through the whole silent period.
                        var closedPath = _currentFile;
                        CloseProc();
                        _wasRecordingActive = false;
                        _log.LogInformation("Тишина: запись остановлена, файл сохранён: {Path}", closedPath);
                    }
                    _backfillBuffer.Enqueue(chunk);
                    continue;
                }

                if (_wasRecordingActive) MaybeRotate();

                if (_proc == null)
                {
                    var now = DateTime.Now;
                    if (_crashCooldownUntil.HasValue && now < _crashCooldownUntil.Value)
                    {
                        if (!_wasRecordingActive) _backfillBuffer.Enqueue(chunk);
                        continue;
                    }

                    if (_wasRecordingActive)
                    {
                        OpenProc(now);
                    }
                    else
                    {
                        // Resuming: name the new file resume_seconds before the actual physical
                        // return of sound, i.e. 2*ResumeSeconds before this confirmed-recovery
                        // instant — the trailing window BackfillBuffer holds exactly that span.
                        var nameTime = now.AddSeconds(-2 * _config.SilenceDetector.ResumeSeconds);
                        OpenProc(now, nameTime);
                    }
                }

                if (_proc == null)
                {
                    if (!_wasRecordingActive) _backfillBuffer.Enqueue(chunk);
                    continue;
                }

                if (!_wasRecordingActive)
                {
                    // Just opened the resume file: flush the trailing window buffered during the
                    // stop before this chunk, so the file backfills from just before sound returned.
                    foreach (var buffered in _backfillBuffer.DrainAll())
                        await WriteChunkAsync(buffered, ct);
                    _wasRecordingActive = true;
                }

                await WriteChunkAsync(chunk, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            CloseProc();
        }
    }

    private async Task WriteChunkAsync(AudioChunk chunk, CancellationToken ct)
    {
        if (_proc == null) return;
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

    /// <param name="now">Real wall-clock time the process is actually opened at — drives rotation
    /// tracking (<see cref="_openDate"/>/<see cref="_openTime"/>/<see cref="_nextBoundary"/>).</param>
    /// <param name="nameTime">Timestamp the output filename is derived from, if it should differ
    /// from <paramref name="now"/> — used on silence-resume to backdate the new file's name to
    /// resume_seconds before sound actually returned. Defaults to <paramref name="now"/>.</param>
    private void OpenProc(DateTime now, DateTime? nameTime = null)
    {
        _crashCooldownUntil = null;
        var outPath = MakeOutputPath(nameTime ?? now);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var args = BuildEncodeArgs(ResolveEffectiveFormat(_config), _inputSampleRate, _inputChannels, outPath);

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
            lock (_stderrLock) { _stderrBuffer.Clear(); }
            if (_proc != null) _ = DrainStderrAsync(_proc);
            _currentFile = outPath;
            _openDate = DateOnly.FromDateTime(now);
            _openTime = now;
            _nextBoundary = OutputPathPlanner.ComputeNextBoundary(now, _config.FileDurationMinutes * 60);
            _log.LogInformation("Открыт файл вывода: {Path} (следующая граница: {Boundary})", outPath, _nextBoundary);
        }
        catch (Win32Exception)
        {
            _log.LogError("ffmpeg не найден по пути {Path} — не удалось открыть файл {Out}", _ffmpegPath, outPath);
            _crashCooldownUntil = DateTime.Now.AddSeconds(5);
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

            string stderr;
            lock (_stderrLock) { stderr = _stderrBuffer.ToString(); }
            if (!string.IsNullOrWhiteSpace(stderr))
                _log.LogError("FFmpeg stderr: {Stderr}", stderr.Trim());
        }
    }

    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            var reader = process.StandardError;
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                _log.LogDebug("ffmpeg (writer) stderr: {Line}", line);
                lock (_stderrLock)
                {
                    _stderrBuffer.AppendLine(line);
                    // Bound growth in case of a very chatty/long-running process.
                    if (_stderrBuffer.Length > 16_384)
                        _stderrBuffer.Remove(0, _stderrBuffer.Length - 16_384);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Ошибка чтения stderr ffmpeg (writer)");
        }
    }

    private string MakeOutputPath(DateTime dt)
    {
        var dateStr = OutputPathPlanner.FormatDate(dt, _config.DateFolderFormat);
        var timeStr = OutputPathPlanner.FormatTime(dt, _config.FileNameFormat);
        var ext = ResolveEffectiveFormat(_config).FileFormat;
        var folder = Path.Combine(_config.SavePath, dateStr);
        return Path.Combine(folder, $"{timeStr}.{ext}");
    }

    /// <summary>
    /// "Как во входном потоке" (Mode == "original") previously did nothing but skip the explicit
    /// sample-rate/channel override — it still saved through whatever <c>FileFormat</c> happened to
    /// be configured (defaulting to "mp3" for new channels), so an HLS/AAC source silently got
    /// transcoded to MP3 instead of matching the source. This picks the codec/extension from the
    /// actual source instead: soundcard capture is raw PCM, so it's saved as WAV; HLS audio is
    /// virtually always AAC; Icecast (plain or MP3) is saved as MP3. In "custom" mode the
    /// user-chosen <see cref="OutputFormatConfig.FileFormat"/> is used unchanged.
    /// </summary>
    internal static OutputFormatConfig ResolveEffectiveFormat(ChannelConfig config)
    {
        var fmt = config.OutputFormat;
        if (fmt.Mode != "original") return fmt;

        var originalFileFormat = config.Source.Type switch
        {
            // Both are raw PCM at the capture backend's own native rate/channels — WAV keeps that
            // lossless, matching this app's "only ever save through an explicit ffmpeg encode, never
            // a raw byte passthrough" architecture (AudioWriter always pipes f32le through ffmpeg
            // regardless of format) while still defaulting to no lossy transcoding for these sources.
            "soundcard" or "livewire" => "wav",
            _ => config.Source.StreamType switch
            {
                "hls" => "aac",
                _ => "mp3", // icecast, icecast_mp3
            },
        };

        return new OutputFormatConfig
        {
            Mode = fmt.Mode,
            FileFormat = originalFileFormat,
            SampleRate = fmt.SampleRate,
            BitDepth = fmt.BitDepth,
            Channels = fmt.Channels,
            BitrateKbps = fmt.BitrateKbps,
        };
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
            // Without this, ffmpeg can't prompt for overwrite confirmation (stdin here is the raw
            // PCM pipe, not a terminal) and just exits immediately with "File already exists" if the
            // output path is ever reused (e.g. an orphaned ffmpeg from a not-fully-clean previous
            // shutdown still holding a same-second filename) — silently dropping several seconds of
            // audio for a 24/7 recorder every time it happens (docs/HISTORY.md #136). Always overwrite
            // instead: the filename is always freshly derived from the current timestamp, so a
            // collision only ever means stale/incomplete data from an aborted process, never real
            // content worth preserving over continuity.
            "-y",
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
