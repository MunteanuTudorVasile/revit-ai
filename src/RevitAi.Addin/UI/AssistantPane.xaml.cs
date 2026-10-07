using System.Windows.Controls;

namespace RevitAi.Addin.UI;

public partial class AssistantPane : Page
{
    public AssistantPane(AssistantViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
