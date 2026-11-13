using System;
using System.IO;
using System.Windows;
using System.Threading;
using System.Windows.Controls;
using System.Security.Cryptography;
using System.Text;
using ExplorerTabUtility.UI.Views;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;

namespace ExplorerTabUtility;

// ReSharper disable once RedundantExtendsListEntry
public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _closeRequest;
    private RegisteredWaitHandle? _closeWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, Constants.MutexId, out var createdNew);

        if (createdNew)
        {
            base.OnStartup(e);
            SetupTooltipBehavior();

            try
            {
                SettingsManager.Load();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CustomMessageBox.Show($"Could not read settings. Close any program using the settings file and restart Explorer Tab Utility.\n\n{ex.Message}",
                    Constants.AppName, icon: MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            var window = new MainWindow();
            using var sha256 = SHA256.Create();
            var pathBytes = Encoding.Unicode.GetBytes(Helper.GetExecutablePath().ToUpperInvariant());
            var pathHash = BitConverter.ToString(sha256.ComputeHash(pathBytes)).Replace("-", "");
            _closeRequest = new EventWaitHandle(false, EventResetMode.AutoReset, Constants.CloseEventPrefix + pathHash);
            _closeWait = ThreadPool.RegisterWaitForSingleObject(_closeRequest, (_, _) =>
                Dispatcher.BeginInvoke(window.RequestExit), null, Timeout.Infinite, executeOnlyOnce: false);
            return;
        }

        CustomMessageBox.Show("""
                              Another instance is already running.
                              Check in System Tray Icons.
                              """, Constants.AppName, icon: MessageBoxImage.Information);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            base.OnExit(e);
        }
        finally
        {
            _closeWait?.Unregister(null);
            _closeRequest?.Dispose();
            _mutex?.Dispose();
        }
    }

    private static void SetupTooltipBehavior()
    {
        ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(3500));
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(1700));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(150));
        ToolTipService.ShowsToolTipOnKeyboardFocusProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(false));
    }
}
