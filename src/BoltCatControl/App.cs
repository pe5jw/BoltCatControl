using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using BoltCat.Core;
using BoltCatControl.Theming;

namespace BoltCatControl;

/// <summary>Applicatie zonder AXAML: thema en hoofdvenster worden in code gezet.</summary>
public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        BoltThemes.Amber.Apply(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            (BoltThemes.All.FirstOrDefault(t => t.Name == settings.Theme) ?? BoltThemes.Amber).Apply(this);
            var rig = new RigController();
            desktop.MainWindow = new MainWindow(rig, settings, autoStart: true);
            // Ook bij afsluiten via het systeem: PTT uit en rigctld stoppen
            desktop.ShutdownRequested += (_, _) => rig.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
