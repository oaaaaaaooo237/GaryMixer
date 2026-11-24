using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;

internal static class Installer
{
    private const string Title = "Gary调音台";
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GaryMixer";
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GaryMixer");
    private static readonly string AppPath = Path.Combine(Folder, "GaryMixer.exe");
    private static readonly string SetupPath = Path.Combine(Folder, "GaryMixerSetup.exe");
    private static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Title + ".lnk");
    private const int Cancelled = 1223;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string oldPath, string newPath, int flags);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--package-check")
            {
                using (var payload = Payload())
                using (var sha = SHA256.Create())
                    Console.WriteLine("{0} {1}", payload.Length, BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", ""));
                return 0;
            }
            if (args.Length == 2 && (args[0] == "--install-worker" || args[0] == "--remove-worker"))
            {
                if (!Broker.IsElevated() || args[1] != Broker.Sid)
                    return 7;
                if (args[0] == "--install-worker") Install();
                else Remove();
                return 0;
            }
            bool remove = args.Length == 1 && args[0] == "--uninstall";
            if (args.Length != 0 && !remove) return 2;
            if (!remove && !SupportedGpu())
            {
                MessageBox.Show("未检测到支持调整功率上限的 NVIDIA 显卡，安装未开始。", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 3;
            }
            string action = remove ? "卸载" : "安装";
            string details = remove ? "将移除应用及其后台任务。" :
                "将安装到 " + Folder + "，并注册后台任务。\n\n完成后，调功率和登录时自动应用都无需再确认 UAC。";
            if (MessageBox.Show(details + "\n\n继续时，Windows 会请求一次管理员授权。",
                action + " " + Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return Cancelled;

            var start = new ProcessStartInfo(Application.ExecutablePath, (remove ? "--remove-worker " : "--install-worker ") + Broker.Sid)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.SystemDirectory };
            using (var worker = Process.Start(start))
            {
                worker.WaitForExit();
                if (worker.ExitCode == 7) throw new InvalidOperationException("请使用当前登录账户的管理员授权。");
                if (worker.ExitCode != 0) return worker.ExitCode;
            }
            if (remove)
            {
                if (Settings.StartupRegisteredFor(AppPath)) Settings.RegisterStartup(false);
                if (File.Exists(Shortcut)) File.Delete(Shortcut);
                MessageBox.Show("已卸载。", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                if (!Broker.IsReady()) throw new InvalidOperationException("已安装，但当前账户无法触发功率后台任务。");
                if (!Broker.IsElevated()) VerifyPowerControl();
                string shortcutError = null;
                try { CreateShortcut(); }
                catch (Exception error) { shortcutError = error.Message; }
                string message = "安装完成。免弹窗后台任务已就绪。";
                if (shortcutError != null) message += "\n开始菜单快捷方式创建失败。主程序位于 " + AppPath;
                MessageBox.Show(message, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (!Broker.IsElevated()) Process.Start(new ProcessStartInfo(AppPath) { UseShellExecute = true });
            }
            return 0;
        }
        catch (Win32Exception error)
        {
            if (error.NativeErrorCode == Cancelled) return Cancelled;
            return Report(error);
        }
        catch (Exception error) { return Report(error); }
    }

    private static bool SupportedGpu()
    {
        foreach (var card in Gpu.Read())
            if (card.Adjustable) return true;
        return false;
    }

    private static void VerifyPowerControl()
    {
        var card = Gpu.Read().First(gpu => gpu.Adjustable);
        decimal limit = card.Limit.Value;
        try
        {
            // Reapply the current value so the installation check does not change the user's limit.
            if (limit == decimal.Truncate(limit) && limit >= card.Minimum && limit <= card.Maximum)
                Broker.Apply(card, (int)limit);
            else if (limit == card.Default)
                Broker.Apply(card, null);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("已安装，但普通权限下的免弹窗调节验证失败。 " + error.Message, error);
        }
    }

    private static Stream Payload()
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream("GaryMixer.exe");
    }

    private static void Install()
    {
        EnsureOwner();
        Directory.CreateDirectory(Folder);
        GuardPath(Folder);
        GuardPath(AppPath);
        GuardPath(SetupPath);
        string staged = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".tmp");
        string saved = File.Exists(AppPath) ? staged + ".bak" : null;
        bool installed = false;
        bool taskWasReady = Broker.IsReady();
        try
        {
            using (var source = Payload())
            using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(file);
            if (saved == null) File.Move(staged, AppPath);
            else File.Replace(staged, AppPath, saved);
            installed = true;

            Broker.Enable();
            if (!string.Equals(Application.ExecutablePath, SetupPath, StringComparison.OrdinalIgnoreCase))
                File.Copy(Application.ExecutablePath, SetupPath, true);
            using (var key = Registry.LocalMachine.CreateSubKey(RegistryPath))
            {
                key.SetValue("OwnerSid", Broker.Sid);
                key.SetValue("DisplayName", Title);
                key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
                key.SetValue("InstallLocation", Folder);
                key.SetValue("DisplayIcon", AppPath);
                key.SetValue("UninstallString", "\"" + SetupPath + "\" --uninstall");
                key.SetValue("NoModify", 1);
                key.SetValue("NoRepair", 1);
            }
        }
        catch
        {
            if (!taskWasReady && Broker.IsReady()) Broker.Configure(false);
            if (installed && saved == null) File.Delete(AppPath);
            if (installed && saved != null && File.Exists(saved)) File.Replace(saved, AppPath, null);
            throw;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
            if (saved != null && File.Exists(saved)) File.Delete(saved);
        }
    }

    private static void Remove()
    {
        EnsureOwner();
        GuardPath(Folder);
        GuardPath(AppPath);
        GuardPath(SetupPath);
        if (File.Exists(AppPath)) File.Delete(AppPath);
        Broker.Configure(false);
        Registry.LocalMachine.DeleteSubKey(RegistryPath, false);
        if (!File.Exists(SetupPath)) return;
        if (!string.Equals(Application.ExecutablePath, SetupPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(SetupPath);
            return;
        }
        const int afterRestart = 4;
        if (!MoveFileEx(SetupPath, null, afterRestart))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void EnsureOwner()
    {
        using (var key = Registry.LocalMachine.OpenSubKey(RegistryPath))
        {
            string owner = key == null ? null : key.GetValue("OwnerSid") as string;
            if (owner != null && owner != Broker.Sid) throw new UnauthorizedAccessException("请使用最初安装此应用的 Windows 账户。");
        }
    }

    private static void GuardPath(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("安装目标含有重解析点。");
    }

    private static void CreateShortcut()
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        dynamic link = shell.CreateShortcut(Shortcut);
        link.TargetPath = AppPath;
        link.WorkingDirectory = Folder;
        link.Description = Title;
        link.Save();
    }

    private static int Report(Exception error)
    {
        MessageBox.Show(error.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
    }
}
