using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ModelDesk.Services;

public sealed record PromptProgress(int Slot, long TaskId, double Percent, long ProcessedTokens, double? TokensPerSecond, DateTimeOffset ObservedAt);

public sealed class PromptProgressTracker
{
    private readonly object gate = new();
    private readonly Dictionary<int, PromptProgress> progress = new();
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);

    public void Reset() { lock (gate) progress.Clear(); }

    public PromptProgress? Snapshot(DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (int slot in progress.Where(x => now - x.Value.ObservedAt > Freshness).Select(x => x.Key).ToArray()) progress.Remove(slot);
            return progress.Values.OrderByDescending(x => x.ObservedAt).FirstOrDefault();
        }
    }

    public void Observe(string line, DateTimeOffset observedAt)
    {
        var slotMatch = Regex.Match(line, @"\b(?:id|id_slot)\s*(?:=|:)?\s*(?<id>\d+)", RegexOptions.IgnoreCase);
        var taskMatch = Regex.Match(line, @"\b(?:task|id_task)\s*(?:=|:)?\s*(?<id>\d+)", RegexOptions.IgnoreCase);
        int? slot = slotMatch.Success && int.TryParse(slotMatch.Groups["id"].Value, out int slotId) ? slotId : null;
        long? task = taskMatch.Success && long.TryParse(taskMatch.Groups["id"].Value, out long taskId) ? taskId : null;
        if (line.Contains("prompt processing", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("prompt processing done", StringComparison.OrdinalIgnoreCase))
        {
            lock (gate)
                if (slot.HasValue && task.HasValue && progress.TryGetValue(slot.Value, out var previous)
                    && previous.TaskId != task.Value && observedAt >= previous.ObservedAt) progress.Remove(slot.Value);
            var tokensMatch = Regex.Match(line, @"\bn_tokens\s*=\s*(?<value>[^,\s]+)", RegexOptions.IgnoreCase);
            var percentMatch = Regex.Match(line, @"\bprogress\s*=\s*(?<value>[^,\s]+)", RegexOptions.IgnoreCase);
            if (slot == null || task == null || !tokensMatch.Success || !percentMatch.Success
                || !long.TryParse(tokensMatch.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long tokens) || tokens < 0
                || !double.TryParse(percentMatch.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction)
                || !double.IsFinite(fraction) || fraction < 0 || fraction > 1) return;
            double? speed = null;
            var speedMatch = Regex.Match(line, @"(?<value>[^\s/]+)\s+tokens per second", RegexOptions.IgnoreCase);
            if (speedMatch.Success && double.TryParse(speedMatch.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedSpeed)
                && double.IsFinite(parsedSpeed) && parsedSpeed >= 0) speed = parsedSpeed;
            lock (gate)
                if (!progress.TryGetValue(slot.Value, out var current) || observedAt >= current.ObservedAt)
                    progress[slot.Value] = new(slot.Value, task.Value, fraction * 100, tokens, speed, observedAt);
            return;
        }
        bool newTask = Regex.IsMatch(line, @"\b(?:new (?:prompt|task)|processing task|task started)\b|\blaunch_slot_", RegexOptions.IgnoreCase);
        bool terminal = Regex.IsMatch(line, @"\b(?:prompt done|prompt processing done|generating|generation|decoding|decode|eval time|release(?:d)?|cancel(?:led|ed)?|stop processing)\b", RegexOptions.IgnoreCase);
        if (!newTask && !terminal) return;
        lock (gate)
        {
            if (slot.HasValue)
            {
                if (progress.TryGetValue(slot.Value, out var current) && observedAt >= current.ObservedAt
                    && (newTask || task == current.TaskId || task == null && !line.Contains("cancel", StringComparison.OrdinalIgnoreCase))) progress.Remove(slot.Value);
            }
            else if (task.HasValue)
            {
                foreach (int id in progress.Where(x => x.Value.TaskId == task.Value && observedAt >= x.Value.ObservedAt).Select(x => x.Key).ToArray()) progress.Remove(id);
            }
        }
    }

    public void ObserveSlots(IEnumerable<SlotSample> slots, DateTimeOffset requestedAt)
    {
        lock (gate)
            foreach (var slot in slots)
                if (progress.TryGetValue(slot.Id, out var current) && current.ObservedAt <= requestedAt
                    && (!slot.Active || slot.TaskId.HasValue && slot.TaskId.Value != current.TaskId || slot.Decoded > 0)) progress.Remove(slot.Id);
    }
}
