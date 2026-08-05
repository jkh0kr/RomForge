using CD.Core.Services.Readers;
using CD.Core.Services.Writers;
using Common;
using Common.WPF.ViewModels;
using RomForge.Core.Models;
using RomForge.Core.Models.CD;
using RomForge.Core.UI.Command;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace RomForge.ViewModels;

public class CdConvertMainViewModel : ToolTabViewModel
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mds",
    };

    private CancellationTokenSource _cts = new();

    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    public ObservableCollection<CdConvertFileItem> FileItems { get; } = [];

    public Visibility HintVisibility => FileItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public ICommand RunCommand { get; }

    public event Action<CdConvertFileItem>? ScrollToItemRequested;

    public CdConvertMainViewModel()
    {
        RunCommand = new RelayCommand(async _ => await RunAsync(), _ => !IsLocked && FileItems.Count > 0);
        CancelCommand = new RelayCommand(_ => _cts.Cancel(), _ => IsLocked);
    }

    public static string GetFileDialogFilter()
    {
        string wildcards = string.Join(";", SupportedExtensions.Select(ext => $"*{ext}"));

        return $"지원 파일|{wildcards}|모든 파일|*.*";
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        var existing = FileItems.Select(f => f.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ExpandPaths(paths))
        {
            if (!SupportedExtensions.Contains(Path.GetExtension(path)))
                continue;

            if (!existing.Add(path))
                continue;

            var item = new CdConvertFileItem(path);

            FileItems.Add(item);

            for (int i = 0; i < FileItems.Count; i++)
                FileItems[i].No = i + 1;
        }

        OnPropertyChanged(nameof(HintVisibility));
        CommandManager.InvalidateRequerySuggested();
    }

    public void RemoveItems(IEnumerable<CdConvertFileItem> items)
    {
        foreach (var item in items.ToList())
            FileItems.Remove(item);

        for (int i = 0; i < FileItems.Count; i++)
            FileItems[i].No = i + 1;

        OnPropertyChanged(nameof(HintVisibility));
    }

    public void ClearItems()
    {
        FileItems.Clear();
        OnPropertyChanged(nameof(HintVisibility));
    }

    private async Task RunAsync()
    {
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        ClearLog();

        using (BeginWork())
        {
            int totalCount = FileItems.Count;

            AppendLog($"총 {totalCount}개의 작업을 시작합니다.", LogLevel.Highlight);

            int cnt = 0;

            foreach (var item in FileItems)
            {
                if (_cts.Token.IsCancellationRequested)
                {
                    CancelRemainingItems();
                    break;
                }

                if (item.Status == "완료" || item.Status == "미지원")
                    continue;

                try
                {
                    item.Status = "대기중";
                    item.Progress = 0;

                    if (item.SourceFormat == CdSourceFormat.Unknown)
                    {
                        item.Status = "미지원";
                        AppendLog($"[{item.FileName}] 지원하지 않는 포맷", LogLevel.Error);
                        continue;
                    }

                    item.Status = "변환중";

                    ScrollToItemRequested?.Invoke(item);

                    var progressHandler = new Progress<ProgressInfo>(p =>
                    {
                        item.Progress = p.Percent;
                    });

                    switch (item.SourceFormat)
                    {
                        case CdSourceFormat.MdfMds:
                            {
                                var reader = DiscImageReaderFactory.Resolve(item.FilePath);
                                var discImage = reader.Read(item.FilePath);

                                await BinCueWriter.WriteAsync(
                                    discImage,
                                    item.Directory,
                                    item.FileName,
                                    progressHandler,
                                    _cts.Token);
                            }
                            break;
                    }

                    item.Progress = 100;
                    item.Status = "완료";
                    cnt++;
                }
                catch (OperationCanceledException)
                {
                    AppendLog("작업이 취소되었습니다.", LogLevel.Error);
                    CancelRemainingItems();

                    break;
                }
                catch (Exception ex)
                {
                    AppendLog($"오류 ([{item.FileName}]): {ex.Message}", LogLevel.Error);
                    item.Status = "실패";
                }
            }

            if (cnt > 0)
            {
                AppendLog($"총 {cnt}개의 작업을 완료했습니다.", LogLevel.Ok);
            }
        }
    }

    private void CancelRemainingItems()
    {
        foreach (var remainingItem in FileItems.Where(i => i.Status == "대기중" || i.Status == "변환중"))
        {
            remainingItem.Status = "취소";
            remainingItem.Progress = 0;
        }
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
        };

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                foreach (var f in Directory.EnumerateFiles(path, "*.*", opts))
                    yield return f;
            else if (File.Exists(path))
                yield return path;
        }
    }

    private void AppendLog(string msg, LogLevel level = LogLevel.Info) => Application.Current.Dispatcher.Invoke(() => LogEntries.Add(new LogEntry { Message = msg, Level = level }));

    private void ClearLog() => Application.Current.Dispatcher.Invoke(() => LogEntries.Clear());
}