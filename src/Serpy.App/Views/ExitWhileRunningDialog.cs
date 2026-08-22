using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Serpy.App.Views;

/// <summary>Collects an explicit stop, leave-running, or cancel decision before exiting Serpy.</summary>
public sealed class ExitWhileRunningDialog : Window
{
    public ExitWhileRunningDialog()
    {
        Title = "Serpy is still running";
        Width = 440;
        Height = 230;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var stop = new Button { Content = "Stop appliance and exit", HorizontalAlignment = HorizontalAlignment.Right };
        var leave = new Button { Content = "Leave running", HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right };
        stop.Click += (_, _) => Close(ExitWhileRunningChoice.Stop);
        leave.Click += (_, _) => Close(ExitWhileRunningChoice.LeaveRunning);
        cancel.Click += (_, _) => Close(ExitWhileRunningChoice.Cancel);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "The ERPNext appliance is still running.",
                    FontSize = 16,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                },
                new TextBlock
                {
                    Text = "Stopping waits for a confirmed graceful shutdown. Leaving it running closes only Serpy; a later launch can reconnect to the recorded appliance.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, leave, stop },
                },
            },
        };
    }
}
