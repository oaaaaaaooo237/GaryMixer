using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

internal sealed class Gpu
{
    internal static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    internal static readonly Regex UuidPattern = new Regex(@"\AGPU-[a-fA-F0-9]{8}(?:-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}\z");
    internal readonly string Name, Uuid, Fan;
    internal readonly decimal? Draw, Limit, Default, Minimum, Maximum, Effective, FanPercent, Used, Total, Utilization, Temperature;
    internal bool Adjustable { get { return Limit.HasValue && Minimum > 0 && Minimum < Maximum && Default >= Minimum && Default <= Maximum; } }

    private Gpu(string[] row)
    {
        Name = row[0]; Uuid = row[1];
        Draw = Number(row[2]); Limit = Number(row[3]); Effective = Number(row[4]);
        Default = Number(row[5]); Minimum = Number(row[6]); Maximum = Number(row[7]);
        FanPercent = Number(row[8]); Fan = FanReader.Read(Uuid);
        Used = Number(row[9]); Total = Number(row[10]); Utilization = Number(row[11]); Temperature = Number(row[12]);
    }

    private static decimal? Number(string value)
    {
        decimal result;
        return decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, Invariant, out result)
            ? (decimal?)result : null;
    }

    internal static List<Gpu> Read()
    {
        const string query = "--query-gpu=name,uuid,power.draw,power.limit,enforced.power.limit,power.default_limit,power.min_limit,power.max_limit,fan.speed,memory.used,memory.total,utilization.gpu,temperature.gpu --format=csv,noheader,nounits";
        var output = RunSmi(query);
        var cards = new List<Gpu>();
        foreach (string line in output.Split(new[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] row = line.Split(',').Select(value => value.Trim()).ToArray();
            if (row.Length == 13 && UuidPattern.IsMatch(row[1])) cards.Add(new Gpu(row));
        }
        if (cards.Count == 0) throw new InvalidOperationException("未读到 NVIDIA 显卡数据。");
        return cards;
    }

    private static string RunSmi(string arguments)
    {
        string path = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(path))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvidia-smi.exe");
        var options = new ProcessStartInfo(path, arguments) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var process = Process.Start(options))
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2500))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                throw new TimeoutException("读取 NVIDIA 驱动超时。");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException("无法读取 NVIDIA 驱动。 " + (output.Result + error.Result).Trim());
            return output.Result;
        }
    }

    // Some drivers expose per-fan RPM only via NVML.
    private static class FanReader
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FanSpeed { internal uint Version, Fan, Rpm; }

        [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
        [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetHandleByUUID(string uuid, out IntPtr gpu);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetNumFans(IntPtr gpu, out uint count);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetFanSpeedRPM(IntPtr gpu, ref FanSpeed speed);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

        private static bool initialized;
        private static bool unavailable;

        internal static string Read(string uuid)
        {
            const string fallback = null;
            if (unavailable) return fallback;
            try
            {
                if (!initialized)
                {
                    const uint System32Search = 0x800;
                    if (LoadLibraryEx("nvml.dll", IntPtr.Zero, System32Search) == IntPtr.Zero) { unavailable = true; return fallback; }
                    if (nvmlInit_v2() != 0) return fallback;
                    initialized = true;
                }
                IntPtr gpu;
                uint count;
                if (nvmlDeviceGetHandleByUUID(uuid, out gpu) != 0 || nvmlDeviceGetNumFans(gpu, out count) != 0) return fallback;
                var speeds = new List<string>();
                for (uint index = 0; index < Math.Min(count, 8); index++)
                {
                    var speed = new FanSpeed { Version = 12 | (1u << 24), Fan = index };
                    if (nvmlDeviceGetFanSpeedRPM(gpu, ref speed) == 0) speeds.Add(speed.Rpm.ToString(Invariant));
                }
                return speeds.Count > 0 ? string.Join(" / ", speeds) + " RPM" : fallback;
            }
            catch (DllNotFoundException) { unavailable = true; return fallback; }
            catch (EntryPointNotFoundException) { unavailable = true; return fallback; }
        }
    }
}
