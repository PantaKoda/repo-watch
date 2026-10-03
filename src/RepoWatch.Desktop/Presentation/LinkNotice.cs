using CommunityToolkit.Mvvm.ComponentModel;
using RepoWatch.Core.Platform;

namespace RepoWatch.Desktop.Presentation;

/// <summary>
/// Opens links through the validated browser adapter and keeps a short message when a link was
/// refused or the browser could not be launched, so a link button never fails silently.
/// The message clears on the next successful open.
/// </summary>
public sealed partial class LinkNotice(IExternalBrowser browser) : ObservableObject, IExternalBrowser
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    public bool HasNotice => Notice is not null;

    public async Task<LinkOpenResult> OpenAsync(Uri url)
    {
        var result = await browser.OpenAsync(url);
        Notice = Describe(result);
        return result;
    }

    public static string? Describe(LinkOpenResult result) => result switch
    {
        LinkOpenResult.Refused => "That link isn't on GitHub, so it wasn't opened.",
        LinkOpenResult.Failed => "Couldn't open your web browser.",
        _ => null,
    };
}
