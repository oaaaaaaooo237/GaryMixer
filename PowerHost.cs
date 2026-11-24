using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

internal static class PowerHost
{
    private static int Main()
    {
        try
        {
            string script;
            using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("Broker.ps1")))
                script = reader.ReadToEnd();
            string system = Environment.SystemDirectory;
            string powerShell = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            string command = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var options = new ProcessStartInfo(powerShell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + command) {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = system
            };
            using (var worker = Process.Start(options))
            {
                if (worker.WaitForExit(20000)) return worker.ExitCode;
                worker.Kill();
                return 1;
            }
        }
        catch { return 1; }
    }
}
