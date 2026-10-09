using System.ComponentModel;
using Fonoo.Windows.Telephony;

namespace Fonoo.Windows.Desktop;

public sealed class ContactListItem(ContactRow contact) : INotifyPropertyChanged
{
    public ContactRow Contact { get; } = contact;
    public string Name => Contact.Name;
    public string Detail => Contact.Detail;
    public string Initials => Contact.Initials;
    public override string ToString() => $"{Name} · {Detail}";
    private bool isFavorite;
    private bool canSave;
    public bool CanAddFavorite => canSave && !isFavorite;
    public string FavoriteGlyph => isFavorite ? "\uE735" : "\uE734";
    public string FavoriteAction => isFavorite ? "Bereits als Favorit gespeichert" : "Zu Favoriten hinzufügen";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateFavoriteState(bool saved, bool writable)
    {
        if (isFavorite == saved && canSave == writable) return;
        isFavorite = saved; canSave = writable;
        PropertyChanged?.Invoke(this, new(nameof(CanAddFavorite)));
        PropertyChanged?.Invoke(this, new(nameof(FavoriteGlyph)));
        PropertyChanged?.Invoke(this, new(nameof(FavoriteAction)));
    }
}
