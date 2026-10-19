using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ModelDesk.Services;

public sealed record SlotSample(int Id, int? TaskId, bool Active, int? Decoded);

public sealed class SlotTelemetry
{
    private Dictionary<int, SlotSample>? previous;
    private double lastTime;
    public void Reset() { previous = null; lastTime = 0; }
    public static List<SlotSample> Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("slots", out var wrapped)) root = wrapped;
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("无法识别 slots 数据格式。");
        var result = new List<SlotSample>();
        int index = 0;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new JsonException("无法识别 slot 数据格式。");
            int id = Number(item, "id") ?? Number(item, "id_slot") ?? index;
            int? task = Number(item, "id_task");
            int? decoded = Number(item, "n_decoded");
            bool hasNext = false;
            if (item.TryGetProperty("next_token", out var next))
            {
                if (next.ValueKind == JsonValueKind.Object) ReadNext(next, ref decoded, ref hasNext);
                else if (next.ValueKind == JsonValueKind.Array)
                    foreach (var token in next.EnumerateArray()) if (token.ValueKind == JsonValueKind.Object) ReadNext(token, ref decoded, ref hasNext);
            }
            bool active = item.TryGetProperty("is_processing", out var processing) && processing.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? processing.GetBoolean()
                : task >= 0 || hasNext || (item.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "processing");
            result.Add(new(id, task, active, decoded >= 0 ? decoded : null));
            index++;
        }
        return result;
    }
    private static int? Number(JsonElement root, string property) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
    private static void ReadNext(JsonElement token, ref int? decoded, ref bool hasNext)
    {
        int? count = Number(token, "n_decoded");
        if (count >= 0) decoded = Math.Max(decoded ?? 0, count.Value);
        hasNext |= token.TryGetProperty("has_next_token", out var next) && next.ValueKind == JsonValueKind.True;
    }
    public double? Observe(List<SlotSample> current, double seconds)
    {
        double elapsed = seconds - lastTime;
        int matched = 0;
        long delta = 0;
        if (previous != null && elapsed > 0)
            foreach (var slot in current)
                if (slot.Active && slot.TaskId >= 0 && slot.Decoded.HasValue && previous.TryGetValue(slot.Id, out var old) && old.Active && old.TaskId == slot.TaskId && old.Decoded.HasValue && slot.Decoded >= old.Decoded)
                { delta += slot.Decoded.Value - old.Decoded.Value; matched++; }
        previous = new Dictionary<int, SlotSample>();
        foreach (var slot in current) previous[slot.Id] = slot;
        lastTime = seconds;
        int activeCount = current.FindAll(x => x.Active).Count;
        return activeCount == 0 ? 0 : matched == activeCount && matched > 0 ? delta / elapsed : null;
    }
}
