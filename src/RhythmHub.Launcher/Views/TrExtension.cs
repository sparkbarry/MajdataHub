using Avalonia;
using RhythmHub.Launcher.Services;

namespace RhythmHub.Launcher.Views;

/// <summary>
/// <c>{l:Tr Key}</c>: a Strings.resx entry that follows language changes, in place of
/// <c>{x:Static res:Strings.Key}</c>, which is read once when the view is built.
/// </summary>
public sealed class TrExtension(string key)
{
    public string Key { get; } = key;

    public object ProvideValue(IServiceProvider serviceProvider) => Localizer.Instance.Observe(Key).ToBinding();
}
