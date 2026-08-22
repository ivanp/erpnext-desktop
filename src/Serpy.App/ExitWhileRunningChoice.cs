namespace Serpy.App;

/// <summary>Explicit user decision when closing Serpy while the appliance runs.</summary>
public enum ExitWhileRunningChoice
{
    Cancel = 0,
    LeaveRunning,
    Stop,
}
