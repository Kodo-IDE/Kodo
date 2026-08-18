// Licensed under GPL-v3.0
using System;
using System.ComponentModel;

namespace Kodo.Models;

public sealed class RecentFileItem : INotifyPropertyChanged
{
    private bool _isPinned;

    public RecentFileItem(string path, bool isFolder, DateTime lastOpened, bool isPinned = false)
    {
        Path       = path;
        IsFolder   = isFolder;
        LastOpened = lastOpened;
        _isPinned  = isPinned;
    }

    public string Path { get; }
    public bool IsFolder { get; }
    public DateTime LastOpened { get; set; }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinButtonText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinTooltipText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinnedBadgeText)));
        }
    }

    public string PinButtonText => IsPinned ? "Unpin" : "Pin";

    public string PinTooltipText => IsPinned ? "Unpin this item" : "Pin this item";

    public string PinnedBadgeText => "Pinned";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName
    {
        get
        {
            if (IsFolder)
                return System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            var name = System.IO.Path.GetFileName(Path);
            var dot = name.IndexOf("."[0]);
            return dot > 0 ? name[..dot] : name;
        }
    }

    public string DirectoryPath => IsFolder
        ? System.IO.Path.GetDirectoryName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? string.Empty
        : System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    public string FileTypeName
    {
        get
        {
            if (IsFolder) return "Folder";
            var ext = System.IO.Path.GetExtension(Path);
            if (string.IsNullOrEmpty(ext))
            {
                var name = System.IO.Path.GetFileName(Path);
                return string.IsNullOrWhiteSpace(name) ? "File" : $"{name} file";
            }
            return $"{ext.ToLowerInvariant()} file";
        }
    }

    public string LastOpenedText
    {
        get
        {
            var diff = DateTime.Now - LastOpened;
            if (diff.TotalMinutes < 1)  return "Just now";
            if (diff.TotalHours   < 1)  return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalDays    < 1)  return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays    < 30) return $"{(int)diff.TotalDays}d ago";
            return LastOpened.ToString("MMM d");
        }
    }

    public string LastOpenedLongText
    {
        get
        {
            var diff = DateTime.Now - LastOpened;
            if (diff.TotalMinutes < 1)  return "just now";
            if (diff.TotalMinutes < 2)  return "1 minute ago";
            if (diff.TotalHours   < 1)  return $"{(int)diff.TotalMinutes} minutes ago";
            if (diff.TotalHours   < 2)  return "1 hour ago";
            if (diff.TotalDays    < 1)  return $"{(int)diff.TotalHours} hours ago";
            if (diff.TotalDays    < 2)  return "yesterday";
            if (diff.TotalDays    < 7)  return $"{(int)diff.TotalDays} days ago";
            if (diff.TotalDays    < 14) return "1 week ago";
            if (diff.TotalDays    < 30) return $"{(int)(diff.TotalDays / 7)} weeks ago";
            if (diff.TotalDays    < 60) return "1 month ago";
            if (diff.TotalDays    < 365) return $"{(int)(diff.TotalDays / 30)} months ago";
            if (diff.TotalDays    < 730) return "1 year ago";
            return $"{(int)(diff.TotalDays / 365)} years ago";
        }
    }
}