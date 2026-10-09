using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private bool previewMode = false;
    private object? destinationBeforeCall;

    private void InitializeNavigation()
    {
        DesktopNavigation.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdateNavigationSidebar());
        foreach (var text in new[] { Status, CompanyName, ExtensionLabel })
            text.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateNavigationSidebar());
        UpdateNavigationSidebar();
    }

    private void UpdateNavigationSidebar()
    {
        if (SidebarFooter is null) return;
        var open = DesktopNavigation.IsPaneOpen;
        var textVisibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidebarFooter.Margin = open ? new Thickness(4, 12, 12, 20) : new Thickness(4, 12, 4, 20);
        SidebarStatus.ColumnSpacing = SidebarIdentity.ColumnSpacing = DndContent.ColumnSpacing = ReconnectContent.ColumnSpacing = open ? 8 : 0;
        Status.Visibility = SidebarIdentityText.Visibility = DndLabel.Visibility = ReconnectLabel.Visibility = textVisibility;
        var connected = snapshot.Registered || snapshot.InCall;
        SidebarConnectedIcon.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        SidebarDisconnectedIcon.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        SidebarConnectedIcon.Symbol = snapshot.InCall ? Symbol.Phone : Symbol.Accept;
        ToolTipService.SetToolTip(SidebarStatus, Status.Text);
        AutomationProperties.SetName(SidebarStatus, Status.Text);
        var identity = string.Join("\n", new[] { CompanyName.Text, ExtensionLabel.Text }.Where(t => !string.IsNullOrWhiteSpace(t)));
        SidebarIdentity.Visibility = identity.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(SidebarIdentity, identity);
        AutomationProperties.SetName(SidebarIdentity, identity);
        var dnd = snapshot.DoNotDisturb;
        DndLabel.Text = dnd ? "Nicht stören aktiv" : "Nicht stören";
        DndActiveIcon.Visibility = dnd ? Visibility.Visible : Visibility.Collapsed;
        DndInactiveIcon.Visibility = dnd ? Visibility.Collapsed : Visibility.Visible;
        var dndAction = dnd ? "Nicht stören ist aktiv · ausschalten" : "Nicht stören einschalten";
        ToolTipService.SetToolTip(DndButton, dndAction);
        AutomationProperties.SetName(DndButton, dndAction);
    }

    private void DesktopNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (PageTitle is not null)
        {
            RenderDestination();
            if ((DesktopNavigation.SelectedItem as NavigationViewItem)?.Tag as string == "recents") _ = SyncHistoryAsync();
            if ((DesktopNavigation.SelectedItem as NavigationViewItem)?.Tag as string == "team" && teamSnapshot is null && configuration is not null)
                _ = RunAsync(RefreshTeamAsync);
        }
    }

    private void RenderDestination()
    {
        var item = DesktopNavigation.SelectedItem as NavigationViewItem;
        var destination = item?.Tag as string ?? "dial";
        var ready = configuration is not null || previewMode;
        var showAccount = destination == "account" || !ready;
        AccountContainer.Visibility = showAccount ? Visibility.Visible : Visibility.Collapsed;
        PhonePanel.Visibility = showAccount ? Visibility.Collapsed : Visibility.Visible;
        LoginPanel.Visibility = !account.SignedIn ? Visibility.Visible : Visibility.Collapsed;
        CallReturnButton.Visibility = snapshot.InCall && destination != "call" ? Visibility.Visible : Visibility.Collapsed;
        SetupPanel.Visibility = account.SignedIn && (configuration is null || choosingCompany) ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = account.SignedIn && configuration is not null && !choosingCompany ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = showAccount ? (account.SignedIn ? "Konto" : "Anmelden") : item?.Content?.ToString() ?? "Wählen";
        DialpadPanel.Visibility = !showAccount && destination == "dial" ? Visibility.Visible : Visibility.Collapsed;
        FavoritesPanel.Visibility = !showAccount && destination == "favorites" ? Visibility.Visible : Visibility.Collapsed;
        CallPanel.Visibility = !showAccount && destination == "call" && snapshot.InCall ? Visibility.Visible : Visibility.Collapsed;
        RecentsPanel.Visibility = !showAccount && destination == "recents" ? Visibility.Visible : Visibility.Collapsed;
        ContactsPanel.Visibility = !showAccount && destination == "contacts" ? Visibility.Visible : Visibility.Collapsed;
        TeamPanel.Visibility = !showAccount && destination == "team" ? Visibility.Visible : Visibility.Collapsed;
        DndButton.Visibility = ready && !previewMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigationSidebar();
        RefreshActivityCadence();

    }

    private void ReturnToCallClicked(object sender, RoutedEventArgs e) => DesktopNavigation.SelectedItem = ConversationTab;

}
