using System.ComponentModel;
using System.Windows;
using SoundStudio.App.ViewModels;
using Wpf.Ui.Controls;

namespace SoundStudio.App;

/// <summary>Shows how dropped/chosen files matched sounds before anything is converted.</summary>
public partial class ReviewReplacementsWindow : FluentWindow
{
    private readonly IReadOnlyList<MatchItemViewModel> _items;

    public ReviewReplacementsWindow(IReadOnlyList<MatchItemViewModel> items, string scopeName)
    {
        InitializeComponent();
        _items = items;
        // Matched first, then unmatched.
        MatchList.ItemsSource = items.OrderByDescending(i => i.IsMatched).ThenBy(i => i.FileName).ToList();

        var matched = items.Count(i => i.IsMatched);
        var sounds = items.Where(i => i.IsMatched).Sum(i => i.Rows.Count);
        SummaryText.Text = matched == 0
            ? "No files match a sound"
            : $"{matched} of {items.Count} files match {sounds} sound{(sounds == 1 ? "" : "s")}";
        ScopeText.Text = matched == 0
            ? "Files are matched by name: name a file after the sound it replaces, e.g. vox_us_death_00.wav. Exported WAVs already have the right names."
            : $"Matched by file name within {scopeName}. Nothing is changed in the game until you click Apply.";

        foreach (var item in items)
            item.PropertyChanged += OnItemChanged;
        UpdateButton();
        Closed += (_, _) =>
        {
            foreach (var item in items)
                item.PropertyChanged -= OnItemChanged;
        };
    }

    public ReviewResult? Result { get; private set; }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MatchItemViewModel.IsSelected))
            UpdateButton();
    }

    private void UpdateButton()
    {
        var count = _items.Where(i => i.IsSelected && i.IsMatched).Sum(i => i.Rows.Count);
        StageButton.Content = count == 1 ? "Stage 1 replacement" : $"Stage {count} replacements";
        StageButton.IsEnabled = count > 0;
    }

    private void Stage_Click(object sender, RoutedEventArgs e)
    {
        Result = new ReviewResult(TrimLong: TrimLongCheck.IsChecked == true);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
