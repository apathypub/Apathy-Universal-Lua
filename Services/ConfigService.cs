using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Apathy.Models;

namespace Apathy.Services;

/// <summary>Named JSON configs stored in a "configs" folder next to the executable.</summary>
public static class ConfigService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new ColorJsonConverter(), new JsonStringEnumConverter() },
    };

    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "configs");

    public static IReadOnlyList<string> List()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.EnumerateFiles(Folder, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void Save(string name, MenuSettings settings)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathFor(name), Serialize(settings));
    }

    public static MenuSettings Load(string name) => Deserialize(File.ReadAllText(PathFor(name)));

    public static void Delete(string name) => File.Delete(PathFor(name));

    public static string Serialize(MenuSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static MenuSettings Deserialize(string json) =>
        JsonSerializer.Deserialize<MenuSettings>(json, Options) ?? throw new JsonException("Empty config.");

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string PathFor(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException($"Invalid config name '{name}'.", nameof(name));
        return Path.Combine(Folder, name + ".json");
    }

    private sealed class ColorJsonConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ColorConverter.ConvertFromString(reader.GetString()) is Color c ? c : throw new JsonException("Invalid color.");

        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
