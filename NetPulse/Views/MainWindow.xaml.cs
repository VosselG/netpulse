using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetPulse.ViewModels;

namespace NetPulse.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void DevicesList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Ensure right-click selects the item so "Delete" acts on the intended card.
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is ListBoxItem item)
            item.IsSelected = true;
    }

    private void DevicesList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Forward wheel scrolling from inner ListBoxes to the outer ScrollViewer so the wheel
        // works when hovering device cards.
        if (DevicesScrollViewer is null)
            return;

        // Respect the user's OS setting where possible.
        var lines = SystemParameters.WheelScrollLines;
        if (lines <= 0)
            lines = 3;

        // 16 is a typical WPF line-height approximation (DIPs per line).
        var delta = (e.Delta / 120.0) * lines * 16.0;

        DevicesScrollViewer.ScrollToVerticalOffset(DevicesScrollViewer.VerticalOffset - delta);
        e.Handled = true;
    }
}