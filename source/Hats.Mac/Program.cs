using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Media;

namespace Hats.Mac;
public static class Program
{
    [STAThread] public static void Main(string[] args)
    {
        if(args.Length==3 && args[0]=="--validate-update")
        {
            var work=Path.GetFullPath(args[2]);Directory.CreateDirectory(work);
            var installed=Path.Combine(work,MacAppUpdater.BundleName);
            if(Directory.Exists(installed))throw new IOException("Update test destination already exists.");
            MacAppUpdater.RunAsync("/usr/bin/ditto",[MacAppUpdater.CurrentBundle(),installed]).GetAwaiter().GetResult();
            var staged=MacAppUpdater.PrepareAsync(Path.GetFullPath(args[1]),installed,MainWindow.CurrentVersion).GetAwaiter().GetResult();
            var quarantine=MacAppUpdater.RunAsync("/usr/bin/xattr",["-p","com.apple.quarantine",staged]).GetAwaiter().GetResult();
            if(!quarantine.Contains("MagicMonkeiHATS"))throw new IOException("Update quarantine was not preserved.");
            File.WriteAllText(Path.Combine(work,"update-validation.json"),System.Text.Json.JsonSerializer.Serialize(new{validated=true,architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),quarantine_preserved=true,installed_app_replaced=false,sd_accessed=false}));
            return;
        }
        if(args.Length==1 && args[0]=="--native-smoke")
        {
            var mount=Hats.Core.MacVolumeGuard.Read("/");
            if(mount.Mount!="/" || mount.FileSystem.Length==0) throw new IOException("macOS mount API check failed.");
            Console.WriteLine($"Native macOS check passed: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, {mount.FileSystem}");return;
        }
        if(args.Length==2 && args[0]=="--startup-check") App.StartupReport=Path.GetFullPath(args[1]);
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch(Exception e) { try { AppSettings.Log("Startup failure: " + e); } catch { } throw; }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
public sealed class App : Application
{
    public static string? StartupReport { get; set; }
    public override void Initialize()
    {
        RequestedThemeVariant=ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Resources["SystemAccentColor"] = Color.Parse("#B494FF");
        Resources["SystemAccentColorLight1"] = Color.Parse("#CCB5FF");
        Resources["SystemAccentColorDark1"] = Color.Parse("#9873EE");
        Styles.Add(new Style(s=>s.OfType<Avalonia.Controls.Button>()) { Setters = { new Setter(Avalonia.Controls.Button.CornerRadiusProperty,new CornerRadius(8)) } });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window=new MainWindow(StartupReport!=null); desktop.MainWindow=window;
            if(StartupReport!=null) window.Opened+=(_,_)=>Avalonia.Threading.DispatcherTimer.RunOnce(()=>
            {
                File.WriteAllText(StartupReport,System.Text.Json.JsonSerializer.Serialize(new{started=true,architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),width=window.Bounds.Width,height=window.Bounds.Height,sd_accessed=false}));
                window.Close();
            },TimeSpan.FromSeconds(2));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
