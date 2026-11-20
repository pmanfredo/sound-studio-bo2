using System.Windows;
using System.Windows.Input;
using SoundStudio.App.Services;
using SoundStudio.App.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace SoundStudio.App;

public partial class MainWindow : FluentWindow, IUserDialogs
{
    private readonly SnackbarService _snackbar = new();

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _snackbar.SetSnackbarPresenter(SnackbarPresenter);
        Closed += async (_, _) =>
        {
            viewModel.Player.Dispose();
            await viewModel.Sync.DisposeAsync(); // stops hosting / the tunnel cleanly
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public MainViewModel ViewModel { get; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            ViewModel.RescanCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.R && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ViewModel.RecordSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ViewModel.ReplaceSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ViewModel.ApplyPendingCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && !IsTyping())
        {
            ViewModel.Player.PlayPause();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !IsTyping() && SoundGrid.IsKeyboardFocusWithin)
        {
            ViewModel.Player.PlayFromStart();
            e.Handled = true;
        }
    }

    /// <summary>Keyboard focus is in a text field, so keys like Space belong to it.</summary>
    private static bool IsTyping() => Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;

    private void SoundGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            System.Windows.Media.VisualTreeHelper.GetParent(source) != null &&
            FindAncestor<System.Windows.Controls.DataGridRow>(source) != null)
            ViewModel.Player.PlayFromStart();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T)
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    private void LibraryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is LibraryNodeViewModel node)
            ViewModel.SelectedNode = node;
    }

    // ---- IUserDialogs ------------------------------------------------------------------------------------

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Owner = this,
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = confirmText,
            PrimaryButtonAppearance = ControlAppearance.Primary,
            CloseButtonText = "Cancel",
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    public void Notify(string title, string message, NoticeKind kind = NoticeKind.Success)
    {
        var (appearance, icon) = kind switch
        {
            NoticeKind.Success => (ControlAppearance.Success, SymbolRegular.CheckmarkCircle24),
            NoticeKind.Warning => (ControlAppearance.Caution, SymbolRegular.Warning24),
            NoticeKind.Error => (ControlAppearance.Danger, SymbolRegular.ErrorCircle24),
            _ => (ControlAppearance.Secondary, SymbolRegular.Info24),
        };
        _snackbar.Show(title, message, appearance, new SymbolIcon(icon), TimeSpan.FromSeconds(kind == NoticeKind.Error ? 10 : 5));
    }

    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (initialFolder != null)
            dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private SyncWindow? _syncWindow;

    public void ShowSync(SyncViewModel sync)
    {
        if (_syncWindow is { IsLoaded: true })
        {
            _syncWindow.Activate();
            return;
        }
        _syncWindow = new SyncWindow(sync) { Owner = this };
        _syncWindow.Show();
    }

    public void ShowRecorder(RecorderViewModel recorder) =>
        new RecorderWindow(recorder) { Owner = this }.ShowDialog();

    public string? PickFile(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, Multiselect = false };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public Task<ReviewResult?> ReviewReplacementsAsync(IReadOnlyList<MatchItemViewModel> items, string scopeName)
    {
        var window = new ReviewReplacementsWindow(items, scopeName) { Owner = this };
        return Task.FromResult(window.ShowDialog() == true ? window.Result : null);
    }

    // ---- drag & drop -------------------------------------------------------------------------------------

    private static string[]? DraggedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private static SoundRowViewModel? RowUnder(DragEventArgs e) =>
        FindAncestor<System.Windows.Controls.DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext as SoundRowViewModel;

    private void Sounds_DragOver(object sender, DragEventArgs e)
    {
        var files = DraggedFiles(e);
        e.Effects = files != null ? DragDropEffects.Copy : DragDropEffects.None;
        ViewModel.UpdateDropHint(files, RowUnder(e) ?? ViewModel.SelectedRow);
        e.Handled = true;
    }

    private void Sounds_DragLeave(object sender, DragEventArgs e) => ViewModel.UpdateDropHint(null, null);

    private async void Sounds_Drop(object sender, DragEventArgs e)
    {
        if (DraggedFiles(e) is { } files)
            await ViewModel.HandleDropAsync(files, RowUnder(e));
    }

    private void Details_DragOver(object sender, DragEventArgs e)
    {
        var files = DraggedFiles(e);
        e.Effects = files != null && ViewModel.SelectedRow != null ? DragDropEffects.Copy : DragDropEffects.None;
        ViewModel.UpdateDropHint(files, ViewModel.SelectedRow);
        e.Handled = true;
    }

    private async void Details_Drop(object sender, DragEventArgs e)
    {
        if (DraggedFiles(e) is { } files)
            await ViewModel.HandleDropAsync(files, ViewModel.SelectedRow);
    }

    public string? PickSaveFile(string title, string fileName, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, FileName = fileName, Filter = filter, AddExtension = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}
