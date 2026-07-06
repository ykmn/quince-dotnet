namespace Quince.Service.Audio;

/// <summary>Common shape of <see cref="IcecastMetadataReader"/> and <see cref="HlsMetadataReader"/>
/// so <see cref="ChannelEngine"/> can start/stop whichever one a channel's metadata mode selects
/// without caring which.</summary>
public interface IMetadataReader
{
    void Start();
    void Stop();

    /// <summary>True once metadata has actually been confirmed for this stream (ICY metaint
    /// present, or an HLS JSON endpoint / ID3 tag found). Stays false if the reader is still
    /// trying, or has given up without ever finding metadata.</summary>
    bool HasMetadata { get; }
}
