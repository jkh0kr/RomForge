using CHD.Core.Services;
using Common;
using DolphinTool.Core.Services;
using Patch.Core.Formats.DCP.Services;
using RomForge.Core.Models.Compression;
using RomForge.Core.Services.Compression;
using System.IO;

namespace RomForge.Core.Services.Patch;

public static class CompressedSourceDecompressor
{
    public static async Task<(string ActualSourcePath, DetectResult Detected)> ResolveAsync(string sourcePath, string workDir, Action<string, LogLevel> log, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();

        if (ext != ".chd" && ext != ".rvz")
            return (sourcePath, FormatDetector.Detect(sourcePath));

        Directory.CreateDirectory(workDir);

        var workingPath = Path.Combine(workDir, Path.GetFileName(sourcePath));

        if (!PathsEqual(sourcePath, workingPath))
            File.Copy(sourcePath, workingPath, true);

        log($"{Path.GetFileName(workingPath)} 압축 해제 중...", LogLevel.Highlight);

        var result = ext == ".chd"
            ? await DecompressChdAsync(workingPath, progress, log, ct)
            : await DecompressRvzAsync(workingPath, progress, log, ct);

        log($"압축 해제 완료: {Path.GetFileName(result.ActualSourcePath)}", LogLevel.Ok);

        return result;
    }

    private static async Task<(string ActualSourcePath, DetectResult Detected)> DecompressChdAsync(string chdPath, IProgress<ProgressInfo> progress, Action<string, LogLevel> log, CancellationToken ct)
    {
        var converter = new FileConverter(AppConfig.Instance.Chdman.Compression);
        converter.LogMessage += (_, e) => log(e.Message, e.Level);

        var result = await converter.ConvertFileAsync(chdPath, progress, ct);

        if (!result.Success)
            throw new Exception($"CHD 압축 해제 실패: {result.Message}");

        var outExt = Path.GetExtension(result.OutputFile).ToLowerInvariant();

        if (outExt == ".cue")
        {
            var bins = ConversionSource.ParseBinsFromCue(result.OutputFile);

            if (bins.Count == 0)
                throw new Exception("CUE 파일이 참조하는 BIN 파일을 찾을 수 없습니다.");

            var mainBin = Path.Combine(Path.GetDirectoryName(result.OutputFile)!, Path.GetFileName(bins[0]));

            return (mainBin, new DetectResult { Format = RomFormat.Bin, Direction = ConvertDirection.Compress, OutputExtension = "chd" });
        }

        if (outExt == ".gdi")
        {
            var gdi = GdiFile.Parse(result.OutputFile);
            var mainTrackPath = gdi.GetTrackFullPath(gdi.DataTrack);

            return (mainTrackPath, new DetectResult { Format = RomFormat.Gdi, Direction = ConvertDirection.Compress, OutputExtension = "chd" });
        }

        return (result.OutputFile, new DetectResult { Format = RomFormat.Iso, Direction = ConvertDirection.Compress, OutputExtension = "chd" });
    }

    private static async Task<(string ActualSourcePath, DetectResult Detected)> DecompressRvzAsync( string rvzPath, IProgress<ProgressInfo> progress, Action<string, LogLevel> log, CancellationToken ct)
    {
        var dolphin = new DolphinService();
        dolphin.LogMessage += (_, e) => log(e.Message, e.Level);
        dolphin.ProgressChanged += (_, e) => progress.Report(new ProgressInfo { Label = "압축 해제 중...", Percent = e.Progress });

        await dolphin.ConvertFileAsync(rvzPath, "rvz", "iso", 0, ct);

        var isoPath = Path.ChangeExtension(rvzPath, ".iso");

        if (!File.Exists(isoPath))
        {
            var candidate = Directory
                .GetFiles(Path.GetDirectoryName(rvzPath)!, Path.GetFileNameWithoutExtension(rvzPath) + "*.iso")
                .FirstOrDefault();

            isoPath = candidate ?? throw new Exception("RVZ 압축 해제 결과 파일을 찾을 수 없습니다.");
        }

        return (isoPath, new DetectResult { Format = RomFormat.Gcm, Direction = ConvertDirection.Compress, OutputExtension = "rvz" });
    }

    private static bool PathsEqual(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}