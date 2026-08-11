using Common;
using Patch.Core;
using RomForge.Core.Models.Compression;
using System.IO;

namespace RomForge.Core.Services.Patch;

public class PatchOrchestrator(Action<string, LogLevel> log, IProgress<ProgressInfo> progress, bool autoCompress, int dolphinCompressLevel)
{
    private string? _outputCuePath;
    private string? _outputCcdPath;
    private string? _outputGdiPath;
    private List<string> _copiedTrackPaths = [];
    private readonly BinTrackCopier _binTrackCopier = new(log);
    private readonly CcdCompanionCopier _ccdCompanionCopier = new(log);
    private readonly GdiTrackCopier _gdiTrackCopier = new(log);
    private readonly ZipCompressor _zipCompressor = new(log, progress);
    private readonly CompressKnownConverter _compressKnownConverter = new(log, progress, dolphinCompressLevel);

    public async Task PatchAsync(string sourcePath, string patchPath, DetectResult detected, string outputDir, string outputPath, CancellationToken ct)
    {
        _outputCuePath = null;
        _outputCcdPath = null;
        _outputGdiPath = null;
        _copiedTrackPaths = [];

        bool isZipTarget = detected.Format is not (RomFormat.Bin or RomFormat.Iso or RomFormat.Gcm or RomFormat.Wii or RomFormat.Wbfs or RomFormat.Ccd or RomFormat.Cci or RomFormat.Cia or RomFormat.Gdi);

        await UniversalPatcher.ApplyPatchAsync(sourcePath, patchPath, outputPath, progress, ct);

        progress.Report(new ProgressInfo { Label = "패치 완료", Percent = 100 });
        log($"패치 완료: {outputPath}", LogLevel.Ok);

        bool skipCompress = false;
        if (detected.Format == RomFormat.Bin)
        {
            _outputCuePath = await _binTrackCopier.CopyBinTracksAsync(sourcePath, outputDir, outputPath, _copiedTrackPaths);
            skipCompress = _outputCuePath is null;
        }
        else if (detected.Format == RomFormat.Ccd)
        {
            _outputCcdPath = _ccdCompanionCopier.CopyCcd(sourcePath, outputDir, outputPath);
            skipCompress = _outputCcdPath is null;
        }
        else if (detected.Format == RomFormat.Gdi)
        {
            _outputGdiPath = _gdiTrackCopier.CopyGdiTracks(sourcePath, outputDir, outputPath, _copiedTrackPaths);
            skipCompress = _outputGdiPath is null;
        }

        if (!autoCompress || skipCompress)
            return;

        if (isZipTarget)
        {
            progress.Report(new ProgressInfo { Label = "압축 중...", Percent = 0 });
            await _zipCompressor.CompressFromFileAsync(sourcePath, outputPath, outputDir, ct);
        }
        else
            await _compressKnownConverter.ConvertAsync(detected, outputPath, _outputCuePath, _copiedTrackPaths, _outputCcdPath, _outputGdiPath, outputDir, ct);
    }

    public void Cleanup(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            if (_outputCuePath is not null && File.Exists(_outputCuePath))
                File.Delete(_outputCuePath);

            if (_outputCcdPath is not null && File.Exists(_outputCcdPath))
                File.Delete(_outputCcdPath);

            if (_outputGdiPath is not null && File.Exists(_outputGdiPath))
                File.Delete(_outputGdiPath);

            foreach (var trackPath in _copiedTrackPaths)
                if (File.Exists(trackPath))
                    File.Delete(trackPath);
        }
        catch (Exception ex)
        {
            log($"파일 정리 실패: {ex.Message}", LogLevel.Error);
        }
    }
}