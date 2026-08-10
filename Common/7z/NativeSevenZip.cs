using SevenZip;

namespace Patch.Core.Services;

public static class NativeSevenZip
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
            return;

        lock (InitLock)
        {
            if (_initialized)
                return;

            string dllPath = Path.Combine(AppContext.BaseDirectory, "7z.dll");

            if (!File.Exists(dllPath))
            {
                throw new FileNotFoundException(
                    $"네이티브 7z.dll을 찾을 수 없습니다. 공식 7-Zip 배포판(예: C:\\Program Files\\7-Zip\\7z.dll)의 7z.dll을 다음 경로에 배치하세요: {dllPath}",
                    dllPath);
            }

            SevenZipExtractor.SetLibraryPath(dllPath);
            _initialized = true;
        }
    }
}