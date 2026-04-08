using MasterApp.Diagnostics;
using MasterApp.Hosting;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace MasterApp.Tray;

public sealed class MasterAppApplicationContext : ApplicationContext
{
    private readonly MasterAppRuntime _runtime;
    private readonly FileLogManager _log;
    private readonly NotifyIcon _notifyIcon;
    private int _shutdownStarted;

    public MasterAppApplicationContext(MasterAppRuntime runtime, FileLogManager log)
    {
        _runtime = runtime;
        _log = log;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => SafeRun("Open Dashboard", _runtime.OpenDashboard));
        menu.Items.Add("Open Public", null, (_, _) => SafeRun("Open Public", _runtime.OpenPublic));
        menu.Items.Add("Open Phone QR", null, (_, _) => SafeRun("Open Phone QR", _runtime.OpenPhoneQr));
        menu.Items.Add("Open Logs Folder", null, (_, _) => SafeRun("Open Logs Folder", _runtime.OpenLogsFolder));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart Tunnel", null, (_, _) => SafeRunResult("Restart Tunnel", _runtime.RestartTunnel));
        menu.Items.Add("Stop Tunnel", null, (_, _) => SafeRunResult("Stop Tunnel", _runtime.StopTunnel));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitApplication());

        _notifyIcon = new NotifyIcon
        {
            Text = "MasterApp",
            Icon = MasterAppBrand.CreateTrayIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => SafeRun("Open Dashboard", _runtime.OpenDashboard);

        _log.Info("Tray", "Tray icon initialized.");
    }

    private void ExitApplication()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        try
        {
            var current = Process.GetCurrentProcess();
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping 127.0.0.1 -n 3 > nul & taskkill /PID {current.Id} /T /F > nul 2>nul",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            _log.Info("Tray", $"Forced shutdown scheduled for PID={current.Id}.");
        }
        catch (Exception ex)
        {
            _log.Error("Tray", "Forced process shutdown scheduling failed.", ex);
        }

        try
        {
            _log.Info("Tray", "Quit clicked.");
            _notifyIcon.Visible = false;
            _runtime.Dispose();
        }
        catch (Exception ex)
        {
            _log.Error("Tray", "Quit handler failed.", ex);
        }
        finally
        {
            try
            {
                ExitThread();
                Application.ExitThread();
                Application.Exit();
            }
            catch (Exception ex)
            {
                _log.Error("Tray", "Managed shutdown signaling failed.", ex);
            }

            try
            {
                Environment.Exit(0);
            }
            catch
            {
                // forced shutdown was already scheduled above
            }
        }
    }

    protected override void ExitThreadCore()
    {
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        finally
        {
            base.ExitThreadCore();
        }
    }

    private void SafeRun(string actionName, Action action)
    {
        try
        {
            _log.Info("Tray", $"{actionName} clicked.");
            action();
        }
        catch (Exception ex)
        {
            _log.Error("Tray", $"{actionName} failed.", ex);
            MessageBox.Show(ex.Message, "MasterApp", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SafeRunResult(string actionName, Func<Models.OperationResult> action)
    {
        try
        {
            _log.Info("Tray", $"{actionName} clicked.");
            var result = action();
            if (result.Ok)
            {
                _log.Info("Tray", $"{actionName} succeeded: {result.Message}");
                return;
            }

            _log.Error("Tray", $"{actionName} failed: {result.Message}");
            MessageBox.Show(result.Message, "MasterApp", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _log.Error("Tray", $"{actionName} failed.", ex);
            MessageBox.Show(ex.Message, "MasterApp", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
