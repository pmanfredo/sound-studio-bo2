using CommunityToolkit.Mvvm.ComponentModel;
using SoundStudio.Core.Library;

namespace SoundStudio.App.ViewModels;

public enum SoundStatus
{
    Original,
    Modified,
}

/// <summary>One row in the sound list. Wraps the immutable <see cref="SoundInfo"/> with display state.</summary>
public sealed partial class SoundRowViewModel : ObservableObject
{
    public SoundRowViewModel(SoundInfo info, string mapName)
    {
        Info = info;
        MapName = mapName;
        _status = info.IsModified ? SoundStatus.Modified : SoundStatus.Original;
    }

    public SoundInfo Info { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified), nameof(StatusText), nameof(CanRevert), nameof(OriginalMissing), nameof(ShowModifiedPill))]
    private SoundStatus _status;

    /// <summary>The untouched game audio for this sound is saved and can be restored.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert), nameof(OriginalMissing))]
    private bool _hasOriginal;

    /// <summary>New audio staged for this sound, not yet written to the game.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending), nameof(ShowModifiedPill), nameof(CanRevert), nameof(ShowOfferPill))]
    private PendingChange? _pending;

    /// <summary>Someone else has a different version of this sound that can be fetched.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOffer), nameof(ShowOfferPill), nameof(ShowModifiedPill))]
    private OfferViewModel? _offer;

    public bool HasOffer => Offer != null;
    public bool ShowOfferPill => HasOffer && !HasPending;

    public bool HasPending => Pending != null;

    /// <summary>Open in an external editor: its name ("Audacity"), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingExternally))]
    private string? _editingIn;
    public bool IsEditingExternally => EditingIn != null;
    public bool CanReplace => Info.CanReplace && Info.Format != Core.Bo2.AudioFormat.Mp3;

    /// <summary>The grid shows "Pending" instead of "Modified" while a change is staged.</summary>
    public bool ShowModifiedPill => IsModified && !HasPending && !HasOffer;

    public bool IsModified => Status == SoundStatus.Modified;
    public bool CanRevert => IsModified && HasOriginal && !HasPending;
    public bool OriginalMissing => IsModified && !HasOriginal;
    public string StatusText => Status switch
    {
        SoundStatus.Modified => "Modified",
        _ => "Original",
    };

    public string Name => Info.Name;
    public string AssetPath => Info.AssetPath ?? "(unknown asset path)";
    public string MapName { get; }
    public string BankLabel => Info.Bank.Description.ShortLabel;
    public string CategoryAndMap => $"{BankNaming.CategoryName(Info.Bank.Description.Category)} · {MapName}";

    /// <summary>Compact bank label for the grid, e.g. "English · Streamed" or "All · Loaded".</summary>
    public string ShortBankLabel
    {
        get
        {
            var d = Info.Bank.Description;
            var lang = d.Language == "all" ? "All" : char.ToUpperInvariant(d.Language[0]) + d.Language[1..];
            return d.Part.Length > 0 ? $"{d.Part} · {lang} · {d.KindLabel}" : $"{lang} · {d.KindLabel}";
        }
    }
    public string BankFileName => Info.Bank.FileName;
    public string FormatName => Info.FormatName;
    public double DurationSeconds => Info.Duration.TotalSeconds;
    public string DurationText => FormatDuration(Info.Duration);
    public string ChannelsText => Info.Channels switch { 1 => "Mono", 2 => "Stereo", var n => $"{n} ch" };
    public string LoopText => Info.Loop ? "Loops" : "";
    public int SizeBytes => Info.Size;
    public string SizeText => FormatSize(Info.Size);
    public string SampleRateText => $"{Info.SampleRate / 1000.0:0.#} kHz";
    public string IdHex => Info.IdHex;
    public string TypeText => Info.Type.ToString();

    public static string FormatDuration(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00}" : $"{t.TotalSeconds:0.00} s";

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 20 => $"{bytes / 1048576.0:0.0} MB",
        >= 1 << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
