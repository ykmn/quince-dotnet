namespace Quince.Service.Audio;

/// <summary>One "now playing" metadata change, as reported by <see cref="IcecastMetadataReader"/>
/// or <see cref="HlsMetadataReader"/>. Mirrors the legacy Python port's <c>MetadataEvent</c>
/// dataclass (<c>src/audio/metadata_icecast.py</c>).</summary>
public sealed record MetadataEvent(string Raw, string Artist, string Title, DateTimeOffset Timestamp);
