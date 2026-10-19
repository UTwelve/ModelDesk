using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ModelDesk.Models;

namespace ModelDesk.Services;

public sealed record Telemetry(string State, bool Owned, bool Busy, bool External, double? Generation, double? Prompt, int? ActiveSlots, string Health, string Slots, string Metrics, string Detail, PromptProgress? Progress = null, DateTimeOffset? GenerationSampleAt = null);

public sealed class ModelService : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    // 本地模型监控请求绕过系统代理。
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim action = new(1, 1);
    private readonly SlotTelemetry slotTelemetry = new();
    private readonly PromptProgressTracker promptProgress = new();
    private Task<ProbeResult>? endpointProbe;
    private DateTimeOffset nextSlotsProbe;
    private DateTimeOffset lastSlotsProbe;
    private DateTimeOffset nextMetricsProbe;
    private int slotsFailures;
    private int monitoringEpoch;
    private bool healthWasReady;
    private string slotsLabel = "等待探测";
    private string metricsLabel = "等待探测";
    private sealed record ProbeResult(int Epoch, DateTimeOffset RequestedAt, DateTimeOffset ReceivedAt, double MonotonicSeconds, (HttpStatusCode? Code, string Body, string Label) Slots, (HttpStatusCode? Code, string Body, string Label)? Metrics);
    private readonly object logLock = new();
    private readonly Queue<string> logs = new();
    private readonly string logPath = Path.Combine(AppSettings.DataDirectory, "logs", "ModelDesk.log");
    private Process? owned;
    private AppSettings settings;
    private AppSettings? runningSettings;
    private bool starting;
    private bool failed;
    private bool disposed;
    private string failureDetail = "";
    private DateTime startedAt;
    private (double Tokens, double Seconds)? previousGeneration;
    private (double Tokens, double Seconds)? previousPrompt;
    private double? logGeneration;
    private double? logPrompt;
    private DateTime lastGenerationLog;
    private DateTime lastPromptLog;
    private bool logWarningSent;
    public event Action<Telemetry>? Updated;
    public event Action<string>? LogAdded;
    public bool HasOwnedProcess { get { try { return owned is { HasExited: false }; } catch { return false; } } }
    public bool IsStarting => starting;
    public string LogText { get { lock (logLock) return string.Join(Environment.NewLine, logs); } }
    public string LogDirectory => Path.GetDirectoryName(logPath)!;
    public ModelService(AppSettings config) { settings = config.Copy(); }
    public void UpdateSettings(AppSettings config) { settings = config.Copy(); ResetMonitoring(); }
    private void ResetMonitoring()
    {
        Interlocked.Increment(ref monitoringEpoch);
        previousGeneration = null; previousPrompt = null; slotTelemetry.Reset(); promptProgress.Reset();
        lock (logLock) { logGeneration = null; logPrompt = null; lastGenerationLog = default; lastPromptLog = default; }
        slotsFailures = 0; nextSlotsProbe = default; lastSlotsProbe = default; nextMetricsProbe = default;
        healthWasReady = false; slotsLabel = "等待探测"; metricsLabel = "等待探测";
    }
    public void StartPolling() { _ = PollLoop(); }

    public async Task StartAsync()
    {
        if (!await action.WaitAsync(0)) throw new InvalidOperationException("已有模型操作正在进行。");
        try
        {
            if (HasOwnedProcess || starting) throw new InvalidOperationException("模型已经启动或正在启动。");
            AppSettings config = settings.Copy();
            config.Validate();
            if (!File.Exists(config.ServerPath)) throw new FileNotFoundException("找不到 llama-server.exe。", config.ServerPath);
            if (!File.Exists(config.ModelPath)) throw new FileNotFoundException("找不到 GGUF 模型文件。", config.ModelPath);
            if (!string.IsNullOrWhiteSpace(config.MmprojPath) && !File.Exists(config.MmprojPath)) throw new FileNotFoundException("找不到 mmproj 文件；不使用视觉时可在设置中清空。", config.MmprojPath);
            if (IsPortOccupied(config.Port)) throw new InvalidOperationException($"端口 {config.Port} 已被监听。为避免接管外部服务，禁止启动，请更换端口或检查原服务。");
            ResetMonitoring(); healthWasReady = false;
            starting = true;
            failed = false;
            failureDetail = "";
            startedAt = DateTime.UtcNow;
            if (config.FreeComfyMemory)
            {
                AppendLog("已手动启用 ComfyUI 释放显存，正在请求本机 /api/free。");
                try
                {
                    using var body = new StringContent("{\"unload_models\":true,\"free_memory\":true}", System.Text.Encoding.UTF8, "application/json");
                    using var response = await http.PostAsync("http://127.0.0.1:8188/api/free", body, lifetime.Token);
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + (int)response.StatusCode);
                    AppendLog("ComfyUI 释放显存请求成功。");
                }
                catch (Exception ex) { throw new InvalidOperationException("ComfyUI 释放显存请求失败，未启动模型。可取消该选项后重试。" + ex.Message, ex); }
            }
            var info = new ProcessStartInfo(config.ServerPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(config.ServerPath))! };
            void Add(string key, string value) { info.ArgumentList.Add(key); info.ArgumentList.Add(value); }
            Add("--model", config.ModelPath);
            if (!string.IsNullOrWhiteSpace(config.MmprojPath)) Add("--mmproj", config.MmprojPath);
            Add("--host", config.Host); Add("--port", config.Port.ToString(CultureInfo.InvariantCulture));
            Add("--ctx-size", config.ContextSize.ToString(CultureInfo.InvariantCulture)); Add("--n-predict", config.PredictTokens.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--metrics"); info.ArgumentList.Add("--slots");
            foreach (string arg in AppSettings.ParseArguments(config.ExtraArguments)) info.ArgumentList.Add(arg);
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null && ReferenceEquals(owned, process)) { AppendLog(e.Data); if (HasOwnedProcess) ObserveLog(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null && ReferenceEquals(owned, process)) { AppendLog(e.Data); if (HasOwnedProcess) ObserveLog(e.Data); } };
            process.Exited += (_, _) =>
            {
                if (disposed || !ReferenceEquals(owned, process)) return;
                int code = process.ExitCode;
                ResetMonitoring();
                starting = false;
                if (code != 0) { failed = true; failureDetail = $"llama-server 退出码 {code}。请检查日志、模型路径以及 --metrics / --slots 等参数是否受当前版本支持。"; }
                AppendLog($"自有进程已退出，退出码 {code}。");
            };
            owned?.Dispose();
            owned = process;
            runningSettings = config;
            AppendLog("启动 llama-server（不使用 shell）；已添加 --metrics 与 --slots。");
            if (!process.Start()) throw new InvalidOperationException("操作系统未能启动进程。");
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
        catch (Exception ex) { starting = false; failed = true; failureDetail = ex.Message; AppendLog("启动失败：" + ex.Message); throw; }
        finally { action.Release(); }
    }
    public async Task StopAsync()
    {
        if (!await action.WaitAsync(0)) throw new InvalidOperationException("已有模型操作正在进行。");
        try
        {
            var process = owned;
            ResetMonitoring();
            if (process == null || process.HasExited) return;
            AppendLog("用户确认关闭自有模型进程树。");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            failed = false; failureDetail = ""; starting = false; runningSettings = null;
        }
        finally { action.Release(); }
    }
    public static bool IsPortOccupied(int port) => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port);
    private async Task PollLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do { try { await PollOnce(); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; } catch (Exception ex) { AppendLog("监控异常：" + ex.Message); } }
            while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        catch (OperationCanceledException) { }
    }
    private string BaseUrl(AppSettings config)
    {
        string host = config.Host == "0.0.0.0" ? "127.0.0.1" : config.Host == "::" ? "::1" : config.Host;
        if (host.Contains(':')) host = "[" + host + "]";
        return $"http://{host}:{config.Port}";
    }
    private async Task<(HttpStatusCode? Code, string Body, string Label)> Fetch(string url)
    {
        try { using var response = await http.GetAsync(url, lifetime.Token); string body = await response.Content.ReadAsStringAsync(lifetime.Token); return (response.StatusCode, body, response.IsSuccessStatusCode ? "可用" : "HTTP " + (int)response.StatusCode); }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { return (null, "", "超时"); }
        catch (HttpRequestException) { return (null, "", "未连接"); }
    }
    private async Task<ProbeResult> ProbeEndpoints(string root, int epoch, DateTimeOffset requestedAt, bool metricsDue)
    {
        var slots = await Fetch(root + "/slots");
        var receivedAt = DateTimeOffset.UtcNow;
        double monotonicSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        (HttpStatusCode? Code, string Body, string Label)? metrics = null;
        if (slots.Code == HttpStatusCode.OK && metricsDue && epoch == Volatile.Read(ref monitoringEpoch)) metrics = await Fetch(root + "/metrics");
        return new(epoch, requestedAt, receivedAt, monotonicSeconds, slots, metrics);
    }
    private async Task PollOnce()
    {
        int epoch = Volatile.Read(ref monitoringEpoch);
        bool mine = HasOwnedProcess;
        AppSettings config = (mine ? runningSettings : settings)?.Copy() ?? settings.Copy();
        string root = BaseUrl(config);
        var h = await Fetch(root + "/health");
        if (disposed || epoch != Volatile.Read(ref monitoringEpoch)) return;
        var now = DateTimeOffset.UtcNow;
        mine = HasOwnedProcess;
        if (h.Code == HttpStatusCode.OK) healthWasReady = true;
        else if (h.Label != "超时") healthWasReady = false;
        bool ready = h.Code == HttpStatusCode.OK || h.Label == "超时" && healthWasReady;
        bool external = !mine && (h.Code != null || IsPortOccupied(config.Port));
        if (ready && mine) starting = false;
        int? slots = null;
        double? gen = null; double? prompt = null;
        DateTimeOffset? generationSampleAt = null;
        string taskSummary = "";
        if (endpointProbe is { IsCompleted: true })
        {
            Task<ProbeResult> completed = endpointProbe;
            endpointProbe = null;
            var probe = await completed;
            if (probe.Epoch == epoch)
            {
                var s = probe.Slots;
                generationSampleAt = probe.ReceivedAt;
                slotsLabel = s.Label;
                if (s.Code == HttpStatusCode.OK)
                {
                    try
                    {
                        var samples = SlotTelemetry.Parse(s.Body);
                        slots = samples.Count(x => x.Active);
                        taskSummary = string.Join("；", samples.Where(x => x.Active).Take(8).Select(x => $"槽位 {x.Id} / 任务 {x.TaskId?.ToString() ?? "未知"} / 已解码 {x.Decoded?.ToString() ?? "未知"}"));
                        gen = slotTelemetry.Observe(samples, probe.MonotonicSeconds);
                        promptProgress.ObserveSlots(samples, probe.RequestedAt);
                        slotsFailures = 0;
                        nextSlotsProbe = now.AddSeconds(5);
                    }
                    catch (JsonException) { slotsLabel = "响应格式未知"; slotTelemetry.Reset(); slotsFailures++; nextSlotsProbe = now + ProbeBackoff.DelayForFailure(slotsFailures); }
                }
                else
                {
                    slotTelemetry.Reset(); slotsFailures++;
                    nextSlotsProbe = now + ProbeBackoff.DelayForFailure(slotsFailures);
                    slotsLabel += $" · {(int)(nextSlotsProbe - now).TotalSeconds} 秒后重试";
                }
                if (probe.Metrics is { } m)
                {
                    metricsLabel = m.Label;
                    nextMetricsProbe = now.AddSeconds(30);
                    if (m.Code == HttpStatusCode.OK)
                    {
                        var values = ParseMetrics(m.Body);
                        _ = CounterRate(values, "tokens_predicted_total", "tokens_predicted_seconds_total", ref previousGeneration);
                        prompt = CounterRate(values, "prompt_tokens_total", "prompt_seconds_total", ref previousPrompt);
                        if (prompt == null && values.TryGetValue("prompt_tokens_seconds", out double p)) prompt = p;
                        if (values.Count == 0) metricsLabel = "无可解析指标";
                    }
                    else { previousGeneration = null; previousPrompt = null; }
                }
            }
        }
        if (epoch != Volatile.Read(ref monitoringEpoch)) return;
        PromptProgress? progress = mine ? promptProgress.Snapshot(now) : null;
        if (mine)
        {
            lock (logLock)
                if (prompt == null && DateTime.UtcNow - lastPromptLog < TimeSpan.FromSeconds(15)) prompt = logPrompt;
            if (progress != null && now - progress.ObservedAt < TimeSpan.FromSeconds(15)) prompt = progress.TokensPerSecond ?? prompt;
        }
        if (slots == 0) prompt = 0;
        // 慢探测不阻塞健康状态与日志进度；同一时刻只运行一组顺序请求。
        if (endpointProbe == null && ready && now >= nextSlotsProbe)
        {
            if (ProbeBackoff.ShouldDeferForPrompt(progress, now, lastSlotsProbe)) slotsLabel = "提示词处理中 · 暂缓探测";
            else
            {
                lastSlotsProbe = now;
                endpointProbe = ProbeEndpoints(root, epoch, now, now >= nextMetricsProbe);
                if (slotsFailures == 0) slotsLabel = "探测中";
            }
        }
        string state = mine ? (ready ? "就绪" : starting && DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(3) ? "启动中" : "加载中") : external ? "外部服务" : failed ? "失败" : "未启动";
        string detail = external ? "只读监控 · 不接管、不停止外部进程" : mine ? ready ? "服务已响应 /health；没有推理请求时不会产生生成速度。" : "等待模型加载与健康检查；详细过程请查看日志。" : failed ? failureDetail : "模型不会自动启动。确认设置后，手动启动服务。";
        if (h.Label == "超时") detail += " 健康探测超时不代表服务掉线。";
        if (mine && !ready && DateTime.UtcNow - startedAt > TimeSpan.FromMinutes(10)) detail = "模型加载或健康检查已超过 10 分钟，请检查日志；未自动终止进程。";
        if (mine && ready && (metricsLabel is "HTTP 404" or "HTTP 403" || slotsLabel.StartsWith("HTTP 404", StringComparison.Ordinal) || slotsLabel.StartsWith("HTTP 403", StringComparison.Ordinal))) detail += " 当前服务禁用或不支持 /slots、/metrics；请核对 llama.cpp 版本。";
        detail += "\n速度曲线：任务计数差分采样；探测超时或退避时速度未知，不补造数据。";
        if (taskSummary.Length > 0) detail += "\n" + taskSummary;
        if (progress != null) detail += $"\n提示词进度来自槽位 {progress.Slot} / 任务 {progress.TaskId} 日志，更新于 {progress.ObservedAt.ToLocalTime():HH:mm:ss}。";
        Updated?.Invoke(new(state, mine, starting, external, gen, prompt, slots, h.Label, slotsLabel, metricsLabel, detail, progress, generationSampleAt));
    }
    private static Dictionary<string, double> ParseMetrics(string body)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string line in body.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var match = Regex.Match(line, @"^([a-zA-Z_:][a-zA-Z0-9_:]*)(?:\{[^}]*\})?\s+([-+0-9.eE]+)");
            if (!match.Success || !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) continue;
            string name = match.Groups[1].Value;
            if (name.StartsWith("llamacpp:")) name = name[9..];
            if (name.StartsWith("llamacpp_")) name = name[9..];
            values[name] = values.GetValueOrDefault(name) + value;
        }
        return values;
    }
    private static double? CounterRate(Dictionary<string, double> values, string tokenName, string secondsName, ref (double Tokens, double Seconds)? previous)
    {
        if (!values.TryGetValue(tokenName, out double tokens) || !values.TryGetValue(secondsName, out double seconds)) return null;
        var old = previous; previous = (tokens, seconds);
        if (old == null || tokens < old.Value.Tokens || seconds < old.Value.Seconds) return null;
        double dt = tokens - old.Value.Tokens, ds = seconds - old.Value.Seconds;
        if (dt == 0) return 0;
        return ds > 0 ? dt / ds : null;
    }
    private void ParseLog(string line)
    {
        AppendLog(line);
        ObserveLog(line);
    }
    private void ObserveLog(string line)
    {
        promptProgress.Observe(line, DateTimeOffset.UtcNow);
        var match = Regex.Match(line, @"(?<prompt>prompt\s+)?eval time\s*=.*?\(.*?,\s*(?<speed>[0-9.]+)\s+tokens per second", RegexOptions.IgnoreCase);
        if (match.Success && double.TryParse(match.Groups["speed"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed) && double.IsFinite(speed))
        {
            lock (logLock)
            {
                if (match.Groups["prompt"].Success) { logPrompt = speed; lastPromptLog = DateTime.UtcNow; }
                else { logGeneration = speed; lastGenerationLog = DateTime.UtcNow; }
            }
        }
    }
    public void AppendLog(string text)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        lock (logLock)
        {
            logs.Enqueue(line);
            while (logs.Count > 800) logs.Dequeue();
            try
            {
                Directory.CreateDirectory(LogDirectory);
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 2 * 1024 * 1024)
                {
                    for (int i = 2; i >= 1; i--) { string source = logPath + "." + i; if (File.Exists(source)) File.Move(source, logPath + "." + (i + 1), true); }
                    File.Move(logPath, logPath + ".1", true);
                }
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                if (!logWarningSent) { logWarningSent = true; string warning = "日志无法写入磁盘：" + ex.Message; logs.Enqueue(warning); LogAdded?.Invoke(warning); }
            }
        }
        LogAdded?.Invoke(line);
    }
    public void Dispose() { disposed = true; promptProgress.Reset(); lifetime.Cancel(); http.Dispose(); owned?.Dispose(); }
}
