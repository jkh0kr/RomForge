using Common.WPF.ViewModels;
using Microsoft.Win32;
using RomForge.Core;
using RomForge.Core.Models;
using RomForge.Core.Models.Web;
using RomForge.Core.Services.Web;
using RomForge.Core.UI.Command;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Input;

namespace RomForge.ViewModels.Web;

public class PlatformFilterItem : ViewModelBase
{
    public string Name { get; }

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }

    public PlatformFilterItem(string name, bool isChecked)
    {
        Name = name;
        _isChecked = isChecked;
    }
}

public class PatchSearchMainViewModel : ToolTabViewModel
{
    private static readonly DateTime EarliestDate = new(2004, 1, 11);

    public static readonly string[] SystemList =
    [
        "FC","FDS","SFC","GB","GBC","GBA","NDS","3DS","N64","GC","Wii","WiiU",
        "PS1","PS2","PS3","PS4","PSP","PSV",
        "MD","MDCD","SS","DC","GG",
        "NEOGEO","NGP","NGPC",
        "Xbox","Xbox 360","Xbox One",
        "PC98", "PC88",
        "PCE","PCE CD",
        "MSX1","MSX2",
        "WS","WSC",
        "Windows","DOS"
    ];

    private readonly PlatformFilterItem _allPlatformsItem;
    private bool _isUpdatingPlatforms;

    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    public ObservableCollection<PatchEntry> Results { get; } = [];

    public ObservableCollection<PlatformFilterItem> Platforms { get; } = [];

    public static string[] Systems => SystemList;

    private DateTime? _startDate;
    public DateTime? StartDate
    {
        get => _startDate;
        set
        {
            if (SetProperty(ref _startDate, value))
                AppConfig.Instance.PatchSearch.StartDate = value;
        }
    }

    private DateTime? _endDate;
    public DateTime? EndDate
    {
        get => _endDate;
        set
        {
            if (SetProperty(ref _endDate, value))
                AppConfig.Instance.PatchSearch.EndDate = value;
        }
    }

    private string _keyword = "";
    public string Keyword { get => _keyword; set => SetProperty(ref _keyword, value); }

    private bool _isSearching;
    public bool IsSearching { get => _isSearching; set => SetProperty(ref _isSearching, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private string _newTitle = "";
    public string NewTitle { get => _newTitle; set => SetProperty(ref _newTitle, value); }

    private string _newUrl = "";
    public string NewUrl { get => _newUrl; set => SetProperty(ref _newUrl, value); }

    private string? _newSystem;
    public string? NewSystem { get => _newSystem; set => SetProperty(ref _newSystem, value); }

    public string PlatformSummaryText
    {
        get
        {
            var others = Platforms.Skip(1).ToList();

            if (others.Count == 0)
                return "전체";

            var checkedCount = others.Count(p => p.IsChecked);

            if (checkedCount == 0 || checkedCount == others.Count)
                return "전체";

            return $"{checkedCount}개 선택";
        }
    }

    public ICommand SearchCommand { get; }
    public ICommand SetRangeCommand { get; }
    public ICommand AddPatchCommand { get; }
    public ICommand ExportCsvCommand { get; }

    public PatchSearchMainViewModel()
    {
        var config = AppConfig.Instance.PatchSearch;

        _startDate = config.StartDate ?? EarliestDate;
        _endDate = config.EndDate ?? DateTime.Today;

        var savedSystems = config.SelectedSystems;
        var isAllChecked = savedSystems == null || savedSystems.Count == 0;

        _allPlatformsItem = new PlatformFilterItem("전체", isAllChecked);
        Platforms.Add(_allPlatformsItem);

        foreach (var name in SystemList)
        {
            var isChecked = isAllChecked || savedSystems!.Contains(name);
            Platforms.Add(new PlatformFilterItem(name, isChecked));
        }

        _allPlatformsItem.PropertyChanged += OnAllPlatformsItemChanged;

        foreach (var item in Platforms.Skip(1))
            item.PropertyChanged += OnPlatformItemChanged;

        SearchCommand = new RelayCommand(async _ => await SearchAsync(), _ => !IsSearching);
        SetRangeCommand = new RelayCommand(async p => await SetRangeAsync(p as string), _ => !IsSearching);
        AddPatchCommand = new RelayCommand(async _ => await AddPatchAsync(),
            _ => !IsSearching && !string.IsNullOrWhiteSpace(NewTitle) && !string.IsNullOrWhiteSpace(NewUrl));
        ExportCsvCommand = new RelayCommand(_ => ExportCsv(), _ => Results.Count > 0);

        _ = SearchAsync();
    }

    private void OnAllPlatformsItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlatformFilterItem.IsChecked) || _isUpdatingPlatforms)
            return;

        _isUpdatingPlatforms = true;

        try
        {
            foreach (var item in Platforms)
            {
                if (!ReferenceEquals(item, _allPlatformsItem))
                    item.IsChecked = _allPlatformsItem.IsChecked;
            }
        }
        finally
        {
            _isUpdatingPlatforms = false;
        }

        OnPropertyChanged(nameof(PlatformSummaryText));
        AppConfig.Instance.PatchSearch.SelectedSystems = GetSelectedSystems();
    }

    private void OnPlatformItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlatformFilterItem.IsChecked) || _isUpdatingPlatforms)
            return;

        _isUpdatingPlatforms = true;

        try
        {
            _allPlatformsItem.IsChecked = Platforms.Skip(1).All(p => p.IsChecked);
        }
        finally
        {
            _isUpdatingPlatforms = false;
        }

        OnPropertyChanged(nameof(PlatformSummaryText));
        AppConfig.Instance.PatchSearch.SelectedSystems = GetSelectedSystems();
    }

    private List<string>? GetSelectedSystems()
    {
        var others = Platforms.Skip(1).ToList();
        var checkedNames = others.Where(p => p.IsChecked).Select(p => p.Name).ToList();

        if (checkedNames.Count == 0 || checkedNames.Count == others.Count)
            return null;

        return checkedNames;
    }

    private async Task SetRangeAsync(string? type)
    {
        var end = DateTime.Today;

        var start = type switch
        {
            "1w" => end.AddDays(-7),
            "1m" => end.AddMonths(-1),
            "1y" => end.AddYears(-1),
            "all" => EarliestDate,
            _ => end
        };

        StartDate = start;
        EndDate = end;

        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        IsSearching = true;
        StatusText = "데이터를 가져오는 중...";

        try
        {
            var list = await PatchSearchService.SearchAsync(StartDate, EndDate, GetSelectedSystems(), Keyword);

            Results.Clear();

            foreach (var item in list)
                Results.Add(item);

            StatusText = Results.Count == 0 ? "검색 결과가 없습니다." : $"총 {Results.Count} 건 조회 완료";
        }
        catch (Exception ex)
        {
            StatusText = $"조회 실패: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task AddPatchAsync()
    {
        var entry = new PatchEntry
        {
            System = NewSystem ?? "기타",
            Title = NewTitle,
            Version = "",
            Date = DateTime.Today.ToString("yyyy-MM-dd"),
            Url = NewUrl
        };

        try
        {
            StatusText = await PatchSearchService.AddPatchAsync(entry);

            NewTitle = "";
            NewUrl = "";

            await SearchAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"등록 실패: {ex.Message}";
        }
    }

    private void ExportCsv()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV 파일|*.csv",
            FileName = $"검색결과_{DateTime.Today:yyyy-MM-dd}.csv"
        };

        if (dialog.ShowDialog() != true)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("플랫폼,게임,버전,날짜,배포처");

        foreach (var r in Results)
            sb.AppendLine($"{ToCsvField(r.System)},{ToCsvField(r.Title)},{ToCsvField(r.Version)},{ToCsvField(r.Date)},{ToCsvField(r.Url)}");

        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));

        StatusText = $"{dialog.FileName} 로 저장했습니다.";
    }

    private static string ToCsvField(string? value)
    {
        value ??= "";

        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}