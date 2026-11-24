using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Gary调音台")]
[assembly: AssemblyDescription("NVIDIA GPU power limits and monitoring")]
[assembly: AssemblyProduct("Gary调音台")]
[assembly: AssemblyVersion("1.0.1.0")]

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] != "--startup") return Command(args);
        bool first;
        using (var instance = new Mutex(true, @"Local\GpuPowerTray.Window." + Broker.Sid, out first))
        using (var open = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\GpuPowerTray.Open." + Broker.Sid))
        {
            if (!first)
            {
                if (args.Length == 0) open.Set();
                return 0;
            }
            MainWindow.Run(args.Length > 0, open);
        }
        return 0;
    }

    private static int Command(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--read")
            {
                foreach (Gpu gpu in Gpu.Read())
                    Console.WriteLine(string.Join(",", new object[] { gpu.Name, gpu.Uuid, gpu.Draw, gpu.Limit, gpu.Effective,
                        gpu.Minimum, gpu.Maximum, gpu.Default, gpu.FanPercent, gpu.Used, gpu.Total,
                        gpu.Utilization, gpu.Temperature, gpu.Fan, gpu.Adjustable }.Select(value => Convert.ToString(value, Gpu.Invariant))));
            }
            else if (args.Length == 1 && args[0] == "--enable") Broker.Enable();
            else if (args.Length == 1 && args[0] == "--disable") Broker.Configure(false);
            else if ((args.Length == 2 || args.Length == 3 || args.Length == 4) && args[0] == "--preview")
            {
                using (var open = new EventWaitHandle(false, EventResetMode.AutoReset))
                    MainWindow.Run(false, open, args[1], args.Length >= 3 ? int.Parse(args[2], Gpu.Invariant) : 96,
                        args.Length == 4 && args[3] == "--startup-panel");
            }
            else if ((args.Length == 2 || args.Length == 3) && args[0] == "--set")
            {
                var cards = Gpu.Read().Where(gpu => gpu.Adjustable).ToList();
                var card = args.Length == 3 ? cards.SingleOrDefault(gpu => gpu.Uuid == args[2]) : cards.SingleOrDefault();
                if (card == null) throw new InvalidOperationException("请指定显卡 UUID。");
                int watts;
                int? target = null;
                if (args[1] != "default")
                {
                    if (!int.TryParse(args[1], NumberStyles.None, Gpu.Invariant, out watts)) throw new ArgumentException("Expected watts or default.");
                    target = watts;
                }
                Console.WriteLine("{0} W", Broker.Apply(card, target));
            }
            else throw new ArgumentException("Usage: --read | --set <watts|default> [GPU-uuid] | --enable | --disable");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}

internal static class Settings
{
    internal const string Key = @"Software\GpuPowerTray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "Gary调音台";
    private static string StartupCommand(string path) { return "\"" + path + "\" --startup"; }

    internal static string Read(string name)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(Key))
            return key == null ? null : key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    internal static void Write(string name, string value)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(Key))
        {
            if (value == null) key.DeleteValue(name, false);
            else key.SetValue(name, value, RegistryValueKind.String);
        }
    }

    internal static bool StartupRegistered()
    {
        return StartupRegisteredFor(Application.ExecutablePath);
    }

    internal static bool StartupRegisteredFor(string path)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && string.Equals(key.GetValue(StartupName) as string, StartupCommand(path), StringComparison.OrdinalIgnoreCase);
    }

    internal static void RegisterStartup(bool enabled)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) key.SetValue(StartupName, StartupCommand(Application.ExecutablePath), RegistryValueKind.String);
            // Task Manager separately controls whether this registered entry is enabled.
            else key.DeleteValue(StartupName, false);
        }
    }
}

internal static class Broker
{
    internal static readonly string Sid = GetSid();
    private static readonly string TaskName = "GpuPowerTray-v1-" + Sid;
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    private static readonly string HostFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GaryMixerPowerHost");
    private static readonly string HostPath = Path.Combine(HostFolder, "GaryMixerPowerHost.exe");
    private const string PowerShellFlags = "-NoLogo -NoProfile -NonInteractive -EncodedCommand ";
    private const int Running = 0x41301;

    private static string GetSid()
    {
        using (var identity = WindowsIdentity.GetCurrent()) return identity.User.Value;
    }

    private static string Encoded(string script)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    private static byte[] ReadHost()
    {
        using (var stream = typeof(Broker).Assembly.GetManifestResourceStream("GaryMixerPowerHost.exe"))
        using (var output = new MemoryStream())
        {
            stream.CopyTo(output);
            return output.ToArray();
        }
    }

    private static dynamic Scheduler()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", true);
        dynamic service = Activator.CreateInstance(type);
        service.Connect();
        return service;
    }

    internal static bool IsReady()
    {
        try { GetTask(); return true; }
        catch (COMException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static dynamic GetTask()
    {
        dynamic task = Scheduler().GetFolder("\\").GetTask(TaskName);
        dynamic definition = task.Definition;
        dynamic actions = definition.Actions;
        if (!(bool)task.Enabled || (int)actions.Count != 1 || (int)definition.Principal.RunLevel != 1)
            throw new InvalidOperationException("后台权限任务未就绪。");
        dynamic action = actions.Item(1);
        if (!string.Equals((string)action.Path, HostPath, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty((string)action.Arguments) || !File.Exists(HostPath))
            throw new InvalidOperationException("后台权限任务需要重新配置。");
        using (var sha = SHA256.Create())
        using (var host = File.OpenRead(HostPath))
            if (!sha.ComputeHash(host).SequenceEqual(sha.ComputeHash(ReadHost())))
                throw new InvalidOperationException("后台程序需要更新。");
        return task;
    }

    internal static void Enable() { Configure(true); }

    internal static void Configure(bool enabled)
    {
        if (enabled && IsReady()) return;
        string location = "$hostFolder = '" + HostFolder.Replace("'", "''") + "'\n" +
            "$hostFile = '" + HostPath.Replace("'", "''") + "'\n" +
            "foreach ($path in $hostFolder,$hostFile) { if (([IO.Directory]::Exists($path) -or [IO.File]::Exists($path)) -and (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { exit 9 } }\n";
        string command = enabled ?
            "[void][IO.Directory]::CreateDirectory($hostFolder)\n" +
            "$staged = [IO.Path]::Combine($hostFolder, [guid]::NewGuid().ToString('N') + '.tmp')\n" +
            "try {\n" +
            "  [IO.File]::WriteAllBytes($staged, [Convert]::FromBase64String('" + Convert.ToBase64String(ReadHost()) + "'))\n" +
            "  if ([IO.File]::Exists($hostFile)) { [IO.File]::Replace($staged, $hostFile, [NullString]::Value) } else { [IO.File]::Move($staged, $hostFile) }\n" +
            "} finally { if ([IO.File]::Exists($staged)) { [IO.File]::Delete($staged) } }\n" +
            "$definition = $scheduler.NewTask(0)\n" +
            "$definition.RegistrationInfo.Description = 'Gary GPU power control. Sets only driver-supported GPU power limits.'\n" +
            "$definition.Principal.UserId = $user\n$definition.Principal.LogonType = 3\n$definition.Principal.RunLevel = 1\n" +
            "$definition.Settings.DisallowStartIfOnBatteries = $false\n$definition.Settings.StopIfGoingOnBatteries = $false\n" +
            "$definition.Settings.ExecutionTimeLimit = 'PT25S'\n$definition.Settings.MultipleInstances = 2\n" +
            "$action = $definition.Actions.Create(0)\n$action.Path = $hostFile\n" +
            "$action.WorkingDirectory = [Environment]::SystemDirectory\n" +
            "try { $null = $folder.RegisterTaskDefinition('" + TaskName + "', $definition, 6, $user, $null, 3, $null) } catch { exit 8 }" :
            "try { $folder.DeleteTask('" + TaskName + "', 0) } catch { if ($_.Exception.HResult -ne -2147024894) { throw } }\n" +
            "if ([IO.File]::Exists($hostFile)) {\n" +
            "  $hostInUse = $false\n" +
            "  foreach ($item in $folder.GetTasks(1)) { if ($item.Name -like 'GpuPowerTray-v1-*') { foreach ($registeredAction in $item.Definition.Actions) { if ($registeredAction.Path -eq $hostFile) { $hostInUse = $true } } } }\n" +
            "  if (!$hostInUse) { [IO.File]::Delete($hostFile) }\n" +
            "}";
        string script = "$ErrorActionPreference = 'Stop'\n" +
            "$user = '" + Sid + "'\nif ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $user) { exit 7 }\n" +
            "$env:Path = [Environment]::SystemDirectory\n" +
            "$env:PSModulePath = [IO.Path]::Combine([Environment]::SystemDirectory, 'WindowsPowerShell\\v1.0\\Modules')\n" +
            "$scheduler = New-Object -ComObject Schedule.Service\n$scheduler.Connect()\n$folder = $scheduler.GetFolder('\\')\n" + location + command;
        bool elevated = IsElevated();
        var options = new ProcessStartInfo(PowerShell, PowerShellFlags + Encoded(script)) {
            UseShellExecute = !elevated, Verb = elevated ? "" : "runas", WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = elevated,
            WorkingDirectory = Environment.SystemDirectory
        };
        try
        {
            using (var process = Process.Start(options))
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("后台权限配置失败（" + process.ExitCode + "）。请使用当前 Windows 账户的管理员权限。");
            }
        }
        catch (Win32Exception error)
        {
            if (error.NativeErrorCode == 1223) throw new InvalidOperationException("已取消管理员授权。");
            throw;
        }
        if (enabled && !IsReady()) throw new InvalidOperationException("Windows 已注册任务，但当前会话无法触发它。");
    }

    internal static bool IsElevated()
    {
        using (var identity = WindowsIdentity.GetCurrent())
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static decimal Apply(Gpu gpu, int? watts)
    {
        decimal? expected = watts.HasValue ? watts.Value : gpu.Default;
        if (!gpu.Adjustable || !expected.HasValue || expected < gpu.Minimum || expected > gpu.Maximum)
            throw new ArgumentOutOfRangeException("watts", "目标瓦数超出驱动允许范围。");

        using (var gate = new Mutex(false, @"Global\GpuPowerTray.Apply." + Sid))
        {
            try { if (!gate.WaitOne(0)) throw new InvalidOperationException("正在设置功率，请稍后再试。"); }
            catch (AbandonedMutexException) { }
            try
            {
                dynamic task;
                try { task = GetTask(); }
                catch (COMException) { throw new InvalidOperationException("请先启用免弹窗设置。"); }
                catch (UnauthorizedAccessException) { throw new InvalidOperationException("当前会话无法触发后台任务。"); }
                catch (FileNotFoundException) { throw new InvalidOperationException("请先启用免弹窗设置。"); }
                catch (DirectoryNotFoundException) { throw new InvalidOperationException("当前会话无法访问 Windows 任务计划程序。"); }
                if ((int)task.GetInstances(0).Count != 0) throw new InvalidOperationException("后台任务正在运行，请稍后再试。");
                string value = watts.HasValue ? watts.Value.ToString(Gpu.Invariant) : "default";
                Settings.Write("Request", Guid.NewGuid().ToString("N") + "|" + gpu.Uuid + "|" + value);
                try
                {
                    task.Run(null);
                    var clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 30000)
                    {
                        Thread.Sleep(150);
                        if ((int)task.GetInstances(0).Count != 0) continue;
                        int result = (int)task.LastTaskResult;
                        if (result == Running) continue;
                        if (result != 0) throw new InvalidOperationException(ApplyError(result));
                        var actual = Gpu.Read().FirstOrDefault(card => card.Uuid == gpu.Uuid);
                        if (actual == null || actual.Limit != expected)
                            throw new InvalidOperationException("读回的功率上限与目标不符。");
                        return actual.Limit.Value;
                    }
                    throw new TimeoutException("后台任务超时。请查看当前功率上限。");
                }
                finally { Settings.Write("Request", null); }
            }
            finally { gate.ReleaseMutex(); }
        }
    }

    private static string ApplyError(int code)
    {
        switch (code)
        {
            case 2: return "后台任务未收到有效的功率请求。";
            case 3: return "后台任务无法读取驱动功率范围。";
            case 4: return "目标瓦数超出驱动允许范围。";
            case 5: return "NVIDIA 驱动拒绝设置功率上限。";
            case 6: return "驱动读回的功率上限与目标不符。";
            default: return "后台任务失败（0x" + code.ToString("X", Gpu.Invariant) + "）。";
        }
    }
}
