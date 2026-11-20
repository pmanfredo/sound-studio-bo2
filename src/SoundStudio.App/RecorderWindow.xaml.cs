using System.Windows;
using System.Windows.Input;
using SoundStudio.App.ViewModels;
using Wpf.Ui.Controls;

namespace SoundStudio.App;

public partial class RecorderWindow : FluentWindow
{
    public RecorderWindow(RecorderViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.RequestClose += (_, _) => Close();
        Loaded += (_, _) => viewModel.Initialize();
        Closed += (_, _) => viewModel.Dispose(); // releases the microphone
        PreviewKeyDown += OnKey;
    }

    public RecorderViewModel ViewModel { get; }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.ComboBox)
            return;
        switch (e.Key)
        {
            case Key.R when ViewModel.CanRecord:
                ViewModel.RecordCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space when ViewModel.IsRecording || ViewModel.IsCountdown:
                ViewModel.StopCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space when ViewModel.IsRecorded:
                ViewModel.PlayPreviewCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
