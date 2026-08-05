using Microsoft.Win32;
using NSW.WPF.Services;
using RomForge.Core.Models.CD;
using RomForge.ViewModels;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace RomForge.Controls;

public partial class CdConvertTab : UserControl
{
    private string? _lastSortColumn;
    private ListSortDirection _lastSortDirection;

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public CdConvertTab()
    {
        InitializeComponent();

        DataContextChanged += CdConvertTab_DataContextChanged;
    }

    private void CdConvertTab_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ViewModel?.CdConvertVM != null)
        {
            ViewModel.CdConvertVM.ScrollToItemRequested -= OnScrollToItemRequested;
            ViewModel.CdConvertVM.ScrollToItemRequested += OnScrollToItemRequested;
        }
    }

    private void OnScrollToItemRequested(object item)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (item != null)
                lvFiles.ScrollIntoView(item);
        }, DispatcherPriority.Background);
    }

    private void LvFiles_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void LvFiles_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            ViewModel.CdConvertVM.AddPaths(paths);
    }

    private void LvFiles_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;

        var selected = lvFiles.SelectedItems.Cast<CdConvertFileItem>().ToList();

        ViewModel.CdConvertVM.RemoveItems(selected);
    }

    private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = CdConvertMainViewModel.GetFileDialogFilter()
        };

        if (dlg.ShowDialog() == true)
            ViewModel.CdConvertVM.AddPaths(dlg.FileNames);
    }

    private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
        {
            Description = "추가할 폴더를 선택하세요",
            UseDescriptionForTitle = true
        };

        if (dlg.ShowDialog() == true)
            ViewModel.CdConvertVM.AddPaths([dlg.SelectedPath]);
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        var selected = lvFiles.SelectedItems.Cast<CdConvertFileItem>().ToList();

        ViewModel.CdConvertVM.RemoveItems(selected);
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e) => ViewModel.CdConvertVM.ClearItems();

    private void LvFiles_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (lvFiles.SelectedItems.Count == 0)
            e.Handled = true;
    }

    private void MenuItem_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var selected = lvFiles.SelectedItems.Cast<CdConvertFileItem>().ToList();

        if (selected.Count == 0)
            return;

        string? dir = Path.GetDirectoryName(selected[0].FilePath);

        dir?.OpenFolder();
    }

    private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header)
            return;

        if (header.Tag is not string sortBy)
            return;

        var direction = _lastSortColumn == sortBy && _lastSortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        var view = CollectionViewSource.GetDefaultView(lvFiles.ItemsSource);

        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(sortBy, direction));
        view.Refresh();

        _lastSortColumn = sortBy;
        _lastSortDirection = direction;
    }
}