namespace CD.Core.Models;

public class DiscImage
{
    public List<DiscTrack> Tracks { get; set; } = [];

    public int TrackCount => Tracks.Count;

    public bool IsSingleTrack => TrackCount == 1;
}

public class DiscTrack
{
    public int Number { get; set; }

    public string DataType { get; set; } = string.Empty;

    public int PregapSectors { get; set; }

    public int LengthSectors { get; set; }

    public int TotalSectors => PregapSectors + LengthSectors;

    public Func<Stream> OpenSectorStream { get; set; } = null!;

    public int SourceSectorSize { get; set; } = 2352;

    public int SubchannelSize { get; set; }
}