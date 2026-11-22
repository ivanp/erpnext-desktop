using Serpy.Core.Contracts;
using Serpy.Core.Setup;

namespace Serpy.App.Text;

/// <summary>
/// The one table of user-facing words for setup (KTD23). Wording follows the mockups, section E:
/// default screens use plain words (R39), and a technical term appears only where the user must act
/// on it, with a sentence beside it (R40). Anything that names a file, a tool or a protocol belongs
/// under Show details and stays out of this class.
///
/// Every default-screen string, and every template that a number is formatted into, is created through
/// <see cref="Plain"/> in a static initializer, which records it. <see cref="DefaultScreenStrings"/> is
/// therefore complete before any screen runs, and a test can check all of it against the banned terms
/// of AE23 without reflection (the app is Native-AOT).
/// </summary>
public static class UiText
{
    // Declared first: the initializers below call Plain, and static initializers run in text order.
    private static readonly List<string> Registry = [];

    /// <summary>Every string and template that a default screen can show. AE23 checks these.</summary>
    public static IReadOnlyList<string> DefaultScreenStrings => Registry;

    private static string Plain(string text)
    {
        Registry.Add(text);
        return text;
    }

    // ── the strip of steps ───────────────────────────────────────────────────

    public const int StripSteps = 5;

    private static readonly string[] StripLabels =
    [
        Plain("Check PC"), Plain("Download"), Plain("Install"), Plain("Company data"), Plain("Start"),
    ];

    /// <summary>
    /// The mockups show five stages. Prepare belongs to the first, because it only makes the PC
    /// ready, so a person counting steps sees one number per thing they understand.
    /// </summary>
    public static int StripPosition(SetupStepId step) => step switch
    {
        SetupStepId.PcCheck or SetupStepId.Prepare => 1,
        SetupStepId.Download => 2,
        SetupStepId.Build => 3,
        SetupStepId.CompanyData => 4,
        SetupStepId.Start => 5,
        _ => 1,
    };

    public static string StripLabel(int position) => StripLabels[Math.Clamp(position, 1, StripSteps) - 1];

    // ── Welcome ──────────────────────────────────────────────────────────────

    public static readonly string WelcomeTitle = Plain("Set up ERPNext on this PC");
    public static readonly string WelcomeBody = Plain(
        "Serpy will download ERPNext and put it together on this PC. You don't have to do anything while it works.");
    public static readonly string WelcomeTime = Plain("Takes about 1 hour");
    public static readonly string WelcomeInternet = Plain("Needs the internet until the downloads finish");
    public static readonly string WelcomeKeepUsing = Plain("You can keep using your PC");
    public static readonly string StartAtSignInLabel = Plain("Start Serpy when I sign in to Windows");
    public static readonly string StartAtSignInHint = Plain(
        "ERPNext will be ready a minute or two after you sign in. You can change this later.");
    public static readonly string StartSetup = Plain("Start setup");

    private static readonly string WelcomeSpaceTemplate = Plain("Needs about {0} of free space");

    /// <summary>The space line of the Welcome screen. The figure is the threshold the PC check uses (KTD13).</summary>
    public static string WelcomeSpace(PcThresholds thresholds) =>
        string.Format(WelcomeSpaceTemplate, GbText(thresholds.MinFreeDiskBytes));

    // ── PC check ─────────────────────────────────────────────────────────────

    public static readonly string CheckingTitle = Plain("Checking that this PC can run ERPNext");
    public static readonly string CheckAgain = Plain("Check again");
    public static readonly string Close = Plain("Close");

    private static readonly Dictionary<PcCheckId, string> CheckLabels = new()
    {
        [PcCheckId.WindowsVersion] = Plain("Windows version"),
        [PcCheckId.Memory] = Plain("Memory"),
        [PcCheckId.DiskSpace] = Plain("Free space"),
        [PcCheckId.Virtualization] = Plain("Can run ERPNext"),
        [PcCheckId.Internet] = Plain("Internet connection"),
    };

    private static readonly string CheckValueTemplate = Plain("{0} · needs {1}");
    private static readonly string VirtualizationOn = Plain("Turned on");
    private static readonly string VirtualizationPrepare = Plain("Serpy will turn on a Windows feature");
    private static readonly string VirtualizationOff = Plain("Turned off in the startup settings");
    private static readonly string Connected = Plain("Connected");
    private static readonly string NotNeeded = Plain("Not needed");
    private static readonly string NoAnswer = Plain("No answer");

    public static string CheckLabel(PcCheckId check) => CheckLabels.GetValueOrDefault(check, "Check");

    /// <summary>
    /// The value on one line of the check list. The numeric lines show what Core measured and needed.
    /// Virtualization and the internet line never echo Core's own wording, because that wording names
    /// the hypervisor and the download sites, which belong under Show details.
    /// </summary>
    public static string CheckValue(PcCheckLine line) => line.Check switch
    {
        PcCheckId.Virtualization => line.Result switch
        {
            PcCheckResult.Pass => VirtualizationOn,
            PcCheckResult.NeedsPrepare => VirtualizationPrepare,
            _ => VirtualizationOff,
        },
        PcCheckId.Internet => line.Result == PcCheckResult.Fail
            ? NoAnswer
            : line.Measured.Contains("already on this PC", StringComparison.Ordinal) ? NotNeeded : Connected,
        _ => string.Format(CheckValueTemplate, line.Measured, line.Needed),
    };

    private static readonly string SpaceFindingTemplate = Plain("Serpy needs about {0}. This PC has {1} free.");

    public static string SpaceFinding(long neededBytes, long freeBytes) =>
        string.Format(SpaceFindingTemplate, GbText(neededBytes), GbText(freeBytes));

    public static readonly string VirtualizationOffHeadline = Plain("A setting this PC needs is turned off");
    public static readonly string VirtualizationOffExplanation = Plain(
        "The setting is called virtualization. It lets Serpy run ERPNext inside Windows. " +
        "Serpy can't turn it on, because it lives in the PC's startup settings.");
    public static readonly string VirtualizationOffAdvice = Plain(
        "ask the person who looks after this PC to turn on virtualization, then check again.");

    // ── restart and password ─────────────────────────────────────────────────

    public static readonly string RestartTitle = Plain("Windows needs to restart");
    public static readonly string RestartBody = Plain(
        "Serpy turned on a Windows feature that it needs to run ERPNext. The feature starts working after a restart. " +
        "Setup continues by itself when you sign in again.");
    public static readonly string RestartAdvice = Plain("Save your work in other programs first.");
    public static readonly string RestartLater = Plain("Later");
    public static readonly string RestartNow = Plain("Restart now");

    public static readonly string PasswordTitle = Plain("ERPNext needs your password");
    public static readonly string PasswordBody = Plain(
        "Serpy found your company data from an earlier setup. The password Serpy has saved doesn't open it.");
    public static readonly string PasswordFieldLabel = Plain("Password for the ERPNext user \"Administrator\"");
    public static readonly string PasswordShow = Plain("Show");
    public static readonly string PasswordPrivacy = Plain(
        "Serpy keeps the password on this PC, locked to your Windows account. It uses it only to check that ERPNext is working.");
    public static readonly string PasswordWrong = Plain("That password didn't work. Check it and try again.");
    public static readonly string Cancel = Plain("Cancel");
    public static readonly string Continue = Plain("Continue");

    // ── downloads and installing ─────────────────────────────────────────────

    public static readonly string DownloadsTitle = Plain("Downloading what ERPNext needs");
    public static readonly string DownloadsMostlyCached = Plain("Almost nothing to download");
    public static readonly string AlreadyOnThisPc = Plain("Already on this PC");
    public static readonly string PartDone = Plain("Done");
    public static readonly string PartWaiting = Plain("Waiting");
    public static readonly string PartChecking = Plain("Checking…");
    public static readonly string DamagedFileAgain = Plain(
        "One saved file was damaged, so Serpy is downloading that file again.");

    private static readonly string PartsProgressTemplate = Plain("{0} of {1} parts");
    private static readonly string PartsCachedTemplate = Plain("{0} of {1} parts already on this PC");

    public static string PartsProgress(int done, int total) => string.Format(PartsProgressTemplate, done, total);

    public static string PartsCached(int cached, int total) => string.Format(PartsCachedTemplate, cached, total);

    public static readonly string InstallingTitle = Plain("Installing ERPNext");
    public static readonly string StayAwake = Plain("Your PC will stay awake until setup finishes.");
    public static readonly string CloseHint = Plain(
        "You can close this window; setup keeps going next to the clock.");

    private static readonly string NowTemplate = Plain("Now: {0}");
    private static readonly string TimeOnStepTemplate = Plain("{0} min on this step");
    private static readonly string TimeLeftTemplate = Plain("about {0} min left");

    public static string NowLine(string activity) => string.Format(NowTemplate, activity);

    public static string TimeOnStep(TimeSpan elapsed) =>
        string.Format(TimeOnStepTemplate, Math.Max(0, (int)elapsed.TotalMinutes));

    public static string TimeLeft(TimeSpan left) =>
        string.Format(TimeLeftTemplate, Math.Max(1, (int)Math.Round(left.TotalMinutes)));

    // ── a step that went quiet ───────────────────────────────────────────────

    public static readonly string QuietTitle = Plain("This step is taking longer than usual");
    public static readonly string QuietAdvice = Plain(
        "keep waiting, or try this step again. Trying again downloads nothing.");
    public static readonly string KeepWaiting = Plain("Keep waiting");
    public static readonly string TryAgain = Plain("Try again");

    private static readonly string QuietBodyTemplate = Plain(
        "Nothing new has happened for {0} minutes. It may still be working.");

    public static string QuietBody(TimeSpan quiet) =>
        string.Format(QuietBodyTemplate, Math.Max(1, (int)quiet.TotalMinutes));

    // ── ready ────────────────────────────────────────────────────────────────

    public static readonly string ReadyTitle = Plain("Serpy is ready");
    public static readonly string ReadyBody = Plain("ERPNext is running on this PC. Sign in to ERPNext with");
    public static readonly string UsernameLabel = Plain("Username");
    public static readonly string PasswordLabel = Plain("Password");
    public static readonly string Copy = Plain("Copy");
    public static readonly string OpenErpNext = Plain("Open ERPNext");
    public static readonly string ReadyFooter = Plain(
        "Serpy stays next to the clock and shows whether ERPNext is ready.");

    /// <summary>The sign-in details shown on the Ready screen. They are the documented defaults of KTD12.</summary>
    public const string DefaultUsername = "Administrator";

    public const string DefaultPassword = "admin";

    // ── problems ─────────────────────────────────────────────────────────────

    public static readonly string WhatToDoLabel = Plain("What to do:");
    public static readonly string SaveDetails = Plain("Save details…");
    public static readonly string ShowDetails = Plain("Show details");
    public static readonly string HideDetails = Plain("Hide details");

    private static readonly string NothingLostTemplate = Plain(
        "Nothing is lost: {0} of {1} parts are already on this PC.");

    public static string NothingLost(int cached, int total) => string.Format(NothingLostTemplate, cached, total);

    /// <summary>A headline, a sentence of what happened and a what-to-do sentence for one failure (R28).</summary>
    public sealed record FailureText(string Headline, string Detail, string WhatToDo);

    private static FailureText F(string headline, string detail, string whatToDo) =>
        new(Plain(headline), Plain(detail), Plain(whatToDo));

    private const string AskForHelp =
        "try again. If it happens again, save the details and send them to the person who helps you with Serpy.";

    private static readonly Dictionary<FailureCode, FailureText> Failures = new()
    {
        [FailureCode.PcCheckFinding] = F("This PC can't run ERPNext yet",
            "Setup stopped before downloading anything.", "fix what is listed above, then check again."),
        [FailureCode.DiskFull] = F("This PC doesn't have enough free space",
            "Setup stopped before downloading anything.", "free up some space, then check again."),
        [FailureCode.NetworkUnavailable] = F("Serpy can't reach the internet",
            "Setup stopped while downloading.", "check that this PC is online, then try again."),
        [FailureCode.ChecksumMismatch] = F("A downloaded file was damaged",
            "Serpy did not use it.", "try again. Serpy will download that file once more."),
        [FailureCode.ElevationDeclined] = F("Windows didn't get your permission",
            "Serpy needs it once, to install what ERPNext runs on.", "try again and choose Yes when Windows asks."),
        [FailureCode.RestartRequired] = F("Windows needs to restart",
            "The Windows feature Serpy turned on starts working after a restart.",
            "restart Windows. Setup continues when you sign in again."),
        [FailureCode.ProvisioningNotStarted] = F("Installing ERPNext did not start",
            "Setup stopped at the start of the installation.", AskForHelp),
        [FailureCode.ProvisioningFailed] = F("Installing ERPNext stopped",
            "One part of the installation failed.", AskForHelp),
        [FailureCode.GuestConsoleClosed] = F("Installing ERPNext stopped unexpectedly",
            "The installation ended before it finished.", AskForHelp),
        [FailureCode.PasswordRejected] = F("ERPNext needs your password",
            "The password Serpy has saved doesn't open your company data.",
            "enter the password for the ERPNext user \"Administrator\"."),
        [FailureCode.DataUnusable] = F("Serpy found company data on this PC that it can't open",
            "Your data has not been changed. Setup stopped so that nothing is lost.",
            "save the details and send them to the person who helps you with Serpy."),
        [FailureCode.PortInUse] = F("Another program is using the web address ERPNext needs",
            "Setup stopped before starting the installation.",
            "close the other program, then try again."),
        [FailureCode.Cancelled] = F("Setup was cancelled",
            "Nothing is lost. Setup continues where it stopped.", "start setup again when you are ready."),
        [FailureCode.Unknown] = F("Something went wrong", "Setup stopped.", AskForHelp),
    };

    /// <summary>Every failure code has a text. A code that is added later and has none shows the generic one.</summary>
    public static FailureText Failure(FailureCode code) => Failures.GetValueOrDefault(code) ?? Failures[FailureCode.Unknown];

    /// <summary>The codes that have their own text. A test checks that this covers every code except None.</summary>
    public static IReadOnlyCollection<FailureCode> FailureCodesWithText => Failures.Keys;

    // ── tray and window after setup ──────────────────────────────────────────

    public static readonly string ErpNextIsReady = Plain("ERPNext is ready");
    public static readonly string StopErpNext = Plain("Stop ERPNext");
    public static readonly string StartErpNext = Plain("Start ERPNext");
    public static readonly string StartingUpErpNext = Plain("Starting up ERPNext…");
    public static readonly string ErpNextStoppedUnexpectedly = Plain("ERPNext stopped unexpectedly");
    public static readonly string StartingErpNext = Plain("Starting ERPNext…");
    public static readonly string NotSetUp = Plain("Not set up");
    public static readonly string SetupIncomplete = Plain("Setup incomplete");
    public static readonly string Stopped = Plain("Stopped");

    // EverydayView labels
    public static readonly string AddressLabel = Plain("Address");
    public static readonly string SignInLabel = Plain("Sign-in");
    public static readonly string DataFolderLabel = Plain("Data folder");
    public static readonly string EverydaySignInHint = Plain("Administrator · password saved on this PC");
    public static readonly string WorksOnlyOnThisPc = Plain("works only on this PC");
    // The tray tooltip says the state in words (R34). Each one is a whole sentence, so it needs no join.
    public static readonly string TraySettingUp = Plain("Serpy: setting up");
    public static readonly string TrayStarting = Plain("Serpy: starting ERPNext");
    public static readonly string TrayReady = Plain("Serpy: ERPNext is ready");
    public static readonly string TrayStopped = Plain("Serpy: ERPNext is stopped");
    public static readonly string TrayNeedsAttention = Plain("Serpy: needs attention");

    private static readonly string TraySettingUpStepTemplate = Plain("Serpy: setting up, step {0} of {1}");

    /// <summary>The tooltip while setup works. The step number counts the five stages of the strip, not the six internal steps.</summary>
    public static string TraySettingUpStep(SetupStepId step) =>
        string.Format(TraySettingUpStepTemplate, StripPosition(step), StripSteps);

    private static string GbText(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";
}
