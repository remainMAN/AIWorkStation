using System.Windows.Controls;
using System.Windows;

namespace AIWorkStation.Views;

public partial class ResultStep : UserControl
{
    public ResultStep() => InitializeComponent();

    private void FinishClicked(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
