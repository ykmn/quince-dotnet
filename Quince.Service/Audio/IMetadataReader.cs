namespace Quince.Service.Audio;

/// <summary>Common shape of <see cref="IcecastMetadataReader"/> and <see cref="HlsMetadataReader"/>
/// so <see cref="ChannelEngine"/> can start/stop whichever one a channel's metadata mode selects
/// without caring which.</summary>
public interface IMetadataReader
{
    void Start();
    void Stop();
}
