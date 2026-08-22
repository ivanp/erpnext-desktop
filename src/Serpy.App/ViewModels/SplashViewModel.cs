using CommunityToolkit.Mvvm.ComponentModel;
using Serpy.Core.Contracts;

namespace Serpy.App.ViewModels;

/// <summary>
/// Drives the always-on-top start splash (AE8, R17).
/// Bound to the same OperationUpdate stream as the dashboard.
/// Dismissed on terminal result by the App layer.
/// </summary>
public sealed partial class SplashViewModel : ObservableObject
{
    [ObservableProperty] private string _stageText = "Starting…";
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private bool _isIndeterminate = true;

    public void ApplyUpdate(OperationUpdate update)
    {
        StageText = update.Message;
        if (update.ProgressPercent.HasValue)
        {
            ProgressPercent = update.ProgressPercent.Value;
            IsIndeterminate = false;
        }
    }
}
