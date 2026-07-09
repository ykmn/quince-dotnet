namespace Quince.Service.Audio;

/// <summary><c>IsRecording</c>: the capture pipeline is running for any reason — including a
/// temporary auto-start purely to serve browser listen-in on a channel the user hasn't pressed
/// "Start recording" on (see <see cref="ChannelEngine.Start"/>'s <c>suppressRecording</c>). Drives
/// the level meter/status-dot "is anything live" UI. <c>IsFileRecording</c>: true only when audio is
/// actually being written to disk right now (an <see cref="AudioWriter"/> exists) — drives the
/// Start/Stop RECORDING button specifically, so listening to a stopped channel no longer makes that
/// button falsely flip to "Stop recording" (docs/HISTORY.md #64).
/// <c>MetadataOk</c>: null if the channel has no metadata URL configured (nothing to
/// check), true once metadata has been detected, false if a metadata URL is configured but
/// nothing has been detected after a grace period — this is what the UI's health dot reports.
/// <c>HasError</c>: set (alongside <c>IsRecording: false</c>) when the channel stopped itself
/// because it exhausted its reconnect-attempt budget, as opposed to a deliberate user stop —
/// stays true until the channel is started again.</summary>
public sealed record EngineStatus(bool IsRecording = false, bool IsFileRecording = false, int ReconnectAttempt = 0, bool IsSilent = false, bool? MetadataOk = null, bool HasError = false);
