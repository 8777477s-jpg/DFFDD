using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoltMacro;

public sealed class MacroStepJsonConverter : JsonConverter<MacroStep>
{
    public override MacroStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        int delay = root.TryGetProperty("DelayMsBefore", out var d) ? d.GetInt32() : 0;

        string? type = null;
        if (root.TryGetProperty("type", out var typeEl))
            type = typeEl.GetString();

        // Legacy inference if discriminator absent.
        type ??= InferLegacyType(root);

        return type switch
        {
            nameof(DelayStep) => new DelayStep { DelayMsBefore = delay, Ms = root.GetProperty("Ms").GetInt32() },

            nameof(KeyStep) => new KeyStep
            {
                DelayMsBefore = delay,
                VirtualKey = root.GetProperty("VirtualKey").GetInt32(),
                IsKeyDown = root.GetProperty("IsKeyDown").GetBoolean()
            },

            nameof(MouseMoveStep) => new MouseMoveStep
            {
                DelayMsBefore = delay,
                X = root.GetProperty("X").GetInt32(),
                Y = root.GetProperty("Y").GetInt32(),
                Space = root.TryGetProperty("Space", out var sp) && Enum.TryParse<CoordSpace>(sp.GetString(), out var cs)
                    ? cs
                    : CoordSpace.Screen
            },

            nameof(MouseClickStep) => new MouseClickStep
            {
                DelayMsBefore = delay,
                X = root.GetProperty("X").GetInt32(),
                Y = root.GetProperty("Y").GetInt32(),
                Space = root.TryGetProperty("Space", out var sp) && Enum.TryParse<CoordSpace>(sp.GetString(), out var cs)
                    ? cs
                    : CoordSpace.Screen,
                Button = root.TryGetProperty("Button", out var btn) && Enum.TryParse<MouseButton>(btn.GetString(), out var mb)
                    ? mb
                    : MouseButton.Left,
                Clicks = root.TryGetProperty("Clicks", out var cl) ? cl.GetInt32() : 1
            },

            _ => throw new JsonException($"Unknown MacroStep type '{type}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, MacroStep value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.GetType().Name);
        writer.WriteNumber("DelayMsBefore", value.DelayMsBefore);

        switch (value)
        {
            case DelayStep d:
                writer.WriteNumber("Ms", d.Ms);
                break;

            case KeyStep k:
                writer.WriteNumber("VirtualKey", k.VirtualKey);
                writer.WriteBoolean("IsKeyDown", k.IsKeyDown);
                break;

            case MouseMoveStep mm:
                writer.WriteNumber("X", mm.X);
                writer.WriteNumber("Y", mm.Y);
                writer.WriteString("Space", mm.Space.ToString());
                break;

            case MouseClickStep mc:
                writer.WriteNumber("X", mc.X);
                writer.WriteNumber("Y", mc.Y);
                writer.WriteString("Space", mc.Space.ToString());
                writer.WriteString("Button", mc.Button.ToString());
                writer.WriteNumber("Clicks", mc.Clicks);
                break;

            default:
                throw new NotSupportedException($"Unsupported MacroStep runtime type: {value.GetType().Name}");
        }

        writer.WriteEndObject();
    }

    private static string InferLegacyType(JsonElement root)
    {
        if (root.TryGetProperty("VirtualKey", out _)) return nameof(KeyStep);
        if (root.TryGetProperty("Ms", out _)) return nameof(DelayStep);

        bool hasX = root.TryGetProperty("X", out _);
        bool hasY = root.TryGetProperty("Y", out _);
        if (hasX && hasY)
        {
            if (root.TryGetProperty("Button", out _) || root.TryGetProperty("Clicks", out _))
                return nameof(MouseClickStep);
            return nameof(MouseMoveStep);
        }

        throw new JsonException("Cannot infer legacy MacroStep type.");
    }
}
