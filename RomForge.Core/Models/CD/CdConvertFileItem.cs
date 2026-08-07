using Common.WPF.ViewModels;
using System.Windows.Media;

namespace RomForge.Core.Models.CD;

public enum CdSourceFormat
{
    Unknown,
    MdfMds,

    // 확장 예정: CcdImgSub (CCD+IMG+SUB → BIN+CUE)
}

// CD.Core의 실제 변환 엔진(BinCueWriter/IsoWriter)에 대응하는 UI용 출력 포맷 선택지.
// RomForge.Core는 CD.Core를 참조하지 않으므로 여기서는 UI 바인딩용 얇은 enum만 두고,
// 실제 라이터 호출 분기는 CD.Core를 참조하는 ViewModel(CdConvertMainViewModel)에서 담당한다.
public enum CdOutputFormat
{
    BinCue,
    Iso,
}

public class CdConvertFileItem(string filePath) : FileItemBase(filePath)
{
    public CdSourceFormat SourceFormat => Extension switch
    {
        "mds" => CdSourceFormat.MdfMds,
        _ => CdSourceFormat.Unknown
    };

    private int _trackCount;

    // MDS 메타데이터를 미리 파싱해 트랙 수를 설정해두면(ViewModel에서 AddPaths 시 채움),
    // 멀티트랙 이미지에서 ISO 선택지를 막을 수 있다. 아직 파싱 전이거나 실패하면 0(알 수 없음).
    public int TrackCount
    {
        get => _trackCount;
        set
        {
            if (SetProperty(ref _trackCount, value))
            {
                OnPropertyChanged(nameof(IsMultiTrack));
                OnPropertyChanged(nameof(AvailableOutputFormats));

                // 멀티트랙으로 확인됐는데 ISO가 선택되어 있었다면 BIN+CUE로 되돌린다.
                if (IsMultiTrack && OutputFormat == CdOutputFormat.Iso)
                    OutputFormat = CdOutputFormat.BinCue;
            }
        }
    }

    public bool IsMultiTrack => TrackCount > 1;

    // 단일 트랙이면 BIN+CUE / ISO 둘 다 가능, 멀티트랙이면 BIN+CUE만 가능.
    public IReadOnlyList<CdOutputFormat> AvailableOutputFormats => IsMultiTrack
        ? [CdOutputFormat.BinCue]
        : [CdOutputFormat.BinCue, CdOutputFormat.Iso];

    private CdOutputFormat _outputFormat = CdOutputFormat.BinCue;

    public CdOutputFormat OutputFormat
    {
        get => _outputFormat;
        set
        {
            // 멀티트랙인데 ISO를 시도하면 방어적으로 BIN+CUE로 강제한다.
            if (value == CdOutputFormat.Iso && IsMultiTrack)
                value = CdOutputFormat.BinCue;

            if (SetProperty(ref _outputFormat, value))
                OnPropertyChanged(nameof(ExtensionLabel));
        }
    }

    public string ExtensionLabel => SourceFormat switch
    {
        CdSourceFormat.MdfMds => $"{Extension}→{(OutputFormat == CdOutputFormat.Iso ? "iso" : "cue")}",
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
