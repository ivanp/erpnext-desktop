using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Serpy.App.ViewModels;

namespace Serpy.App.Views;

public partial class SetupView : UserControl
{
    private DispatcherTimer? _timer;

    public SetupView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is SetupViewModel vm)
        {
            vm.RefreshDetails();
        }

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            if (DataContext is SetupViewModel active)
            {
                active.RefreshDetails();
            }
        });
        _timer.Start();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
    }
}
