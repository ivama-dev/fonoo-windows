using System.ComponentModel;
using Fonoo.Windows.Accounts;
using Fonoo.Windows.Telephony;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Fonoo.Windows.Desktop;

public sealed class PhonePresenceIndicator : INotifyPropertyChanged
{
    private PhonePresenceDisplay? value;
    public string Text => value?.Text ?? "";
    public string Detail => value?.Detail ?? "";
    public Visibility Visibility => value is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DetailVisibility => Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public SolidColorBrush Brush { get; private set; } = new(global::Windows.UI.Color.FromArgb(255, 119, 115, 134));
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(PhonePresenceDisplay? next)
    {
        if (value == next) return;
        value = next;
        var color = next?.Color ?? "#777386";
        Brush = new(global::Windows.UI.Color.FromArgb(255, Convert.ToByte(color.Substring(1, 2), 16),
            Convert.ToByte(color.Substring(3, 2), 16), Convert.ToByte(color.Substring(5, 2), 16)));
        PropertyChanged?.Invoke(this, new(""));
    }
}
public sealed class TeamListItem(TeamMember member)
{
    public TeamMember Member { get; } = member;
    public string Name => Member.Name;
    public string Detail => Member.Detail;
    public string Initials => Member.Initials;
    public string AvailabilityLabel => Member.AvailabilityLabel;
    public PhonePresenceIndicator Phone { get; } = new();
    public override string ToString() => $"{Name} · {Detail} · {Phone.Text} {Phone.Detail}";
}
public sealed class FavoriteListItem(Favorite favorite)
{
    public Favorite Favorite { get; } = favorite;
    public string Name => Favorite.Name;
    public string Number => Favorite.Number;
    public string Initials => Favorite.Initials;
    public PhonePresenceIndicator Phone { get; } = new();
    public override string ToString() => $"{Name} · {Number} · {Phone.Text} {Phone.Detail}";
}
