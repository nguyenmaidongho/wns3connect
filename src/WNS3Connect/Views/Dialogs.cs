using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace WNS3Connect.Views;

/// <summary>Hộp thoại nhỏ dựng bằng code: thông báo, xác nhận, nhập chữ, chọn file/thư mục.</summary>
public static class Dialogs
{
    public static Window? Owner { get; set; }

    private static Window MakeWindow(string title, Control content, double width = 420)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new Border { Padding = new Thickness(18, 16), Child = content }
        };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        return w;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var b in buttons) { b.MinWidth = 84; b.HorizontalContentAlignment = HorizontalAlignment.Center; p.Children.Add(b); }
        return p;
    }

    public static async Task MessageAsync(string text, string title = "WNS3Connect")
    {
        if (Owner == null) return;
        var ok = new Button { Content = "OK", IsDefault = true };
        var panel = new StackPanel();
        panel.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxHeight = 400 });
        panel.Children.Add(Buttons(ok));
        var w = MakeWindow(title, panel);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(Owner);
    }

    public static async Task<bool> ConfirmAsync(string text, string yes = "Đồng ý", string title = "Xác nhận", bool danger = false)
    {
        if (Owner == null) return false;
        bool result = false;
        var y = new Button { Content = yes, IsDefault = !danger };
        if (danger) y.Classes.Add("accent");
        var n = new Button { Content = "Hủy", IsCancel = true, IsDefault = danger };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Buttons(y, n));
        var w = MakeWindow(title, panel);
        y.Click += (_, _) => { result = true; w.Close(); };
        n.Click += (_, _) => w.Close();
        await w.ShowDialog(Owner);
        return result;
    }

    public static async Task<string?> PromptAsync(string label, string title, string initial = "", string ok = "OK")
    {
        if (Owner == null) return null;
        string? result = null;
        var box = new TextBox { Text = initial };
        var y = new Button { Content = ok, IsDefault = true };
        var n = new Button { Content = "Hủy", IsCancel = true };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(Buttons(y, n));
        var w = MakeWindow(title, panel);
        y.Click += (_, _) => { result = box.Text ?? ""; w.Close(); };
        n.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await w.ShowDialog(Owner);
        return result;
    }

    public static async Task<List<string>> PickFilesAsync(string title)
    {
        if (Owner == null) return new();
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true });
        return files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
    }

    public static async Task<string?> PickFolderAsync(string title, string? start = null)
    {
        if (Owner == null) return null;
        var opts = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
            opts.SuggestedStartLocation = await Owner.StorageProvider.TryGetFolderFromPathAsync(start);
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(opts);
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<bool> SetClipboardAsync(string text)
    {
        try
        {
            var cb = Owner?.Clipboard;
            if (cb == null) return false;
            await cb.SetTextAsync(text);
            return true;
        }
        catch { return false; }
    }
}
