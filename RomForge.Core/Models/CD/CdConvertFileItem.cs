using Common.WPF.ViewModels;
using System.Windows.Media;

namespace RomForge.Core.Models.CD;

public enum CdSourceFormat
{
    Unknown,
    MdfMds,
}

public class CdConvertFileItem(string filePath) : FileItemBase(filePath)
{
    public CdSourceFormat SourceFormat => Extension switch
    {
        "mds" => CdSourceFormat.MdfMds,
        _ => CdSourceFormat.Unknown
    };

    public string ExtensionLabel => SourceFormat switch
    {
        CdSourceFormat.MdfMds => $"{Extension}→cue",
        _ => Extension
    };

    public Brush ExtensionBackground => ExtensionColorMap.Resolve(Extension, ColorMap);

    private static readonly Dictionary<string, string> ColorMap = new()
    {
        ["mds"] = "#EAE2A6",
        ["mdf"] = "#D2DAA5",
    };

    protected override string FormatSize(long bytes) => PickPack.Disk.ETC.FileSize.FormatSize(bytes);
}