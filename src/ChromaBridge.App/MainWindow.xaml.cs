// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

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

        ShowLockupForTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (args.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(ShowLockupForTheme);
    }

    /// <summary>
    /// The lockup's wordmark is drawn into the image, so it has to match the theme: black text
    /// on the light theme, white on the dark one. The theme follows Windows' app setting,
    /// which is what ThemeMode="System" follows too.
    /// </summary>
    private void ShowLockupForTheme()
    {
        var file = AppsUseLightTheme() ? "chromabridge-lockup.png" : "chromabridge-lockup-white.png";

        Lockup.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{file}"));
    }

    private static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return true;
        }
    }
}
