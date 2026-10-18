using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WNS3Connect.Services;

namespace WNS3Connect.Views;

/// <summary>Đăng nhập tài khoản WebNow (chung WNTerm) và backup / đồng bộ danh sách tài khoản S3.</summary>
public class WebNowDialog : Window
{
    private readonly WebNowAccount _acc;
    private readonly Func<bool, Task<WebNowSyncResult?>> _sync;
    private readonly StackPanel _root = new() { Spacing = 10, Margin = new Thickness(20, 16) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private bool _busy;

    public WebNowDialog(WebNowAccount acc, Func<bool, Task<WebNowSyncResult?>> sync)
    {
        _acc = acc;
        _sync = sync;
        Title = "Tài khoản WebNow";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_busy) Close(); };
        Content = _root;
        Build();
    }

    private void SetStatus(string text, bool ok)
    {
        _status.Text = text;
        _status.Foreground = ok ? Brushes.SeaGreen : (IBrush?)this.FindResource("ThemeDangerBrush") ?? Brushes.Red;
    }

    private static TextBlock Muted(string t) => new() { Text = t, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    private void Build()
    {
        _root.Children.Clear();
        _status.Text = "";
        _root.Children.Add(new TextBlock { Text = "Tài khoản WebNow", FontSize = 16, FontWeight = FontWeight.SemiBold });
        if (_acc.IsLoggedIn) BuildLoggedIn(); else BuildLogin();
    }

    private void BuildLogin()
    {
        _root.Children.Add(Muted("Dùng chung tài khoản với WNTerm. Danh sách tài khoản S3 (kèm Secret Key) được mã hóa ngay trên máy rồi mới gửi lên — máy chủ không đọc được."));

        var wn = WebNowAccount.ReadWNTermLogin();
        if (wn != null)
        {
            var useWn = new Button { Content = $"Dùng đăng nhập WNTerm trên máy này ({wn.Value.email})", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            useWn.Classes.Add("accent");
            useWn.Click += async (_, _) => await Run(async () =>
            {
                await _acc.UseWNTermLoginAsync();
                await AfterLogin();
            }, "Đang kết nối…");
            _root.Children.Add(useWn);
            _root.Children.Add(Muted("— hoặc đăng nhập bằng email —"));
        }

        var email = new TextBox { PlaceholderText = "Email", Text = _acc.Email };
        var pw = new TextBox { PlaceholderText = "Mật khẩu", PasswordChar = '●' };
        var login = new Button { Content = "Đăng nhập", IsDefault = true, MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (wn == null) login.Classes.Add("accent");
        login.Click += async (_, _) =>
        {
            string e = (email.Text ?? "").Trim(), p = pw.Text ?? "";
            if (e.Length == 0 || p.Length == 0) { SetStatus("Nhập email và mật khẩu.", false); return; }
            await Run(async () =>
            {
                await _acc.LoginAsync(e, p);
                await AfterLogin();
            }, "Đang đăng nhập… (mất vài giây để tạo khóa)");
        };
        var signup = new Button { Content = "Chưa có tài khoản? Đăng ký", Classes = { "tool" } };
        signup.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(WebNowAccount.SignUpUrl) { UseShellExecute = true }); } catch { } };

        _root.Children.Add(email);
        _root.Children.Add(pw);
        var bar = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(signup, Dock.Left);
        DockPanel.SetDock(login, Dock.Right);
        bar.Children.Add(signup);
        bar.Children.Add(login);
        _root.Children.Add(bar);
        _root.Children.Add(_status);
        Opened += (_, _) => (string.IsNullOrEmpty(email.Text) ? email : pw).Focus();
    }

    private async Task AfterLogin()
    {
        var r = await _sync(false);
        Build();
        if (r != null) SetStatus(Describe(r), true);
    }

    private static string Describe(WebNowSyncResult r)
    {
        var parts = new List<string>();
        if (r.Added > 0) parts.Add($"thêm {r.Added}");
        if (r.Updated > 0) parts.Add($"cập nhật {r.Updated}");
        if (r.Deleted > 0) parts.Add($"xóa {r.Deleted}");
        string changes = parts.Count > 0 ? " (" + string.Join(", ", parts) + " từ máy chủ)" : "";
        return $"Đã đồng bộ {r.Total} tài khoản S3{changes}. Bản trên máy chủ: #{r.ServerVersion}.";
    }

    private void BuildLoggedIn()
    {
        _root.Children.Add(new TextBlock { Text = "Đã đăng nhập: " + _acc.Email, FontWeight = FontWeight.SemiBold });
        if (_acc.FromWNTerm) _root.Children.Add(Muted("Đang dùng chung phiên đăng nhập của WNTerm trên máy này."));
        string last = _acc.LastSyncUtc is DateTime d ? d.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") : "chưa";
        _root.Children.Add(Muted($"Đồng bộ lần cuối: {last}. Mỗi khi thêm/sửa/xóa tài khoản S3, app tự backup lên tài khoản này. Cài lại máy → đăng nhập là có lại đủ."));

        var backup = new Button { Content = "☁ Backup ngay", MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Center };
        backup.Classes.Add("accent");
        backup.Click += async (_, _) => await Run(async () =>
        {
            var r = await _sync(true);
            Build();
            if (r != null) SetStatus(Describe(r), true);
        }, "Đang đồng bộ…");
        var logout = new Button { Content = "Đăng xuất", MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        logout.Click += async (_, _) => await Run(async () =>
        {
            await _acc.LogoutAsync();
            Build();
            SetStatus("Đã đăng xuất. Tài khoản S3 trên máy này vẫn giữ nguyên.", true);
        }, "Đang đăng xuất…");
        var close = new Button { Content = "Đóng", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(backup);
        bar.Children.Add(logout);
        bar.Children.Add(close);
        _root.Children.Add(bar);
        _root.Children.Add(_status);
    }

    private async Task Run(Func<Task> action, string busyText)
    {
        if (_busy) return;
        _busy = true;
        _root.IsEnabled = false;
        SetStatus(busyText, true);
        try { await action(); }
        catch (Exception ex)
        {
            if (!_acc.IsLoggedIn) Build();
            SetStatus(ex.Message, false);
        }
        finally
        {
            _busy = false;
            _root.IsEnabled = true;
        }
    }
}
