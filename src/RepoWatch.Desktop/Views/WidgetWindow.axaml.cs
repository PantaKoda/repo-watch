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
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>() is { DataContext: RepositoryRowViewModel row })
        {
            OpenDetails(row);
        }
    }

    private void OnRepositoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && RepositoryList.SelectedItem is RepositoryRowViewModel row)
        {
            OpenDetails(row);
            e.Handled = true;
        }
    }

    private void OpenDetails(RepositoryRowViewModel row)
    {
        ViewModel?.ShowRepositoryCommand.Execute(row);
        BackButton.Focus(NavigationMethod.Tab);
    }
}
