using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Serpy.App.Views;

/// <summary>
/// Requires explicit acknowledgement before the guarded recover operation can
/// replace the current system image. Cancel returns false and starts no mutation.
/// </summary>
public sealed class RecoveryConfirmationDialog : Window
{
    public RecoveryConfirmationDialog(string replacementImagePath)
    {
        Title = "Recover Appliance";
        Width = 460;
        Height = 270;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var recover = new Button
        {
            Content = "Replace system image and recover",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button
        {
            Content = "Cancel",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0),
        };

        recover.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Replace system image?",
                    FontSize = 16,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                },
                new TextBlock
                {
                    Text = "Serpy will replace the current system.qcow2 while retaining your data.img. " +
                           "The migration cannot be rolled back automatically.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "The selected image must be a Serpy-built, equal-or-newer image. " +
                           "Downgrades are rejected before migration.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = replacementImagePath,
                    FontFamily = Avalonia.Media.FontFamily.Parse("Cascadia Code, Consolas, monospace"),
                    FontSize = 11,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 8),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, recover },
                },
            },
        };
    }
}