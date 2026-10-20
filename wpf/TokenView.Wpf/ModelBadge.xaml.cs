using System.Windows;
using System.Windows.Controls;

namespace TokenView.Wpf;

public partial class ModelBadge : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(string), typeof(ModelBadge),
        new PropertyMetadata(string.Empty));

    public ModelBadge()
    {
        InitializeComponent();
    }

    public string Model
    {
        get => (string)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }
}
