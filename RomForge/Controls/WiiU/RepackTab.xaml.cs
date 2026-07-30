using NSW.Core.Enums;
using NSW.WPF.Services;
using RomForge.Core.Models.WiiU;
using RomForge.ViewModels.WiiU;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RomForge.Controls.WiiU
{
    public partial class RepackTab : UserControl
    {
        RepackMainViewModel ViewModel => (RepackMainViewModel)DataContext;

        public RepackTab()
        {
            InitializeComponent();
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
            if (sender is not FrameworkElement { DataContext: TitleInputEntry entry }) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

            string path = paths[0];

            if (Directory.Exists(path) || (File.Exists(path) && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)))
                entry.PatchPath = path;

            e.Handled = true;
        }

        private static bool IsValidPatchDrop(IDataObject data)
        {
            if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1)
                return false;

            string path = paths[0];

            return Directory.Exists(path) || (File.Exists(path) && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase));
        }

        private void Root_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Root_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Handled = true;
                return;
            }

            string[]? items = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (items is null || items.Length == 0)
            {
                e.Handled = true;
                return;
            }

            foreach (var item in items)
                await ViewModel.AddDroppedItemAsync(item);

            e.Handled = true;
        }

        private void LvFiles_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete)
                return;

            var selected = ViewModel?.SelectedEntry;

            if (selected is not null)
                ViewModel?.Entries.Remove(selected);
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.IsLocked)
            {
                ViewModel.Cancel();
                return;
            }

            await ViewModel.StartAsync(BuildMode.FullProcess);
        }

        private async void BtnUnpack_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.IsLocked)
            {
                ViewModel.Cancel();
                return;
            }

            await ViewModel.StartAsync(BuildMode.UnpackOnly);
        }

        private async void BtnRebuild_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.IsLocked)
            {
                ViewModel.Cancel();
                return;
            }

            await ViewModel.StartAsync(BuildMode.RebuildOnly);
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            var selected = ViewModel?.SelectedEntry;

            if (selected is not null)
                ViewModel?.Entries.Remove(selected);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e) => ViewModel?.Entries.Clear();

        private void BtnRemovePatch_Click(object sender, RoutedEventArgs e)
        {
            if(ViewModel?.SelectedEntry != null)
                ViewModel.SelectedEntry.PatchPath = null;
        }

        private void LvFiles_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var selected = ViewModel?.SelectedEntry;

            if (selected == null)
                e.Handled = true;
        }

        private void MenuItem_OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var selected = ViewModel?.SelectedEntry;

            if (selected == null)
                return;

            string? dir = Path.GetDirectoryName(selected.FilePath);

            dir?.OpenFolder();
        }
    }
}