using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ModelDesk.Services;

public sealed record GpuSnapshot(string Name, double? Utilization, double? Temperature, double? MemoryUsedMiB, double? MemoryTotalMiB, string Status);

public sealed class GpuMonitor : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private Task? loop;
    public event Action<GpuSnapshot>? Updated;
    public void Start() { loop ??= Run(); }
    public static GpuSnapshot Parse(string line)
    {
        string[] fields = line.Trim().Split(',');
        if (fields.Length != 6) throw new FormatException("GPU 数据字段不完整。");
        double? Number(int i, double max = double.MaxValue)
        {
            return double.TryParse(fields[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) && n >= 0 && n <= max ? n : null;
        }
        return new(fields[1].Trim(), Number(2, 100), Number(3, 150), Number(4), Number(5), "NVIDIA 驱动 · GPU 0 · 整卡利用率（非模型独占）");
    }
    public static async Task<GpuSnapshot> ReadAsync(CancellationToken token)
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (!File.Exists(exe)) return new("未检测到 NVIDIA 监控工具", null, null, null, null, "nvidia-smi 不可用；GPU 温度和占用暂不可读。");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--id=0");
        info.ArgumentList.Add("--query-gpu=index,name,utilization.gpu,temperature.gpu,memory.used,memory.total");
        info.ArgumentList.Add("--format=csv,noheader,nounits");
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        if (!process.Start()) throw new IOException("无法读取 NVIDIA 驱动监控。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string text = await output;
            await error;
            if (process.ExitCode != 0) throw new IOException("NVIDIA 监控命令失败，退出码 " + process.ExitCode);
            return Parse(text);
        }
        catch (OperationCanceledException)
        {
            // 只清理本次创建的短生命周期监控命令，不操作模型进程。
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
    }
    private async Task Run()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                GpuSnapshot snapshot;
                try { snapshot = await ReadAsync(lifetime.Token); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { snapshot = new("GPU 0", null, null, null, null, "GPU 驱动读取超时，保留缺测。"); }
                catch (Exception ex) { snapshot = new("GPU 0", null, null, null, null, "GPU 监控不可用：" + ex.Message); }
                if (!lifetime.IsCancellationRequested) Updated?.Invoke(snapshot);
            } while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose() => lifetime.Cancel();
}
