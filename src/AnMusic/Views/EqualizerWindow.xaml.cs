using System.Windows;
using System.Windows.Controls;
using AnMusic.ViewModels;

namespace AnMusic;

/// <summary>
/// 均衡器窗口。
/// </summary>
public partial class EqualizerWindow : Window
{
    private readonly EqualizerViewModel _viewModel;

    public EqualizerWindow(EqualizerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.SelectedPreset is not null)
            _viewModel.ApplyPresetCommand.Execute(null);
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetCommand.Execute(null);
    }
}
