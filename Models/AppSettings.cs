using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ModelDesk.Models;

public sealed class AppSettings
{
    public string ServerPath { get; set; } = "E:/AI/llama/llama-server.exe";
    public string ModelPath { get; set; } = "E:/AI/models/HauhauCS/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf";
    public string MmprojPath { get; set; } = "E:/AI/models/HauhauCS/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive/mmproj-Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-f16.gguf";
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8080;
    public int ContextSize { get; set; } = 131072;
    public int PredictTokens { get; set; } = 8192;
    public bool FreeComfyMemory { get; set; }
    public string ExtraArguments { get; set; } = "--gpu-layers all --flash-attn on --cache-type-k q8_0 --cache-type-v q8_0 --jinja --alias qwen36 --temp 1.0 --top-p 0.95 --top-k 20 --min-p 0.0 --presence-penalty 1.5";
    public static string DataDirectory => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    public static string FilePath => Path.Combine(DataDirectory, "ModelDesk.settings.json");
    public static AppSettings Load(out string? error)
    {
        error = null;
        try
        {
            var settings = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
            settings.Validate();
            return settings;
        }
        catch (Exception ex) { error = "配置读取失败，已载入默认值：" + ex.Message; return new(); }
    }
    public void Save()
    {
        Validate();
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, FilePath, true);
    }
    public AppSettings Copy() => (AppSettings)MemberwiseClone();
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerPath) || string.IsNullOrWhiteSpace(ModelPath) || Host == null || ExtraArguments == null || MmprojPath == null) throw new ArgumentException("配置的路径和参数不能为 null，服务和模型路径不能为空。");
        if (Port is < 1 or > 65535) throw new ArgumentException("端口必须介于 1–65535。");
        if (ContextSize < 512 || PredictTokens < 1) throw new ArgumentException("上下文至少为 512，最大生成 token 必须大于 0。");
        if (!System.Net.IPAddress.TryParse(Host, out _)) throw new ArgumentException("监听地址必须是有效 IPv4 或 IPv6 地址。");
        var tokens = ParseArguments(ExtraArguments);
        string[] reserved = { "-m", "--model", "--mmproj", "--host", "--port", "-c", "--ctx-size", "-n", "--n-predict", "--metrics", "--slots" };
        foreach (string token in tokens)
            foreach (string flag in reserved)
                if (token == flag || token.StartsWith(flag + "=", StringComparison.Ordinal))
                    throw new ArgumentException("额外参数不能覆盖受管理参数：" + flag);
    }
    public static List<string> ParseArguments(string input)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';
        bool started = false;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '\0' || c == '\r' || c == '\n') throw new ArgumentException("额外参数不能含换行或空字符。");
            if (c == '\\' && i + 1 < input.Length && (input[i + 1] == '"' || input[i + 1] == '\'' || input[i + 1] == '\\')) { token.Append(input[++i]); started = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; else token.Append(c); started = true; continue; }
            if (c is '"' or '\'') { quote = c; started = true; continue; }
            if (char.IsWhiteSpace(c)) { if (started) { result.Add(token.ToString()); token.Clear(); started = false; } }
            else { token.Append(c); started = true; }
        }
        if (quote != '\0') throw new ArgumentException("额外参数存在未闭合的引号。");
        if (started) result.Add(token.ToString());
        return result;
    }
}
