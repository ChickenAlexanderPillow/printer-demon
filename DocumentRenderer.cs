using ImageMagick;
using System.IO;
using System.Diagnostics;
using System.IO.Compression;

namespace PrinterDemon;

public sealed record RenderedPage(string ImagePath, double WidthInches, double HeightInches);

public sealed class DocumentRenderer
{
    private const int RenderDpi = 400;
    private const string GhostscriptBundleResource = "PrinterDemon.GhostscriptBundle.zip";
    private static readonly object GhostscriptExtractionGate = new();
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" };

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    public IReadOnlyList<RenderedPage> Render(string path, string outputDirectory)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Source file not found.", path);
        if (!IsSupported(path)) throw new InvalidOperationException("Unsupported file type.");

        Directory.CreateDirectory(outputDirectory);
        var pages = new List<RenderedPage>();
        var extension = Path.GetExtension(path);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return RenderPdf(path, outputDirectory);

        var settings = new MagickReadSettings { Density = new Density(RenderDpi, RenderDpi) };
        using var images = new MagickImageCollection();
        images.Read(path, settings);

        var index = 0;
        foreach (var image in images)
        {
            image.AutoOrient();
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
            image.Format = MagickFormat.Png;
            var outputPath = Path.Combine(outputDirectory, $"page-{index++:0000}.png");
            image.Write(outputPath);
            pages.Add(new RenderedPage(outputPath, Math.Max(0.01, image.Width / (double)RenderDpi),
                Math.Max(0.01, image.Height / (double)RenderDpi)));
        }

        return pages;
    }

    private static IReadOnlyList<RenderedPage> RenderPdf(string path, string outputDirectory)
    {
        var ghostscript = ResolveGhostscript();
        if (ghostscript is null)
            throw new InvalidOperationException(
                "Ghostscript is required to render PDF files. Reinstall using the complete PrinterDemon-win-x64 package, including the tools folder.");

        var pattern = Path.Combine(outputDirectory, "page-%04d.png");
        var startInfo = new ProcessStartInfo(ghostscript)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[]
        {
            "-dSAFER", "-dBATCH", "-dNOPAUSE", "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4",
            "-dUseFastColor=false", "-sProcessColorModel=DeviceRGB", "-sColorConversionStrategy=RGB",
            "-sDEVICE=png16m", $"-r{RenderDpi}", $"-sOutputFile={pattern}", path
        })
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Ghostscript.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"PDF rendering failed: {error.Trim()}");

        var pages = new List<RenderedPage>();
        foreach (var imagePath in Directory.EnumerateFiles(outputDirectory, "page-*.png").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            using var image = new MagickImage(imagePath);
            pages.Add(new RenderedPage(imagePath, image.Width / (double)RenderDpi, image.Height / (double)RenderDpi));
        }
        return pages;
    }

    private static string? ResolveGhostscript()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ghostscript", "installed", "bin", "gswin64c.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ghostscript", "gswin64c.exe")
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim(), "gswin64c.exe")));
        var existing = candidates.FirstOrDefault(File.Exists);
        if (existing is not null) return existing;

        // The release also embeds Ghostscript so the EXE remains usable when
        // somebody copies it without the adjacent tools folder.
        try
        {
            var extractedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PrinterDemon", "Ghostscript");
            var extractedExecutable = Path.Combine(extractedRoot, "bin", "gswin64c.exe");
            lock (GhostscriptExtractionGate)
            {
                if (!File.Exists(extractedExecutable))
                {
                    using var bundle = typeof(DocumentRenderer).Assembly
                        .GetManifestResourceStream(GhostscriptBundleResource);
                    if (bundle is null) return null;
                    Directory.CreateDirectory(extractedRoot);
                    using var archive = new ZipArchive(bundle, ZipArchiveMode.Read);
                    archive.ExtractToDirectory(extractedRoot, overwriteFiles: true);
                }
            }
            return File.Exists(extractedExecutable) ? extractedExecutable : null;
        }
        catch
        {
            return null;
        }
    }
}
