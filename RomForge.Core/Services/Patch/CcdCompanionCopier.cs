using Common;
using System.IO;

namespace RomForge.Core.Services.Patch;

public class CcdCompanionCopier(Action<string, LogLevel> log)
{
    public string? CopyCcd(string sourcePath, string outputDir, string outputPath)
    {
        string sourceCcdPath = Path.ChangeExtension(sourcePath, ".ccd");

        if (!File.Exists(sourceCcdPath))
        {
            log("CCD 파일을 찾을 수 없습니다. CD 이미지가 아니거나 CCD가 누락되었을 수 있습니다.", LogLevel.Error);

            return null;
        }

        string outputCcdPath = Path.ChangeExtension(outputPath, ".ccd");

        try
        {
            File.Copy(sourceCcdPath, outputCcdPath, true);

            return outputCcdPath;
        }
        catch (Exception ex)
        {
            log($"CCD 파일 복사 중 오류 발생: {ex.Message}", LogLevel.Error);

            return null;
        }
    }
}