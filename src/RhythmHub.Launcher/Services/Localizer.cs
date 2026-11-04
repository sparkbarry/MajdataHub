using System.Globalization;
using RhythmHub.Launcher.Resources;

namespace RhythmHub.Launcher.Services;

/// <summary>
/// The UI language at runtime. Switching it updates every <c>{l:Tr}</c> binding and raises
/// <see cref="LanguageChanged"/> so view models can re-read their strings; no restart needed.
/// </summary>
public sealed class Localizer
{
    /// <summary>The system language, captured before the player's choice replaces it.</summary>
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;

    public static Localizer Instance { get; } = new();

    private Localizer() => Strings.Culture = SystemCulture;

    /// <summary>The language resources are read in.</summary>
    public CultureInfo Culture => Strings.Culture ?? SystemCulture;

    /// <summary>Raised on the UI thread after the language changed.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>Switches to <paramref name="id"/> ("zh", "en", "ja"); null or empty follows the system.</summary>
    public void SetLanguage(string? id)
    {
        var culture = string.IsNullOrEmpty(id) || LanguageOption.Find(id).Id != id
            ? SystemCulture
            : CultureInfo.GetCultureInfo(id);
        if (Equals(culture, Strings.Culture)) return;

        // Strings.Culture is a plain static, unlike CurrentUICulture, which flows with each async
        // operation and would leave work started before the switch in the old language.
        Strings.Culture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string this[string key] => Strings.ResourceManager.GetString(key, Culture) ?? key;

    /// <summary>The string for <paramref name="key"/> now and after every language change.</summary>
    public IObservable<string> Observe(string key) => new KeyObservable(this, key);

    private sealed class KeyObservable(Localizer localizer, string key) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            void OnChanged(object? sender, EventArgs e) => observer.OnNext(localizer[key]);
            localizer.LanguageChanged += OnChanged;
            observer.OnNext(localizer[key]);
            return new Unsubscriber(() => localizer.LanguageChanged -= OnChanged);
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
