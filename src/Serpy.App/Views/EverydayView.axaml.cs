using System;
using Avalonia;
using Avalonia.Controls;
using Serpy.App.ViewModels;

namespace Serpy.App.Views;

public partial class EverydayView : UserControl
{
    private DashboardViewModel? _subscribedVm;

    public EverydayView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        UpdateSubscription();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (VisualRoot is not null)
        {
            UpdateSubscription();
        }
    }

    private void UpdateSubscription()
    {
        var targetVm = DataContext as DashboardViewModel;
        if (ReferenceEquals(_subscribedVm, targetVm))
        {
            return;
        }

        Unsubscribe();
        if (targetVm is not null)
        {
            _subscribedVm = targetVm;
            _subscribedVm.CopyTextRequested += OnCopyRequested;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.CopyTextRequested -= OnCopyRequested;
            _subscribedVm = null;
        }
    }

    private async void OnCopyRequested(object? sender, string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
