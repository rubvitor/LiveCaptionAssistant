using System.Diagnostics;
using LiveCaptionAssistant.Core.Platform;

namespace LiveCaptionAssistant.Infrastructure.Platform;

internal sealed class ShellBrowserLauncher : IBrowserLauncher
{
    public void Open(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("Only http and https addresses can be opened.", nameof(address));
        }

        using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }
}
