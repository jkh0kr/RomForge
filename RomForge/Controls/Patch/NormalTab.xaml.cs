using Microsoft.Win32;
using RomForge.Core.Models.Patch;
using RomForge.ViewModels;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RomForge.Controls.Patch;

public partial class NormalTab : UserControl
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private static class PatchExtensions
    {
        public static readonly string[] AllowedExtensions = [".ips", ".bps", ".ups", ".ppf", ".aps", ".xdelta"];
        public static string FileFilter => $"패치 파일|{string.Join(";", AllowedExtensions.Select(ext => "*" + ext))}|모든 파일|*.*";
    }

    public NormalTab()
    {
        InitializeComponent();

        Loaded += (_, _) => ViewModel.PatchVM.NormalVM.RequestSourceSelectionAsync = ShowSourceSelectionDialogAsync;
    }

    private Task<string?> ShowSourceSelectionDialogAsync(IReadOnlyList<ArchiveCandidate> candidates)
    {
        var tcs = new TaskCompletionSource<string?>();

        Dispatcher.Invoke(() =>
        {
            var window = new Window
            {
                Title = "원본 롬 파일 선택",
                Width = 480,
                Height = 380,
                Owner = Application.Current?.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize
            };

            var listBox = new ListBox { Margin = new Thickness(12, 8, 12, 8) };

            foreach (var candidate in candidates)
            {
                listBox.Items.Add(new ListBoxItem
                {
                    Content = $"{Path.GetFileName(candidate.FullPath)}  ({FormatSize(candidate.Size)})",
                    Tag = candidate.FullPath
                });
            }

            if (listBox.Items.Count > 0)
                listBox.SelectedIndex = 0;

            var header = new TextBlock
            {
                Text = "압축 안에 패치 대상 후보가 여러 개입니다. 패치할 원본 파일을 선택하세요:",
                Margin = new Thickness(12, 12, 12, 0),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.Bold
            };

            var okButton = new Button { Content = "선택", Width = 80, Margin = new Thickness(4, 0, 0, 0), IsDefault = true };
            var cancelButton = new Button { Content = "취소", Width = 80, Margin = new Thickness(4, 0, 0, 0), IsCancel = true };

            string? selectedPath = null;

            okButton.Click += (_, _) =>
            {
                selectedPath = (listBox.SelectedItem as ListBoxItem)?.Tag as string;
                window.DialogResult = selectedPath is not null;
            };

            cancelButton.Click += (_, _) => window.DialogResult = false;

            var buttonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 12, 12)
            };
            buttonsPanel.Children.Add(okButton);
            buttonsPanel.Children.Add(cancelButton);

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(buttonsPanel, Dock.Bottom);
            root.Children.Add(header);
            root.Children.Add(buttonsPanel);
            root.Children.Add(listBox);

            window.Content = root;

            bool? result = window.ShowDialog();

            tcs.SetResult(result == true ? selectedPath : null);
        });

        return tcs.Task;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.#} {units[unitIndex]}";
    }

    private void NormalSourceDrop_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "원본 파일 선택" };

        if (dlg.ShowDialog() == true)
            ViewModel.PatchVM.NormalVM.SourcePath = dlg.FileName;
    }

    private void NormalSourceDrop_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var patchFiles = files
                .Where(f => PatchExtensions.AllowedExtensions.Contains(Path.GetExtension(f).ToLower()))
                .ToList();

            var sourceFiles = files.Except(patchFiles).ToList();

            if (patchFiles.Count > 0)
                ViewModel.PatchVM.NormalVM.PatchPath = patchFiles[0];

            if (sourceFiles.Count > 0)
                ViewModel.PatchVM.NormalVM.SourcePath = sourceFiles[0];
        }
    }

    private void NormalPatchDrop_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "패치 파일 선택",
            Filter = PatchExtensions.FileFilter
        };

        if (dlg.ShowDialog() == true)
            ViewModel.PatchVM.NormalVM.PatchPath = dlg.FileName;
    }

    private void NormalPatchDrop_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var patchFiles = files
                .Where(f => PatchExtensions.AllowedExtensions.Contains(Path.GetExtension(f).ToLower()))
                .ToList();

            var sourceFiles = files.Except(patchFiles).ToList();

            if (patchFiles.Count > 0)
                ViewModel.PatchVM.NormalVM.PatchPath = patchFiles[0];

            if (sourceFiles.Count > 0)
                ViewModel.PatchVM.NormalVM.SourcePath = sourceFiles[0];
        }
    }
}