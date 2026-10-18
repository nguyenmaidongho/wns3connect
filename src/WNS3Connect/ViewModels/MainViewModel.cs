using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WNS3Connect.Models;
using WNS3Connect.Services;
using WNS3Connect.Services.Restic;
using WNS3Connect.Views;

namespace WNS3Connect.ViewModels;

public enum SortColumn { Name, Size, Type, Modified, StorageClass }

public partial class MainViewModel : ObservableObject
{
    public AppSettings Settings { get; } = SettingsStore.Load();
    public AccountStore Store { get; } = AccountStore.Load();
    public TransferQueue Queue { get; } = new();
    public WebNowAccount Cloud { get; }

    /// <summary>Tất cả tài khoản trên sidebar (mỗi tài khoản mở rộng ra danh sách bucket).</summary>
    public ObservableCollection<AccountNode> AccountNodes { get; } = new();
    public ObservableCollection<Crumb> Crumbs { get; } = new();
    public RangeCollection<S3Entry> Items { get; } = new();
    public ObservableCollection<TransferTask> Tasks { get; } = new();
    public RangeCollection<TransferTask> VisibleTasks { get; } = new();
    public ObservableCollection<string> EventLog { get; } = new();

    [ObservableProperty] private S3Account? _currentAccount;
    [ObservableProperty] private string? _selectedBucket;
    [ObservableProperty] private string _currentPrefix = "";
    [ObservableProperty] private string _pathText = "/";
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _propertiesText = "Chọn một file để xem thuộc tính.";
    [ObservableProperty] private TransferStatus? _taskFilter;
    [ObservableProperty] private string _taskSummary = "";
    [ObservableProperty] private int _activeTaskCount;
    [ObservableProperty] private SortColumn _sort = SortColumn.Name;
    [ObservableProperty] private bool _sortDesc;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty] private string _cloudText = "Đăng nhập WebNow";
    [ObservableProperty] private bool _isCloudLoggedIn;
    /// <summary>Thư mục đang mở là một kho restic (có config, keys/, snapshots/…) → hiện thanh "Mở kho restic".</summary>
    [ObservableProperty] private bool _isResticHere;
    /// <summary>Kho restic nằm ngay trong thư mục con "restic/" (thư mục WebNowBackup/&lt;máy chủ&gt;/) → gợi ý mở luôn.</summary>
    [ObservableProperty] private bool _isResticChild;
    public bool ShowResticBar => IsResticHere || IsResticChild;
    public string ResticBarText => IsResticHere
        ? "Thư mục này là một kho restic (sao lưu WebNowBackup, đã mã hóa). Mở bằng mật khẩu kho để xem snapshot và tải file về."
        : "Thư mục restic/ ở đây là kho sao lưu WebNowBackup (đã mã hóa). Mở bằng mật khẩu kho để xem snapshot và tải file về.";
    partial void OnIsResticHereChanged(bool value) { OnPropertyChanged(nameof(ShowResticBar)); OnPropertyChanged(nameof(ResticBarText)); }
    partial void OnIsResticChildChanged(bool value) { OnPropertyChanged(nameof(ShowResticBar)); OnPropertyChanged(nameof(ResticBarText)); }

    public string WindowTitle => CurrentAccount == null ? "WNS3Connect" : $"WNS3Connect — {CurrentAccount.DisplayName}";
    public bool HasAccount => CurrentAccount != null;
    public bool HasBucket => SelectedBucket != null;
    public bool HasNoAccounts => AccountNodes.Count == 0;
    public bool IsEmptyFolder => !IsLoading && HasBucket && Items.Count == 0;

    private S3Browser? _browser;
    private readonly Dictionary<Guid, S3Browser> _browsers = new();
    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _propsCts;
    private List<S3Entry> _all = new();
    private FolderListing? _shown;
    private readonly List<(string bucket, string prefix)> _back = new();
    private readonly List<(string bucket, string prefix)> _forward = new();
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _refreshSoon;
    private readonly HashSet<string> _dirtyFolders = new();
    private DateTime _lastTick = DateTime.UtcNow;
    private readonly DispatcherTimer _autoSync;

    public MainViewModel()
    {
        Queue.Parallel = Math.Clamp(Settings.ParallelTransfers, 1, 16);
        Queue.Log += msg => Dispatcher.UIThread.Post(() => Log(msg));
        Queue.Added += t => Dispatcher.UIThread.Post(() => Tasks.Add(t));
        Queue.Finished += OnTransferFinished;

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        _refreshSoon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _refreshSoon.Tick += (_, _) => { _refreshSoon.Stop(); FlushDirtyFolders(); };

        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmptyFolder));
        AccountNodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoAccounts));
        BuildNodes();

        Cloud = new WebNowAccount(Settings, Store);
        _autoSync = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoSync.Tick += async (_, _) => { _autoSync.Stop(); await SyncAsync(false, silent: true); };
        Store.ChangedByUser += () => { if (Cloud.IsLoggedIn) { _autoSync.Stop(); _autoSync.Start(); } };
        UpdateCloudText();
    }

    public void UpdateCloudText()
    {
        IsCloudLoggedIn = Cloud.IsLoggedIn;
        CloudText = Cloud.IsLoggedIn ? Cloud.Email : "Đăng nhập WebNow";
    }

    /// <summary>Đồng bộ danh sách tài khoản S3 với tài khoản WebNow. silent: lỗi chỉ ghi nhật ký.</summary>
    public async Task<WebNowSyncResult?> SyncAsync(bool force, bool silent = false)
    {
        if (!Cloud.IsLoggedIn) { UpdateCloudText(); return null; }
        try
        {
            var r = await Cloud.SyncAsync(forceNewVersion: force);
            if (r.Added + r.Updated + r.Deleted > 0) await ReloadAccountsAsync();
            Log($"Đồng bộ WebNow: {r.Total} tài khoản S3, bản #{r.ServerVersion}" + (r.Pushed ? " (đã backup)" : ""));
            return r;
        }
        catch (Exception ex)
        {
            Log("Đồng bộ WebNow lỗi: " + ex.Message);
            if (!silent) throw;
            return null;
        }
        finally { UpdateCloudText(); }
    }

    public void Log(string msg)
    {
        EventLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
        while (EventLog.Count > 2000) EventLog.RemoveAt(EventLog.Count - 1);
    }

    // ================= Tài khoản (sidebar) =================

    private AccountNode MakeNode(S3Account a) => new(a) { ExpandRequested = n => _ = LoadBucketsAsync(n) };

    /// <summary>Dựng lại sidebar từ AccountStore, giữ trạng thái mở rộng / bucket đã nạp.</summary>
    private void BuildNodes()
    {
        var old = AccountNodes.ToDictionary(n => n.Account.Id);
        AccountNodes.Clear();
        foreach (var a in Store.Accounts)
        {
            if (old.TryGetValue(a.Id, out var n)) { n.Account = a; AccountNodes.Add(n); }
            else AccountNodes.Add(MakeNode(a));
        }
    }

    private AccountNode? NodeOf(Guid id) => AccountNodes.FirstOrDefault(n => n.Account.Id == id);

    private S3Browser BrowserFor(S3Account a)
    {
        // Không Dispose bản cũ: tác vụ upload/download đang chạy có thể vẫn dùng client đó.
        if (!_browsers.TryGetValue(a.Id, out var b) || !ReferenceEquals(b.Account, a))
            _browsers[a.Id] = b = new S3Browser(a);
        return b;
    }

    public async Task StartAsync()
    {
        if (Cloud.IsLoggedIn) await SyncAsync(false, silent: true);
        if (AccountNodes.Count == 0) { await AddAccountAsync(); return; }

        // Mở rộng mọi tài khoản, nạp bucket song song.
        var last = NodeOf(Settings.LastAccountId ?? Guid.Empty) ?? AccountNodes[0];
        foreach (var n in AccountNodes) if (n != last) n.IsExpanded = true;
        await ActivateAsync(last, Settings.LastBucket);

        // Debug/test: tự mở đường dẫn khi đặt WNS3_AUTOPATH=bucket/thu-muc/
        if (Environment.GetEnvironmentVariable("WNS3_AUTOPATH") is { Length: > 0 } auto)
            await GoToPathAsync(auto);
    }

    partial void OnCurrentAccountChanged(S3Account? value)
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(HasAccount));
        foreach (var n in AccountNodes) n.IsCurrent = value != null && n.Account.Id == value.Id;
    }

    partial void OnSelectedBucketChanged(string? value)
    {
        OnPropertyChanged(nameof(HasBucket));
        OnPropertyChanged(nameof(IsEmptyFolder));
        UpdateBucketHighlight();
        UpdateCrumbs();
    }

    partial void OnCurrentPrefixChanged(string value) => UpdateCrumbs();
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmptyFolder));

    private void UpdateBucketHighlight()
    {
        foreach (var n in AccountNodes)
            foreach (var b in n.Buckets)
                b.IsCurrent = CurrentAccount != null && n.Account.Id == CurrentAccount.Id && b.Name == SelectedBucket;
    }

    private void UpdateCrumbs()
    {
        Crumbs.Clear();
        if (SelectedBucket == null) return;
        var parts = CurrentPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Crumbs.Add(new Crumb(SelectedBucket, "", true, parts.Length == 0));
        string acc = "";
        for (int i = 0; i < parts.Length; i++)
        {
            acc += parts[i] + "/";
            Crumbs.Add(new Crumb(parts[i], acc, false, i == parts.Length - 1));
        }
    }

    /// <summary>Chuyển sang làm việc với tài khoản này (nạp bucket nếu chưa), mở bucket chỉ định hoặc bucket đầu tiên.</summary>
    public async Task ActivateAsync(AccountNode node, string? bucket = null)
    {
        bool switching = CurrentAccount?.Id != node.Account.Id || _browser == null || !ReferenceEquals(_browser.Account, node.Account);
        if (switching)
        {
            _listCts?.Cancel();
            _browser = BrowserFor(node.Account);
            CurrentAccount = node.Account;
            Settings.LastAccountId = node.Account.Id;
            SettingsStore.Save(Settings);
            _back.Clear(); _forward.Clear(); UpdateNav();
            SelectedBucket = null;
            ShowEntries(null, new List<S3Entry>());
        }
        node.IsExpanded = true;
        if (!node.Loaded) await LoadBucketsAsync(node);
        if (CurrentAccount?.Id != node.Account.Id) return;
        string? target = bucket != null && node.Buckets.Any(b => b.Name == bucket) ? bucket : node.Buckets.FirstOrDefault()?.Name;
        if (target != null && (switching || SelectedBucket == null)) await OpenFolderAsync(target, "", pushHistory: false);
        else UpdateBucketHighlight();
    }

    public async Task OpenBucketNodeAsync(BucketNode b)
    {
        if (CurrentAccount?.Id != b.Owner.Account.Id)
        {
            _listCts?.Cancel();
            _browser = BrowserFor(b.Owner.Account);
            CurrentAccount = b.Owner.Account;
            Settings.LastAccountId = b.Owner.Account.Id;
            _back.Clear(); _forward.Clear(); UpdateNav();
            SelectedBucket = null;
        }
        if (b.Name == SelectedBucket && CurrentPrefix == "") return;
        await OpenFolderAsync(b.Name, "");
    }

    public async Task LoadBucketsAsync(AccountNode node)
    {
        if (node.IsLoading) return;
        var browser = BrowserFor(node.Account);
        node.IsLoading = true;
        node.Status = "";
        var list = new List<string>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            list = await browser.ListBucketsAsync(cts.Token);
        }
        catch (Exception ex)
        {
            node.Status = ex is OperationCanceledException ? "Không phản hồi sau 15 giây — kiểm tra endpoint / mạng." : S3ClientFactory.Describe(ex);
            Log($"[{node.Name}] Không liệt kê được bucket: {node.Status}");
        }
        finally { node.IsLoading = false; }

        var external = node.Account.ExternalBuckets.Where(e => !list.Contains(e)).ToList();
        node.Buckets.Clear();
        foreach (var b in list) node.Buckets.Add(new BucketNode(node, b, false));
        foreach (var e in external) node.Buckets.Add(new BucketNode(node, e, true));
        node.Loaded = true;
        if (node.Buckets.Count == 0 && node.Status.Length == 0) node.Status = "Chưa có bucket";
        UpdateBucketHighlight();
    }

    public async Task RefreshAccountAsync(AccountNode node)
    {
        node.Loaded = false;
        await LoadBucketsAsync(node);
    }

    public async Task AddAccountAsync()
    {
        if (Dialogs.Owner == null) return;
        var a = await new AccountDialog(null).ShowAsync(Dialogs.Owner);
        if (a == null) return;
        Store.Upsert(a);
        var node = MakeNode(a);
        AccountNodes.Add(node);
        Log("Đã thêm tài khoản " + a.DisplayName);
        await ActivateAsync(node);
    }

    public async Task EditAccountAsync(AccountNode? node = null)
    {
        node ??= CurrentAccount != null ? NodeOf(CurrentAccount.Id) : null;
        if (node == null || Dialogs.Owner == null) return;
        var a = await new AccountDialog(node.Account).ShowAsync(Dialogs.Owner);
        if (a == null) return;
        Store.Upsert(a);
        bool wasCurrent = CurrentAccount?.Id == a.Id;
        node.Account = a;
        _browsers.Remove(a.Id);
        Log("Đã sửa tài khoản " + a.DisplayName);
        node.Loaded = false;
        if (wasCurrent) { CurrentAccount = null; await ActivateAsync(node, SelectedBucket); }
        else await LoadBucketsAsync(node);
    }

    public async Task DeleteAccountAsync(AccountNode? node = null)
    {
        node ??= CurrentAccount != null ? NodeOf(CurrentAccount.Id) : null;
        if (node == null) return;
        var acc = node.Account;
        if (!await Dialogs.ConfirmAsync($"Xóa tài khoản \"{acc.DisplayName}\" khỏi WNS3Connect?\n(Dữ liệu trên S3 không bị ảnh hưởng.)", "Xóa", danger: true)) return;
        Store.Remove(acc.Id);
        AccountNodes.Remove(node);
        _browsers.Remove(acc.Id);
        Log("Đã xóa tài khoản " + acc.DisplayName);
        if (CurrentAccount?.Id == acc.Id)
        {
            _browser = null; CurrentAccount = null;
            SelectedBucket = null; ShowEntries(null, new List<S3Entry>());
            if (AccountNodes.FirstOrDefault() is { } next) await ActivateAsync(next);
        }
    }

    /// <summary>Dựng lại sidebar sau khi đồng bộ (giữ tài khoản đang mở nếu còn).</summary>
    public async Task ReloadAccountsAsync()
    {
        Guid? cur = CurrentAccount?.Id;
        BuildNodes();
        foreach (var n in AccountNodes.Where(n => n.IsExpanded && !n.Loaded)) _ = LoadBucketsAsync(n);
        var same = cur is Guid g ? NodeOf(g) : null;
        if (same == null) { if (AccountNodes.FirstOrDefault() is { } f) await ActivateAsync(f); }
        else if (!ReferenceEquals(same.Account, CurrentAccount))
        {
            _browser = BrowserFor(same.Account);
            CurrentAccount = same.Account;
        }
    }

    // ================= Bucket =================

    private AccountNode? CurrentNode => CurrentAccount != null ? NodeOf(CurrentAccount.Id) : null;

    public async Task NewBucketAsync()
    {
        if (_browser == null || CurrentNode is not { } node) return;
        var name = await Dialogs.PromptAsync("Tên bucket mới (chữ thường, số, dấu -):", "Tạo bucket", "", "Tạo");
        name = name?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            await _browser.Client.PutBucketAsync(new Amazon.S3.Model.PutBucketRequest { BucketName = name, UseClientRegion = true });
            Log("Đã tạo bucket " + name);
            await RefreshAccountAsync(node);
            await OpenFolderAsync(name, "");
        }
        catch (Exception ex) { await Dialogs.MessageAsync("Không tạo được bucket: " + S3ClientFactory.Describe(ex)); }
    }

    public async Task AddExternalBucketAsync()
    {
        if (CurrentNode is not { } node) return;
        var name = await Dialogs.PromptAsync("Tên bucket (dùng khi key không có quyền liệt kê bucket):", "Thêm bucket ngoài", "", "Thêm");
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var acc = node.Account.Clone();
        if (!acc.ExternalBuckets.Contains(name)) acc.ExternalBuckets.Add(name);
        Store.Upsert(acc);
        node.Account = acc;
        CurrentAccount = acc;
        _browser?.Account.ExternalBuckets.Add(name);
        if (node.Buckets.All(b => b.Name != name)) node.Buckets.Add(new BucketNode(node, name, true));
        await OpenFolderAsync(name, "");
    }

    public async Task DeleteBucketAsync(string bucket)
    {
        if (_browser == null || CurrentNode is not { } node) return;
        var acc = node.Account;
        if (acc.ExternalBuckets.Contains(bucket))
        {
            if (!await Dialogs.ConfirmAsync($"Bỏ bucket \"{bucket}\" khỏi danh sách? (Không xóa dữ liệu.)", "Bỏ")) return;
            var c = acc.Clone();
            c.ExternalBuckets.Remove(bucket);
            Store.Upsert(c);
            node.Account = c;
            CurrentAccount = c;
            _browser.Account.ExternalBuckets.Remove(bucket);
            if (node.Buckets.FirstOrDefault(b => b.Name == bucket) is { } bn) node.Buckets.Remove(bn);
            return;
        }
        if (!await Dialogs.ConfirmAsync($"XÓA HẲN bucket \"{bucket}\" trên máy chủ?\nBucket phải rỗng mới xóa được.", "Xóa bucket", danger: true)) return;
        try
        {
            await _browser.Client.DeleteBucketAsync(new Amazon.S3.Model.DeleteBucketRequest { BucketName = bucket });
            Log("Đã xóa bucket " + bucket);
            _browser.InvalidateBucket(bucket);
            if (SelectedBucket == bucket) { SelectedBucket = null; ShowEntries(null, new List<S3Entry>()); }
            await RefreshAccountAsync(node);
        }
        catch (Exception ex) { await Dialogs.MessageAsync("Không xóa được bucket: " + S3ClientFactory.Describe(ex)); }
    }

    // ================= Duyệt thư mục (nhanh) =================

    private static string ParentPrefix(string prefix)
    {
        string p = prefix.TrimEnd('/');
        int i = p.LastIndexOf('/');
        return i >= 0 ? p[..(i + 1)] : "";
    }

    public Task GoUpAsync() => SelectedBucket == null || CurrentPrefix.Length == 0 ? Task.CompletedTask : OpenFolderAsync(SelectedBucket, ParentPrefix(CurrentPrefix));

    public async Task GoBackAsync()
    {
        if (_back.Count == 0 || SelectedBucket == null) return;
        var t = _back[^1]; _back.RemoveAt(_back.Count - 1);
        _forward.Add((SelectedBucket, CurrentPrefix));
        await OpenFolderAsync(t.bucket, t.prefix, pushHistory: false);
    }

    public async Task GoForwardAsync()
    {
        if (_forward.Count == 0 || SelectedBucket == null) return;
        var t = _forward[^1]; _forward.RemoveAt(_forward.Count - 1);
        _back.Add((SelectedBucket, CurrentPrefix));
        await OpenFolderAsync(t.bucket, t.prefix, pushHistory: false);
    }

    private void UpdateNav() { CanGoBack = _back.Count > 0; CanGoForward = _forward.Count > 0; }

    /// <summary>Đi tới đường dẫn gõ tay: "bucket/thu/muc/" hoặc "/thu/muc" (trong bucket hiện tại).</summary>
    public async Task GoToPathAsync(string path)
    {
        path = (path ?? "").Trim().Replace('\\', '/');
        if (path.StartsWith("s3://", StringComparison.OrdinalIgnoreCase)) path = path[5..];
        string bucket, prefix;
        if (path.StartsWith('/'))
        {
            if (SelectedBucket == null) return;
            bucket = SelectedBucket;
            prefix = path.TrimStart('/');
        }
        else
        {
            int i = path.IndexOf('/');
            bucket = i < 0 ? path : path[..i];
            prefix = i < 0 ? "" : path[(i + 1)..];
        }
        if (bucket.Length == 0) return;
        if (prefix.Length > 0 && !prefix.EndsWith('/')) prefix += "/";
        await OpenFolderAsync(bucket, prefix);
    }

    public Task RefreshAsync() => SelectedBucket == null ? Task.CompletedTask : OpenFolderAsync(SelectedBucket, CurrentPrefix, pushHistory: false, forceRefresh: true);

    /// <summary>
    /// Mở thư mục: hiện ngay bản cache (nếu có), trang đầu (1000 mục) hiện ngay khi về, các trang sau nối dần.
    /// Bấm sang thư mục khác giữa chừng sẽ hủy lượt tải cũ.
    /// </summary>
    public async Task OpenFolderAsync(string bucket, string prefix, bool pushHistory = true, bool forceRefresh = false)
    {
        var browser = _browser;
        if (browser == null) return;

        if (pushHistory && SelectedBucket != null && (SelectedBucket != bucket || CurrentPrefix != prefix))
        {
            _back.Add((SelectedBucket, CurrentPrefix));
            if (_back.Count > 100) _back.RemoveAt(0);
            _forward.Clear();
        }
        UpdateNav();

        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        var ct = cts.Token;

        bool sameFolder = SelectedBucket == bucket && CurrentPrefix == prefix;
        SelectedBucket = bucket;
        CurrentPrefix = prefix;
        PathText = "/" + prefix;
        if (!sameFolder) PropertiesText = "Chọn một file để xem thuộc tính.";
        Settings.LastBucket = bucket;

        FolderListing l;
        bool replaceOnFirstPage;
        if (!forceRefresh && browser.TryGet(bucket, prefix, out var cached) && cached.Started)
        {
            ShowEntries(cached, cached.Items);
            l = cached;
            replaceOnFirstPage = false;
            if (cached.Complete)
            {
                if (!S3Browser.IsStale(cached)) { IsLoading = false; return; }
                // Bản cache cũ: vẫn hiện, làm mới ngầm rồi thay khi trang đầu về.
                l = browser.Fresh(bucket, prefix);
                replaceOnFirstPage = true;
            }
        }
        else
        {
            l = forceRefresh ? browser.Fresh(bucket, prefix) : browser.Get(bucket, prefix);
            replaceOnFirstPage = true;
            if (!(forceRefresh && sameFolder)) ShowEntries(l, new List<S3Entry>());
        }

        IsLoading = true;
        UpdateStatus(l);
        var sw = Stopwatch.StartNew();
        int pages = 0;
        try
        {
            while (!l.Complete)
            {
                var page = await browser.FetchNextPageAsync(l, ct);
                if (ct.IsCancellationRequested) return;
                pages++;
                if (replaceOnFirstPage) { ShowEntries(l, l.Items); replaceOnFirstPage = false; }
                else AppendEntries(page);
                UpdateStatus(l);
            }
            if (replaceOnFirstPage) ShowEntries(l, l.Items);
            // Nhiều trang: thư mục ở trang sau đang nằm sau file → sắp lại 1 lần cho đúng thứ tự.
            if (pages > 1) ApplyView();
            if (pages > 0 && sw.ElapsedMilliseconds > 3000) Log($"Đã tải {l.Items.Count:N0} mục trong {bucket}/{prefix} ({sw.Elapsed.TotalSeconds:0.0}s)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!l.Started) browser.Invalidate(bucket, prefix);
            string msg = S3ClientFactory.Describe(ex);
            StatusText = "Lỗi: " + msg;
            Log($"Lỗi tải {bucket}/{prefix}: {msg}");
            return;
        }
        finally
        {
            if (_listCts == cts) IsLoading = false;
        }
        if (_listCts == cts) UpdateStatus(l);
    }

    public void StopLoading()
    {
        _listCts?.Cancel();
        IsLoading = false;
        if (_shown != null) UpdateStatus(_shown);
    }

    /// <summary>Rê chuột vào thư mục → nạp trước trang đầu.</summary>
    public void Prefetch(S3Entry e)
    {
        if (!e.IsFolder || _browser == null || SelectedBucket == null) return;
        _ = _browser.PrefetchAsync(SelectedBucket, e.Key);
    }

    private void ShowEntries(FolderListing? l, List<S3Entry> items)
    {
        _shown = l;
        _all = new List<S3Entry>(items);
        ApplyView();
        if (l != null) UpdateStatus(l); else { StatusText = ""; IsResticHere = IsResticChild = false; }
    }

    private void AppendEntries(IReadOnlyList<S3Entry> page)
    {
        _all.AddRange(page);
        var f = FilterText;
        IReadOnlyList<S3Entry> add = f.Length == 0 ? page : page.Where(e => e.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        if (Sort == SortColumn.Name && !SortDesc) Items.AddRange(add);
        else ApplyView();
    }

    partial void OnFilterTextChanged(string value) { ApplyView(); if (_shown != null) UpdateStatus(_shown); }

    public void SortBy(SortColumn c)
    {
        if (Sort == c) SortDesc = !SortDesc;
        else { Sort = c; SortDesc = false; }
        ApplyView();
    }

    private void ApplyView()
    {
        IEnumerable<S3Entry> q = _all;
        if (FilterText.Length > 0) q = q.Where(e => e.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
        Comparison<S3Entry> cmp = Sort switch
        {
            SortColumn.Size => (a, b) => a.Size.CompareTo(b.Size),
            SortColumn.Type => (a, b) => string.Compare(a.TypeText, b.TypeText, StringComparison.OrdinalIgnoreCase),
            SortColumn.Modified => (a, b) => Nullable.Compare(a.LastModified, b.LastModified),
            SortColumn.StorageClass => (a, b) => string.Compare(a.StorageClass, b.StorageClass, StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => string.CompareOrdinal(a.Key, b.Key)
        };
        var list = q.ToList();
        bool desc = SortDesc;
        list.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            int r = cmp(a, b);
            if (r == 0 && Sort != SortColumn.Name) r = string.CompareOrdinal(a.Key, b.Key);
            return desc ? -r : r;
        });
        Items.ReplaceAll(list);
    }

    private void UpdateStatus(FolderListing l)
    {
        if (l != _shown) return;
        IsResticHere = ResticRepository.LooksLikeRepo(_all.Select(e => e.Name));
        IsResticChild = !IsResticHere && _all.Count <= 50 && _all.Any(e => e.IsFolder && e.Name == "restic/");
        string s = $"{l.FileCount:N0} file ({S3Entry.FormatSize(l.TotalBytes)}) và {l.FolderCount:N0} thư mục";
        if (FilterText.Length > 0) s = $"Hiện {Items.Count:N0} / {l.Items.Count:N0} — " + s;
        if (!l.Complete) s += l.Started ? " — đang tải thêm…" : "Đang tải…";
        if (!l.Started) s = "Đang tải…";
        StatusText = s;
    }

    // ================= Thuộc tính =================

    public async Task ShowPropertiesAsync(S3Entry? e)
    {
        _propsCts?.Cancel();
        if (e == null || _browser == null || SelectedBucket == null) { PropertiesText = "Chọn một file để xem thuộc tính."; return; }
        if (e.IsFolder) { PropertiesText = $"Thư mục: {SelectedBucket}/{e.Key}"; return; }
        var cts = _propsCts = new CancellationTokenSource();
        string bucket = SelectedBucket;
        PropertiesText = $"Key: {e.Key}\nKích thước: {e.SizeText} ({e.Size:N0} bytes)\nSửa lần cuối: {e.LastModifiedText}\nStorage class: {e.StorageClass}\nETag: {e.ETag}\n\nĐang lấy metadata…";
        try
        {
            await Task.Delay(250, cts.Token);
            var h = await _browser.HeadAsync(bucket, e.Key, cts.Token);
            var lines = new List<string>
            {
                $"Bucket: {bucket}",
                $"Key: {e.Key}",
                $"Kích thước: {S3Entry.FormatSize(h.ContentLength)} ({h.ContentLength:N0} bytes)",
                $"Content-Type: {h.Headers.ContentType}",
                $"Sửa lần cuối: {h.LastModified?.ToLocalTime():dd/MM/yyyy HH:mm:ss}",
                $"ETag: {h.ETag}",
                $"Storage class: {(string.IsNullOrEmpty(h.StorageClass?.Value) ? "STANDARD" : h.StorageClass.Value)}"
            };
            if (!string.IsNullOrEmpty(h.Headers.CacheControl)) lines.Add($"Cache-Control: {h.Headers.CacheControl}");
            if (!string.IsNullOrEmpty(h.Headers.ContentEncoding)) lines.Add($"Content-Encoding: {h.Headers.ContentEncoding}");
            if (!string.IsNullOrEmpty(h.VersionId)) lines.Add($"VersionId: {h.VersionId}");
            foreach (var k in h.Metadata.Keys) lines.Add($"{k}: {h.Metadata[k]}");
            PropertiesText = string.Join("\n", lines);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PropertiesText += "\n(Lỗi: " + S3ClientFactory.Describe(ex) + ")"; }
    }

    // ================= Thao tác file =================

    public async Task NewFolderAsync()
    {
        if (_browser == null || SelectedBucket == null) return;
        var name = await Dialogs.PromptAsync("Tên thư mục mới:", "Tạo thư mục", "", "Tạo");
        name = name?.Trim().Trim('/');
        if (string.IsNullOrEmpty(name)) return;
        string bucket = SelectedBucket, prefix = CurrentPrefix;
        try
        {
            await _browser.CreateFolderAsync(bucket, prefix + name + "/", CancellationToken.None);
            Log($"Đã tạo thư mục {bucket}/{prefix}{name}/");
            await OpenFolderAsync(bucket, prefix, pushHistory: false, forceRefresh: true);
        }
        catch (Exception ex) { await Dialogs.MessageAsync("Không tạo được thư mục: " + S3ClientFactory.Describe(ex)); }
    }

    /// <summary>Tải lên file/thư mục local vào thư mục đang mở.</summary>
    public void UploadPaths(IEnumerable<string> paths)
    {
        if (_browser == null || SelectedBucket == null) return;
        string bucket = SelectedBucket, prefix = CurrentPrefix;
        var browser = _browser;
        var tasks = new List<TransferTask>();
        foreach (var p in paths)
        {
            if (File.Exists(p))
                tasks.Add(new TransferTask { Kind = TransferKind.Upload, Browser = browser, Bucket = bucket, Key = prefix + Path.GetFileName(p), LocalPath = p });
            else if (Directory.Exists(p))
            {
                string root = Path.GetDirectoryName(Path.GetFullPath(p).TrimEnd('\\', '/')) ?? "";
                foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System }))
                {
                    string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                    tasks.Add(new TransferTask { Kind = TransferKind.Upload, Browser = browser, Bucket = bucket, Key = prefix + rel, LocalPath = f });
                }
            }
        }
        if (tasks.Count == 0) return;
        Log($"Thêm {tasks.Count:N0} file vào hàng đợi tải lên {bucket}/{prefix}");
        TaskFilter = null;
        Queue.Enqueue(tasks);
    }

    public async Task UploadFilesAsync()
    {
        var files = await Dialogs.PickFilesAsync("Chọn file để tải lên");
        UploadPaths(files);
    }

    public async Task UploadFolderAsync()
    {
        var dir = await Dialogs.PickFolderAsync("Chọn thư mục để tải lên");
        if (dir != null) UploadPaths(new[] { dir });
    }

    private async Task<string?> AskDownloadFolderAsync()
    {
        var dir = await Dialogs.PickFolderAsync("Lưu vào thư mục", Settings.DownloadFolder);
        if (dir == null) return null;
        Settings.DownloadFolder = dir;
        SettingsStore.Save(Settings);
        return dir;
    }

    public async Task DownloadAsync(IReadOnlyList<S3Entry> sel)
    {
        if (_browser == null || SelectedBucket == null || sel.Count == 0) return;
        var dir = await AskDownloadFolderAsync();
        if (dir == null) return;
        string bucket = SelectedBucket, prefix = CurrentPrefix;
        var browser = _browser;
        var tasks = new List<TransferTask>();
        try
        {
            foreach (var e in sel)
            {
                if (!e.IsFolder)
                {
                    tasks.Add(new TransferTask { Kind = TransferKind.Download, Browser = browser, Bucket = bucket, Key = e.Key, LocalPath = LocalPathFor(dir, e.Key[prefix.Length..]) });
                    continue;
                }
                StatusText = $"Đang liệt kê {e.Name}…";
                await foreach (var o in browser.ListAllAsync(bucket, e.Key, CancellationToken.None))
                {
                    if (o.Key.EndsWith('/')) continue;
                    tasks.Add(new TransferTask { Kind = TransferKind.Download, Browser = browser, Bucket = bucket, Key = o.Key, LocalPath = LocalPathFor(dir, o.Key[prefix.Length..]) });
                }
            }
        }
        catch (Exception ex) { await Dialogs.MessageAsync("Không liệt kê được: " + S3ClientFactory.Describe(ex)); }
        if (_shown != null) UpdateStatus(_shown);
        if (tasks.Count == 0) return;
        Log($"Thêm {tasks.Count:N0} file vào hàng đợi tải về {dir}");
        TaskFilter = null;
        Queue.Enqueue(tasks);
    }

    private static string LocalPathFor(string dir, string relKey)
    {
        var parts = relKey.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeName);
        return Path.Combine(new[] { dir }.Concat(parts).ToArray());
    }

    private static string SafeName(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return n is "." or ".." ? "_" : n;
    }

    /// <summary>Mở file: tải về thư mục tạm rồi mở bằng ứng dụng mặc định của Windows.</summary>
    public async Task OpenEntryAsync(S3Entry e)
    {
        if (e.IsFolder) { if (SelectedBucket != null) await OpenFolderAsync(SelectedBucket, e.Key); return; }
        if (_browser == null || SelectedBucket == null) return;
        string dir = Path.Combine(AppPaths.TempDir, Guid.NewGuid().ToString("N")[..8]);
        string local = Path.Combine(dir, SafeName(e.Name));
        var t = new TransferTask { Kind = TransferKind.Download, Browser = _browser, Bucket = SelectedBucket, Key = e.Key, LocalPath = local };
        void Done(TransferTask x)
        {
            if (x != t) return;
            Queue.Finished -= Done;
            if (x.State == TransferStatus.Done)
                Dispatcher.UIThread.Post(() => { try { Process.Start(new ProcessStartInfo(local) { UseShellExecute = true }); } catch (Exception ex) { Log("Không mở được file: " + ex.Message); } });
        }
        Queue.Finished += Done;
        Queue.Enqueue(new[] { t });
        await Task.CompletedTask;
    }

    public async Task DeleteAsync(IReadOnlyList<S3Entry> sel)
    {
        if (_browser == null || SelectedBucket == null || sel.Count == 0) return;
        int folders = sel.Count(e => e.IsFolder), files = sel.Count - folders;
        string what = sel.Count == 1 ? $"\"{sel[0].Name}\"" : $"{files} file và {folders} thư mục";
        string extra = folders > 0 ? "\nThư mục sẽ bị xóa cùng TOÀN BỘ nội dung bên trong." : "";
        if (!await Dialogs.ConfirmAsync($"Xóa vĩnh viễn {what}?{extra}\nNếu bucket có lưu phiên bản (versioning), các phiên bản cũ cũng bị xóa hết — không khôi phục được.", "Xóa", danger: true)) return;

        // Mỗi mục chọn = 1 tác vụ trong tab Tác vụ (thư mục: liệt kê rồi xóa theo lô, có tiến độ và nút dừng).
        var browser = _browser;
        string bucket = SelectedBucket, prefix = CurrentPrefix;
        // Thư mục: mỗi thư mục 1 tác vụ. File: gộp hết vào 1 tác vụ (xóa 1000 file / request, nhiều lô song song).
        var tasks = sel.Where(e => e.IsFolder).Select(e => new TransferTask
        {
            Kind = TransferKind.Delete, Browser = browser, Bucket = bucket, Key = e.Key, LocalPath = "", IsFolder = true
        }).ToList();
        var fileKeys = sel.Where(e => !e.IsFolder).Select(e => e.Key).ToList();
        if (fileKeys.Count == 1)
            tasks.Add(new TransferTask { Kind = TransferKind.Delete, Browser = browser, Bucket = bucket, Key = fileKeys[0], LocalPath = "" });
        else if (fileKeys.Count > 1)
            tasks.Add(new TransferTask { Kind = TransferKind.Delete, Browser = browser, Bucket = bucket, Key = $"{prefix}({fileKeys.Count:N0} file)", LocalPath = "", Keys = fileKeys });
        Log($"Thêm {tasks.Count:N0} tác vụ xóa trong {SelectedBucket}/{CurrentPrefix}");
        TaskFilter = null;
        Queue.Enqueue(tasks);
    }

    public async Task CopyUrlAsync(S3Entry e, bool presigned)
    {
        if (_browser == null || SelectedBucket == null) return;
        string text;
        if (presigned) text = await _browser.PresignAsync(SelectedBucket, e.Key, TimeSpan.FromDays(7));
        else text = $"s3://{SelectedBucket}/{e.Key}";
        if (await Dialogs.SetClipboardAsync(text))
            Log(presigned ? "Đã copy link tải (hết hạn sau 7 ngày): " + e.Name : "Đã copy " + text);
    }

    // ================= Kho restic (WebNowBackup) =================

    /// <summary>Mở cửa sổ duyệt kho restic ở bucket/prefix (mặc định: thư mục đang mở).</summary>
    public void OpenRestic(string? prefix = null)
    {
        if (_browser == null || SelectedBucket == null) return;
        if (prefix == null && !IsResticHere && IsResticChild) prefix = CurrentPrefix + "restic/";
        var vm = new ResticViewModel(this, _browser, SelectedBucket, prefix ?? CurrentPrefix);
        var w = new ResticWindow(vm);
        if (Dialogs.Owner != null) w.Show(Dialogs.Owner); else w.Show();
    }

    /// <summary>Lưu thay đổi nhỏ của tài khoản (vd mật khẩu kho restic) mà không dựng lại client / cache.</summary>
    public void SaveAccountQuietly(S3Account acc)
    {
        Store.Upsert(acc);
        if (NodeOf(acc.Id) is { } node) node.Account = acc;
        if (_browsers.TryGetValue(acc.Id, out var b)) b.Account.ResticRepos = acc.ResticRepos.Select(r => r.Clone()).ToList();
        if (CurrentAccount?.Id == acc.Id) CurrentAccount = acc;
    }

    public void EnqueueTask(TransferTask t)
    {
        TaskFilter = null;
        Queue.Enqueue(new[] { t });
    }

    // ================= Hàng đợi =================

    private void OnTransferFinished(TransferTask t)
    {
        bool changed = t.Kind == TransferKind.Upload ? t.State == TransferStatus.Done
                     : t.Kind == TransferKind.Delete && t.State is TransferStatus.Done or TransferStatus.Failed or TransferStatus.Stopped;
        if (!changed) return;
        // File mới lên / vừa xóa → thư mục chứa nó (và các cấp cha tới thư mục đang xem) cần làm mới.
        string folder = ParentPrefix(t.Key);
        if (t.Kind == TransferKind.Delete && t.IsFolder) t.Browser.Invalidate(t.Bucket, t.Key);
        Dispatcher.UIThread.Post(() =>
        {
            lock (_dirtyFolders) _dirtyFolders.Add(t.Bucket + "\n" + folder);
            t.Browser.Invalidate(t.Bucket, folder);
            if (!_refreshSoon.IsEnabled) _refreshSoon.Start();
        });
    }

    private void FlushDirtyFolders()
    {
        List<string> dirty;
        lock (_dirtyFolders) { dirty = _dirtyFolders.ToList(); _dirtyFolders.Clear(); }
        if (SelectedBucket == null || _browser == null) return;
        string cur = SelectedBucket + "\n";
        bool affectsCurrent = dirty.Any(d => d.StartsWith(cur, StringComparison.Ordinal) && d[cur.Length..].StartsWith(CurrentPrefix, StringComparison.Ordinal));
        foreach (var d in dirty)
        {
            // Thư mục con mới tạo cũng làm cha của nó thay đổi.
            var parts = d.Split('\n');
            string p = parts[1];
            while (p.Length > 0) { p = ParentPrefix(p); _browser.Invalidate(parts[0], p); }
        }
        if (affectsCurrent && !IsLoading) _ = RefreshAsync();
    }

    partial void OnTaskFilterChanged(TransferStatus? value) => RebuildVisibleTasks();

    private void RebuildVisibleTasks()
    {
        var f = TaskFilter;
        var list = f == null ? Tasks.ToList()
            : f == TransferStatus.Running ? Tasks.Where(t => t.Status is TransferStatus.Running).ToList()
            : Tasks.Where(t => t.Status == f).ToList();
        if (!list.SequenceEqual(VisibleTasks)) VisibleTasks.ReplaceAll(list);
    }

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        double sec = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        int run = 0, queued = 0, failed = 0, done = 0, stopped = 0;
        foreach (var t in Tasks)
        {
            if (t.State == TransferStatus.Running || t.Status == TransferStatus.Running) t.Tick(sec);
            switch (t.Status)
            {
                case TransferStatus.Running: run++; break;
                case TransferStatus.Queued: queued++; break;
                case TransferStatus.Failed: failed++; break;
                case TransferStatus.Done: done++; break;
                case TransferStatus.Stopped: stopped++; break;
            }
        }
        ActiveTaskCount = run + queued;
        TaskSummary = Tasks.Count == 0 ? "" : $"Đang chạy {run} · Chờ {queued} · Xong {done} · Dừng {stopped} · Lỗi {failed}";
        RebuildVisibleTasks();
    }

    public void StartAll() { foreach (var t in Tasks.Where(t => t.State is TransferStatus.Stopped or TransferStatus.Failed).ToList()) Queue.Start(t); }
    public void StopAll() { foreach (var t in Tasks.Where(t => t.State is TransferStatus.Running or TransferStatus.Queued).ToList()) Queue.Stop(t); }

    /// <summary>Dừng mọi tác vụ rồi xóa khỏi danh sách những tác vụ không còn chạy.</summary>
    public void CancelAll()
    {
        StopAll();
        ClearFinished(includeStopped: true);
    }

    public void ClearFinished(bool includeStopped = false)
    {
        foreach (var t in Tasks.Where(t => t.State == TransferStatus.Done || t.State == TransferStatus.Failed || (includeStopped && t.State == TransferStatus.Stopped)).ToList())
        {
            Queue.Remove(t);
            Tasks.Remove(t);
        }
        RebuildVisibleTasks();
    }

    public void SetParallel(int n)
    {
        Queue.Parallel = Settings.ParallelTransfers = Math.Clamp(n, 1, 16);
        SettingsStore.Save(Settings);
        Log($"Số file truyền song song: {Queue.Parallel}");
    }
}
