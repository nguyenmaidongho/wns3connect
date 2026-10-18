using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WNS3Connect.Services;
using WNS3Connect.ViewModels;

namespace WNS3Connect.Views;

public partial class ResticWindow : Window
{
    private readonly ResticViewModel _vm;

    public ResticWindow() : this(null!) { }

    public ResticWindow(ResticViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Opened += async (_, _) =>
        {
            PasswordBox.Focus();
            if (_vm != null) await _vm.StartAsync();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.D && e.KeyModifiers == KeyModifiers.Control) { Restore_Click(null, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control) { SearchBox.Focus(); e.Handled = true; }
        };
    }

    private List<ResticEntry> Selected() => EntryList.SelectedItems?.OfType<ResticEntry>().ToList() ?? new();

    private async void Unlock_Click(object? s, RoutedEventArgs e) => await _vm.UnlockAsync();
    private void Close_Click(object? s, RoutedEventArgs e) => Close();
    private async void PasswordBox_KeyDown(object? s, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await _vm.UnlockAsync(); } }
    private async void ReloadSnapshots_Click(object? s, RoutedEventArgs e) => await _vm.ReloadSnapshotsAsync();
    private void Forget_Click(object? s, RoutedEventArgs e) => _vm.ForgetPassword();
    private async void Search_Click(object? s, RoutedEventArgs e) => await _vm.SearchAsync();

    private async void Search_KeyDown(object? s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await _vm.SearchAsync(); }
        else if (e.Key == Key.Escape && _vm.SearchText.Length > 0) { e.Handled = true; _vm.SearchText = ""; await _vm.OpenDirAsync(_vm.CurrentPath); }
    }

    private async void Up_Click(object? s, RoutedEventArgs e) => await _vm.UpAsync();
    private async void Crumb_Click(object? s, RoutedEventArgs e) { if (s is Button { Tag: string p }) await _vm.OpenDirAsync(p); }

    private void EntryList_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        var sel = Selected();
        RestoreBtn.IsEnabled = sel.Count > 0;
        OpenFileBtn.IsEnabled = sel.Count == 1;
    }

    private async Task OpenSelectedAsync()
    {
        var sel = Selected();
        if (sel.Count != 1) return;
        if (sel[0].IsFolder) await _vm.OpenDirAsync(sel[0].Path);
        else _vm.OpenFile(sel[0]);
    }

    private async void EntryList_DoubleTapped(object? s, TappedEventArgs e) => await OpenSelectedAsync();
    private async void OpenFile_Click(object? s, RoutedEventArgs e) => await OpenSelectedAsync();

    private async void EntryList_KeyDown(object? s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await OpenSelectedAsync(); }
        else if (e.Key == Key.Back) { e.Handled = true; await _vm.UpAsync(); }
    }

    private async Task<string?> PickTargetAsync()
    {
        // Test: bỏ qua hộp chọn thư mục.
        if (Environment.GetEnvironmentVariable("WNS3_TEST_RESTORE_DIR") is { Length: > 0 } testDir) return testDir;
        var settings = _vm.Settings;
        var opts = new FolderPickerOpenOptions { Title = "Khôi phục vào thư mục", AllowMultiple = false };
        if (!string.IsNullOrEmpty(settings?.DownloadFolder) && Directory.Exists(settings.DownloadFolder))
            opts.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(settings.DownloadFolder);
        var dir = (await StorageProvider.OpenFolderPickerAsync(opts)).FirstOrDefault()?.TryGetLocalPath();
        if (dir != null && settings != null) { settings.DownloadFolder = dir; SettingsStore.Save(settings); }
        return dir;
    }

    private async void Restore_Click(object? s, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (await PickTargetAsync() is { } dir) _vm.Restore(sel, dir);
    }

    private async void RestoreAll_Click(object? s, RoutedEventArgs e)
    {
        var all = _vm.Entries.ToList();
        if (all.Count == 0) return;
        // Cả thư mục → tạo thư mục con cùng tên trong nơi lưu (không đổ lẫn vào thư mục đích).
        if (await PickTargetAsync() is { } dir) _vm.Restore(all, _vm.CurrentFolderTarget(dir));
    }

    private async void CopyPath_Click(object? s, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0 || Clipboard == null) return;
        await Clipboard.SetTextAsync(string.Join(Environment.NewLine, sel.Select(x => x.Path)));
    }
}
