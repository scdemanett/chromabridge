// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Windows;
using System.Windows.Media.Imaging;

namespace ChromaBridge.App;

public partial class MainWindow : Window
{
    /// <summary>At this width and above, provider cards sit two to a row.</summary>
    private const double TwoColumnWidth = 1500;

    public static readonly DependencyProperty CardColumnsProperty =
        DependencyProperty.Register(nameof(CardColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(1));

    /// <summary>How many provider cards sit side by side.</summary>
    public int CardColumns
    {
        get => (int)GetValue(CardColumnsProperty);
        set => SetValue(CardColumnsProperty, value);
    }

    public MainWindow()
    {
        InitializeComponent();

        SizeChanged += (_, args) => CardColumns = args.NewSize.Width >= TwoColumnWidth ? 2 : 1;

        // Given the whole .ico, WPF picks the title bar and Alt+Tab frames itself
        using var icon = AppIcon.Open();

        Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
}
