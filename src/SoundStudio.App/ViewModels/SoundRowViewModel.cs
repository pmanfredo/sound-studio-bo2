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
    [NotifyPropertyChangedFor(nameof(IsModified), nameof(StatusText), nameof(OriginalMissing), nameof(IsMasked), nameof(ListName), nameof(VisiblePending), nameof(VisibleOffer), nameof(HasVisiblePending), nameof(ShowOfferPill), nameof(ShowModifiedPill), nameof(CanRevert), nameof(ShowModifiedCard), nameof(ShowOriginalCard), nameof(ShowOverlapNote))]
    private SoundStatus _status;

    /// <summary>The untouched game audio for this sound is saved and can be restored.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert), nameof(OriginalMissing))]
    private bool _hasOriginal;

    /// <summary>New audio staged for this sound, not yet written to the game.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending), nameof(IsMasked), nameof(ListName), nameof(VisiblePending), nameof(VisibleOffer), nameof(HasVisiblePending), nameof(ShowOfferPill), nameof(ShowModifiedPill), nameof(CanRevert), nameof(ShowModifiedCard), nameof(ShowOriginalCard), nameof(ShowOverlapNote))]
    private PendingChange? _pending;

    /// <summary>Someone else has a different version of this sound that can be fetched.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOffer), nameof(IsMasked), nameof(ListName), nameof(VisiblePending), nameof(VisibleOffer), nameof(HasVisiblePending), nameof(ShowOfferPill), nameof(ShowModifiedPill), nameof(CanRevert), nameof(ShowModifiedCard), nameof(ShowOriginalCard), nameof(ShowOverlapNote))]
    private OfferViewModel? _offer;

    /// <summary>The list this row is showing in right now (set when the list is filtered).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMasked), nameof(ListName), nameof(VisiblePending), nameof(VisibleOffer), nameof(HasVisiblePending), nameof(ShowOfferPill), nameof(ShowModifiedPill), nameof(CanRevert), nameof(ShowModifiedCard), nameof(ShowOriginalCard), nameof(ShowOverlapNote))]
    private LibraryNodeKind _shownIn;

    public bool HasOffer => Offer != null;
    public bool HasPending => Pending != null;

    /// <summary>
    /// Shown as someone's hidden change: "Hidden sound from ...", with nothing that tells which sound it is.
    /// Only under Pending changes and Available from others; everywhere else the sound looks as it does in your game.
    /// </summary>
    public bool IsMasked => ShownIn switch
    {
        LibraryNodeKind.Pending => Pending?.IsHidden == true,
        LibraryNodeKind.Available => Offer?.IsHidden == true,
        _ => false,
    };

    /// <summary>The staged change shown here: someone's hidden one only where it's masked, never elsewhere.</summary>
    public PendingChange? VisiblePending => Pending is { } p && p.IsHidden == IsMasked ? p : null;
    public OfferViewModel? VisibleOffer => Offer is { } o && (IsMasked || !o.IsHidden) ? o : null;
    public bool HasVisiblePending => VisiblePending != null;

    /// <summary>The name shown in the list and details: the sound's name, unless it's masked.</summary>
    public string ListName => IsMasked ? $"Hidden sound from {Pending?.FromAuthor ?? Offer?.OwnerName}" : Name;

    public bool ShowModifiedCard => IsModified && !IsMasked;
    public bool ShowOriginalCard => !IsModified && !IsMasked;
    public bool ShowOfferPill => VisibleOffer != null && !HasVisiblePending;

    /// <summary>A hidden change for a sound you've changed yourself: say so, since applying it replaces yours.</summary>
    public bool ShowOverlapNote => IsMasked && IsModified;

    /// <summary>Open in an external editor: its name ("Audacity"), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingExternally))]
    private string? _editingIn;
    public bool IsEditingExternally => EditingIn != null;
    public bool CanReplace => Info.CanReplace && Info.Format != Core.Bo2.AudioFormat.Mp3;

    /// <summary>The grid shows "Pending" instead of "Modified" while a change is staged.</summary>
    public bool ShowModifiedPill => IsModified && !HasVisiblePending && VisibleOffer == null;

    public bool IsModified => Status == SoundStatus.Modified;
    public bool CanRevert => IsModified && HasOriginal && !HasVisiblePending && !IsMasked;
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
