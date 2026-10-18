using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WNS3Connect.Models;
using WNS3Connect.Services;

namespace WNS3Connect.Views;

/// <summary>Thêm / sửa tài khoản S3 (giống "Edit Account" của S3 Browser).</summary>
public class AccountDialog : Window
{
    private readonly S3Account _acc;
    private readonly bool _isNew;
    private S3Account? _result;

    private readonly TextBox _name = new();
    private readonly ComboBox _type = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _endpoint = new() { PlaceholderText = "vd: s3.cloudfly.vn" };
    private readonly TextBox _region = new() { PlaceholderText = "us-east-1" };
    private readonly TextBox _access = new();
    private readonly TextBox _secret = new() { PasswordChar = '●' };
    private readonly CheckBox _ssl = new() { Content = "Dùng kết nối bảo mật (SSL/TLS)" };
    private readonly CheckBox _pathStyle = new() { Content = "Path-style (endpoint/bucket/key) — nên bật cho S3-compatible" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _endpointRow = new() { Spacing = 3 };

    public AccountDialog(S3Account? existing)
    {
        _isNew = existing == null;
        _acc = existing?.Clone() ?? new S3Account { Endpoint = "s3.cloudfly.vn" };

        Title = _isNew ? "Thêm tài khoản S3" : "Sửa tài khoản";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        _type.ItemsSource = new[] { "Amazon S3 Compatible Storage", "Amazon S3 (AWS)" };
        _type.SelectedIndex = string.IsNullOrWhiteSpace(_acc.Endpoint) && !_isNew ? 1 : 0;
        _type.SelectionChanged += (_, _) => _endpointRow.IsVisible = _type.SelectedIndex == 0;

        _name.Text = _acc.Name;
        _endpoint.Text = _acc.Endpoint;
        _region.Text = _acc.Region;
        _access.Text = _acc.AccessKey;
        _secret.PlaceholderText = _isNew ? "" : "(để trống = giữ nguyên)";
        _ssl.IsChecked = _acc.UseSsl;
        _pathStyle.IsChecked = _acc.PathStyle;

        var root = new StackPanel { Spacing = 10, Margin = new Thickness(20, 16) };
        root.Children.Add(new TextBlock { Text = _isNew ? "Nhập thông tin tài khoản rồi bấm Lưu" : "Sửa thông tin tài khoản rồi bấm Lưu", FontSize = 15, FontWeight = FontWeight.SemiBold });
        root.Children.Add(Field("Tên hiển thị:", _name, "Đặt tên bất kỳ cho tài khoản."));
        root.Children.Add(Field("Loại tài khoản:", _type, null));
        _endpointRow.Children.Add(new TextBlock { Text = "API endpoint:" });
        _endpointRow.Children.Add(_endpoint);
        _endpointRow.Children.Add(Hint("Endpoint S3-compatible của nhà cung cấp, vd: s3.cloudfly.vn"));
        _endpointRow.IsVisible = _type.SelectedIndex == 0;
        root.Children.Add(_endpointRow);
        root.Children.Add(Field("Region:", _region, "CloudFly/MinIO thường để us-east-1."));
        root.Children.Add(Field("Access Key ID:", _access, null));
        root.Children.Add(Field("Secret Access Key:", _secret, "Được mã hóa bằng Windows DPAPI — chỉ user Windows này đọc được."));
        root.Children.Add(_ssl);
        root.Children.Add(_pathStyle);
        root.Children.Add(_status);

        var test = new Button { Content = "Kiểm tra kết nối" };
        test.Click += async (_, _) => await TestAsync();
        var import = new Button { Content = "Nhập từ WNTerm", IsVisible = _isNew };
        import.Click += (_, _) => ImportFromWNTerm();
        var save = new Button { Content = "Lưu", IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        save.Classes.Add("accent");
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Hủy", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close();

        var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(test);
        left.Children.Add(import);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(save);
        right.Children.Add(cancel);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(left);
        bar.Children.Add(right);
        root.Children.Add(bar);

        Content = root;
        Opened += (_, _) => (_isNew ? _name : _access).Focus();
    }

    private static TextBlock Hint(string t) => new() { Text = t, FontSize = 11.5, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };

    private static Control Field(string label, Control input, string? hint)
    {
        var p = new StackPanel { Spacing = 3 };
        p.Children.Add(new TextBlock { Text = label });
        p.Children.Add(input);
        if (hint != null) p.Children.Add(Hint(hint));
        return p;
    }

    public async Task<S3Account?> ShowAsync(Window owner)
    {
        await ShowDialog(owner);
        return _result;
    }

    private void SetStatus(string text, bool ok)
    {
        _status.Text = text;
        _status.Foreground = ok ? Brushes.SeaGreen : (IBrush?)this.FindResource("ThemeDangerBrush") ?? Brushes.Red;
    }

    /// <summary>Đọc form vào một bản sao; secret trả riêng (null = giữ secret cũ).</summary>
    private (S3Account acc, string? secret) Collect()
    {
        var a = _acc.Clone();
        a.Name = (_name.Text ?? "").Trim();
        bool aws = _type.SelectedIndex == 1;
        string ep = (_endpoint.Text ?? "").Trim();
        if (ep.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) _ssl.IsChecked = false;
        a.Endpoint = aws ? "" : S3ClientFactory.StripScheme(ep);
        a.Region = string.IsNullOrWhiteSpace(_region.Text) ? "us-east-1" : _region.Text.Trim();
        a.AccessKey = (_access.Text ?? "").Trim();
        a.UseSsl = _ssl.IsChecked == true;
        a.PathStyle = _pathStyle.IsChecked == true;
        string s = _secret.Text ?? "";
        return (a, s.Length > 0 ? s.Trim() : null);
    }

    private string? Validate(S3Account a, string? secret)
    {
        if (_type.SelectedIndex == 0 && a.Endpoint.Length == 0) return "Nhập API endpoint.";
        if (a.AccessKey.Length == 0) return "Nhập Access Key ID.";
        if (secret == null && string.IsNullOrEmpty(a.SecretKeyEnc)) return "Nhập Secret Access Key.";
        return null;
    }

    private async Task TestAsync()
    {
        var (a, secret) = Collect();
        if (Validate(a, secret) is { } err) { SetStatus(err, false); return; }
        SetStatus("Đang kết nối…", true);
        try
        {
            using var client = S3ClientFactory.Create(a, secret);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var r = await client.ListBucketsAsync(new Amazon.S3.Model.ListBucketsRequest(), cts.Token);
            int n = r.Buckets?.Count ?? 0;
            SetStatus($"Kết nối OK — thấy {n} bucket.", true);
        }
        catch (Exception ex)
        {
            SetStatus("Không được: " + S3ClientFactory.Describe(ex), false);
        }
    }

    private void ImportFromWNTerm()
    {
        var w = AccountStore.ReadFromWNTerm();
        if (w == null) { SetStatus("Không tìm thấy cấu hình Cloud backup S3 trong WNTerm trên máy này.", false); return; }
        _acc.ExternalBuckets = w.ExternalBuckets;
        _acc.SecretKeyEnc = w.SecretKeyEnc;
        _name.Text = w.Name;
        _type.SelectedIndex = w.Endpoint.Length > 0 ? 0 : 1;
        _endpoint.Text = w.Endpoint;
        _region.Text = w.Region;
        _access.Text = w.AccessKey;
        _secret.Text = "";
        _secret.PlaceholderText = w.SecretKeyEnc != null ? "(đã lấy từ WNTerm)" : "";
        _ssl.IsChecked = w.UseSsl;
        _pathStyle.IsChecked = w.PathStyle;
        SetStatus("Đã điền từ WNTerm. Bấm \"Kiểm tra kết nối\" rồi Lưu.", true);
    }

    private void Save()
    {
        var (a, secret) = Collect();
        if (Validate(a, secret) is { } err) { SetStatus(err, false); return; }
        if (secret != null) a.SecretKeyEnc = SecretProtector.Encrypt(secret);
        if (a.Name.Length == 0) a.Name = a.Endpoint.Length > 0 ? a.Endpoint : "Amazon S3";
        _result = a;
        Close();
    }
}
