using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RepoWatch.Desktop.ViewModels;

namespace RepoWatch.Desktop.Views;

public partial class WidgetWindow : Window
{
    public WidgetWindow()
    {
        InitializeComponent();

        // Lists stay put while the pointer is over them or the keyboard is in them.
        Body.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsPointerOverProperty || e.Property == IsKeyboardFocusWithinProperty)
            {
                UpdateInteraction();
            }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsActiveProperty)
            {
                UpdateInteraction();
            }
        };

        RepositoryList.AddHandler(TappedEvent, OnRepositoryTapped);
        RepositoryList.AddHandler(KeyDownEvent, OnRepositoryKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
            }
        };
    }

    // Returning from details puts keyboard focus back on the selected repository.
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetViewModel.ShowDetails) && ViewModel is { ShowDetails: false } && IsActive)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var item = RepositoryList.SelectedItem is { } selected ? RepositoryList.ContainerFromItem(selected) : null;
                (item ?? RepositoryList).Focus(NavigationMethod.Tab);
            });
        }
    }

    private WidgetViewModel? ViewModel => DataContext as WidgetViewModel;

    private void UpdateInteraction()
    {
        if (ViewModel is { } vm)
        {
            vm.IsInteracting = Body.IsPointerOver || (IsActive && Body.IsKeyboardFocusWithin);
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && ViewModel is { PositionLocked: false })
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>The footer grip and the frame's edges and corners; each names its edge in <c>Tag</c>.</summary>
    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && sender is Control { Tag: string tag } && Enum.TryParse<WindowEdge>(tag, out var edge))
        {
            BeginResizeDrag(edge, e);
            e.Handled = true;
        }
    }

    private void OnRepositoryTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<ListBoxItem>() is not { DataContext: RepositoryRowViewModel row })
        {
            return;
        }

        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            // The row's own button (open on GitHub) did its job; select that row so the keyboard acts on it next.
            RepositoryList.SelectedItem = row;
            return;
        }

        OpenDetails(row);
    }

    private void OnRepositoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && RepositoryList.SelectedItem is RepositoryRowViewModel selected)
        {
            ViewModel?.OpenOnGitHubCommand.Execute(selected); // Ctrl+Enter: the repository in the browser
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Space && RepositoryList.SelectedItem is RepositoryRowViewModel row)
        {
            OpenDetails(row);
            e.Handled = true;
        }
    }

    // Ctrl+F jumps to the name filter; Down from the filter moves into the list.
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && ViewModel is { ShowToolbar: true })
        {
            SearchBox.Focus(NavigationMethod.Tab);
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && RepositoryList.ItemCount > 0)
        {
            RepositoryList.SelectedIndex = Math.Max(0, RepositoryList.SelectedIndex);
            (RepositoryList.ContainerFromIndex(RepositoryList.SelectedIndex) ?? RepositoryList).Focus(NavigationMethod.Tab);
            e.Handled = true;
        }
    }

    private void OpenDetails(RepositoryRowViewModel row)
    {
        ViewModel?.ShowRepositoryCommand.Execute(row);
        BackButton.Focus(NavigationMethod.Tab);
    }
}
