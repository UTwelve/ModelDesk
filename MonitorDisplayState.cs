using ModelDesk.Services;

namespace ModelDesk;

public sealed class MonitorDisplayState
{
    private DateTimeOffset? lastSampleAt, generationAt, activeAt, promptAt;
    private double? generation, prompt;
    private int? active;
    private bool generationMissing, activeMissing;

    public string StateText { get; private set; } = "尚未连接";
    public string SummaryText { get; private set; } = "模型不会自动启动。";
    public string GenerationText { get; private set; } = "— token/s";
    public string PromptText { get; private set; } = "— token/s";
    public string ActiveText { get; private set; } = "—";
    public string UpdatedText { get; private set; } = "等待首次采样";
    public string PromptUpdatedText { get; private set; } = "等待首次记录";
    public string PromptStageText { get; private set; } = "";
    public bool ShowPromptStage { get; private set; }
    public string ConnectionText { get; private set; } = "服务连接：等待检测";
    public string TasksText { get; private set; } = "任务监测：等待检测";
    public string MetricsText { get; private set; } = "性能采集：等待检测";
    public string GpuSummaryText { get; private set; } = "GPU — · 温度 — · 显存 —";
    public string GpuDetailText { get; private set; } = "正在读取显卡与采集来源…";
    public DateTimeOffset? GenerationSampleAt => lastSampleAt;
    public double? GenerationSampleValue { get; private set; }

    // 返回 true 仅表示收到一个新的真实采样时间，可据此向曲线追加值（包括缺测）。
    public void Reset()
    {
        lastSampleAt = generationAt = activeAt = promptAt = null;
        generation = prompt = null; active = null;
        generationMissing = activeMissing = false; GenerationSampleValue = null;
        ShowPromptStage = false; PromptStageText = "";
    }

    public bool Update(Telemetry sample, DateTimeOffset now)
    {
        if (!sample.Owned && !sample.External && sample.State is "未启动" or "失败") Reset();
        bool newSample = sample.GenerationSampleAt is { } at && (!lastSampleAt.HasValue || at > lastSampleAt.Value);
        if (newSample)
        {
            lastSampleAt = sample.GenerationSampleAt;
            GenerationSampleValue = Valid(sample.Generation);
            generationMissing = !GenerationSampleValue.HasValue;
            if (!generationMissing) { generation = GenerationSampleValue; generationAt = lastSampleAt; }
            activeMissing = !sample.ActiveSlots.HasValue;
            if (!activeMissing) { active = sample.ActiveSlots; activeAt = lastSampleAt; }
        }
        if (Valid(sample.Prompt) is { } promptValue)
        {
            prompt = promptValue;
            promptAt = now;
        }
        bool progressFresh = sample.Progress is { } p && Age(now, p.ObservedAt) <= 15;
        if (progressFresh && sample.Progress is { TokensPerSecond: { } progressRate } progress && Valid(progressRate).HasValue)
        {
            prompt = progressRate;
            promptAt = progress.ObservedAt;
        }
        double generationAge = Age(now, generationAt ?? lastSampleAt);
        GenerationText = generationAge >= 60 || !generation.HasValue ? (generationMissing && generationAge < 60 ? "采集中" : "— token/s")
            : generationMissing ? "等待更新"
            : generationAge >= 15 ? "最近 " + Rate(generation) : Rate(generation);
        if (generationAge >= 60) GenerationText = "— token/s";
        UpdatedText = generationAt is { } observed
            ? $"{(generationMissing ? "最后记录" : "更新于")} {observed.ToLocalTime():HH:mm:ss}" +
              (generationMissing && generationAge < 60 ? $" · {Rate(generation)}（非实时）" : "") +
              (generationMissing || generationAge >= 15 ? " · 等待更新" : "")
            : "等待首次有效采样";
        double activeAge = Age(now, activeAt);
        ActiveText = activeAge >= 60 || !active.HasValue ? "—" : (activeMissing || activeAge >= 15 ? "最近 " : "") + active.Value;
        double promptAge = Age(now, promptAt);
        PromptText = promptAge >= 60 ? "— token/s" : Rate(prompt);
        PromptUpdatedText = promptAt is { } promptObserved
            ? $"{(progressFresh ? "进度采样" : "最近记录")} {promptObserved.ToLocalTime():HH:mm:ss}" + (promptAge > 35 ? " · 等待更新" : "")
            : "等待首次记录";
        ShowPromptStage = sample.Progress is { } stage && Age(now, stage.ObservedAt) < 90;
        PromptStageText = ShowPromptStage && sample.Progress is { } visibleStage
            ? $"提示词预处理 · {visibleStage.Percent:0}% · 已处理 {visibleStage.ProcessedTokens:N0} token"
            : "";
        StateText = sample.State;
        SummaryText = sample.State switch
        {
            "未启动" => "模型不会自动启动。",
            "失败" => "模型未能启动，请查看任务与日志。",
            "启动中" or "加载中" => "正在加载模型，详细进度可在日志中查看。",
            _ when sample.Health.Contains("超时", StringComparison.Ordinal) => "连接响应较慢，等待更新。",
            _ when progressFresh && sample.Progress is { } current => $"正在处理提示词 · {current.Percent:0}% · 已处理 {current.ProcessedTokens:N0} token",
            _ when !generationMissing && generationAge < 15 && generation > 0 => "正在生成回复",
            _ when !activeMissing && activeAge <= 15 && active > 0 => "请求处理中",
            _ when !activeMissing && activeAge <= 15 && active == 0 => "等待请求",
            _ when sample.Progress is { } previous => $"最后进度 {previous.Percent:0}% · 等待更新",
            _ => "等待任务采样"
        };
        if (sample.External) SummaryText += " · 外部服务，只读监控";
        ConnectionText = "服务连接：" + EndpointStatus(sample.Health);
        TasksText = "任务监测：" + EndpointStatus(sample.Slots);
        MetricsText = "性能采集：" + EndpointStatus(sample.Metrics);
        return newSample;
    }

    public void Update(GpuSnapshot sample)
    {
        string utilization = sample.Utilization.HasValue ? $"{sample.Utilization:0}%" : "—";
        string temperature = sample.Temperature.HasValue ? $"{sample.Temperature:0} °C" : "—";
        string memory = sample.MemoryUsedMiB.HasValue && sample.MemoryTotalMiB.HasValue
            ? $"{sample.MemoryUsedMiB / 1024:0.0} / {sample.MemoryTotalMiB / 1024:0.0} GiB" : "—";
        GpuSummaryText = $"GPU {utilization} · 温度 {temperature} · 显存 {memory}";
        GpuDetailText = sample.Name + "\n采集来源与状态：" + sample.Status;
    }

    public static string FormatPromptProgress(PromptProgress progress, DateTimeOffset now)
        => $"{(Age(now, progress.ObservedAt) > 15 ? "最后进度" : "正在处理提示词")}：{progress.Percent:0}% · 已处理 {progress.ProcessedTokens:N0} token · {Rate(progress.TokensPerSecond)} · 更新于 {progress.ObservedAt.ToLocalTime():HH:mm:ss}";
    private static double Age(DateTimeOffset now, DateTimeOffset? at) => at.HasValue ? Math.Max(0, (now - at.Value).TotalSeconds) : double.PositiveInfinity;
    private static double? Valid(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 ? value : null;
    private static string Rate(double? value) => value.HasValue ? $"{value:0.0} token/s" : "— token/s";
    private static string EndpointStatus(string status) => status switch
    {
        "可用" => "已连接",
        _ when status.Contains("超时", StringComparison.Ordinal) => "响应超时，等待重试",
        _ when status.Contains("暂缓", StringComparison.Ordinal) => "提示词处理中，稍后采集",
        _ when status.Contains("探测", StringComparison.Ordinal) => "正在采集",
        _ when status.Contains("404", StringComparison.Ordinal) || status.Contains("403", StringComparison.Ordinal) => "未开放",
        _ when status.Contains("未连接", StringComparison.Ordinal) => "未连接",
        _ when status.Contains("格式", StringComparison.Ordinal) || status.Contains("无可解析", StringComparison.Ordinal) => "数据暂不可用",
        _ => "等待采集"
    };
}
