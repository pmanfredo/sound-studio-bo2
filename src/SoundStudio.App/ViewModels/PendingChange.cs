using System.IO;
using SoundStudio.Audio;

namespace SoundStudio.App.ViewModels;

/// <summary>A converted replacement waiting to be applied to the game files.</summary>
public sealed class PendingChange(SoundRowViewModel row, PreparedReplacement prepared)
{
    /// <summary>Replacements longer than this beyond the original get a warning.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(0.25);

    public SoundRowViewModel Row { get; } = row;

    /// <summary>Set when this change came from someone you sync with.</summary>
    public string? FromAuthor { get; init; }

    /// <summary>Someone you sync with reverted this sound to the original.</summary>
    public bool IsRevert { get; init; }

    /// <summary>Only the volume of the current game audio changed (no new file).</summary>
    public bool IsVolumeOnly { get; init; }

    public double GainDb => Prepared.AppliedGainDb;
    private string GainText => Math.Abs(GainDb) < 0.05 ? "" : $" · {GainDb:+0.0;-0.0} dB";

    /// <summary>What goes in the history, e.g. "voice.wav, +3.0 dB" or "Volume -4.5 dB".</summary>
    public string Detail => IsVolumeOnly
        ? $"Volume {GainDb:+0.0;-0.0} dB"
        : FromAuthor != null ? $"from {FromAuthor}" : SourceFileName + (Math.Abs(GainDb) < 0.05 ? "" : $", {GainDb:+0.0;-0.0} dB");
    public PreparedReplacement Prepared { get; } = prepared;

    public string SourceFileName => Path.GetFileName(Prepared.SourcePath);
    public string DurationText => SoundRowViewModel.FormatDuration(Prepared.Duration);
    public string OriginalDurationText => SoundRowViewModel.FormatDuration(Row.Info.Duration);
    public bool WasTrimmed => Prepared.Duration < Prepared.SourceDuration - TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Longer replacements are the known cause of missions loading forever (reported with the old Sound Studio),
    /// so they're flagged, with a one-click trim.
    /// </summary>
    public bool IsTooLong => Prepared.Duration > Row.Info.Duration + Tolerance;

    public string Summary => IsVolumeOnly
        ? $"Volume {GainDb:+0.0;-0.0} dB · game audio"
        : FromAuthor != null
            ? $"From {FromAuthor} · " + (IsRevert ? "back to the original" : $"{DurationText}") + GainText
            : $"{SourceFileName} · {DurationText}" + (WasTrimmed ? " (trimmed)" : "") + GainText;

    public string? Warning => IsTooLong
        ? $"{DurationText} long, the original is {OriginalDurationText}. Sounds longer than the original can make some levels load forever."
        : null;
}
