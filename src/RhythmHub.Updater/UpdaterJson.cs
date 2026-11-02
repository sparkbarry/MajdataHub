using System.Text.Json;

namespace RhythmHub.Updater;

internal static class UpdaterJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
