using System.Text.Json;
using Microsoft.UI.Xaml;
using ModelDesk.Models;
using ModelDesk.Services;

namespace ModelDesk;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTest.Run();
        using var instance = new Mutex(true, "Local\\ModelDesk.WinUI3.Instance", out bool first);
        if (!first && !args.Contains("--validate-ui")) return 0;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            new App(args.Contains("--validate-ui"));
        });
        return Environment.ExitCode;
    }
}

internal static class SelfTest
{
    public static int Run()
    {
        var checks = new List<string>();
        try
        {
            void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
            var settings = new AppSettings(); settings.Validate();
            Check(!settings.FreeComfyMemory && settings.Port == 8080 && settings.ContextSize == 131072, "默认配置及 ComfyUI 安全默认值");
            var parsed = AppSettings.ParseArguments("--alias \"test model\" --temp 1.0");
            Check(parsed.Count == 4 && parsed[1] == "test model", "参数保留含空格的值，不依赖 shell");
            bool rejected = false;
            try { var invalid = settings.Copy(); invalid.ExtraArguments = "--port=9999"; invalid.Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "禁止额外参数覆盖受管理端口");
            var sampler = new SlotTelemetry();
            var first = SlotTelemetry.Parse("[{\"id\":0,\"id_task\":42,\"is_processing\":true,\"n_decoded\":20}]");
            var second = SlotTelemetry.Parse("[{\"id\":0,\"id_task\":42,\"is_processing\":true,\"n_decoded\":50}]");
            Check(sampler.Observe(first, 10) == null && sampler.Observe(second, 12) == 15, "同一任务 token 增量 / 真实间隔采样");
            Check(sampler.Observe(SlotTelemetry.Parse("[{\"id\":0,\"id_task\":43,\"is_processing\":true,\"n_decoded\":5}]"), 13) == null, "任务切换不产生虚假尖峰");
            Check(sampler.Observe(SlotTelemetry.Parse("[{\"id\":0,\"is_processing\":false}]"), 14) == 0, "空闲返回零速率");
            var gpu = GpuMonitor.Parse("0, NVIDIA Test, 35, 52, 10240, 16384");
            Check(gpu.Utilization == 35 && gpu.MemoryTotalMiB == 16384 && gpu.Temperature == 52, "GPU CSV 字段及数值解析");
            using var service = new ModelService(settings);
            Check(!service.HasOwnedProcess && !service.IsStarting, "构造服务不启动模型与轮询");
            Save(new { success = true, mode = "self-test", checks, guiShown = false, modelStarted = false });
            return 0;
        }
        catch (Exception ex) { Save(new { success = false, mode = "self-test", checks, error = ex.ToString(), guiShown = false, modelStarted = false }); return 1; }
    }
    public static void Save(object result)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        string json = JsonSerializer.Serialize(result, options);
        using var document = JsonDocument.Parse(json);
        string mode = document.RootElement.GetProperty("mode").GetString()!;
        File.WriteAllText(Path.Combine(AppSettings.DataDirectory, mode + "-result.json"), json);
    }
}
