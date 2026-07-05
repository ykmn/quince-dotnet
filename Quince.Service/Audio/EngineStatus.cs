namespace Quince.Service.Audio;

public sealed record EngineStatus(bool IsRecording = false, int ReconnectAttempt = 0, bool IsSilent = false);
