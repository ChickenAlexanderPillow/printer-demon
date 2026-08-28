namespace PrinterDemon;

public sealed record PrintSettings(
    string PrinterName,
    bool Color,
    bool OneSided,
    string Tray,
    int Copies,
    bool AutoOrientation,
    bool ShrinkToFit,
    bool EconomyModeOff)
{
    public static PrintSettings Default => new(
        "Xerox VersaLink C600", true, true, "Tray1", 1, true, true, true);
}

public sealed record PrintJobResult(string SourcePath, int PageCount, bool Success, bool Submitted, string Message)
{
    public static PrintJobResult Failure(string path, string message) => new(path, 0, false, false, message);
}

public sealed record PrintSubmissionResult(bool Completed, string Message, PrintJobReference? Job = null);

public sealed record PrintJobReference(string QueueName, int JobIdentifier);

public sealed record PrintJobMonitoringResult(bool Printed, bool Failed, string Message)
{
    public static PrintJobMonitoringResult PrintedSuccessfully(string message) => new(true, false, message);
    public static PrintJobMonitoringResult FailedWith(string message) => new(false, true, message);
    public static PrintJobMonitoringResult SentWithoutConfirmation(string message) => new(false, false, message);
}
