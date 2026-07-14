namespace Quince.Service.Audio;

/// <summary>
/// Rolling window of the most recently seen <see cref="AudioChunk"/>s, capped by total duration
/// rather than chunk count (real feeds jitter in per-chunk sample count). Used by
/// <see cref="AudioWriter"/> while recording is paused for confirmed silence: every chunk that
/// arrives during the pause is buffered here, trimmed to the last <c>windowSeconds</c>, so that once
/// the silence detector confirms sound has returned, <see cref="DrainAll"/> yields exactly the audio
/// from just before that confirmation instant to backfill into the file — see docs/HISTORY.md #142
/// for the exact timing contract this satisfies (recording resumes from
/// <c>2 * resume_seconds</c> before the confirmed-recovered instant, i.e. <c>resume_seconds</c>
/// before the actual physical return of sound).
/// </summary>
internal sealed class BackfillBuffer
{
    private readonly double _windowSeconds;
    private readonly int _sampleRate;
    private readonly Queue<AudioChunk> _chunks = new();
    private double _bufferedSeconds;

    public BackfillBuffer(double windowSeconds, int sampleRate)
    {
        _windowSeconds = windowSeconds;
        _sampleRate = sampleRate;
    }

    public void Enqueue(AudioChunk chunk)
    {
        if (_windowSeconds <= 0 || _sampleRate <= 0) return;

        _chunks.Enqueue(chunk);
        _bufferedSeconds += chunk.FrameCount / (double)_sampleRate;
        while (_bufferedSeconds > _windowSeconds && _chunks.Count > 0)
        {
            var oldest = _chunks.Dequeue();
            _bufferedSeconds -= oldest.FrameCount / (double)_sampleRate;
        }
    }

    /// <summary>Returns every currently buffered chunk in arrival order and empties the buffer.</summary>
    public IReadOnlyList<AudioChunk> DrainAll()
    {
        if (_chunks.Count == 0) return Array.Empty<AudioChunk>();
        var result = _chunks.ToArray();
        _chunks.Clear();
        _bufferedSeconds = 0;
        return result;
    }
}
