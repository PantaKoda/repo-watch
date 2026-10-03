using Avalonia.Controls;
using Avalonia.Input;

namespace RepoWatch.Desktop.Views;

public partial class RepositoriesWindow : Window
{
    public RepositoriesWindow()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
