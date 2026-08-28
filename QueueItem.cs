using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;

namespace PrinterDemon;

public sealed class QueueItem : INotifyPropertyChanged
{
    private string _status = "Queued";
    private string _detail = string.Empty;
    private bool _isActive;

    public QueueItem(string path)
    {
        SourcePath = path;
        FileName = Path.GetFileName(path);
    }

    public string SourcePath { get; }

    public string FileName { get; }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSent));
        }
    }

    public string Detail
    {
        get => _detail;
        set { if (_detail == value) return; _detail = value; OnPropertyChanged(); }
    }

    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; OnPropertyChanged(); }
    }

    public bool IsSent => Status is "Sent" or "Printed";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
