using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WNS3Connect.Models;
using WNS3Connect.Services;
using WNS3Connect.Services.Restic;

namespace WNS3Connect.ViewModels;

/// <summary>Một dòng trong danh sách file của snapshot.</summary>
public sealed class ResticEntry(ResticNode node, string path)
{
    public ResticNode Node { get; } = node;
    /// <summary>Đường dẫn đầy đủ trong snapshot (bắt đầu bằng "/").</summary>
    public string Path { get; } = path;
    public bool IsFolder => Node.IsDir;
    public bool IsLink => Node.Type == "symlink";
    public string Name => Node.Name + (IsFolder ? "/" : "");
    /// <summary>Kết quả tìm kiếm: hiện cả đường dẫn.</summary>
    public bool ShowFullPath { get; init; }
    public string Display => ShowFullPath ? Path.TrimStart('/') + (IsFolder ? "/" : "") : Name;
    public long Size => Node.IsFile ? Node.Size : 0;
    public string SizeText => Node.IsFile ? S3Entry.FormatSize(Node.Size) : "";
    public string TypeText => IsFolder ? "Thư mục" : IsLink ? "Symlink" : Node.IsFile ? new S3Entry { Key = Node.Name }.TypeText : Node.Type;
    public string IconColor => IsLink ? "#98A2B3" : new S3Entry { Key = Node.Name + (IsFolder ? "/" : ""), IsFolder = IsFolder }.IconColor;
    public string ModifiedText => Node.Mtime?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "";
    public string Extra => IsLink ? "→ " + Node.LinkTarget : "";
    public override string ToString() => Display;
}

public sealed record ResticCrumb(string Name, string Path, bool IsLast);

/// <summary>Cửa sổ duyệt kho restic: mở khóa, chọn snapshot, duyệt thư mục, khôi phục file về máy.</summary>
public partial class ResticViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly S3Browser _browser;
    private ResticRepository? _repo;
    private List<ResticSnapshot> _allSnaps = new();
    private CancellationTokenSource? _dirCts;

    public string Bucket { get; }
    public string Prefix { get; }
    public string RepoText => $"{Bucket}/{Prefix}";
    public AppSettings Settings => _main.Settings;
    public string WindowTitle => $"Kho restic — {Bucket}/{Prefix}";

    public ObservableCollection<ResticSnapshot> Snapshots { get; } = new();
    public ObservableCollection<ResticEntry> Entries { get; } = new();
    public ObservableCollection<ResticCrumb> Crumbs { get; } = new();

    [ObservableProperty] private bool _needPassword = true;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _rememberPassword = true;
    [ObservableProperty] private string _passwordError = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _snapshotFilter = "";
    [ObservableProperty] private ResticSnapshot? _selectedSnapshot;
    [ObservableProperty] private string _currentPath = "/";
    [ObservableProperty] private string _snapshotInfo = "";
    [ObservableProperty] private bool _hasSavedPassword;
    [ObservableProperty] private string _snapshotCountText = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearchResult;

    public bool IsOpen => !NeedPassword;
    public bool IsEmptyDir => !IsBusy && SelectedSnapshot != null && Entries.Count == 0;

    public ResticViewModel(MainViewModel main, S3Browser browser, string bucket, string prefix)
    {
        _main = main; _browser = browser;
        Bucket = bucket;
        Prefix = prefix.Length == 0 || prefix.EndsWith('/') ? prefix : prefix + "/";
        HasSavedPassword = SavedRef() != null;
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmptyDir));
    }

    partial void OnNeedPasswordChanged(bool value) => OnPropertyChanged(nameof(IsOpen));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsEmptyDir));
    partial void OnSnapshotFilterChanged(string value) => ApplySnapshotFilter();
    partial void OnSelectedSnapshotChanged(ResticSnapshot? value)
    {
        OnPropertyChanged(nameof(IsEmptyDir));
        if (value == null) { SnapshotInfo = ""; return; }
        var lines = new List<string> { $"Snapshot {value.ShortId} · {value.TimeText} · máy {value.Hostname}" };
        if (value.IsWnb) lines.Add($"WebNowBackup: user {value.WnbUser} · {value.KindText}" + (value.WnbBackupId.Length > 0 ? $" · bản {value.WnbBackupId}" : ""));
        lines.Add("Đường dẫn: " + string.Join(", ", value.Paths));
        SnapshotInfo = string.Join("\n", lines);
        _ = OpenDirAsync(value.StartPath, fallbackToRoot: true);
    }

    private ResticRepoRef? SavedRef() =>
        _browser.Account.ResticRepos.FirstOrDefault(r => r.Bucket == Bucket && r.Path == Prefix && !string.IsNullOrEmpty(r.PasswordEnc));

    /// <summary>Mở cửa sổ: có mật khẩu đã lưu thì thử luôn.</summary>
    public async Task StartAsync()
    {
        if (SavedRef()?.PasswordEnc is { } enc && SecretProtector.Decrypt(enc) is { Length: > 0 } pw)
            await UnlockAsync(pw, save: false, fromSaved: true);
    }

    public Task UnlockAsync() => UnlockAsync(Password, RememberPassword, fromSaved: false);

    private async Task UnlockAsync(string password, bool save, bool fromSaved)
    {
        if (IsBusy) return;
        if (password.Length == 0) { PasswordError = "Nhập mật khẩu kho (key restic của WebNowBackup)."; return; }
        IsBusy = true;
        PasswordError = "";
        StatusText = "Đang mở khóa kho…";
        var sw = Stopwatch.StartNew();
        try
        {
            var backend = new S3ResticBackend(_browser.Client, Bucket, Prefix);
            _repo = await ResticRepository.OpenAsync(backend, password, CancellationToken.None);
            if (save) SavePassword(password);
            StatusText = "Đang đọc danh sách snapshot…";
            _allSnaps = await _repo.LoadSnapshotsAsync(CancellationToken.None);
            // Nạp index ngầm ngay để lúc bấm vào snapshot đã sẵn sàng.
            _ = _repo.EnsureIndexAsync(CancellationToken.None);
            NeedPassword = false;
            Password = "";
            ApplySnapshotFilter();
            _main.Log($"Đã mở kho restic {RepoText}: {_allSnaps.Count} snapshot ({sw.Elapsed.TotalSeconds:0.0}s)");
            StatusText = $"{_allSnaps.Count:N0} snapshot · kho bản {_repo.Version}";
        }
        catch (ResticWrongPasswordException)
        {
            PasswordError = fromSaved ? "Mật khẩu đã lưu không còn đúng — nhập lại." : "Sai mật khẩu kho restic.";
            StatusText = "";
        }
        catch (Exception ex)
        {
            PasswordError = "Không mở được kho: " + S3ClientFactory.Describe(ex);
            StatusText = "";
            _main.Log($"Không mở được kho restic {RepoText}: {S3ClientFactory.Describe(ex)}");
        }
        finally { IsBusy = false; }
        if (!NeedPassword) SelectedSnapshot = Snapshots.FirstOrDefault();
    }

    private void SavePassword(string password)
    {
        var node = _main.AccountNodes.FirstOrDefault(n => n.Account.Id == _browser.Account.Id);
        var acc = (node?.Account ?? _browser.Account).Clone();
        acc.ResticRepos.RemoveAll(r => r.Bucket == Bucket && r.Path == Prefix);
        acc.ResticRepos.Add(new ResticRepoRef { Bucket = Bucket, Path = Prefix, PasswordEnc = SecretProtector.Encrypt(password) });
        _main.SaveAccountQuietly(acc);
        HasSavedPassword = true;
    }

    public void ForgetPassword()
    {
        var node = _main.AccountNodes.FirstOrDefault(n => n.Account.Id == _browser.Account.Id);
        var acc = (node?.Account ?? _browser.Account).Clone();
        if (acc.ResticRepos.RemoveAll(r => r.Bucket == Bucket && r.Path == Prefix) > 0) _main.SaveAccountQuietly(acc);
        HasSavedPassword = false;
        _main.Log("Đã xóa mật khẩu đã lưu của kho restic " + RepoText);
    }

    private void ApplySnapshotFilter()
    {
        var f = SnapshotFilter.Trim();
        var keep = SelectedSnapshot;
        var list = f.Length == 0 ? _allSnaps : _allSnaps.Where(s =>
            s.UserText.Contains(f, StringComparison.OrdinalIgnoreCase) || s.KindText.Contains(f, StringComparison.OrdinalIgnoreCase) ||
            s.Hostname.Contains(f, StringComparison.OrdinalIgnoreCase) || s.Id.StartsWith(f, StringComparison.OrdinalIgnoreCase) ||
            s.TimeText.Contains(f) || s.Tags.Any(t => t.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        Snapshots.Clear();
        foreach (var s in list) Snapshots.Add(s);
        SnapshotCountText = (f.Length > 0 ? $"Hiện {list.Count:N0} / " : "") + $"{_allSnaps.Count:N0} snapshot" + (_repo != null ? $" · kho restic bản {_repo.Version}" : "");
        if (keep != null && list.Contains(keep)) SelectedSnapshot = keep;
    }

    public async Task ReloadSnapshotsAsync()
    {
        if (_repo == null || IsBusy) return;
        IsBusy = true;
        try
        {
            _allSnaps = await _repo.LoadSnapshotsAsync(CancellationToken.None);
            ApplySnapshotFilter();
            StatusText = $"{_allSnaps.Count:N0} snapshot · kho bản {_repo.Version}";
        }
        catch (Exception ex) { StatusText = "Lỗi: " + S3ClientFactory.Describe(ex); }
        finally { IsBusy = false; }
    }

    // ================= Duyệt thư mục =================

    private static string Norm(string p) => "/" + string.Join("/", p.Split('/', StringSplitOptions.RemoveEmptyEntries));

    public async Task OpenDirAsync(string path, bool fallbackToRoot = false)
    {
        if (_repo == null || SelectedSnapshot is not { } snap) return;
        _dirCts?.Cancel();
        var cts = _dirCts = new CancellationTokenSource();
        path = Norm(path);
        IsBusy = true;
        StatusText = _repo.IndexLoaded ? "Đang mở thư mục…" : "Đang nạp index của kho (lần đầu hơi lâu, lần sau dùng cache)…";
        try
        {
            var nodes = await _repo.ListDirAsync(snap, path, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (nodes == null && fallbackToRoot) { path = "/"; nodes = await _repo.ListDirAsync(snap, path, cts.Token); }
            if (nodes == null) { StatusText = "Thư mục không có trong snapshot: " + path; return; }
            CurrentPath = path;
            if (IsSearchResult) { IsSearchResult = false; SearchText = ""; }
            UpdateCrumbs();
            var list = nodes.Select(n => new ResticEntry(n, (path == "/" ? "" : path) + "/" + n.Name))
                .OrderBy(e => e.IsFolder ? 0 : 1).ThenBy(e => e.Node.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Entries.Clear();
            foreach (var e in list) Entries.Add(e);
            int files = list.Count(e => e.Node.IsFile);
            StatusText = $"{files:N0} file ({S3Entry.FormatSize(list.Sum(e => e.Size))}) và {list.Count(e => e.IsFolder):N0} thư mục";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusText = "Lỗi: " + S3ClientFactory.Describe(ex);
            _main.Log($"Lỗi đọc {RepoText} snapshot {snap.ShortId} {path}: {S3ClientFactory.Describe(ex)}");
        }
        finally { if (_dirCts == cts) IsBusy = false; }
    }

    /// <summary>Tìm theo tên (không phân biệt hoa thường) trong mọi cấp của thư mục đang xem.</summary>
    public async Task SearchAsync()
    {
        if (_repo == null || SelectedSnapshot is not { } snap) return;
        string q = SearchText.Trim();
        if (q.Length == 0) { await OpenDirAsync(CurrentPath); return; }
        _dirCts?.Cancel();
        var cts = _dirCts = new CancellationTokenSource();
        string where = CurrentPath;
        IsBusy = true;
        StatusText = $"Đang tìm \"{q}\" trong {where}…";
        try
        {
            var nodes = await _repo.ListDirAsync(snap, where, cts.Token);
            if (nodes == null) return;
            bool finished = false;
            var plan = await _repo.PlanRestoreAsync(nodes, n => Dispatcher.UIThread.Post(() => { if (!finished && _dirCts == cts) StatusText = $"Đang tìm \"{q}\"… đã xem {n:N0} mục"; }), cts.Token);
            finished = true;
            if (cts.IsCancellationRequested) return;
            string basePath = where == "/" ? "" : where;
            var hits = plan.Items.Where(i => i.Node.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            const int Max = 5000;
            Entries.Clear();
            foreach (var i in hits.Take(Max).OrderBy(i => i.Node.IsDir ? 0 : 1).ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase))
                Entries.Add(new ResticEntry(i.Node, basePath + "/" + i.RelativePath) { ShowFullPath = true });
            IsSearchResult = true;
            StatusText = $"Tìm thấy {hits.Count:N0} mục khớp \"{q}\" trong {plan.Items.Count:N0} mục" + (hits.Count > Max ? $" (hiện {Max:N0} đầu tiên)" : "");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { StatusText = "Lỗi: " + S3ClientFactory.Describe(ex); }
        finally { if (_dirCts == cts) IsBusy = false; }
    }

    private void UpdateCrumbs()
    {
        Crumbs.Clear();
        var parts = CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Crumbs.Add(new ResticCrumb(SelectedSnapshot?.ShortId ?? "/", "/", parts.Length == 0));
        string acc = "";
        for (int i = 0; i < parts.Length; i++)
        {
            acc += "/" + parts[i];
            Crumbs.Add(new ResticCrumb(parts[i], acc, i == parts.Length - 1));
        }
    }

    public Task UpAsync()
    {
        if (IsSearchResult) { SearchText = ""; return OpenDirAsync(CurrentPath); }
        if (CurrentPath == "/") return Task.CompletedTask;
        int i = CurrentPath.LastIndexOf('/');
        return OpenDirAsync(i <= 0 ? "/" : CurrentPath[..i]);
    }

    // ================= Khôi phục =================

    /// <summary>Thư mục đích khi tải cả thư mục đang xem: &lt;nơi lưu&gt;/&lt;tên thư mục&gt; (gốc snapshot → &lt;user&gt;-&lt;id&gt;).</summary>
    public string CurrentFolderTarget(string dir)
    {
        var name = CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(name) && SelectedSnapshot is { } s) name = (s.UserText.Length > 0 ? s.UserText + "-" : "") + s.ShortId;
        return System.IO.Path.Combine(dir, ResticRepository.SafeName(name ?? "restic"));
    }

    private string LabelFor(IReadOnlyList<ResticEntry> sel, ResticSnapshot snap)
    {
        string what = sel.Count == 1 ? sel[0].Path : $"{CurrentPath} ({sel.Count} mục)";
        return $"{snap.ShortId} {snap.UserText} {what}".Replace("  ", " ");
    }

    /// <summary>Khôi phục các mục đã chọn vào thư mục trên máy (mỗi lần = 1 tác vụ trong tab Tác vụ).</summary>
    public void Restore(IReadOnlyList<ResticEntry> sel, string targetDir)
    {
        if (_repo == null || SelectedSnapshot is not { } snap || sel.Count == 0) return;
        var repo = _repo;
        // Kết quả tìm kiếm: giữ đường dẫn tính từ thư mục đã tìm (2 thư mục "inc/" khác nhau không đè lên nhau).
        string basePath = CurrentPath == "/" ? "/" : CurrentPath + "/";
        var nodes = sel.Select(e => new ResticRepository.RestoreItem(e.Node,
            e.ShowFullPath && e.Path.StartsWith(basePath, StringComparison.Ordinal) ? e.Path[basePath.Length..] : e.Node.Name)).ToList();
        var task = new TransferTask
        {
            Kind = TransferKind.Restore, Browser = _browser, Bucket = Bucket, Key = Prefix, LocalPath = targetDir,
            Label = LabelFor(sel, snap),
            Job = (t, ct) => RunRestoreAsync(repo, nodes, targetDir, t, ct)
        };
        _main.Log($"Khôi phục {task.Label} → {targetDir}");
        _main.EnqueueTask(task);
    }

    private async Task<string?> RunRestoreAsync(ResticRepository repo, List<ResticRepository.RestoreItem> nodes, string targetDir, TransferTask t, CancellationToken ct)
    {
        Interlocked.Exchange(ref t.LiveDone, 0);
        t.Listing = true;
        List<ResticRepository.RestoreItem> items;
        long total;
        try { (items, total) = await repo.PlanRestoreAsync(nodes, n => Interlocked.Exchange(ref t.LiveFound, n), ct); }
        finally { t.Listing = false; }
        Interlocked.Exchange(ref t.LiveTotal, Math.Max(1, total));
        var r = await repo.RestoreAsync(items, targetDir, n => Interlocked.Add(ref t.LiveDone, n), ct);
        Interlocked.Exchange(ref t.LiveDone, Math.Max(1, total));
        string summary = $"{r.Files:N0} file, {r.Dirs:N0} thư mục, {S3Entry.FormatSize(r.Bytes)}";
        if (r.Skipped > 0) summary += $", bỏ qua {r.Skipped} symlink/đặc biệt";
        Dispatcher.UIThread.Post(() =>
        {
            _main.Log($"Khôi phục xong {t.Label}: {summary}");
            foreach (var e in r.Errors.Take(20)) _main.Log("   " + e);
        });
        return r.Failed > 0 ? $"{r.Failed:N0} file lỗi ({summary}) — {r.Errors.FirstOrDefault()}" : null;
    }

    /// <summary>Mở 1 file: khôi phục vào thư mục tạm rồi mở bằng ứng dụng mặc định.</summary>
    public void OpenFile(ResticEntry e)
    {
        if (_repo == null || SelectedSnapshot is not { } snap || !e.Node.IsFile) return;
        var repo = _repo;
        string dir = System.IO.Path.Combine(AppPaths.TempDir, Guid.NewGuid().ToString("N")[..8]);
        string local = System.IO.Path.Combine(dir, ResticRepository.SafeName(e.Node.Name));
        var nodes = new List<ResticRepository.RestoreItem> { new(e.Node, e.Node.Name) };
        var task = new TransferTask
        {
            Kind = TransferKind.Restore, Browser = _browser, Bucket = Bucket, Key = Prefix, LocalPath = dir,
            Label = LabelFor(new[] { e }, snap),
            Job = (t, ct) => RunRestoreAsync(repo, nodes, dir, t, ct)
        };
        void Done(TransferTask x)
        {
            if (x != task) return;
            _main.Queue.Finished -= Done;
            if (x.State == TransferStatus.Done)
                Dispatcher.UIThread.Post(() => { try { Process.Start(new ProcessStartInfo(local) { UseShellExecute = true }); } catch (Exception ex) { _main.Log("Không mở được file: " + ex.Message); } });
        }
        _main.Queue.Finished += Done;
        _main.EnqueueTask(task);
    }
}
