using System.IO;
using System.Text.Json;

namespace BrakeDiscInspector_GUI_ROI
{
    public static class PresetManager
    {
        public static string GetDefaultPath(PresetFile preset)
            => Path.Combine(preset.Home, "configs", "preset.json");

        public static void Save(PresetFile preset, string? path = null)
        {
            path ??= GetDefaultPath(preset);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static PresetFile Load(string path)
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PresetFile>(json) ?? new PresetFile();
        }

        public static PresetFile LoadOrDefault(PresetFile preset)
        {
            var path = GetDefaultPath(preset);
            return File.Exists(path) ? Load(path) : preset;
        }
    }
}
