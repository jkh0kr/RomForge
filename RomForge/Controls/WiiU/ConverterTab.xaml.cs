using NSW.WPF.Services;
using RomForge.Core.Models.WiiU;
using RomForge.ViewModels.WiiU;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace RomForge.Controls.WiiU;

public partial class ConverterTab : UserControl
{
    private ConverterMainViewModel? ViewModel => DataContext as ConverterMainViewModel;

    public ConverterTab()
    {
        InitializeComponent();

        DataContextChanged += ConverterTab_DataContextChanged;
    }

    private void ConverterTab_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ConverterMainViewModel oldVm)
            oldVm.ScrollToItemRequested -= OnScrollToItemRequested;

        if (e.NewValue is ConverterMainViewModel newVm)
            newVm.ScrollToItemRequested += OnScrollToItemRequested;
    }

    private void OnScrollToItemRequested(WiiUFileItem item)
    {
        Dispatcher.InvokeAsync(() => lvFiles.ScrollIntoView(item), DispatcherPriority.Background);
    }

    private void LvFiles_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private async void LvFiles_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel == null)
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        await ViewModel.AddPaths(paths);
    }

    private void LvFiles_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;

        var selected = lvFiles.SelectedItems.Cast<WiiUFileItem>().ToList();
        ViewModel?.RemoveItems(selected);
    }

    private async void BtnAddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
            return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "변환할 파일 선택",
            Multiselect = true,
            Filter = "WiiU 파일 (*.wud;*.wux;*.wua)|*.wud;*.wux;*.wua|모든 파일 (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
            await ViewModel.AddPaths(dialog.FileNames);
    }

    private async void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
            return;

        var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
        {
            Description = "추가할 폴더를 선택하세요 (WUP 또는 Loadiine 폴더)",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() == true)
            await ViewModel.AddPaths([dialog.SelectedPath]);
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        var selected = lvFiles.SelectedItems.Cast<WiiUFileItem>().ToList();
        ViewModel?.RemoveItems(selected);
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.ClearItems();
    }

    private void LvFiles_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (lvFiles.SelectedItems.Count == 0)
            e.Handled = true;
    }

    private void MenuItem_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var selected = lvFiles.SelectedItems.Cast<WiiUFileItem>().ToList();

        if (selected.Count == 0)
            return;

        string target = Directory.Exists(selected[0].FilePath)
            ? selected[0].FilePath
            : Path.GetDirectoryName(selected[0].FilePath) ?? selected[0].FilePath;

        target.OpenFolder();
    }
}