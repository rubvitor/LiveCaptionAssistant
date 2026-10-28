using System.Windows;
using LiveCaptionAssistant.App.ViewModels;

namespace LiveCaptionAssistant.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, saved) => DialogResult = saved;
    }
}
