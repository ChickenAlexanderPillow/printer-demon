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

namespace PrinterDemon;

public sealed class PrinterService
{
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
            && queue.QueueDriver.Name.Contains("Xerox", StringComparison.OrdinalIgnoreCase)
            && !queue.IsOffline);
        if (exact is not null) return exact;

        var matches = queues
            .Where(queue => queue.FullName.Contains("VersaLink C600", StringComparison.OrdinalIgnoreCase))
            .Where(queue => !queue.IsOffline)
            // Do not use Microsoft's generic IPP class driver. It produces
            // lower-quality output and does not expose the Xerox media model.
            .Where(queue => queue.QueueDriver.Name.Contains("Xerox", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(queue => queue.QueueDriver.Name.Contains("Xerox VersaLink C600 V4 PS", StringComparison.OrdinalIgnoreCase))
            // Windows often leaves duplicate '(Copy N)' queues behind after
            // an IPP/WSD driver reinstall. Prefer the original queue next.
            .ThenBy(queue => queue.FullName.Contains("(Copy", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToArray();
        return matches.FirstOrDefault()
            ?? throw new InvalidOperationException("No online Xerox VersaLink C600 queue using a Xerox driver was found. Install the Xerox C600 driver and recreate the queue.");
    }

    public void Validate()
    {
        using var server = new LocalPrintServer();
        var queue = ResolveQueue(server, _settings.PrinterName);
        if (queue.IsOffline) throw new InvalidOperationException("The Xerox printer is offline.");
        if (!queue.FullName.Contains("Xerox VersaLink C600", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected queue is not the Xerox VersaLink C600.");

    }

    public PrintSubmissionResult Print(IReadOnlyList<RenderedPage> pages, string jobName)
    {
        if (pages.Count == 0) throw new InvalidOperationException("The document has no printable pages.");
        using var server = new LocalPrintServer();
        var queue = ResolveQueue(server, _settings.PrinterName);

        // Xerox installations vary: some accept XPS, while others accept
        // GDI/EMF. Try the path appropriate to the installed driver first,
        // then retry once through the other spool format after an immediate
        // rejection.
        var prefersXps = queue.QueueDriver.Name.Contains("Xerox VersaLink C600 V4 PS", StringComparison.OrdinalIgnoreCase);
        var first = TrySubmit(() => prefersXps
            ? PrintXpsPages(queue, pages, jobName)
            : PrintRasterPages(pages, jobName));
        if (first.Completed) return first;

        var second = TrySubmit(() => prefersXps
            ? PrintRasterPages(pages, jobName)
            : PrintXpsPages(queue, pages, jobName));
        if (second.Completed) return second;

        return new PrintSubmissionResult(false,
            $"The Xerox queue rejected both print formats. First attempt: {first.Message} Second attempt: {second.Message}");
    }

    private static PrintSubmissionResult TrySubmit(Func<PrintSubmissionResult> submit)
    {
        try { return submit(); }
        catch (Exception ex) { return new PrintSubmissionResult(false, ex.Message); }
    }

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
            if (job.IsInError || job.IsBlocked || job.IsDeleted)
                return new PrintSubmissionResult(false,
                    $"The printer reported a job error ({job.JobStatus}).");
            return new PrintSubmissionResult(true, "Sent to printer.");
        }

        return new PrintSubmissionResult(true, "Sent to printer.");
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
        SetParameter(printTicket, "PageMediaSizeMediaSizeWidth", "210000");
        SetParameter(printTicket, "PageMediaSizeMediaSizeHeight", "297000");
        ticket = CreatePrintTicket(printTicket);

        var validation = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, ticket);
        if (validation.ValidatedPrintTicket is null)
            throw new InvalidOperationException("The Xerox printer rejected the A4 / Tray 1 print ticket.");
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
        var feature = ticket.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Feature" &&
            (element.Attribute("name")?.Value ?? string.Empty).EndsWith(featureSuffix, StringComparison.OrdinalIgnoreCase));
        if (feature is null) return;
        feature.Elements().Where(element => element.Name.LocalName == "Option").Remove();
        feature.Add(new XElement(feature.Name.Namespace + "Option", new XAttribute("name", optionName)));
    }

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
