using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.IO;
using System.Xml.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using Microsoft.Win32;

namespace PrinterDemon;

public sealed class PrinterService
{
    // Captured from the user's known-good C600 queue. Keep these values as
    // the cross-PC benchmark; a mismatch is reported only when a job is sent.
    private const string BenchmarkQueueName = "Xerox VersaLink C600 (8a:c6:4a)";
    private const string BenchmarkDriverName = "Xerox VersaLink C600 V4 PS";
    private const string BenchmarkDriverVersion = "7.95.0.0";
    private const string BenchmarkPortName = "X_XC-8AC64A-192.168.1.184";
    private const string BenchmarkPrinterAddress = "192.168.1.184";
    private const int BenchmarkPortNumber = 9100;
    private const int BenchmarkPortProtocol = 1; // Standard TCP/IP RAW
    private const string BenchmarkPaperSize = "ISOA4";
    private const string BenchmarkInputBin = "tray-1";
    private const string BenchmarkDuplex = "TwoSidedLongEdge";
    private const string BenchmarkColor = "Color";
    private const string BenchmarkResolution = "Res_600x600";
    private const bool BenchmarkPageScalingSupported = false;

    private const double A4Width = 793.7008;
    private const double A4Height = 1122.5196;
    private const double PageMargin = 12;
    private readonly PrintSettings _settings;

    public PrinterService(PrintSettings? settings = null) => _settings = settings ?? PrintSettings.Default;

    private static PrintQueue ResolveQueue(LocalPrintServer server, string requestedName)
    {
        var queues = server.GetPrintQueues().ToArray();
        var exact = queues.FirstOrDefault(queue =>
            string.Equals(queue.FullName, requestedName, StringComparison.OrdinalIgnoreCase)
            && queue.QueueDriver.Name.Contains("Xerox VersaLink C600 V4 PS", StringComparison.OrdinalIgnoreCase)
            && !queue.IsOffline);
        if (exact is not null) return exact;

        var matches = queues
            .Where(queue => queue.FullName.Contains("VersaLink C600", StringComparison.OrdinalIgnoreCase))
            .Where(queue => !queue.IsOffline)
            // Use only the supported driver for the XPS ticket and its
            // validated per-job media, tray, color, duplex, and scaling fields.
            .Where(queue => queue.QueueDriver.Name.Contains("Xerox VersaLink C600 V4 PS", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.FirstOrDefault()
            ?? throw new InvalidOperationException("No online Xerox VersaLink C600 queue using the Xerox VersaLink C600 V4 PS driver was found. Install that driver and recreate the queue.");
    }

    public void Validate()
    {
        using var server = new LocalPrintServer();
        var queue = ResolveQueue(server, _settings.PrinterName);
        if (queue.IsOffline) throw new InvalidOperationException("The Xerox printer is offline.");
        if (!queue.FullName.Contains("Xerox VersaLink C600", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected queue is not the Xerox VersaLink C600.");

    }

    public IReadOnlyList<string> CheckQueueConfiguration()
    {
        using var server = new LocalPrintServer();
        var queue = ResolveQueue(server, _settings.PrinterName);
        var warnings = new List<string>();

        Compare(warnings, "Queue", queue.FullName, BenchmarkQueueName);
        Compare(warnings, "Driver", queue.QueueDriver.Name, BenchmarkDriverName);
        var driverVersion = ReadDriverVersion(queue.QueueDriver.Name);
        Compare(warnings, "Driver package version", driverVersion ?? "unavailable", BenchmarkDriverVersion);
        Compare(warnings, "Port", queue.QueuePort.Name, BenchmarkPortName);

        var port = ReadTcpPort(queue.QueuePort.Name);
        Compare(warnings, "Printer address", port.Host ?? "unavailable", BenchmarkPrinterAddress);
        Compare(warnings, "Port number", port.PortNumber?.ToString() ?? "unavailable", BenchmarkPortNumber.ToString());
        Compare(warnings, "Port protocol", port.Protocol?.ToString() ?? "unavailable", BenchmarkPortProtocol.ToString());

        var ticket = queue.DefaultPrintTicket;
        Compare(warnings, "Default paper", ticket.PageMediaSize?.PageMediaSizeName?.ToString() ?? "unset", BenchmarkPaperSize);
        Compare(warnings, "Default input tray", ReadTicketFeatureOption(ticket, "PageInputBin") ?? "unset", BenchmarkInputBin);
        Compare(warnings, "Default job input tray", ReadTicketFeatureOption(ticket, "JobInputBin") ?? "unset", "unset");
        Compare(warnings, "Default duplex", ticket.Duplexing?.ToString() ?? "unset", BenchmarkDuplex);
        Compare(warnings, "Default color", ticket.OutputColor?.ToString() ?? "unset", BenchmarkColor);
        Compare(warnings, "Default resolution", ReadTicketFeatureOption(ticket, "PageResolution") ?? "unset", BenchmarkResolution);
        Compare(warnings, "Default scaling", ReadTicketFeatureOption(ticket, "PageScaling") ?? "unset", "unset");

        var pageScalingSupported = queue.GetPrintCapabilities().PageScalingFactorRange is not null;
        Compare(warnings, "Page scaling support", pageScalingSupported.ToString(), BenchmarkPageScalingSupported.ToString());
        return warnings;
    }

    private static void Compare(ICollection<string> warnings, string setting, string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"{setting}: {actual} (benchmark: {expected})");
    }

    private static string? ReadTicketFeatureOption(PrintTicket ticket, string featureName)
    {
        var xmlText = ReadXml(ticket);
        if (string.IsNullOrWhiteSpace(xmlText)) return null;
        var feature = FindFeature(XDocument.Parse(xmlText), featureName);
        return feature?.Elements().FirstOrDefault(element => element.Name.LocalName == "Option")?
            .Attribute("name")?.Value.Split(':').Last();
    }

    private static string? ReadDriverVersion(string driverName)
    {
        const string root = @"SYSTEM\CurrentControlSet\Control\Print\Environments\Windows x64\Drivers\Version-4";
        using var driverKey = Registry.LocalMachine.OpenSubKey($"{root}\\{driverName}");
        return driverKey?.GetValue("DriverVersion")?.ToString();
    }

    private static (string? Host, int? PortNumber, int? Protocol) ReadTcpPort(string portName)
    {
        const string root = @"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports";
        using var portKey = Registry.LocalMachine.OpenSubKey($"{root}\\{portName}");
        if (portKey is null) return (null, null, null);

        return (
            portKey.GetValue("HostName")?.ToString() ?? portKey.GetValue("IPAddress")?.ToString(),
            int.TryParse(portKey.GetValue("PortNumber")?.ToString(), out var number) ? number : null,
            int.TryParse(portKey.GetValue("Protocol")?.ToString(), out var protocol) ? protocol : null);
    }

    public PrintSubmissionResult Print(IReadOnlyList<RenderedPage> pages, string jobName)
    {
        if (pages.Count == 0) throw new InvalidOperationException("The document has no printable pages.");
        using var server = new LocalPrintServer();
        var queue = ResolveQueue(server, _settings.PrinterName);

        // Keep all computers on the same XPS path. The raster fallback cannot
        // validate or override the queue's page-scaling feature.
        try
        {
            return TrySubmit(() => PrintXpsPages(queue, pages, jobName));
        }
        catch (PrintScalingNotGuaranteedException ex)
        {
            return new PrintSubmissionResult(false, ex.Message);
        }
    }

    private static PrintSubmissionResult TrySubmit(Func<PrintSubmissionResult> submit)
    {
        try { return submit(); }
        catch (PrintScalingNotGuaranteedException) { throw; }
        catch (Exception ex) { return new PrintSubmissionResult(false, ex.Message); }
    }

    private sealed class PrintScalingNotGuaranteedException(string message) : InvalidOperationException(message) { }

    private PrintSubmissionResult PrintXpsPages(
        PrintQueue queue, IReadOnlyList<RenderedPage> pages, string jobName)
    {
        var document = BuildDocument(pages);
        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        var submittedAt = DateTime.UtcNow;
        // Supply one explicit, validated input-bin selection for both the
        // job and its pages. Sending the untouched queue ticket can let the
        // Xerox driver add a second media-source request for the bypass tray.
        var ticket = BuildTicket(queue);
        writer.Write(document, ticket);
        return ConfirmSubmission(queue, jobName, submittedAt);
    }

    private PrintSubmissionResult PrintRasterPages(IReadOnlyList<RenderedPage> pages, string jobName)
    {
        using var server = new LocalPrintServer();
        var queue = ResolveQueue(server, _settings.PrinterName);
        var submittedAt = DateTime.UtcNow;
        using var document = new PrintDocument
        {
            DocumentName = jobName,
            PrinterSettings = { PrinterName = queue.FullName, Copies = (short)_settings.Copies },
            DefaultPageSettings = { Color = _settings.Color, Landscape = false, Margins = new Margins(0, 0, 0, 0) }
        };
        document.PrinterSettings.Duplex = Duplex.Simplex;
        var a4 = document.PrinterSettings.PaperSizes.Cast<PaperSize>()
            .FirstOrDefault(size => size.Kind == PaperKind.A4 || size.PaperName.Contains("A4", StringComparison.OrdinalIgnoreCase));
        var tray1 = document.PrinterSettings.PaperSources.Cast<PaperSource>()
            .FirstOrDefault(source => source.SourceName.Contains("Tray 1", StringComparison.OrdinalIgnoreCase) ||
                                      source.SourceName.Contains("Tray1", StringComparison.OrdinalIgnoreCase));
        if (a4 is not null) document.DefaultPageSettings.PaperSize = a4;
        if (tray1 is not null) document.DefaultPageSettings.PaperSource = tray1;
        SelectHighestResolution(document);
        var pageIndex = 0;
        document.QueryPageSettings += (_, args) =>
        {
            if (pageIndex < pages.Count)
                args.PageSettings.Landscape = pages[pageIndex].WidthInches > pages[pageIndex].HeightInches;
            args.PageSettings.Color = _settings.Color;
            args.PageSettings.Margins = new Margins(0, 0, 0, 0);
            if (a4 is not null) args.PageSettings.PaperSize = a4;
            if (tray1 is not null) args.PageSettings.PaperSource = tray1;
        };
        document.PrintPage += (_, args) =>
        {
            var page = pages[pageIndex++];
            using var image = System.Drawing.Image.FromFile(page.ImagePath);
            var graphics = args.Graphics
                ?? throw new InvalidOperationException("The printer did not provide a graphics surface.");
            var bounds = GetPrintableBounds(args);
            var scale = Math.Min((double)bounds.Width / image.Width, (double)bounds.Height / image.Height);
            var width = Math.Max(1, (int)Math.Round(image.Width * scale));
            var height = Math.Max(1, (int)Math.Round(image.Height * scale));
            var x = bounds.Left + (bounds.Width - width) / 2;
            var y = bounds.Top + (bounds.Height - height) / 2;
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(image, new Rectangle(x, y, width, height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
            args.HasMorePages = pageIndex < pages.Count;
        };
        document.Print();
        return ConfirmSubmission(queue, jobName, submittedAt);
    }

    public PrintSubmissionResult PrintPdf(string pdfPath, string jobName, int expectedPageCount)
    {
        if (!File.Exists(pdfPath)) throw new FileNotFoundException("PDF not found.", pdfPath);
        var temp = Path.Combine(Path.GetTempPath(), "PrinterDemon", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var pages = new DocumentRenderer().Render(pdfPath, temp);
            return Print(pages, jobName);
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private static PrintSubmissionResult ConfirmSubmission(PrintQueue queue, string jobName, DateTime submittedAt)
    {
        // Write() has completed the spool handoff. Only inspect the queue
        // briefly for an immediate driver rejection; do not wait for the
        // printer to physically finish the job before accepting more drops.
        var deadline = DateTime.UtcNow.AddSeconds(1.5);
        while (DateTime.UtcNow < deadline)
        {
            queue.Refresh();
            var job = queue.GetPrintJobInfoCollection()
                .Cast<PrintSystemJobInfo>()
                .Where(candidate => candidate.Name.Contains(jobName, StringComparison.OrdinalIgnoreCase) ||
                    candidate.TimeJobSubmitted >= submittedAt.AddSeconds(-2))
                .OrderByDescending(candidate => candidate.TimeJobSubmitted)
                .FirstOrDefault();
            if (job is null)
            {
                Thread.Sleep(250);
                continue;
            }

            job.Refresh();
            if (job.IsInError || job.IsDeleted)
                return new PrintSubmissionResult(false,
                    $"The printer reported a job error ({job.JobStatus}).");
            return new PrintSubmissionResult(true, "Accepted by Windows spooler.",
                new PrintJobReference(queue.FullName, job.JobIdentifier));
        }

        return new PrintSubmissionResult(true, "Sent to printer.");
    }

    public PrintJobMonitoringResult WaitForCompletion(PrintJobReference reference)
    {
        // Drivers do not all retain completed jobs for the same amount of
        // time. Keep polling long enough to observe normal printer queues,
        // but do not hold the app's queue forever if the driver hides status.
        const int maxPolls = 1200; // 20 minutes at one-second intervals
        var observed = false;
        using var server = new LocalPrintServer();
        PrintQueue? queue = null;
        try
        {
            queue = server.GetPrintQueue(reference.QueueName);
            var lastStatusMessage = string.Empty;
            for (var poll = 0; poll < maxPolls; poll++)
            {
                queue.Refresh();
                if (queue.IsOffline)
                    lastStatusMessage = "Xerox printer is offline.";

                var job = queue.GetPrintJobInfoCollection()
                    .Cast<PrintSystemJobInfo>()
                    .FirstOrDefault(candidate => candidate.JobIdentifier == reference.JobIdentifier);

                if (job is null)
                {
                    if (observed)
                        return PrintJobMonitoringResult.PrintedSuccessfully("Printer completed the job.");

                    Thread.Sleep(1000);
                    continue;
                }

                observed = true;
                job.Refresh();
                var jobStatusMessage = DescribeXeroxJobStatus(job);
                if (!string.IsNullOrWhiteSpace(jobStatusMessage))
                    lastStatusMessage = jobStatusMessage;
                if (job.IsInError || job.IsDeleted)
                    return PrintJobMonitoringResult.FailedWith(
                        jobStatusMessage ?? $"Xerox printer reported a job error ({job.JobStatus}).");
                if (job.IsPrinted || job.IsCompleted)
                    return PrintJobMonitoringResult.PrintedSuccessfully("Printer completed the job.");

                Thread.Sleep(1000);
            }

            return string.IsNullOrWhiteSpace(lastStatusMessage)
                ? PrintJobMonitoringResult.SentWithoutConfirmation(
                    "Accepted by Windows spooler; the printer did not expose a final status.")
                : PrintJobMonitoringResult.FailedWith(
                    $"{lastStatusMessage} Final print status was not confirmed.");
        }
        catch (Exception ex)
        {
            return PrintJobMonitoringResult.SentWithoutConfirmation(
                $"Accepted by Windows spooler; final status unavailable ({ex.Message}).");
        }
        finally
        {
            queue?.Dispose();
        }
    }

    private static string? DescribeXeroxJobStatus(PrintSystemJobInfo job)
    {
        if (job.IsPaperOut)
            return "Xerox printer is out of paper.";
        if (job.IsOffline)
            return "Xerox printer is offline.";
        if (job.IsUserInterventionRequired)
            return "Xerox printer needs user intervention.";
        if (job.IsPaused)
            return "Xerox print job is paused.";
        if (job.IsBlocked)
            return "Xerox printer is waiting before printing this job.";
        if (job.IsInError)
            return $"Xerox printer reported an error ({job.JobStatus}).";
        if (job.IsDeleted)
            return "Xerox print job was deleted or cancelled.";
        return null;
    }


    private static void SelectHighestResolution(PrintDocument document)
    {
        var resolution = document.PrinterSettings.PrinterResolutions.Cast<PrinterResolution>()
            .OrderByDescending(value => Math.Max(value.X, value.Y))
            .FirstOrDefault();
        if (resolution is not null) document.DefaultPageSettings.PrinterResolution = resolution;
    }

    private static Rectangle GetPrintableBounds(PrintPageEventArgs args)
    {
        var graphics = args.Graphics
            ?? throw new InvalidOperationException("The printer did not provide a graphics surface.");
        var dpiX = graphics.DpiX;
        var dpiY = graphics.DpiY;
        var left = Math.Max(0, (int)Math.Round(args.PageSettings.HardMarginX * dpiX / 100.0));
        var top = Math.Max(0, (int)Math.Round(args.PageSettings.HardMarginY * dpiY / 100.0));
        var right = Math.Max(left + 1, args.PageBounds.Width - left);
        var bottom = Math.Max(top + 1, args.PageBounds.Height - top);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private PrintTicket BuildTicket(PrintQueue queue)
    {
        var ticket = queue.DefaultPrintTicket.Clone();
        ticket.PageResolution = new PageResolution(600, 600);
        ticket.Duplexing = Duplexing.OneSided;
        var ticketXml = ReadXml(ticket);
        if (string.IsNullOrWhiteSpace(ticketXml))
            throw new InvalidOperationException("The Xerox driver did not provide a usable print ticket.");

        var printTicket = XDocument.Parse(ticketXml);
        var isXeroxV4Ps = queue.QueueDriver.Name.Contains("Xerox VersaLink C600 V4 PS", StringComparison.OrdinalIgnoreCase);
        var inputFeature = isXeroxV4Ps ? "PageInputBin" : "JobInputBin";
        var trayOption = isXeroxV4Ps ? "ns0000:tray-1" : "ns0001:Tray1";
        var resolutionOption = isXeroxV4Ps ? "ns0000:Res_600x600" : "ns0001:DPI600x600";
        ReplaceFeatureOption(printTicket, inputFeature, trayOption);
        // Xerox can expose both a page-level and a job-level input-bin
        // feature. Force both when present so later chunks in a large drop
        // cannot inherit the queue default or select a different tray.
        ReplaceFeatureOptionIfPresent(printTicket, "PageInputBin", trayOption);
        ReplaceFeatureOptionIfPresent(printTicket, "JobInputBin", trayOption);
        ReplaceFeatureOption(printTicket, "PageMediaSize", "psk:ISOA4");
        ReplaceFeatureOptionIfPresent(printTicket, "PageMediaColor", "ns0000:use-ready");
        ReplaceFeatureOptionIfPresent(printTicket, "PageMediaType", "ns0000:use-ready");
        ReplaceFeatureOptionIfPresent(printTicket, "PageOutputQuality", "psk:High");
        ReplaceFeatureOption(printTicket, "PageOutputColor", "psk:Color");
        ReplaceFeatureOption(printTicket, "PageResolution", resolutionOption);
        // Do not inherit a queue-level reduction/enlargement setting. The
        // FixedDocument is already laid out at the intended A4 size.
        // Some Xerox driver builds do not advertise PageScaling at all. In
        // that case there is no driver scaling control to override; keep the
        // FixedDocument and ticket media both at A4. When the driver does
        // expose scaling, explicitly request and validate None.
        var scalingSupported = queue.GetPrintCapabilities().PageScalingFactorRange is not null;
        if (scalingSupported) SetPageScalingNone(printTicket);
        ReplaceFeatureOptionIfPresent(printTicket, "JobDuplexAllDocumentsContiguously", "psk:OneSided");
        SetParameter(printTicket, "PageMediaSizeMediaSizeWidth", "210000");
        SetParameter(printTicket, "PageMediaSizeMediaSizeHeight", "297000");
        ticket = CreatePrintTicket(printTicket);

        var validation = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, ticket);
        if (validation.ValidatedPrintTicket is null)
            throw new InvalidOperationException("The Xerox printer rejected the A4 / Tray 1 print ticket.");
        if (validation.ValidatedPrintTicket.Duplexing != Duplexing.OneSided)
            throw new InvalidOperationException("The Xerox printer did not accept the single-sided print setting.");
        var validatedXml = XDocument.Parse(ReadXml(validation.ValidatedPrintTicket));
        var validatedScaling = FindFeature(validatedXml, "PageScaling")?
            .Elements().FirstOrDefault(element => element.Name.LocalName == "Option")?
            .Attribute("name")?.Value;
        if (scalingSupported && !string.Equals(validatedScaling?.Split(':').Last(), "None", StringComparison.OrdinalIgnoreCase))
            throw new PrintScalingNotGuaranteedException(
                "The Xerox driver did not accept the no-scaling setting. Printing was stopped to avoid reduced-size output.");
        return validation.ValidatedPrintTicket;
    }

    private static string ReadXml(object printObject)
    {
        var method = printObject.GetType().GetMethod("GetXmlStream");
        if (method?.Invoke(printObject, null) is not Stream stream) return string.Empty;
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static PrintTicket CreatePrintTicket(XDocument xml)
    {
        using var stream = new MemoryStream();
        xml.Save(stream);
        stream.Position = 0;
        return new PrintTicket(stream);
    }

    private static string? ResolveGhostscript()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ghostscript", "installed", "bin", "gswin64c.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ghostscript", "gswin64c.exe")
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim(), "gswin64c.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void ReplaceFeatureOption(XDocument ticket, string featureSuffix, string optionName)
    {
        var feature = ticket.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Feature" &&
            (element.Attribute("name")?.Value ?? string.Empty).EndsWith(featureSuffix, StringComparison.OrdinalIgnoreCase));
        if (feature is null) throw new InvalidOperationException($"The print ticket has no {featureSuffix} feature.");
        feature.Elements().Where(element => element.Name.LocalName == "Option").Remove();
        feature.Add(new XElement(feature.Name.Namespace + "Option", new XAttribute("name", optionName)));
    }

    private static void ReplaceFeatureOptionIfPresent(XDocument ticket, string featureSuffix, string optionName)
    {
        var feature = FindFeature(ticket, featureSuffix);
        if (feature is null) return;
        feature.Elements().Where(element => element.Name.LocalName == "Option").Remove();
        feature.Add(new XElement(feature.Name.Namespace + "Option", new XAttribute("name", optionName)));
    }

    private static void SetPageScalingNone(XDocument ticket)
    {
        var feature = FindFeature(ticket, "PageScaling");
        if (feature is null)
        {
            XNamespace framework = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            XNamespace xs = "http://www.w3.org/2001/XMLSchema";
            feature = new XElement(framework + "Feature",
                new XAttribute("name", "psk:PageScaling"),
                new XElement(framework + "Property",
                    new XAttribute("name", "psf:SelectionType"),
                    new XElement(framework + "Value", new XAttribute(xsi + "type", "xsd:string"), "psk:PickOne")),
                new XElement(framework + "Option", new XAttribute("name", "psk:None")));
            ticket.Root?.Add(feature);
        }
        else
        {
            feature.Elements().Where(element => element.Name.LocalName == "Option").Remove();
            feature.Add(new XElement(feature.Name.Namespace + "Option", new XAttribute("name", "psk:None")));
        }
    }

    private static XElement? FindFeature(XDocument ticket, string featureSuffix) => ticket.Descendants().FirstOrDefault(element =>
        element.Name.LocalName == "Feature" &&
        (element.Attribute("name")?.Value ?? string.Empty).EndsWith(featureSuffix, StringComparison.OrdinalIgnoreCase));

    private static void SetParameter(XDocument ticket, string parameterName, string value)
    {
        var parameter = ticket.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "ParameterInit" &&
            string.Equals(element.Attribute("name")?.Value, $"psk:{parameterName}", StringComparison.OrdinalIgnoreCase));
        var valueElement = parameter?.Elements().FirstOrDefault(element => element.Name.LocalName == "Value");
        if (valueElement is not null) valueElement.Value = value;
    }

    private static FixedDocument BuildDocument(IReadOnlyList<RenderedPage> pages)
    {
        var document = new FixedDocument();
        foreach (var page in pages)
        {
            var landscape = page.WidthInches > page.HeightInches;
            var width = landscape ? A4Height : A4Width;
            var height = landscape ? A4Width : A4Height;
            var fixedPage = new FixedPage { Width = width, Height = height, Background = System.Windows.Media.Brushes.White };
            var image = new System.Windows.Controls.Image { Source = LoadBitmap(page.ImagePath), Stretch = Stretch.Uniform };
            image.Width = width - (PageMargin * 2);
            image.Height = height - (PageMargin * 2);
            image.Margin = new Thickness(PageMargin);
            fixedPage.Children.Add(image);
            var content = new PageContent();
            ((IAddChild)content).AddChild(fixedPage);
            document.Pages.Add(content);
        }
        return document;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
