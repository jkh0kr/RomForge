using LibHac.Ncm;
using Microsoft.Win32;
using NSW.Core;
using NSW.Core.Models;
using NSW.WPF.Services;
using NSW.WPF.ViewModels;
using Patch.Core.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Res = NSW.Core.Properties.Resources;

namespace RomForge.Controls.Switch;

public partial class SwitchTitleListControl : UserControl
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".nsp", ".xci", ".nsz", ".xcz" };

    public ObservableCollection<GameFile> GameFiles { get; set; } = [];

    public event Action? FileListChanged;

    public SwitchTitleListControl()
    {
        InitializeComponent();
        lvFiles.ItemsSource = GameFiles;
        UpdateDropHint();
    }

    public static bool KeyExists() => KeySetProvider.Instance.KeySet != null;

    public void RecalcKeyMissingFiles(Action onCompleted)
    {
        var targets = GameFiles.Where(f => f.IsKeyMissing).ToList();

        if (targets.Count == 0) 
        { 
            onCompleted();
            return;
        }

        var keySet = KeySetProvider.Instance.KeySet;

        if (keySet == null) 
        { 
            onCompleted(); 
            return; 
        }

        int remaining = targets.Count;

        foreach (var vm in targets)
        {
            string capturedPath = vm.FilePath;
            _ = Task.Run(() =>
            {
                string result = MetadataReader.DetectFileType(keySet, capturedPath);

                if (Interlocked.Decrement(ref remaining) == 0)
                    Dispatcher.Invoke(() => { vm.FileType = result; onCompleted(); });
                else
                    Dispatcher.Invoke(() => vm.FileType = result);
            });
        }
    }

    private void UpdateDropHint()
    {
        dropHint.Visibility = GameFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FileListChanged?.Invoke();
    }

    private void BtnAddFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Res.Dialog_SelectGameFile,
            Filter = $"{Res.Filter_SwitchFiles} (*.nsp;*.xci;*.nsz;*.xcz)|*.nsp;*.xci;*.nsz;*.xcz|{Res.Filter_AllFiles}|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true)
            _ = AddFilesAsync(ExpandPaths(dlg.FileNames));
    }

    private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "게임 폴더 선택", UseDescriptionForTitle = true };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            _ = AddFilesAsync(ExpandPaths([dlg.SelectedPath]));
    }

    private void BtnBulkPatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: not null } fe) 
            return;

        fe.ContextMenu.PlacementTarget = fe;
        fe.ContextMenu.IsOpen = true;
    }

    private void BulkPatchMenu_FromFolder_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetBulkPatchTargets();
        if (targets == null) return;

        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "한글패치 루트 폴더 선택 (titleId 이름의 폴더 또는 titleId.zip/titleId.7z 파일을 자동 매칭합니다)",
            UseDescriptionForTitle = true
        };

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        int matched = 0;

        foreach (var file in targets)
        {
            string folderCandidate = Path.Combine(dlg.SelectedPath, file.TitleID!);

            if (Directory.Exists(folderCandidate))
            {
                file.PatchPath = folderCandidate;
                matched++;
                continue;
            }

            string? recursiveMatch = PatchFolderResolver.FindSubDir(dlg.SelectedPath, file.TitleID!);

            if (recursiveMatch != null)
            {
                file.PatchPath = recursiveMatch;
                matched++;
                continue;
            }

            string[] exts = [".zip", ".7z"]; 
            string? archiveCandidate = exts
                .Select(ext => Path.Combine(dlg.SelectedPath, file.TitleID! + ext))
                .FirstOrDefault(File.Exists);

            if (archiveCandidate != null)
            {
                file.PatchPath = archiveCandidate;
                matched++;
            }
        }

        ShowBulkPatchResult(targets.Count, matched);
    }

    private void BulkPatchMenu_FromArchive_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetBulkPatchTargets();

        if (targets == null)
            return;

        var dlg = new OpenFileDialog
        {
            Title = "한글패치 루트 압축파일 선택 (안에서 titleId 이름의 폴더를 자동 매칭합니다)",
            Filter = "압축파일 (*.zip;*.7z)|*.zip;*.7z"
        };

        if (dlg.ShowDialog() != true)
            return;

        int matched = 0;

        try
        {
            using var archive = ArchivePatchSourceFactory.Open(dlg.FileName);

            foreach (var file in targets)
            {
                string? prefix = ArchivePatchFolderResolver.FindSubDir(archive.EntryPaths, file.TitleID!);

                if (prefix == null)
                    continue;

                file.PatchPath = ArchivePatchSourceFactory.CombineScope(dlg.FileName, prefix);
                matched++;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"압축파일을 여는 중 오류가 발생했습니다: {ex.Message}", "한글패치 일괄 지정", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ShowBulkPatchResult(targets.Count, matched);
    }

    private List<GameFile>? GetBulkPatchTargets()
    {
        var targets = GameFiles.Where(f => !string.IsNullOrEmpty(f.TitleID)).ToList();

        if (targets.Count == 0)
        {
            MessageBox.Show("타이틀 정보가 있는 항목이 없습니다.", "한글패치 일괄 지정", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        return targets;
    }

    private static void ShowBulkPatchResult(int total, int matched) =>
        MessageBox.Show($"{total}개 중 {matched}개에 패치 매칭됨.", "한글패치 일괄 지정", MessageBoxButton.OK, MessageBoxImage.Information);

    private void BtnRemoveFile_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in lvFiles.SelectedItems.Cast<GameFile>().ToList())
            GameFiles.Remove(item);

        UpdateDropHint();
    }

    private void BtnRemoveAllFiles_Click(object sender, RoutedEventArgs e)
    {
        GameFiles.Clear();
        UpdateDropHint();
    }

    private void BtnHelp_Click(object sender, RoutedEventArgs e)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://sinjunyoung.github.io/RomForge/switch-unpack-repack/",
            UseShellExecute = true
        };

        System.Diagnostics.Process.Start(psi);
    }    

    private void LvFiles_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) 
            BtnRemoveFile_Click(sender, new RoutedEventArgs());
    }

    private void LvFiles_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void LvFiles_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) 
            return;

        await AddFilesAsync(ExpandPaths(paths));
    }

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var keySet = KeySetProvider.Instance.KeySet;
        var existing = GameFiles.Select(f => f.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newPaths = await Task.Run(() =>
            paths.Where(p => SupportedExtensions.Contains(Path.GetExtension(p)))
                 .Where(p => existing.Add(p))
                 .ToList());

        foreach (var path in newPaths)
        {
            var vm = new GameFile(path) { FileType = keySet == null ? Res.Status_NoKey : Res.Status_Analyzing };

            if (keySet != null)
            {
                var info = MetadataReader.GetGameFileInfo(keySet, path);
                if (info != null)
                {
                    vm.TitleName = info.TitleName;
                    vm.TitleID = info.TitleId;
                    vm.Version = info.DisplayVersion;
                    vm.FileType = info.Type;
                    if (info.IconData != null) vm.Icon = info.IconData.ToBitmapImage();
                }

                List<MetadataResult> allMeta;
                try { allMeta = MetadataReader.GetMetadataFromContainer(keySet, path); }
                catch { allMeta = []; }

                var dlcResults = allMeta
                    .Where(m => m.Type is ContentMetaType.AddOnContent or ContentMetaType.Delta)
                    .GroupBy(m => m.TitleId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                if (dlcResults.Count > 0)
                {
                    bool hasBaseOrUpdate = vm.FileType.Contains('B') || vm.FileType.Contains('U');

                    vm.FileType = string.Concat(vm.FileType.Where(c => c != 'D'));

                    foreach (var dlc in dlcResults)
                    {
                        var dlcVm = new GameFile(path)
                        {
                            FileType = "D",
                            TitleID = dlc.TitleId,
                            Version = dlc.GetEffectiveDisplayVersion(),
                            TitleName = string.IsNullOrEmpty(vm.TitleName) ? dlc.TitleId : $"{vm.TitleName} (DLC {dlc.TitleId[^4..]})",
                            Icon = vm.Icon,
                        };

                        AssignOrReplace(dlcVm);
                    }

                    if (!hasBaseOrUpdate)
                    {
                        UpdateDropHint();
                        continue;
                    }
                }
            }

            if (string.IsNullOrEmpty(vm.TitleName))
                vm.TitleName = Path.GetFileNameWithoutExtension(path);

            AssignOrReplace(vm);
            UpdateDropHint();
        }
    }

    private void AssignOrReplace(GameFile vm)
    {
        if (vm.FileType.Contains('B'))
        {
            var existingBase = GameFiles.FirstOrDefault(f => f.FileType.Contains('B'));
            if (existingBase != null)
            {
                vm.PatchPath ??= existingBase.PatchPath;
                GameFiles.Remove(existingBase);
            }
        }

        if (vm.FileType.Contains('U'))
        {
            var existingUpdate = GameFiles.FirstOrDefault(f => f.FileType.Contains('U'));
            if (existingUpdate != null)
            {
                vm.PatchPath ??= existingUpdate.PatchPath;
                GameFiles.Remove(existingUpdate);
            }
        }

        GameFiles.Add(vm);
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                foreach (var f in Directory.EnumerateFiles(path, "*.*", opts)) 
                    yield return f;
            else if (File.Exists(path))
                yield return path;
        }
    }

    private void LvFiles_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (lvFiles.SelectedItems.Count == 0) e.Handled = true;
    }

    private void MenuItem_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var selected = lvFiles.SelectedIndex;

        if (selected < 0)
            return;

        string? dir = Path.GetDirectoryName(GameFiles[selected].FilePath);
        dir?.OpenFolder();
    }

    private void MenuItem_RemovePatch_Click(object sender, RoutedEventArgs e)
    {
        if (lvFiles.SelectedItem is GameFile file)
            file.PatchPath = null;
    }

    private void PatchDropTarget_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: not null } fe)
            return;

        fe.ContextMenu.PlacementTarget = fe;
        fe.ContextMenu.IsOpen = true;
    }

    private void PatchMenu_SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameFile file }) 
            return;

        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"{file.TitleName}에 적용할 한글패치 폴더 선택",
            UseDescriptionForTitle = true
        };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            file.PatchPath = dlg.SelectedPath;
    }

    private void PatchMenu_SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameFile file }) 
            return;

        var dlg = new OpenFileDialog
        {
            Title = $"{file.TitleName}에 적용할 한글패치 압축파일 선택",
            Filter = "압축파일 (*.zip;*.7z)|*.zip;*.7z"
        };

        if (dlg.ShowDialog() == true)
            file.PatchPath = dlg.FileName;
    }

    private void PatchDropTarget_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = IsValidPatchDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void PatchDropTarget_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
    }

    private void PatchDropTarget_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameFile file })
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return;

        string path = paths[0];

        if (IsValidPatchPath(path))
            file.PatchPath = path;
    }

    private static bool IsValidPatchDrop(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1)
            return false;

        return IsValidPatchPath(paths[0]);
    }

    private static bool IsValidPatchPath(string path)
    {
        if (Directory.Exists(path))
            return true;

        if (!File.Exists(path))
            return false;

        string ext = Path.GetExtension(path);

        return string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".7z", StringComparison.OrdinalIgnoreCase);
    }
}