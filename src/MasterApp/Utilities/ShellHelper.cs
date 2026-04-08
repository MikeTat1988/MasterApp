using System.Diagnostics;

namespace MasterApp.Utilities;

public static class ShellHelper
{
    public static void OpenPath(string pathOrUrl)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = pathOrUrl,
            UseShellExecute = true
        });
    }
}
