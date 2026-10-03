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

    private void OnResizeGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.SouthEast, e);
        }
    }

    private void OnRepositoryTapped(object? sender, TappedEventArgs e)
    {
        // A button inside the row (open on GitHub) does its own thing; only the row itself opens details.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is null
            && source.FindAncestorOfType<ListBoxItem>() is { DataContext: RepositoryRowViewModel row })
        {
            OpenDetails(row);
        }
    }

    private void OnRepositoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control && RepositoryList.SelectedItem is RepositoryRowViewModel selected)
        {
            selected.OpenOnGitHubCommand.Execute(null); // Ctrl+Enter: the repository in the browser
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None && RepositoryList.SelectedItem is RepositoryRowViewModel row)
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
