// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Windows;
using Forms = System.Windows.Forms;

namespace ChromaBridge.App;

/// <summary>
/// The tray app. It only shows status and edits settings; the service does the lighting,
/// so closing this app changes nothing on the devices.
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private bool _updateWaiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, BridgePaths.AppMutexName, out var first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, BridgePaths.AppShowEventName);

        if (!first)
        {
            // Another copy is running, perhaps with its tray icon hidden: ask it to show its window
            _showEvent.Set();
            Shutdown();
            return;
        }

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, -1, false);

        _viewModel = new MainViewModel();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open ChromaBridge", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _trayIcon = new Forms.NotifyIcon
        {
            // The small-icon size follows the display scale, so the tray gets a frame drawn for it
            Icon = AppIcon.Create(Forms.SystemInformation.SmallIconSize.Width),
            Text = BridgePaths.ProductName,
            ContextMenuStrip = menu,
            Visible = _viewModel.ShowTrayIcon
        };

        // A new version is announced once from the tray; installing it is the user's click.
        // A hidden icon comes back for it, since the notification hangs off the icon.
        _trayIcon.BalloonTipClicked += (_, _) => ShowWindow();
        _viewModel.UpdateFound += version =>
        {
            _updateWaiting = true;
            ApplyTrayIconVisibility();
            TrayBalloon.Show(_trayIcon, "ChromaBridge update", $"Version {version} is available. Open ChromaBridge to install it.", 10000);
        };
        _viewModel.ShowTrayIconChanged += _ => ApplyTrayIconVisibility();
        _viewModel.ExitRequested += ExitApp;

        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                ShowWindow();
        };

        // "--tray" starts hidden, for launching at login
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            ShowWindow();

        // "--test-balloon" shows the update notification without an update, for checking its look
        if (e.Args.Contains("--test-balloon", StringComparer.OrdinalIgnoreCase))
            TrayBalloon.Show(_trayIcon, "ChromaBridge update", "Version 9.9.9 is available. Open ChromaBridge to install it.", 10000);

        _viewModel.Start();
    }

    private void ApplyTrayIconVisibility()
    {
        if (_trayIcon != null && _viewModel != null)
            _trayIcon.Visible = _viewModel.ShowTrayIcon || _updateWaiting;
    }

    private void ShowWindow()
    {
        if (_window == null)
        {
            _window = new MainWindow { DataContext = _viewModel };

            // Closing the window keeps the app in the tray
            _window.Closing += (_, args) =>
            {
                args.Cancel = true;
                _window.SavePlacement();
                _window.Hide();
            };
        }

        _window.Show();

        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
    }

    private void ExitApp()
    {
        if (_window is { IsVisible: true })
            _window.SavePlacement();

        _showWait?.Unregister(null);

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _viewModel?.Dispose();
        Shutdown();
    }
}
