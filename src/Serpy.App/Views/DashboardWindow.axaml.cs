using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Serpy.App.ViewModels;

namespace Serpy.App.Views;

public sealed partial class DashboardWindow : Window
{
    private DispatcherTimer? _setupDetailsTimer;

    public DashboardWindow() : this(true)
    {
    }

    internal DashboardWindow(bool startSetupTimer)
    {
        InitializeComponent();
        if (startSetupTimer)
        {
            StartSetupDetailsTimer();
        }
    }

    /// <summary>
    /// Polls the guided-setup log tail while the window is open; the setup view
    /// itself is visible only when setup is active.
    /// </summary>
    private void StartSetupDetailsTimer()
    {
        _setupDetailsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            if (DataContext is DashboardViewModel { Setup: { } setup })
            {
                setup.RefreshDetails();
            }
        });
        _setupDetailsTimer.Start();
    }
}
