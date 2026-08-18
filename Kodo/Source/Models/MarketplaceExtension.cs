// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace Kodo.Models;

public class MarketplaceExtension : INotifyPropertyChanged
{
    private bool _isInstalling;
    private string _installButtonText = "Install";
    private bool _isUpdateAvailable;
    private string _installedVersion = string.Empty;
    private DateTime? _installedOnUtc;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string IconUrl { get; init; } = string.Empty;

    // Version/DownloadUrl/FileName are mutable (not init-only) because compiler entries now
    // resolve these live from the vendor after the initial fast paint - see
    // MainWindow.RefreshCompilerResolutionsAsync. Extensions still only ever set them once,
    // at construction, so this is a no-op behavior change for the Marketplace tab.
    private string _version = string.Empty;
    public string Version
    {
        get => _version;
        set { if (_version == value) return; _version = value; OnPropertyChanged(); }
    }

    private string _downloadUrl = string.Empty;
    public string DownloadUrl
    {
        get => _downloadUrl;
        set { if (_downloadUrl == value) return; _downloadUrl = value; OnPropertyChanged(); }
    }

    private string _fileName = string.Empty;
    public string FileName
    {
        get => _fileName;
        set { if (_fileName == value) return; _fileName = value; OnPropertyChanged(); }
    }

    // Compiler-only metadata used by the editor's Run/Build buttons. Extensions never set these.
    // FileExtensions maps open files onto this compiler; LanguageExtensionIds lists the language
    // extensions it provides tooling for (so installing a compiler can also install its language
    // counterpart). RunCommandTemplate/BuildCommandTemplate are command lines with {file}/{name}/
    // {folder}/{args} placeholders; FileCommands override them per file extension where a compiler
    // needs different tools for different file types (e.g. gcc for .c vs g++ for .cpp).
    public string[] FileExtensions { get; init; } = [];
    public string[] LanguageExtensionIds { get; init; } = [];
    public string? RunCommandTemplate { get; init; }
    public string? BuildCommandTemplate { get; init; }
    public IReadOnlyDictionary<string, (string? Run, string? Build)>? FileCommands { get; init; }

    private Bitmap? _iconImage;
    public Bitmap? IconImage
    {
        get => _iconImage;
        set
        {
            if (_iconImage == value) return;
            _iconImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    private string? _svgData;
    public string? SvgData
    {
        get => _svgData;
        set
        {
            if (_svgData == value) return;
            _svgData = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => IconImage is not null || SvgData is not null;
    public string NameAbbreviation => Name.Length >= 2 ? Name[..2] : Name;

    public bool IsInstalling
    {
        get => _isInstalling;
        set
        {
            if (_isInstalling == value) return;
            _isInstalling = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInstallEnabled));
        }
    }

    public bool IsInstalled { get; private set; }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set
        {
            if (_isUpdateAvailable == value) return;
            _isUpdateAvailable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowInstalledBadge));
        }
    }

    public string InstalledVersion
    {
        get => _installedVersion;
        private set
        {
            if (_installedVersion == value) return;
            _installedVersion = value;
            OnPropertyChanged();
        }
    }

    public DateTime? InstalledOnUtc
    {
        get => _installedOnUtc;
        private set
        {
            if (_installedOnUtc == value) return;
            _installedOnUtc = value;
            OnPropertyChanged();
        }
    }

    public bool IsInstallEnabled => !IsInstalling && (!IsInstalled || IsUpdateAvailable) && !string.IsNullOrWhiteSpace(DownloadUrl);

    // Installed with nothing to update - shown as a quiet status pill instead of an action button.
    public bool ShowInstalledBadge => IsInstalled && !IsUpdateAvailable;

    public string InstallButtonText
    {
        get => _installButtonText;
        set
        {
            if (_installButtonText == value) return;
            _installButtonText = value;
            OnPropertyChanged();
        }
    }

    public void SetInstalledState(LoadedExtension? installedExtension, bool isUpdateAvailable)
    {
        var isInstalled = installedExtension is not null;
        ApplyInstalledState(isInstalled, installedExtension?.Version ?? string.Empty, installedExtension?.InstalledOnUtc, isUpdateAvailable);
    }

    // Compilers aren't Kodo extensions (no LoadedExtension record), so their installed state
    // comes from the local Compilers install registry instead - see SyncCompilerInstallStates.
    public void SetCompilerInstalledState(string? installedVersion, DateTime? installedOnUtc, bool isUpdateAvailable) =>
        ApplyInstalledState(installedVersion is not null, installedVersion ?? string.Empty, installedOnUtc, isUpdateAvailable);

    private void ApplyInstalledState(bool isInstalled, string installedVersion, DateTime? installedOnUtc, bool isUpdateAvailable)
    {
        InstalledVersion = installedVersion;
        InstalledOnUtc = installedOnUtc;
        IsUpdateAvailable = isUpdateAvailable;

        if (IsInstalled != isInstalled)
        {
            IsInstalled = isInstalled;
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(ShowInstalledBadge));
        }

        if (!IsInstalling)
        {
            InstallButtonText = isInstalled
                ? (isUpdateAvailable ? "Update" : "Installed")
                : "Install";
        }

        OnPropertyChanged(nameof(IsInstallEnabled));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}