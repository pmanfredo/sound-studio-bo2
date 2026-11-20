using System.ComponentModel;
using System.Windows.Media;
using SoundStudio.App.ViewModels;
using Wpf.Ui.Controls;

namespace SoundStudio.App;

public partial class SyncWindow : FluentWindow
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24));

    public SyncWindow(SyncViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.PropertyChanged += OnChanged;
        viewModel.Peers.CollectionChanged += (_, _) => UpdateDot();
        Closed += (_, _) => viewModel.PropertyChanged -= OnChanged;
        UpdateDot();
    }

    public SyncViewModel ViewModel { get; }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SyncViewModel.IsConnected) or nameof(SyncViewModel.Mode))
            UpdateDot();
    }

    private void UpdateDot()
    {
        StatusDot.Fill = ViewModel.Mode switch
        {
            SyncMode.Off => Brushes.Gray,
            _ when ViewModel.IsConnected => Green,
            _ => Amber,
        };
        NoPeers.Visibility = ViewModel.Peers.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }
}
