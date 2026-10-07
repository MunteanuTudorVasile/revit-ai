using System.Windows;
using System.Windows.Controls;

namespace RevitAi.Addin.UI;

public partial class AssistantPane : Page
{
    private readonly AssistantViewModel _viewModel;

    public AssistantPane(AssistantViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    // PasswordBox.Password cannot be data-bound, by design; hand it over once and clear it.
    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveApiKey(KeyBox.Password);
        KeyBox.Clear();
    }
}
