using System.Windows;
using System.Windows.Input;
using SoundStudio.App.ViewModels;
using SoundStudio.Audio;
using Wpf.Ui.Controls;

namespace SoundStudio.App;

public partial class LayersWindow : FluentWindow
{
    public LayersWindow(LayersViewModel viewModel, LayerSession? session)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.RequestClose += (_, _) => Close();
        viewModel.ShowRecorder = recorder => new RecorderWindow(recorder) { Owner = this }.ShowDialog();
        viewModel.PickFiles = () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Add layers to {viewModel.Target.Name}",
                Filter = ReplacementEncoder.FileFilter,
                Multiselect = true,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileNames : null;
        };
        Loaded += async (_, _) => await viewModel.InitializeAsync(session);
        Closed += (_, _) => viewModel.Dispose();
        PreviewKeyDown += OnKey;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                await viewModel.AddFilesAsync(paths);
        };
    }

    public LayersViewModel ViewModel { get; }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)
            return;
        switch (e.Key)
        {
            case Key.Space:
                ViewModel.PlayMixCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>The "⋯" button opens its menu on a normal click.</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void GainSlider_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayerViewModel layer })
            layer.GainDb = 0;
    }

    private void MasterSlider_DoubleClick(object sender, MouseButtonEventArgs e) => ViewModel.MasterGainDb = 0;
}
