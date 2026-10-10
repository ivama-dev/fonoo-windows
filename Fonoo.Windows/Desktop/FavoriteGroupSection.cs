using Microsoft.UI.Xaml;

namespace Fonoo.Windows.Desktop;

public sealed class FavoriteGroupSection(string? id, string name, FavoriteListItem[] items, bool canMoveUp, bool canMoveDown, bool showHeader = true)
{
    public string? Id { get; } = id;
    public string Name { get; } = name;
    public FavoriteListItem[] Items { get; } = items;
    public string Count => Items.Length.ToString();
    public bool CanMoveUp { get; } = canMoveUp;
    public bool CanMoveDown { get; } = canMoveDown;
    public bool CanDrag => Id is not null;
    public Visibility ActionsVisibility => Id is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HeaderVisibility => showHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => Items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public override string ToString() => Name;
}
