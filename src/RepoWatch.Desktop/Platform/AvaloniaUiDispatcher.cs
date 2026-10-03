using Avalonia.Threading;
using RepoWatch.Desktop.Presentation;

namespace RepoWatch.Desktop.Platform;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
