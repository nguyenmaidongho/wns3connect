using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WNS3Connect.Models;
using WNS3Connect.Services;
using WNS3Connect.ViewModels;

namespace WNS3Connect.Views;

public partial class MainWindow : FluentAvalonia.UI.Windowing.FAAppWindow
{
    private readonly MainViewModel _vm = new();
    private DispatcherTimer? _hoverTimer;
    private S3Entry? _hoverEntry;
    private double _bottomHeight = 210;
    private bool _bottomCollapsed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // Thanh tiêu đề liền với nội dung (kiểu Explorer / Settings của Windows 11).
        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.Height = 48;

        // Tiến độ upload/download hiện luôn trên nút ứng dụng ở taskbar.
        var taskbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        taskbarTimer.Tick += (_, _) => UpdateTaskbar();
        taskbarTimer.Start();
        Dialogs.Owner = this;

        // Một lần: cửa sổ mở ra nhỏ hơn 10% so với kích thước đã lưu.
        // (Cài mới đã có sẵn kích thước nhỏ 990×684 nên không thu nhỏ thêm.)
        if (_vm.Settings.LayoutVersion < 2)
        {
            if (_vm.Settings.WindowWidth > 1000)
            {
                _vm.Settings.WindowWidth = Math.Round(_vm.Settings.WindowWidth * 0.9);
                _vm.Settings.WindowHeight = Math.Round(_vm.Settings.WindowHeight * 0.9);
            }
            _vm.Settings.SidebarWidth = 230;
            _vm.Settings.LayoutVersion = 2;
            SettingsStore.Save(_vm.Settings);
        }
        ApplySidebar();

        Width = Math.Max(MinWidth, _vm.Settings.WindowWidth);
        Height = Math.Max(MinHeight, _vm.Settings.WindowHeight);
        if (_vm.Settings.WindowMaximized) WindowState = WindowState.Maximized;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        DragDrop.SetAllowDrop(FileList, true);
        FileList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        FileList.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropHint.IsVisible = false);
        FileList.AddHandler(DragDrop.DropEvent, OnDrop);

        KeyDown += OnWindowKeyDown;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Sort) or nameof(MainViewModel.SortDesc)) UpdateHeaders();
            if (e.PropertyName == nameof(MainViewModel.TaskFilter)) UpdateTaskFilterButtons();
            if (e.PropertyName == nameof(MainViewModel.CurrentAccount)) RebuildAccountsMenu();
            if (e.PropertyName == nameof(MainViewModel.ActiveTaskCount)) UpdateTasksTab();
        };
        _vm.AccountNodes.CollectionChanged += (_, _) => RebuildAccountsMenu();
        _vm.Tasks.CollectionChanged += (_, e) =>
        {
            // Có tác vụ mới mà panel đang thu gọn / ở tab khác → mở ra tab Tác vụ.
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                if (_bottomCollapsed) SetBottomCollapsed(false);
                BottomTabs.SelectedIndex = 0;
            }
        };
        UpdateTaskFilterButtons();
        RebuildAccountsMenu();
        RebuildParallelMenu();

        Opened += async (_, _) => await _vm.StartAsync();
        Closing += (_, _) =>
        {
            _vm.Settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) { _vm.Settings.WindowWidth = Width; _vm.Settings.WindowHeight = Height; }
            if (!_vm.Settings.SidebarCollapsed) _vm.Settings.SidebarWidth = BodyGrid.ColumnDefinitions[0].ActualWidth;
            SettingsStore.Save(_vm.Settings);
            try { Directory.Delete(AppPaths.TempDir, true); } catch { }
        };
    }

    private void ApplySidebar()
    {
        var cols = BodyGrid.ColumnDefinitions;
        bool hidden = _vm.Settings.SidebarCollapsed;
        cols[0].Width = new GridLength(hidden ? 0 : Math.Clamp(_vm.Settings.SidebarWidth, 160, 480));
        cols[1].Width = new GridLength(hidden ? 0 : 6);
    }

    private void ToggleSidebar_Click(object? s, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        if (!_vm.Settings.SidebarCollapsed) _vm.Settings.SidebarWidth = BodyGrid.ColumnDefinitions[0].ActualWidth;
        _vm.Settings.SidebarCollapsed = !_vm.Settings.SidebarCollapsed;
        ApplySidebar();
        SettingsStore.Save(_vm.Settings);
    }

    private bool _taskbarActive;

    private void UpdateTaskbar()
    {
        var pf = PlatformFeatures;
        if (pf == null) return;
        long done = 0, total = 0;
        bool any = false;
        foreach (var t in _vm.Tasks)
        {
            if (t.State is not (TransferStatus.Running or TransferStatus.Queued)) continue;
            any = true;
            done += t.DoneBytes;
            total += Math.Max(t.TotalBytes, t.DoneBytes);
        }
        if (any)
        {
            if (total > 0)
            {
                pf.SetTaskBarProgressBarState(FluentAvalonia.UI.Windowing.FATaskBarProgressBarState.Normal);
                pf.SetTaskBarProgressBarValue((ulong)done, (ulong)total);
            }
            else pf.SetTaskBarProgressBarState(FluentAvalonia.UI.Windowing.FATaskBarProgressBarState.Indeterminate);
            _taskbarActive = true;
        }
        else if (_taskbarActive)
        {
            pf.SetTaskBarProgressBarState(FluentAvalonia.UI.Windowing.FATaskBarProgressBarState.None);
            _taskbarActive = false;
        }
    }

    private List<S3Entry> Selected() => FileList.SelectedItems?.OfType<S3Entry>().ToList() ?? new();

    private static T? Ctx<T>(object? sender) where T : class => (sender as StyledElement)?.DataContext as T;

    // ================= Menu =================

    /// <summary>Menu "Tài khoản": danh sách mọi tài khoản (bấm để chuyển) + thêm/sửa/xóa.</summary>
    private void RebuildAccountsMenu()
    {
        var items = new List<object>();
        foreach (var n in _vm.AccountNodes)
        {
            var node = n;
            var mi = new MenuItem
            {
                Header = node.Name,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = _vm.CurrentAccount?.Id == node.Account.Id
            };
            mi.Click += async (_, _) => await _vm.ActivateAsync(node);
            items.Add(mi);
        }
        if (items.Count > 0) items.Add(new Separator());
        items.Add(Item("Thêm tài khoản…", async () => await _vm.AddAccountAsync()));
        items.Add(Item("Sửa tài khoản đang dùng…", async () => await _vm.EditAccountAsync(), _vm.HasAccount));
        items.Add(Item("Xóa tài khoản đang dùng…", async () => await _vm.DeleteAccountAsync(), _vm.HasAccount));
        AccountsMenu.ItemsSource = items;
    }

    private static MenuItem Item(string header, Action click, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        mi.Click += (_, _) => click();
        return mi;
    }

    private void RebuildParallelMenu()
    {
        ParallelMenu.ItemsSource = new[] { 1, 2, 3, 4, 6, 8, 12, 16 }.Select(n =>
        {
            var mi = new MenuItem { Header = n + " file", ToggleType = MenuItemToggleType.Radio, IsChecked = _vm.Queue.Parallel == n };
            mi.Click += (_, _) => { _vm.SetParallel(n); RebuildParallelMenu(); };
            return mi;
        }).ToList();
    }

    private void SetTheme(string t)
    {
        _vm.Settings.Theme = t;
        SettingsStore.Save(_vm.Settings);
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = t switch { "dark" => ThemeVariant.Dark, "system" => ThemeVariant.Default, _ => ThemeVariant.Light };
    }

    private void ThemeLight_Click(object? s, RoutedEventArgs e) => SetTheme("light");
    private void ThemeDark_Click(object? s, RoutedEventArgs e) => SetTheme("dark");
    private void ThemeSystem_Click(object? s, RoutedEventArgs e) => SetTheme("system");

    private async void ClearResticCache_Click(object? s, RoutedEventArgs e)
    {
        // Cache chỉ chứa index/snapshot đã mã hóa (đọc lại được từ cloud) → xóa thoải mái.
        string dir = Path.Combine(AppPaths.LocalDir, "restic-cache");
        long size = 0;
        try { if (Directory.Exists(dir)) size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
        if (!await Dialogs.ConfirmAsync($"Xóa cache kho restic ({S3Entry.FormatSize(size)})?\nCache chỉ giúp mở kho nhanh hơn, xóa đi lần mở sau sẽ tải lại index từ cloud.", "Xóa cache")) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); _vm.Log("Đã xóa cache kho restic"); }
        catch (Exception ex) { await Dialogs.MessageAsync("Không xóa hết được cache (có thể đang mở kho): " + ex.Message); }
    }

    private void OpenDataDir_Click(object? s, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }); } catch { }
    }

    private async void Shortcuts_Click(object? s, RoutedEventArgs e) => await Dialogs.MessageAsync(
        "Enter: mở thư mục / file\nBackspace: lên một cấp\nAlt+← / Alt+→: quay lại / tới\nF5: làm mới\nCtrl+U: tải lên file\nCtrl+D: tải về\nDelete: xóa\nCtrl+Shift+N: thư mục mới\nCtrl+F: lọc theo tên\nCtrl+L: gõ đường dẫn\nCtrl+A: chọn tất cả\n\nKéo thả file/thư mục từ Explorer vào danh sách để tải lên.\nChuột phải vào tài khoản / bucket bên trái để sửa, làm mới, xóa.",
        "Phím tắt");

    private async void About_Click(object? s, RoutedEventArgs e) => await Dialogs.MessageAsync(
        $"WNS3Connect {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\nWebNow S3 Connect — quản lý S3 / S3-compatible (CloudFly, AWS, R2, MinIO…).\n\nDữ liệu: {AppPaths.DataDir}\nhttps://webnow.vn",
        "Giới thiệu");

    private async void Cloud_Click(object? s, RoutedEventArgs e)
    {
        await new WebNowDialog(_vm.Cloud, force => _vm.SyncAsync(force)).ShowDialog(this);
        _vm.UpdateCloudText();
    }

    // ================= Sidebar: tài khoản / bucket =================

    private async void AddAccount_Click(object? s, RoutedEventArgs e) => await _vm.AddAccountAsync();

    private async void AccountRow_Tapped(object? sender, TappedEventArgs e)
    {
        if (Ctx<AccountNode>(sender) is { } n) await _vm.ActivateAsync(n);
    }

    private async void AccountRefresh_Click(object? s, RoutedEventArgs e) { if (Ctx<AccountNode>(s) is { } n) await _vm.RefreshAccountAsync(n); }
    private async void AccountEdit_Click(object? s, RoutedEventArgs e) { if (Ctx<AccountNode>(s) is { } n) await _vm.EditAccountAsync(n); }
    private async void AccountDelete_Click(object? s, RoutedEventArgs e) { if (Ctx<AccountNode>(s) is { } n) await _vm.DeleteAccountAsync(n); }

    private async void AccountNewBucket_Click(object? s, RoutedEventArgs e)
    {
        if (Ctx<AccountNode>(s) is not { } n) return;
        await _vm.ActivateAsync(n);
        await _vm.NewBucketAsync();
    }

    private async void AccountAddExternal_Click(object? s, RoutedEventArgs e)
    {
        if (Ctx<AccountNode>(s) is not { } n) return;
        await _vm.ActivateAsync(n);
        await _vm.AddExternalBucketAsync();
    }

    private async void BucketRow_Tapped(object? sender, TappedEventArgs e)
    {
        if (Ctx<BucketNode>(sender) is { } b) { e.Handled = true; await _vm.OpenBucketNodeAsync(b); }
    }

    private async void BucketOpen_Click(object? s, RoutedEventArgs e) { if (Ctx<BucketNode>(s) is { } b) await _vm.OpenBucketNodeAsync(b); }
    private async void BucketCopy_Click(object? s, RoutedEventArgs e) { if (Ctx<BucketNode>(s) is { } b) await Dialogs.SetClipboardAsync(b.Name); }

    private async void BucketDelete_Click(object? s, RoutedEventArgs e)
    {
        if (Ctx<BucketNode>(s) is not { } b) return;
        await _vm.OpenBucketNodeAsync(b);
        await _vm.DeleteBucketAsync(b.Name);
    }

    private async void NewBucket_Click(object? s, RoutedEventArgs e) => await _vm.NewBucketAsync();
    private async void AddExternal_Click(object? s, RoutedEventArgs e) => await _vm.AddExternalBucketAsync();

    private async void DeleteBucket_Click(object? s, RoutedEventArgs e)
    {
        if (_vm.SelectedBucket is { } b) await _vm.DeleteBucketAsync(b);
    }

    // ================= Điều hướng / breadcrumb =================

    private async void Back_Click(object? s, RoutedEventArgs e) => await _vm.GoBackAsync();
    private async void Forward_Click(object? s, RoutedEventArgs e) => await _vm.GoForwardAsync();
    private async void Up_Click(object? s, RoutedEventArgs e) => await _vm.GoUpAsync();
    private async void Refresh_Click(object? s, RoutedEventArgs e) => await _vm.RefreshAsync();
    private void StopLoading_Click(object? s, RoutedEventArgs e) => _vm.StopLoading();

    private async void Crumb_Click(object? sender, RoutedEventArgs e)
    {
        if (Ctx<Crumb>(sender) is { } c && _vm.SelectedBucket is { } b && c.Prefix != _vm.CurrentPrefix)
            await _vm.OpenFolderAsync(b, c.Prefix);
    }

    private void ShowPathBox()
    {
        if (_vm.SelectedBucket == null) return;
        PathBox.Text = _vm.SelectedBucket + "/" + _vm.CurrentPrefix;
        CrumbBar.IsVisible = false;
        PathBox.IsVisible = true;
        PathBox.Focus();
        PathBox.SelectAll();
    }

    private void HidePathBox()
    {
        PathBox.IsVisible = false;
        CrumbBar.IsVisible = true;
    }

    private void EditPath_Click(object? s, RoutedEventArgs e) => ShowPathBox();
    private void CrumbBar_DoubleTapped(object? s, TappedEventArgs e) => ShowPathBox();
    private void PathBox_LostFocus(object? s, RoutedEventArgs e) => HidePathBox();

    private async void PathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            string text = PathBox.Text ?? "";
            HidePathBox();
            await _vm.GoToPathAsync(text);
            FileList.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HidePathBox();
            FileList.Focus();
        }
    }

    private void Header_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string t } && Enum.TryParse<SortColumn>(t, out var c)) _vm.SortBy(c);
    }

    private void UpdateHeaders()
    {
        string Mark(SortColumn c) => _vm.Sort == c ? (_vm.SortDesc ? " ▼" : " ▲") : "";
        HName.Text = "Tên" + Mark(SortColumn.Name);
        HSize.Text = "Kích thước" + Mark(SortColumn.Size);
        HType.Text = "Loại" + Mark(SortColumn.Type);
        HModified.Text = "Sửa lần cuối" + Mark(SortColumn.Modified);
        HStorageClass.Text = "Storage class" + Mark(SortColumn.StorageClass);
    }

    // ================= Danh sách file =================

    private async void FileList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control c && c.FindAncestorOfType<ListBoxItem>() is { DataContext: S3Entry entry })
            await _vm.OpenEntryAsync(entry);
    }

    private async void FileList_KeyDown(object? sender, KeyEventArgs e)
    {
        var sel = Selected();
        switch (e.Key)
        {
            case Key.Enter when sel.Count == 1:
                e.Handled = true;
                await _vm.OpenEntryAsync(sel[0]);
                break;
            case Key.Back:
                e.Handled = true;
                await _vm.GoUpAsync();
                break;
            case Key.Delete when sel.Count > 0:
                e.Handled = true;
                await _vm.DeleteAsync(sel);
                break;
        }
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (e.Key == Key.F5) { e.Handled = true; await _vm.RefreshAsync(); }
        else if (alt && e.Key == Key.Left) { e.Handled = true; await _vm.GoBackAsync(); }
        else if (alt && e.Key == Key.Right) { e.Handled = true; await _vm.GoForwardAsync(); }
        else if (alt && e.Key == Key.Up) { e.Handled = true; await _vm.GoUpAsync(); }
        else if (ctrl && e.Key == Key.F) { e.Handled = true; FilterBox.Focus(); FilterBox.SelectAll(); }
        else if (ctrl && e.Key == Key.L) { e.Handled = true; ShowPathBox(); }
        else if (ctrl && e.Key == Key.B) { e.Handled = true; ToggleSidebar(); }
        else if (ctrl && e.Key == Key.U && _vm.HasBucket) { e.Handled = true; await _vm.UploadFilesAsync(); }
        else if (ctrl && e.Key == Key.D && _vm.HasBucket) { e.Handled = true; await _vm.DownloadAsync(Selected()); }
        else if (ctrl && shift && e.Key == Key.N && _vm.HasBucket) { e.Handled = true; await _vm.NewFolderAsync(); }
    }

    private async void FileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var sel = Selected();
        bool any = sel.Count > 0;
        DownloadBtn.IsEnabled = any;
        DeleteBtn.IsEnabled = any;
        OpenBtn.IsEnabled = sel.Count == 1;
        LinkBtn.IsEnabled = sel.Count == 1 && !sel[0].IsFolder;
        await _vm.ShowPropertiesAsync(sel.Count == 1 ? sel[0] : null);
    }

    /// <summary>Rê chuột lên thư mục ~150ms → nạp trước trang đầu để bấm vào là hiện ngay.</summary>
    private void Row_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { DataContext: S3Entry { IsFolder: true } entry }) return;
        _hoverEntry = entry;
        _hoverTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _hoverTimer!.Stop();
            if (_hoverEntry != null) _vm.Prefetch(_hoverEntry);
        });
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    // ================= Thao tác file =================

    private async void UploadFiles_Click(object? s, RoutedEventArgs e) => await _vm.UploadFilesAsync();
    private async void UploadFolder_Click(object? s, RoutedEventArgs e) => await _vm.UploadFolderAsync();
    private async void Download_Click(object? s, RoutedEventArgs e) => await _vm.DownloadAsync(Selected());
    private void OpenRestic_Click(object? s, RoutedEventArgs e) => _vm.OpenRestic();
    private void OpenResticSelected_Click(object? s, RoutedEventArgs e)
    {
        // Chọn 1 thư mục → mở thư mục đó; không chọn gì → thư mục đang xem.
        var sel = Selected();
        _vm.OpenRestic(sel.Count == 1 && sel[0].IsFolder ? sel[0].Key : null);
    }
    private async void Delete_Click(object? s, RoutedEventArgs e) => await _vm.DeleteAsync(Selected());
    private async void NewFolder_Click(object? s, RoutedEventArgs e) => await _vm.NewFolderAsync();

    private async void Open_Click(object? s, RoutedEventArgs e)
    {
        if (Selected() is [var one]) await _vm.OpenEntryAsync(one);
    }

    private async void CopyPresigned_Click(object? s, RoutedEventArgs e)
    {
        if (Selected() is [{ IsFolder: false } one]) await _vm.CopyUrlAsync(one, presigned: true);
    }

    private async void CopyS3Path_Click(object? s, RoutedEventArgs e)
    {
        if (Selected() is [var one]) await _vm.CopyUrlAsync(one, presigned: false);
    }

    private static IEnumerable<string> DroppedPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!)
        ?? Enumerable.Empty<string>();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool ok = _vm.HasBucket && DroppedPaths(e).Any();
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.IsVisible = ok;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropHint.IsVisible = false;
        var paths = DroppedPaths(e).ToList();
        if (paths.Count > 0) _vm.UploadPaths(paths);
        e.Handled = true;
    }

    // ================= Panel dưới =================

    private void BottomTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TasksPane == null) return;
        int i = BottomTabs.SelectedIndex;
        TasksPane.IsVisible = i == 0;
        TaskTools.IsVisible = i == 0;
        PropsPane.IsVisible = i == 1;
        LogPane.IsVisible = i == 2;
        if (_bottomCollapsed && i >= 0) SetBottomCollapsed(false);
    }

    private void UpdateTasksTab()
    {
        if (BottomTabs.Items[0] is TabStripItem t)
            t.Content = _vm.ActiveTaskCount > 0 ? $"Tác vụ ({_vm.ActiveTaskCount})" : "Tác vụ";
    }

    private void ToggleBottom_Click(object? s, RoutedEventArgs e) => SetBottomCollapsed(!_bottomCollapsed);

    private void SetBottomCollapsed(bool collapsed)
    {
        var row = RootGrid.RowDefinitions[3];
        if (collapsed)
        {
            if (row.Height.Value > 60) _bottomHeight = row.Height.Value;
            row.Height = new GridLength(46);
        }
        else row.Height = new GridLength(Math.Max(150, _bottomHeight));
        _bottomCollapsed = collapsed;
        ToggleBottomText.Text = collapsed ? "▴" : "▾";
        BottomSplitter.IsEnabled = !collapsed;
    }

    private List<TransferTask> SelectedTasks() => TaskList.SelectedItems?.OfType<TransferTask>().ToList() ?? new();

    private void TaskFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string t }) return;
        _vm.TaskFilter = t == "All" ? null : Enum.Parse<TransferStatus>(t);
        UpdateTaskFilterButtons();
    }

    private void UpdateTaskFilterButtons()
    {
        var f = _vm.TaskFilter;
        FRunning.IsChecked = f == TransferStatus.Running;
        FQueued.IsChecked = f == TransferStatus.Queued;
        FStopped.IsChecked = f == TransferStatus.Stopped;
        FFailed.IsChecked = f == TransferStatus.Failed;
        FDone.IsChecked = f == TransferStatus.Done;
        FAll.IsChecked = f == null;
    }

    private void StartAll_Click(object? s, RoutedEventArgs e) => _vm.StartAll();
    private void StopAll_Click(object? s, RoutedEventArgs e) => _vm.StopAll();
    private void CancelAll_Click(object? s, RoutedEventArgs e) => _vm.CancelAll();
    private void ClearDone_Click(object? s, RoutedEventArgs e) => _vm.ClearFinished();
    private void TaskStart_Click(object? s, RoutedEventArgs e) { foreach (var t in SelectedTasks()) _vm.Queue.Start(t); }
    private void TaskStop_Click(object? s, RoutedEventArgs e) { foreach (var t in SelectedTasks()) _vm.Queue.Stop(t); }

    private void TaskRemove_Click(object? s, RoutedEventArgs e)
    {
        foreach (var t in SelectedTasks())
        {
            _vm.Queue.Stop(t);
            _vm.Queue.Remove(t);
            _vm.Tasks.Remove(t);
            _vm.VisibleTasks.Remove(t);
        }
    }

    private void TaskReveal_Click(object? s, RoutedEventArgs e)
    {
        if (SelectedTasks() is not [var t]) return;
        try
        {
            if (File.Exists(t.LocalPath)) Process.Start("explorer.exe", $"/select,\"{t.LocalPath}\"");
            else if (Path.GetDirectoryName(t.LocalPath) is { } d && Directory.Exists(d)) Process.Start(new ProcessStartInfo(d) { UseShellExecute = true });
        }
        catch { }
    }
}
