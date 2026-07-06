namespace Quince.Service.Audio;

/// <summary><c>MetadataOk</c>: null if the channel has no metadata URL configured (nothing to
/// check), true once metadata has been detected, false if a metadata URL is configured but
/// nothing has been detected after a grace period — this is what the UI's health dot reports.</summary>
public sealed record EngineStatus(bool IsRecording = false, int ReconnectAttempt = 0, bool IsSilent = false, bool? MetadataOk = null);
