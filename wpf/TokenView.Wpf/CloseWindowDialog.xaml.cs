using System.Windows;
using TokenView.Wpf.Services;

namespace TokenView.Wpf;

public enum CloseChoice
{
    Cancel,
    Tray,
    Exit,
}

public partial class CloseWindowDialog : Window
{
    private CloseWindowDialog()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
    }

    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;
    public bool Remember => RememberCheck.IsChecked == true;

    public static CloseWindowDialog Show(Window owner)
    {
        var dialog = new CloseWindowDialog { Owner = owner };
        dialog.ShowDialog();
        return dialog;
    }

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Tray;
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Exit;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Cancel;
        DialogResult = false;
    }
}
