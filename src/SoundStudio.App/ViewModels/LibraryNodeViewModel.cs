using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SoundStudio.Core.Library;
using Wpf.Ui.Controls;

namespace SoundStudio.App.ViewModels;

public enum LibraryNodeKind { AllSounds, Modified, Pending, Available, Category, Map, Bank, TypeRoot, TypeGroup, TypeSubgroup }

/// <summary>A node in the library tree (All sounds, Modified, Zombies → Origins → bank file...).</summary>
public sealed partial class LibraryNodeViewModel : ObservableObject
{
    public LibraryNodeViewModel(LibraryNodeKind kind, string title, SymbolRegular icon, IReadOnlyList<SoundRowViewModel> rows)
    {
        Kind = kind;
        Title = title;
        Icon = icon;
        Rows = rows;
    }

    public LibraryNodeKind Kind { get; }
    public string Title { get; }
    public string? Subtitle { get; init; }
    public string? ToolTip { get; init; }
    public SymbolRegular Icon { get; }
    public BankInfo? Bank { get; init; }

    /// <summary>Every sound this node covers (including children).</summary>
    public IReadOnlyList<SoundRowViewModel> Rows { get; }

    public ObservableCollection<LibraryNodeViewModel> Children { get; } = [];

    public int Count => Rows.Count;
    public int ModifiedCount => Rows.Count(r => r.IsModified);
    /// <summary>Show the amber count pill (not on the Modified node itself, where it would repeat the count).</summary>
    public bool HasModified => Kind != LibraryNodeKind.Modified && ModifiedCount > 0;
    public string CountText => Count.ToString("N0");

    /// <summary>Highlights the "Modified" smart node.</summary>
    public bool IsAccent => Kind == LibraryNodeKind.Modified;

    /// <summary>Highlights the "Available from others" smart node.</summary>
    public bool IsAvailableNode => Kind == LibraryNodeKind.Available;

    /// <summary>Highlights the "Pending changes" smart node.</summary>
    public bool IsPendingNode => Kind == LibraryNodeKind.Pending;

    /// <summary>Staged changes among this node's sounds.</summary>
    public int PendingCount => Rows.Count(r => r.HasPending);
    public bool HasPendingRows => Kind != LibraryNodeKind.Pending && PendingCount > 0;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ModifiedCount));
        OnPropertyChanged(nameof(HasModified));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPendingRows));
        foreach (var child in Children)
            child.RefreshCounts();
    }
}
