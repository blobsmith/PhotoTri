using System.Windows;
using Wpf.Ui.Appearance;

namespace PhotoTri.App;

public partial class App : Application
{
    /// <summary>Thème imposé au lancement par « --theme=dark » ou « --theme=light », sinon null (thème de Windows).</summary>
    public static ApplicationTheme? ForcedTheme { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ForcedTheme = e.Args.Select(a => a.ToLowerInvariant()).FirstOrDefault(a => a.StartsWith("--theme=")) switch
        {
            "--theme=dark" => ApplicationTheme.Dark,
            "--theme=light" => ApplicationTheme.Light,
            _ => null,
        };
        // Sans option, le thème clair ou sombre suit le réglage de Windows ; la fenêtre principale suit ensuite ses changements.
        if (ForcedTheme is { } theme) ApplicationThemeManager.Apply(theme);
        else ApplicationThemeManager.ApplySystemTheme();
    }
}
