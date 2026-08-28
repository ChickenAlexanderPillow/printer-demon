using System.IO;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Collections.ObjectModel;

namespace PrinterDemon;

public partial class MainWindow : Window
{
    // Keep each XPS document bounded. WPF decodes page images in memory when
    // building the document, so one enormous bundle is less stable than a
    // small number of ordered bundles.
    private const int MaxFilesPerBundle = 8;
    private const int MaxConcurrentRenders = 4;
    private static readonly Brush ReadyBrush = Brush("#C86D3C");
    private static readonly Brush ActiveBrush = Brush("#C6532D");
    private static readonly Brush BusyBrush = Brush("#D89535");
    private static readonly Brush ErrorBrush = Brush("#B83A3A");
    private readonly DocumentRenderer _renderer = new();
    private readonly PrinterService _printer = new();
    private readonly UpdateService _updateService = new();
    private readonly ConcurrentQueue<PrintBatch> _pendingBatches = new();
    private readonly object _queueGate = new();
    private QueueWindow? _queueWindow;
    private bool _isPrinting;
    private bool _workerRunning;
    private bool _updateCheckStarted;
    private bool _closeWithoutWarning;

    public ObservableCollection<QueueItem> SessionQueue { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        SetIdleState();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_updateCheckStarted) return;
        _updateCheckStarted = true;

        var commandLineFiles = Environment.GetCommandLineArgs()
            .Skip(1)
            .Where(DocumentRenderer.IsSupported)
            .ToArray();
        if (commandLineFiles.Length > 0)
            QueueFiles(commandLineFiles);

        try
        {
            var update = await _updateService.CheckAsync(!HasSkipUpdateCheckArgument());
            if (_updateService.LatestVersion is not null)
                VersionText.Text = $"v{_updateService.LatestVersion}";

            if (update is null) return;

            var details = string.IsNullOrWhiteSpace(update.Notes)
                ? $"Printer Demon {update.Version} is available. Install it now?"
                : $"Printer Demon {update.Version} is available.\n\n{update.Notes}\n\nInstall it now?";
            var answer = MessageBox.Show(
                details,
                "Printer Demon update available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes)
            {
                await _updateService.InstallAndRestartAsync(update);
                Close();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Updates are optional; offline or unavailable GitHub releases must not block printing.
        }
    }

    private static bool HasSkipUpdateCheckArgument()
    {
        foreach (var argument in Environment.GetCommandLineArgs())
        {
            if (string.Equals(argument, "--skip-update-check", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void QueuePopOut_Click(object sender, RoutedEventArgs e)
    {
        if (_queueWindow is null)
        {
            _queueWindow = new QueueWindow
            {
                Owner = this,
                DataContext = this
            };
            _queueWindow.Closed += (_, _) => _queueWindow = null;
        }

        _queueWindow.Show();
        _queueWindow.Activate();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeWithoutWarning)
            return;

        var remaining = SessionQueue.Count(item => item.Status is "Queued" or "Printing");
        if (remaining == 0)
            return;

        var dialog = new CloseWarningWindow(remaining) { Owner = this };
        if (dialog.ShowDialog() != true)
            e.Cancel = true;
        else
            _closeWithoutWarning = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Window_DragEnter(object sender, DragEventArgs e) => UpdateDragState(e);
    private void Window_DragOver(object sender, DragEventArgs e) => UpdateDragState(e);

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        if (!_isPrinting) SetIdleState();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!TryGetFiles(e, out var files))
        {
            SetErrorState("Drop supported PDFs or images.");
            return;
        }

        QueueFiles(files);
        e.Handled = true;
    }

    private void QueueFiles(IReadOnlyList<string> files)
    {
        var queueItems = files.Select(path => new QueueItem(path)).ToArray();
        foreach (var item in queueItems)
        {
            item.Detail = "Waiting for printer";
            SessionQueue.Add(item);
        }
        UpdateQueueSummary();
        _pendingBatches.Enqueue(new PrintBatch(queueItems));
        QueueList.ScrollIntoView(queueItems[^1]);
        SetBusyState(files);

        var startWorker = false;
        lock (_queueGate)
        {
            if (!_workerRunning)
            {
                _workerRunning = true;
                _isPrinting = true;
                startWorker = true;
            }
        }
        if (startWorker) StartQueueWorker();
    }

    private void UpdateDragState(DragEventArgs e)
    {
        if (!TryGetFiles(e, out var files))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            SetErrorState("Drop supported PDFs or images.");
            return;
        }

        e.Effects = DragDropEffects.Copy;
        DropSurface.BorderBrush = ActiveBrush;
        TitleText.Text = "Release";
        StatusText.Text = _isPrinting
            ? (files.Count == 1 ? "Add this file to the print queue." : $"Add {files.Count} files to the print queue.")
            : (files.Count == 1 ? "Print this file automatically." : $"Print {files.Count} files automatically.");
        DetailText.Text = "A4 - Tray 1 - saved printer settings";
        e.Handled = true;
    }

    private void StartQueueWorker()
    {
        _ = StaTaskRunner.RunAsync(() =>
        {
            ProcessQueue();
            return true;
        }).ContinueWith(task =>
        {
            lock (_queueGate)
            {
                // A drop may have started a replacement worker before this
                // continuation reached the UI thread.
                if (_workerRunning) return;
            }
            _isPrinting = false;
            if (task.IsFaulted)
            {
                var message = task.Exception?.GetBaseException().Message ?? "Printing failed.";
                foreach (var item in SessionQueue.Where(item => item.Status is "Queued" or "Printing"))
                {
                    item.Status = "Failed";
                    item.Detail = message;
                    item.IsActive = false;
                }
                SetErrorState(message);
            }
            else
            {
                SetOverallState();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ProcessQueue()
    {
        var completedNormally = false;
        try
        {
            _printer.Validate();
            while (_pendingBatches.TryDequeue(out var batch))
            {
                var chunkCount = (int)Math.Ceiling(batch.Items.Count / (double)MaxFilesPerBundle);
                for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
                {
                    var chunk = batch.Items
                        .Skip(chunkIndex * MaxFilesPerBundle)
                        .Take(MaxFilesPerBundle)
                        .ToArray();
                    var temp = Path.Combine(Path.GetTempPath(), "PrinterDemon", Guid.NewGuid().ToString("N"));
                    try
                    {
                        using var renderGate = new SemaphoreSlim(MaxConcurrentRenders);
                        var renderTasks = new Task<IReadOnlyList<RenderedPage>>[chunk.Length];
                        for (var index = 0; index < chunk.Length; index++)
                        {
                            var item = chunk[index];
                            var globalIndex = chunkIndex * MaxFilesPerBundle + index;
                            UpdateQueueItem(item, "Printing", batch.Items.Count == 1
                                ? "Rendering document"
                                : $"Rendering file {globalIndex + 1}/{batch.Items.Count}");
                            var itemTemp = Path.Combine(temp, index.ToString("D4"));
                            var sourcePath = item.SourcePath;
                            renderTasks[index] = Task.Run(async () =>
                            {
                                await renderGate.WaitAsync();
                                try { return _renderer.Render(sourcePath, itemTemp); }
                                finally { renderGate.Release(); }
                            });
                        }

                        // Render concurrently with a hard limit, then flatten
                        // results in drop order for deterministic output.
                        var renderedFiles = Task.WhenAll(renderTasks).GetAwaiter().GetResult();
                        var allPages = renderedFiles.SelectMany(pages => pages).ToList();

                        foreach (var item in chunk)
                            UpdateQueueItem(item, "Printing", chunkCount == 1
                                ? "Sending bundled job to Xerox"
                                : $"Sending bundle {chunkIndex + 1}/{chunkCount} to Xerox");

                        // Large drops are split only at file boundaries. The
                        // source PDFs are still rendered unchanged, and the
                        // chunks remain ordered on the printer queue.
                        var submission = _printer.Print(allPages, BatchJobName(batch, chunkIndex, chunkCount));
                        foreach (var item in chunk)
                            UpdateQueueItem(item, "Printing", submission.Message);

                        if (submission.Job is null)
                        {
                            foreach (var item in chunk)
                                UpdateQueueItem(item, "Sent", submission.Message);
                        }
                        else
                        {
                            StartSpoolerMonitor(chunk, submission.Job);
                        }
                    }
                    catch (Exception ex)
                    {
                        foreach (var item in chunk)
                            UpdateQueueItem(item, "Failed", ex.Message);
                    }
                    finally
                    {
                        if (Directory.Exists(temp)) Directory.Delete(temp, true);
                    }
                }
            }
            completedNormally = true;
        }
        catch (Exception ex)
        {
            while (_pendingBatches.TryDequeue(out var pendingBatch))
            {
                foreach (var item in pendingBatch.Items)
                    UpdateQueueItem(item, "Failed", ex.Message);
            }
            throw;
        }
        finally
        {
            var restartWorker = false;
            lock (_queueGate)
            {
                if (completedNormally && !_pendingBatches.IsEmpty)
                    restartWorker = true;
                else
                    _workerRunning = false;
            }
            if (restartWorker)
                Dispatcher.Invoke(StartQueueWorker);
        }
    }

    private void UpdateQueueItem(QueueItem item, string status, string detail)
    {
        Dispatcher.Invoke(() =>
        {
            item.Status = status;
            item.Detail = detail;
            item.IsActive = status is "Queued" or "Printing";
            UpdateQueueSummary();
            QueueList.ScrollIntoView(item);
        });
    }

    private void StartSpoolerMonitor(IReadOnlyList<QueueItem> items, PrintJobReference job)
    {
        _ = StaTaskRunner.RunAsync(() => _printer.WaitForCompletion(job)).ContinueWith(task =>
        {
            var result = task.IsCompletedSuccessfully
                ? task.Result
                : PrintJobMonitoringResult.SentWithoutConfirmation(
                    "Accepted by Windows spooler; final status unavailable.");
            Dispatcher.BeginInvoke(() =>
            {
                var finalStatus = result.Failed
                    ? "Failed"
                    : result.Printed ? "Printed" : "Sent";
                foreach (var item in items)
                {
                    item.Status = finalStatus;
                    item.Detail = result.Message;
                    item.IsActive = false;
                }
                UpdateQueueSummary();
                SetOverallState();
            });
        }, TaskScheduler.Default);
    }

    private void UpdateQueueSummary()
    {
        var remaining = SessionQueue.Count(item => item.Status is "Queued" or "Printing");
        var complete = SessionQueue.Count(item => item.Status is "Sent" or "Printed");
        var failed = SessionQueue.Count(item => item.Status == "Failed");
        QueueSummaryText.Text = failed == 0
            ? $"QUEUE · {remaining} REMAINING · {complete} COMPLETE"
            : $"QUEUE · {remaining} REMAINING · {complete} COMPLETE · {failed} FAILED";
    }

    private static string BatchJobName(PrintBatch batch, int chunkIndex, int chunkCount)
    {
        var first = batch.Items[0].FileName;
        var baseName = batch.Items.Count == 1 ? first : $"{first} + {batch.Items.Count - 1} files";
        return chunkCount == 1 ? baseName : $"{baseName} (part {chunkIndex + 1} of {chunkCount})";
    }

    private void SetOverallState()
    {
        var active = SessionQueue.Count(item => item.Status is "Queued" or "Printing");
        _isPrinting = active > 0;
        if (active > 0)
        {
            FadeEdgeGlow(true);
            DemonVisual.ShowPrinting();
            DropSurface.BorderBrush = BusyBrush;
            TitleText.Text = "Printing";
            StatusText.Text = $"{active} job{(active == 1 ? string.Empty : "s")} active in the printer queue.";
            DetailText.Text = "Waiting for Xerox printer status.";
            return;
        }

        var failed = SessionQueue.Count(item => item.Status == "Failed");
        var printed = SessionQueue.Count(item => item.Status == "Printed");
        var sent = SessionQueue.Count(item => item.Status == "Sent");
        var complete = printed + sent;
        if (failed > 0 && complete == 0)
            SetErrorState(SessionQueue.LastOrDefault(item => item.Status == "Failed")?.Detail ?? "Printing failed.");
        else if (failed > 0)
            SetPartialState(printed, failed);
        else if (printed > 0 && sent == 0)
            SetPrintedState(printed);
        else if (complete > 0)
            SetSentState(sent, printed);
        else SetIdleState();
    }

    private static bool TryGetFiles(DragEventArgs e, out IReadOnlyList<string> files)
    {
        files = Array.Empty<string>();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] dropped)
            return false;
        if (dropped.Length == 0 || dropped.Any(file => !DocumentRenderer.IsSupported(file))) return false;
        files = dropped;
        return true;
    }

    private void SetIdleState()
    {
        FadeEdgeGlow(false);
        DemonVisual.ShowIdle();
        DropSurface.BorderBrush = ReadyBrush;
        TitleText.Text = "Drop Files";
        StatusText.Text = "Drop PDFs or images to print.";
        DetailText.Text = "Xerox VersaLink C600 - A4 - Tray 1";
    }

    private void SetBusyState(IReadOnlyList<string> files)
    {
        FadeEdgeGlow(true);
        DemonVisual.ShowPrinting();
        DropSurface.BorderBrush = BusyBrush;
        TitleText.Text = "Printing";
        StatusText.Text = files.Count == 1 ? "Rendering and sending to Xerox." : $"Printing {files.Count} files.";
        DetailText.Text = Path.GetFileName(files[0]);
    }

    private void SetPartialState(int success, int failed)
    {
        FadeEdgeGlow(false);
        DemonVisual.ShowError();
        DropSurface.BorderBrush = BusyBrush;
        TitleText.Text = "Partial";
        StatusText.Text = $"Sent {success}; {failed} failed.";
        DetailText.Text = "Drop again to retry failed files.";
    }

    private void SetPrintedState(int total)
    {
        FadeEdgeGlow(false);
        DemonVisual.ShowDone();
        DropSurface.BorderBrush = BusyBrush;
        TitleText.Text = "Printed";
        StatusText.Text = $"Printed {total} file{(total == 1 ? string.Empty : "s")} successfully.";
        DetailText.Text = "Xerox printer reported the job complete.";
    }

    private void SetSentState(int sent, int printed)
    {
        FadeEdgeGlow(false);
        DemonVisual.ShowDone();
        DropSurface.BorderBrush = BusyBrush;
        TitleText.Text = "Complete";
        StatusText.Text = printed == 0
            ? $"Sent {sent} file{(sent == 1 ? string.Empty : "s")} to Xerox."
            : $"Printed {printed}; sent {sent} file{(sent + printed == 1 ? string.Empty : "s")}.";
        DetailText.Text = sent == 0
            ? "Xerox printer reported the jobs complete."
            : "Some jobs were accepted but final printer status was unavailable.";
    }

    private void SetErrorState(string message)
    {
        FadeEdgeGlow(false);
        DemonVisual.ShowError();
        DropSurface.BorderBrush = ErrorBrush;
        TitleText.Text = "Can't Print";
        StatusText.Text = message;
        DetailText.Text = "Check the saved Xerox printer settings.";
    }

    private void FadeEdgeGlow(bool show)
    {
        HotEdgeGlow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(450),
            EasingFunction = new QuadraticEase()
        });
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private sealed record PrintBatch(IReadOnlyList<QueueItem> Items);
}
