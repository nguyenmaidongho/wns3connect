using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WNS3Connect.Services;

/// <summary>ObservableCollection thêm/thay hàng loạt chỉ bằng 1 sự kiện (thêm 1000 dòng không làm UI dựng lại 1000 lần).</summary>
public class RangeCollection<T> : ObservableCollection<T>
{
    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;
        CheckReentrancy();
        int start = Count;
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, items as System.Collections.IList ?? items.ToList(), start));
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
