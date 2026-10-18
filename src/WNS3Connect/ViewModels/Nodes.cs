using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WNS3Connect.Models;

namespace WNS3Connect.ViewModels;

/// <summary>Một tài khoản S3 trên sidebar; con là các bucket (nạp khi mở rộng).</summary>
public partial class AccountNode : ObservableObject
{
    [ObservableProperty] private S3Account _account;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<BucketNode> Buckets { get; } = new();
    public bool Loaded { get; set; }

    /// <summary>Gọi khi người dùng mở rộng node lần đầu (MainViewModel gán).</summary>
    public Action<AccountNode>? ExpandRequested { get; set; }

    public AccountNode(S3Account account) { _account = account; }

    public string Name => Account.DisplayName;
    public string Subtitle => string.IsNullOrWhiteSpace(Account.Endpoint) ? "Amazon S3 · " + Account.Region : Account.Endpoint;
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public bool HasStatus => Status.Length > 0;

    partial void OnAccountChanged(S3Account value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Initial));
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !Loaded && !IsLoading) ExpandRequested?.Invoke(this);
    }
}

public partial class BucketNode : ObservableObject
{
    public AccountNode Owner { get; }
    public string Name { get; }
    public bool IsExternal { get; }
    [ObservableProperty] private bool _isCurrent;

    public BucketNode(AccountNode owner, string name, bool external)
    {
        Owner = owner;
        Name = name;
        IsExternal = external;
    }
}

/// <summary>Một mảnh của breadcrumb: bucket / thư mục / thư mục con.</summary>
public record Crumb(string Label, string Prefix, bool IsBucket, bool IsLast);
