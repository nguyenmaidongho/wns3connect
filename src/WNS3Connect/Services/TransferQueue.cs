using Amazon.S3;
using Amazon.S3.Transfer;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WNS3Connect.Models;

namespace WNS3Connect.Services;

public enum TransferKind { Upload, Download, Delete, Restore }
public enum TransferStatus { Queued, Running, Done, Stopped, Failed }

/// <summary>Một tác vụ upload/download/xóa. Tiến độ ghi vào biến thường từ luồng nền; <see cref="Tick"/> (gọi trên UI mỗi giây) đẩy ra giao diện.</summary>
public partial class TransferTask : ObservableObject
{
    public TransferKind Kind { get; init; }
    public required S3Browser Browser { get; init; }
    public required string Bucket { get; init; }
    public required string Key { get; init; }
    public required string LocalPath { get; init; }
    /// <summary>Tác vụ xóa: key là thư mục (xóa cả nội dung bên trong).</summary>
    public bool IsFolder { get; init; }
    /// <summary>Tác vụ xóa nhiều file cùng lúc (gộp các file đã chọn thành 1 tác vụ). Null = chỉ Key.</summary>
    public IReadOnlyList<string>? Keys { get; init; }
    /// <summary>Tác vụ khôi phục từ kho restic: tự chạy, báo tiến độ qua LiveDone/LiveTotal; trả về lỗi (null = xong).</summary>
    public Func<TransferTask, CancellationToken, Task<string?>>? Job { get; init; }
    /// <summary>Tên hiển thị thay cho bucket/key (khôi phục restic).</summary>
    public string? Label { get; init; }

    [ObservableProperty] private TransferStatus _status = TransferStatus.Queued;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private long _doneBytes;
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _runningText = "";
    [ObservableProperty] private bool _isListing;

    /// <summary>Trạng thái thật (luồng nền đọc/ghi); Status là bản hiển thị trên UI.</summary>
    internal volatile TransferStatus State = TransferStatus.Queued;
    internal volatile string LastError = "";
    internal long LiveDone;
    internal long LiveTotal;
    /// <summary>Xóa: true khi đang liệt kê object trong thư mục (chưa bắt đầu xóa).</summary>
    internal volatile bool Listing;
    /// <summary>Xóa: đang xóa vĩnh viễn các phiên bản cũ / dấu xóa (bucket có versioning).</summary>
    internal volatile bool Purging;
    internal long LivePurged;
    /// <summary>Khôi phục: số mục đã tìm thấy khi đang đọc cây thư mục.</summary>
    internal long LiveFound;
    private long _lastDone;
    internal CancellationTokenSource? Cts;

    public string Title => (Kind switch { TransferKind.Upload => "↑ ", TransferKind.Download => "↓ ", TransferKind.Restore => "⟲ ", _ => "✕ " }) + (Label ?? Bucket + "/" + Key);
    public string SizeText => TotalBytes <= 0 ? "" : Kind == TransferKind.Delete ? $"{TotalBytes:N0} obj" : S3Entry.FormatSize(TotalBytes);
    public double Percent => TotalBytes > 0 ? Math.Min(100, DoneBytes * 100.0 / TotalBytes) : (Status == TransferStatus.Done ? 100 : 0);
    public string PercentText => IsListing ? "…" : $"{Percent:0}%";
    public string StatusText => Status switch
    {
        TransferStatus.Queued => "Đang chờ",
        TransferStatus.Running => RunningText.Length > 0 ? RunningText : Kind switch { TransferKind.Upload => "Đang tải lên", TransferKind.Download => "Đang tải về", TransferKind.Restore => "Đang khôi phục", _ => "Đang xóa" },
        TransferStatus.Done => "Xong",
        TransferStatus.Stopped => "Đã dừng",
        _ => "Lỗi: " + Error
    };

    partial void OnStatusChanged(TransferStatus value) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(Percent)); OnPropertyChanged(nameof(PercentText)); }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(StatusText));
    partial void OnRunningTextChanged(string value) => OnPropertyChanged(nameof(StatusText));
    partial void OnIsListingChanged(bool value) => OnPropertyChanged(nameof(PercentText));
    partial void OnTotalBytesChanged(long value) { OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(Percent)); OnPropertyChanged(nameof(PercentText)); }
    partial void OnDoneBytesChanged(long value) { OnPropertyChanged(nameof(Percent)); OnPropertyChanged(nameof(PercentText)); }

    /// <summary>Gọi trên UI thread mỗi giây.</summary>
    public void Tick(double seconds)
    {
        long d = Interlocked.Read(ref LiveDone), t = Interlocked.Read(ref LiveTotal);
        if (t > 0) TotalBytes = t;
        DoneBytes = d;
        IsListing = Listing;
        if (Kind == TransferKind.Delete)
            RunningText = Purging ? $"Đang xóa phiên bản cũ… ({Interlocked.Read(ref LivePurged):N0})" : Listing ? $"Đang xóa {d:N0}/{t:N0}+ (vẫn đang liệt kê)" : t > 0 ? $"Đang xóa {d:N0}/{t:N0}" : "";
        if (Kind == TransferKind.Restore)
            RunningText = Listing ? $"Đang đọc cây thư mục… ({Interlocked.Read(ref LiveFound):N0} mục)" : "";
        if (Status == TransferStatus.Running)
        {
            double bps = (d - _lastDone) / Math.Max(0.2, seconds);
            SpeedText = bps <= 0 || Listing ? "" : Kind == TransferKind.Delete ? $"{bps:0} obj/s" : S3Entry.FormatSize((long)bps) + "/s";
        }
        else SpeedText = "";
        _lastDone = d;
    }
}

/// <summary>Hàng đợi truyền file: chạy song song tối đa N tác vụ, multipart cho file lớn.</summary>
public class TransferQueue
{
    private readonly object _lock = new();
    private readonly List<TransferTask> _tasks = new();

    public int Parallel { get; set; } = 4;

    /// <summary>Phát (trên luồng nền) khi một tác vụ kết thúc (xong/lỗi/dừng).</summary>
    public event Action<TransferTask>? Finished;
    public event Action<TransferTask>? Added;
    public event Action<string>? Log;

    public IReadOnlyList<TransferTask> Snapshot() { lock (_lock) return _tasks.ToList(); }

    public void Enqueue(IEnumerable<TransferTask> tasks)
    {
        lock (_lock)
        {
            foreach (var t in tasks)
            {
                _tasks.Add(t);
                Added?.Invoke(t);
            }
        }
        Pump();
    }

    public void Remove(TransferTask t) { lock (_lock) _tasks.Remove(t); }

    public void Stop(TransferTask t)
    {
        lock (_lock)
        {
            if (t.State == TransferStatus.Queued) Set(t, TransferStatus.Stopped);
            else if (t.State == TransferStatus.Running) t.Cts?.Cancel();
        }
    }

    public void Start(TransferTask t)
    {
        lock (_lock)
        {
            if (t.State is TransferStatus.Stopped or TransferStatus.Failed) Set(t, TransferStatus.Queued, "");
        }
        Pump();
    }

    private void Pump()
    {
        List<TransferTask> toStart = new();
        lock (_lock)
        {
            int running = _tasks.Count(x => x.State == TransferStatus.Running);
            foreach (var t in _tasks)
            {
                if (running >= Math.Max(1, Parallel)) break;
                if (t.State != TransferStatus.Queued) continue;
                Set(t, TransferStatus.Running);
                t.Cts = new CancellationTokenSource();
                running++;
                toStart.Add(t);
            }
        }
        foreach (var t in toStart) _ = Task.Run(() => RunAsync(t));
    }

    private async Task RunAsync(TransferTask t)
    {
        var ct = t.Cts!.Token;
        try
        {
            if (t.Kind == TransferKind.Delete)
            {
                await RunDeleteAsync(t, ct);
                Finished?.Invoke(t);
                Pump();
                return;
            }
            if (t.Kind == TransferKind.Restore)
            {
                string? err = await t.Job!(t, ct);
                ct.ThrowIfCancellationRequested();
                lock (_lock) Set(t, err == null ? TransferStatus.Done : TransferStatus.Failed, err);
                if (err != null) Log?.Invoke("LỖI " + t.Title + ": " + err);
                else Log?.Invoke("Đã khôi phục " + (t.Label ?? t.Key) + " → " + t.LocalPath);
                Finished?.Invoke(t);
                Pump();
                return;
            }
            var cfg = new TransferUtilityConfig { ConcurrentServiceRequests = 8, MinSizeBeforePartUpload = 16 * 1024 * 1024 };
            using var tu = new TransferUtility(t.Browser.Client, cfg);
            if (t.Kind == TransferKind.Upload)
            {
                var fi = new FileInfo(t.LocalPath);
                Interlocked.Exchange(ref t.LiveTotal, fi.Length);
                Interlocked.Exchange(ref t.LiveDone, 0);
                var req = new TransferUtilityUploadRequest
                {
                    BucketName = t.Bucket,
                    Key = t.Key,
                    FilePath = t.LocalPath,
                    PartSize = PartSizeFor(fi.Length)
                };
                req.UploadProgressEvent += (_, e) => { Interlocked.Exchange(ref t.LiveDone, e.TransferredBytes); Interlocked.Exchange(ref t.LiveTotal, e.TotalBytes); };
                await tu.UploadAsync(req, ct);
                Interlocked.Exchange(ref t.LiveDone, fi.Length);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(t.LocalPath)!);
                string part = t.LocalPath + ".wns3part";
                var req = new TransferUtilityDownloadRequest { BucketName = t.Bucket, Key = t.Key, FilePath = part };
                req.WriteObjectProgressEvent += (_, e) => { Interlocked.Exchange(ref t.LiveDone, e.TransferredBytes); Interlocked.Exchange(ref t.LiveTotal, e.TotalBytes); };
                try
                {
                    await tu.DownloadAsync(req, ct);
                    File.Move(part, t.LocalPath, overwrite: true);
                }
                catch
                {
                    try { File.Delete(part); } catch { }
                    throw;
                }
            }
            lock (_lock) Set(t, TransferStatus.Done);
            Log?.Invoke((t.Kind == TransferKind.Upload ? "Đã tải lên " : "Đã tải về ") + t.Bucket + "/" + t.Key);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            lock (_lock) Set(t, TransferStatus.Stopped);
        }
        catch (Exception ex)
        {
            string err = S3ClientFactory.Describe(ex);
            lock (_lock) Set(t, TransferStatus.Failed, err);
            Log?.Invoke("LỖI " + t.Title + ": " + err);
        }
        finally
        {
            t.Cts?.Dispose();
            t.Cts = null;
        }
        Finished?.Invoke(t);
        Pump();
    }

    /// <summary>Xóa 1 file, hoặc cả thư mục: liệt kê mọi object bên trong rồi xóa theo lô (tiến độ = số object).</summary>
    private async Task RunDeleteAsync(TransferTask t, CancellationToken ct)
    {
        try
        {
            long total;
            int failed;
            Interlocked.Exchange(ref t.LiveDone, 0);
            if (t.IsFolder)
            {
                // Vừa liệt kê vừa xóa: tổng số tăng dần trong lúc liệt kê.
                t.Listing = true;
                (total, failed) = await t.Browser.DeletePrefixAsync(t.Bucket, t.Key,
                    n => Interlocked.Exchange(ref t.LiveTotal, n), n => Interlocked.Exchange(ref t.LiveDone, n), ct);
                t.Listing = false;
            }
            else
            {
                var keys = t.Keys ?? new[] { t.Key };
                total = keys.Count;
                Interlocked.Exchange(ref t.LiveTotal, total);
                failed = await t.Browser.DeleteManyAsync(t.Bucket, keys, n => Interlocked.Exchange(ref t.LiveDone, n), ct);
            }
            ct.ThrowIfCancellationRequested();

            // Bucket có/từng có versioning: xóa thường chỉ để lại "dấu xóa", bản cũ vẫn còn (và thư mục vẫn hiện).
            // Xóa vĩnh viễn mọi phiên bản dưới thư mục / của các file đã chọn.
            t.Purging = true;
            long purged;
            int purgeFailed;
            if (t.IsFolder)
                (purged, purgeFailed) = await t.Browser.PurgeVersionsAsync(t.Bucket, t.Key, null, n => Interlocked.Exchange(ref t.LivePurged, n), ct);
            else
            {
                var set = new HashSet<string>(t.Keys ?? new[] { t.Key }, StringComparer.Ordinal);
                string folder = CommonFolder(set);
                (purged, purgeFailed) = await t.Browser.PurgeVersionsAsync(t.Bucket, folder, set.Contains, n => Interlocked.Exchange(ref t.LivePurged, n), ct);
            }
            t.Purging = false;
            failed += purgeFailed;
            if (purged > 0) Log?.Invoke($"Đã xóa vĩnh viễn {purged:N0} phiên bản cũ / dấu xóa trong {t.Bucket}/{t.Key}");
            ct.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref t.LiveTotal, total);
            Interlocked.Exchange(ref t.LiveDone, total);
            if (failed > 0)
            {
                string err = $"{failed:N0}/{total:N0} object không xóa được" + (t.Browser.LastDeleteError != null ? " — " + t.Browser.LastDeleteError : "");
                lock (_lock) Set(t, TransferStatus.Failed, err);
                Log?.Invoke("LỖI xóa " + t.Bucket + "/" + t.Key + ": " + err);
            }
            else
            {
                lock (_lock) Set(t, TransferStatus.Done);
                Log?.Invoke($"Đã xóa {t.Bucket}/{t.Key} ({total:N0} object)");
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            lock (_lock) Set(t, TransferStatus.Stopped);
        }
        catch (Exception ex)
        {
            string err = S3ClientFactory.Describe(ex);
            lock (_lock) Set(t, TransferStatus.Failed, err);
            Log?.Invoke("LỖI xóa " + t.Bucket + "/" + t.Key + ": " + err);
        }
        finally
        {
            t.Listing = false;
            t.Purging = false;
            t.Cts?.Dispose();
            t.Cts = null;
        }
    }

    /// <summary>Thư mục chung dài nhất của các key (để liệt kê phiên bản 1 lần).</summary>
    private static string CommonFolder(IEnumerable<string> keys)
    {
        string? common = null;
        foreach (var k in keys)
        {
            int i = k.LastIndexOf('/');
            string f = i >= 0 ? k[..(i + 1)] : "";
            if (common == null) { common = f; continue; }
            int n = 0;
            while (n < common.Length && n < f.Length && common[n] == f[n]) n++;
            common = common[..n];
            int j = common.LastIndexOf('/');
            common = j >= 0 ? common[..(j + 1)] : "";
        }
        return common ?? "";
    }

    private static void Set(TransferTask t, TransferStatus s, string? error = null)
    {
        t.State = s;
        if (error != null) t.LastError = error;
        Dispatcher.UIThread.Post(() =>
        {
            if (error != null) t.Error = error;
            t.Status = s;
            t.Tick(1);
        });
    }

    /// <summary>Part 16 MB, tăng dần để không vượt 10.000 part của S3.</summary>
    private static long PartSizeFor(long size)
    {
        long p = 16L * 1024 * 1024;
        while (size / p > 9000) p *= 2;
        return p;
    }
}
