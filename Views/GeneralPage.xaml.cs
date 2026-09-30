using System.Windows;
using System.Windows.Controls;

namespace Apathy.Views;

public partial class GeneralPage : UserControl
{
    public GeneralPage()
    {
        InitializeComponent();
    }

    private void OpenEditor_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.OpenEditor();

    private void OpenLua_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.OpenScripts();
}
