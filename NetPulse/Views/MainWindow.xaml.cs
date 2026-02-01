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
}