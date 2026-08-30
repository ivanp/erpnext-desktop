using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Serpy.Core.Contracts;

namespace Serpy.App.Views;

/// <summary>
/// Modal setup dialog that allows accountants to choose between:
/// 1. Create a new empty database & ERPNext site (Initialize)
/// 2. Load an existing dataset archive (.img) (Adopt)
/// Returns SetupChoice (CreateNew vs AdoptExisting) on OK; null on Cancel.
/// </summary>
public sealed class InitializeDialog : Window
{
    private readonly RadioButton _createNewRadio;
    private readonly RadioButton _loadExistingRadio;
    private readonly TextBox _siteNameBox;
    private readonly TextBox _archivePathBox;
    private readonly Button _browseButton;
    private readonly TextBox _passwordBox;
    private readonly TextBox _confirmPasswordBox;
    private readonly TextBlock _validationText;
    private readonly StackPanel _createNewPanel;
    private readonly StackPanel _loadExistingPanel;
    private readonly StackPanel _confirmPasswordPanel;

    public InitializeDialog(bool hasCommittedDataOnDisk = false, bool allowChoice = true)
    {
        Title = "Set Up ERPNext Appliance";
        Width = 460;
        Height = 440;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _createNewRadio = new RadioButton
        {
            Content = "Create new database",
            IsChecked = !hasCommittedDataOnDisk,
            GroupName = "SetupMode",
            Margin = new Thickness(0, 0, 16, 0),
        };
        _loadExistingRadio = new RadioButton
        {
            Content = "Load existing data file (.img)",
            IsChecked = hasCommittedDataOnDisk,
            GroupName = "SetupMode",
            IsEnabled = allowChoice || hasCommittedDataOnDisk,
        };

        _siteNameBox = new TextBox { Text = "site1.local", Margin = new Thickness(0, 0, 0, 8) };
        _archivePathBox = new TextBox { Watermark = "Select .img data file…", Margin = new Thickness(0, 0, 8, 8) };
        _browseButton = new Button { Content = "Browse…", Width = 80 };

        _passwordBox = new TextBox { PasswordChar = '•', Margin = new Thickness(0, 0, 0, 8) };
        _confirmPasswordBox = new TextBox { PasswordChar = '•', Margin = new Thickness(0, 0, 0, 4) };

        _validationText = new TextBlock
        {
            Foreground = Avalonia.Media.Brushes.IndianRed,
            FontSize = 11,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var okBtn = new Button { Content = "Continue", Width = 100, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0),
        };

        _createNewPanel = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Site name", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 13 },
                _siteNameBox,
            },
        };

        var archiveRow = new Grid();
        archiveRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        archiveRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(_archivePathBox, 0);
        Grid.SetColumn(_browseButton, 1);
        archiveRow.Children.Add(_archivePathBox);
        archiveRow.Children.Add(_browseButton);

        _loadExistingPanel = new StackPanel
        {
            Spacing = 4,
            IsVisible = hasCommittedDataOnDisk,
            Children =
            {
                new TextBlock { Text = "Backup dataset file (.img)", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 13 },
                archiveRow,
            },
        };

        _confirmPasswordPanel = new StackPanel
        {
            Spacing = 4,
            IsVisible = !hasCommittedDataOnDisk,
            Children =
            {
                new TextBlock { Text = "Confirm password", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 13 },
                _confirmPasswordBox,
            },
        };

        _createNewRadio.IsCheckedChanged += (_, _) => ToggleMode();
        _loadExistingRadio.IsCheckedChanged += (_, _) => ToggleMode();

        _archivePathBox.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(TextBox.Text) && !string.IsNullOrWhiteSpace(_archivePathBox.Text))
            {
                _loadExistingRadio.IsEnabled = true;
            }
        };

        _browseButton.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select ERPNext Data Disk Image",
                FileTypeFilter = [new FilePickerFileType("Raw disk image (*.img)") { Patterns = ["*.img"] }],
            });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                _archivePathBox.Text = path;
                _loadExistingRadio.IsEnabled = true;
            }
        };

        okBtn.Click += (_, _) => ValidateAndSubmit();
        cancelBtn.Click += (_, _) => Close(null);

        var modeChoicePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 16),
            Children = { _createNewRadio, _loadExistingRadio },
        };

        var warningBorder = new Border
        {
            Background = Avalonia.Media.Brushes.LightYellow,
            BorderBrush = Avalonia.Media.Brushes.BurlyWood,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 12),
            Child = new TextBlock
            {
                Text = "Important: Write down this Administrator password! It is required to log in to ERPNext and cannot be recovered if forgotten.",
                FontSize = 11,
                Foreground = Avalonia.Media.Brushes.DarkGoldenrod,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Set Up ERPNext", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Thickness(0, 0, 0, 4) },
                new TextBlock { Text = "Choose how to initialize your ERPNext desktop environment.", Foreground = Avalonia.Media.Brushes.Gray, FontSize = 13, Margin = new Thickness(0, 0, 0, 12) },
                modeChoicePanel,
                _createNewPanel,
                _loadExistingPanel,
                _validationText,
                new TextBlock { Text = "Administrator password", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 13 },
                _passwordBox,
                _confirmPasswordPanel,
                warningBorder,
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

    private void ToggleMode()
    {
        bool isCreate = _createNewRadio.IsChecked == true;
        _createNewPanel.IsVisible = isCreate;
        _loadExistingPanel.IsVisible = !isCreate;
        _confirmPasswordPanel.IsVisible = isCreate;
        _validationText.Text = string.Empty;
    }

    private void ValidateAndSubmit()
    {
        string pwd = _passwordBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pwd))
        {
            _validationText.Text = "Administrator password is required.";
            return;
        }

        bool isCreate = _createNewRadio.IsChecked == true;
        if (isCreate)
        {
            string confirmPwd = _confirmPasswordBox.Text ?? string.Empty;
            if (!string.Equals(pwd, confirmPwd, StringComparison.Ordinal))
            {
                _validationText.Text = "Administrator passwords do not match.";
                return;
            }

            var siteName = _siteNameBox.Text?.Trim() ?? string.Empty;
            if (!InitializationParameters.IsValidSiteName(siteName))
            {
                _validationText.Text = "Enter a lowercase fully qualified domain name (e.g. site1.local).";
                return;
            }
            Close(new SetupChoice.CreateNew(new InitializationParameters(siteName, pwd)));
        }
        else
        {
            var archivePath = _archivePathBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(archivePath) || !System.IO.File.Exists(archivePath))
            {
                _validationText.Text = "Please select a valid existing data file (.img).";
                return;
            }
            Close(new SetupChoice.AdoptExisting(archivePath, pwd));
        }
    }
}
