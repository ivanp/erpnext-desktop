using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Serpy.App.Views;

/// <summary>Collects an explicit stop, leave-running, or cancel decision before exiting Serpy.</summary>
public sealed class ExitWhileRunningDialog : Window
{
    public ExitWhileRunningDialog() : this(isSetup: false) { }

    public ExitWhileRunningDialog(bool isSetup)
    {
        Title = isSetup ? "Setup is still working" : "Serpy is still running";
        Width = 440;
        Height = 230;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var stop = new Button
        {
            Content = isSetup ? "Stop setup and exit" : "Stop appliance and exit",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var leave = new Button
        {
            Content = "Leave running",
            HorizontalAlignment = HorizontalAlignment.Right,
            IsVisible = !isSetup,
        };
        var cancel = new Button
        {
            Content = isSetup ? "Keep going" : "Cancel",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        stop.Click += (_, _) => Close(ExitWhileRunningChoice.Stop);
        leave.Click += (_, _) => Close(ExitWhileRunningChoice.LeaveRunning);
        cancel.Click += (_, _) => Close(ExitWhileRunningChoice.Cancel);
        AutomationProperties.SetAutomationId(stop, isSetup ? "StopSetupButton" : "StopApplianceButton");
        AutomationProperties.SetAutomationId(leave, "LeaveRunningButton");
        AutomationProperties.SetAutomationId(cancel, "CancelButton");

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttonPanel.Children.Add(cancel);
        if (!isSetup) buttonPanel.Children.Add(leave);
        buttonPanel.Children.Add(stop);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = isSetup ? "Stop setup and close Serpy?" : "The ERPNext appliance is still running.",
                    FontSize = 16,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                },
                new TextBlock
                {
                    Text = isSetup
                        ? "Nothing is lost. Setup continues where it stopped when you start Serpy again."
                        : "Stopping waits for a confirmed graceful shutdown. Leaving it running closes only Serpy; a later launch can reconnect to the recorded appliance.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                buttonPanel,
            },
        };
    }
}
