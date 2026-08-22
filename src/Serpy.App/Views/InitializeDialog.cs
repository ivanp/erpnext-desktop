using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Serpy.Core.Contracts;

namespace Serpy.App.Views;

/// <summary>
/// Modal dialog that collects site name and admin password before Initialize runs.
/// Returns InitializationParameters on OK; null on Cancel.
/// Written in code (no .axaml) so it compiles without a separate XAML file.
/// </summary>
public sealed class InitializeDialog : Window
{
    private readonly TextBox _siteNameBox;
    private readonly TextBox _passwordBox;
    private readonly TextBlock _validationText;

    public InitializeDialog()
    {
        Title  = "Initialize Appliance";
        Width  = 400;
        Height = 270;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _siteNameBox = new TextBox { Text = "site1.local", Margin = new Thickness(0, 0, 0, 12) };
        _passwordBox = new TextBox { PasswordChar = '•', Margin = new Thickness(0, 0, 0, 4) };
        _validationText = new TextBlock
        {
            Foreground = Avalonia.Media.Brushes.IndianRed,
            FontSize = 11,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        var okBtn     = new Button { Content = "Initialize", Width = 100, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button { Content = "Cancel",     Width = 80,  HorizontalAlignment = HorizontalAlignment.Right,
                                     Margin = new Thickness(0, 0, 8, 0) };

        okBtn.Click += (_, _) =>
        {
            var siteName = _siteNameBox.Text?.Trim() ?? string.Empty;
            if (!InitializationParameters.IsValidSiteName(siteName))
            {
                _validationText.Text = "Enter a lowercase fully qualified domain name, for example site1.local.";
                return;
            }
            Close(new InitializationParameters(siteName, _passwordBox.Text ?? string.Empty));
        };
        cancelBtn.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Initialize Data Disk", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Thickness(0,0,0,8) },
                new TextBlock { Text = "Creates the persistent data disk and your first ERPNext site.\nThis takes 20–60 minutes and cannot be interrupted safely.",
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Avalonia.Media.Brushes.Gray, FontSize = 13, Margin = new Thickness(0,0,0,16) },
                new TextBlock { Text = "Site name", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 13 },
                _siteNameBox,
                _validationText,
                new TextBlock { Text = "Admin password", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 13 },
                _passwordBox,
                new TextBlock { Text = "Used to log in to ERPNext as Administrator.", FontSize = 11, Foreground = Avalonia.Media.Brushes.Gray, Margin = new Thickness(0,0,0,16) },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelBtn, okBtn },
                },
            },
        };
    }
}
