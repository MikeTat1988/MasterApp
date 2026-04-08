using MasterApp.Bootstrap;
using MasterApp.Diagnostics;
using MasterApp.Hosting;
using MasterApp.Tray;
using System.Windows.Forms;

namespace MasterApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        BootstrapContext? bootstrap = null;
        MasterAppRuntime? runtime = null;

        try
        {
            bootstrap = Bootstrapper.Initialize();
            bootstrap.Log.Info("Program", "Bootstrap complete.");

            runtime = new MasterAppRuntime(bootstrap);
            runtime.Start();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MasterAppApplicationContext(runtime, bootstrap.Log));
        }
        catch (Exception ex)
        {
            bootstrap?.Log.Error("Program", "Fatal startup error.", ex);

            try
            {
                MessageBox.Show(
                    $"MasterApp failed to start.\n\n{ex.Message}\n\nCheck logs in:\n{bootstrap?.Paths.LogsDirectory ?? "(logs unavailable)"}",
                    "MasterApp",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // ignore secondary UI errors
            }
        }
        finally
        {
            try
            {
                runtime?.Dispose();
            }
            catch (Exception ex)
            {
                bootstrap?.Log.Error("Program", "Error during final disposal.", ex);
            }

            bootstrap?.Log.Info("Program", "Process exit.");
        }
    }
}
