using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hats.Core;
using System.Text.Json;

namespace Hats.Mac;

public sealed class MainWindow : Window
{
    readonly AppSettings settings = AppSettings.Load();
    readonly ComboBox drives = new() { PlaceholderText="Choose your SD card", HorizontalAlignment=HorizontalAlignment.Stretch, MinHeight=40 };
    readonly ComboBox packs = new() { PlaceholderText="Checking the latest release…", HorizontalAlignment=HorizontalAlignment.Stretch, MinHeight=40 };
    readonly TextBlock driveDetail = Text("Connect your SD card or Switch in USB storage mode.",12,true);
    readonly TextBlock releaseTitle = Text("Checking GitHub…",17);
    readonly TextBlock releaseDetail = Text("Latest release from TechRepairs4U/HATS",12,true);
    readonly TextBlock status = Text("Choose your SD card to begin.",13);
    readonly TextBlock notice = Text("",13);
    readonly TextBlock backupSummary = Text("Verified backups saved on your Mac",12,true);
    readonly Button refreshDrive = new() {Content="Refresh"};
    readonly Button refreshRelease = new() {Content="Check releases"};
    readonly Button install = new() {Content="Install HATS",Height=44,MinWidth=146,IsEnabled=false};
    readonly Button cancel = new() {Content="Cancel",Height=44,IsVisible=false};
    readonly Button options = new() {Content="Options"};
    readonly ProgressBar progress = new() {Minimum=0,Maximum=100,Height=4,IsVisible=false};
    readonly DispatcherTimer timer = new() {Interval=TimeSpan.FromSeconds(15)};
    readonly List<string> logLines = [];
    HatsRelease? release;
    bool busy,scanning,fetching,closed,dialogOpen;
    CancellationTokenSource? cancellation;
    long lastProgress;
    string? lastBackup;
    public bool PreviewMode { get; }
    static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
    static TextBlock Text(string text,double size,bool muted=false) => new() {Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Foreground=Brush(muted?"#AEA5C1":"#F5F0FC"),VerticalAlignment=VerticalAlignment.Center};
    static StackPanel Stack(params Control[] children) {var stack=new StackPanel {Spacing=9};foreach(var child in children)stack.Children.Add(child);return stack;}
    static Grid Row(Control left,Control right) {var row=new Grid {ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=12};row.Children.Add(left);Grid.SetColumn(right,1);row.Children.Add(right);return row;}
    static Border Card(Control content) => new(){Background=Brush("#191522"),BorderBrush=Brush("#3D334D"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(14),Padding=new Thickness(15),Child=content};
    static Image Art(string file,double width,double height) => new(){Source=new Bitmap(AssetLoader.Open(new Uri("avares://MagicMonkei.HATS/Assets/"+file))),Width=width,Height=height,Stretch=Stretch.Uniform};
    public MainWindow(bool preview=false)
    {
        PreviewMode=preview;Title="Magic Monkei · HATS Installer";Width=700;Height=650;MinWidth=590;MinHeight=570;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;WindowState=WindowState.Normal;
        Background=Brush("#100C18");FontFamily="Inter, -apple-system, sans-serif";
        var header=new Grid {Height=104,ClipToBounds=true};
        var artwork=Art("monkei-background.png",410,231);artwork.HorizontalAlignment=HorizontalAlignment.Right;artwork.VerticalAlignment=VerticalAlignment.Top;artwork.Margin=new Thickness(0,-31,-4,0);header.Children.Add(artwork);
        var fade=new LinearGradientBrush {StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(1,0,RelativeUnit.Relative),GradientStops={new GradientStop(Color.Parse("#100C18"),0),new GradientStop(Color.Parse("#100C18"),.45),new GradientStop(Color.Parse("#00100C18"),.83)}};
        header.Children.Add(new Border {Background=fade});
        var brand=Text("MAGIC MONKEI",25);brand.FontWeight=FontWeight.SemiBold;brand.LetterSpacing=1.1;
        var sub=Text("HATS INSTALLER",11);sub.LetterSpacing=3;sub.Foreground=Brush("#C9B0FF");
        var brandStack=Stack(brand,sub);brandStack.VerticalAlignment=VerticalAlignment.Center;brandStack.Spacing=5;header.Children.Add(brandStack);
        var details=new Button {Content="Details",Background=Brush("#00000000"),Padding=new Thickness(10,4)};
        details.Click+=async(_,_)=>await ProtectionDetails();
        var protectedLine=new Border{CornerRadius=new CornerRadius(8),Background=Brush("#282037"),BorderBrush=Brush("#4C3C66"),BorderThickness=new Thickness(1),Padding=new Thickness(13,5),Child=Row(Text("✓  Games, ports & mods protected",13),details)};
        var heading=new StackPanel {Orientation=Orientation.Horizontal,Spacing=9};heading.Children.Add(Art("fetch-hats.png",32,32));heading.Children.Add(releaseTitle);
        var card=Card(Stack(Row(Text("SD card",16),refreshDrive),drives,driveDetail));
        var packCard=Card(Stack(Row(heading,refreshRelease),packs,releaseDetail));
        notice.IsVisible=false;notice.Foreground=Brush("#F0BFC9");
        var body=Stack(header,protectedLine,notice,card,packCard,Row(backupSummary,options));body.Spacing=12;body.Margin=new Thickness(22,8,22,18);
        var root=new Grid {RowDefinitions=new RowDefinitions("*,Auto")};
        root.Children.Add(new ScrollViewer {Content=body,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled});
        install.Background=Brush("#B494FF");install.Foreground=Brush("#1A1029");install.FontWeight=FontWeight.SemiBold;install.Padding=new Thickness(20,9);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,Spacing=9};buttons.Children.Add(cancel);buttons.Children.Add(install);
        var footer=new Border {Background=Brush("#171020"),BorderBrush=Brush("#3D334D"),BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(22,16),Child=Stack(progress,Row(status,buttons))};Grid.SetRow(footer,1);root.Children.Add(footer);Content=root;
        var menu=new MenuFlyout();
        void Item(string title,Func<Task> action) {var item=new MenuItem {Header=title};item.Click+=async(_,_)=>await Safe(action);menu.Items.Add(item);}
        Item("Backup location…",ChooseBackup);Item("Extra protected folders…",EditProtections);Item("GitHub protection list…",ShowGitHub);
        Item("Activity & recovery…",ShowActivity);menu.Items.Add(new Separator());
        Item("Open logs",()=>{Directory.CreateDirectory(AppSettings.LogFolder);AppSettings.Open(AppSettings.LogFolder);return Task.CompletedTask;});
        Item("View HATS releases",()=>{AppSettings.Open("https://github.com/TechRepairs4U/HATS/releases");return Task.CompletedTask;});
        Item("Open Disk Utility",()=>{AppSettings.Open("/System/Applications/Utilities/Disk Utility.app");return Task.CompletedTask;});options.Flyout=menu;
        refreshDrive.Click+=async(_,_)=>await Scan();refreshRelease.Click+=async(_,_)=>await Fetch();
        drives.SelectionChanged+=(_,_)=>{if(drives.SelectedItem is MacVolume v)driveDetail.Text=$"{v.FreeBytes/1073741824d:N1} GB free  ·  {v.Root}";Controls();};
        packs.SelectionChanged+=(_,_)=>Controls();install.Click+=async(_,_)=>await Install();
        cancel.Click+=(_,_)=>{cancellation?.Cancel();cancel.IsEnabled=false;status.Text="Cancelling preparation…";};
        timer.Tick+=async(_,_)=>{if(!busy&&!dialogOpen)await Scan();};
        Closing+=(_,e)=>{if(busy){e.Cancel=true;notice.Text="Keep the app open until writing or recovery finishes. You can cancel during preparation.";notice.IsVisible=true;}};
        Closed+=(_,_)=>{closed=true;timer.Stop();};
        Opened+=async(_,_)=>{if(!PreviewMode){timer.Start();await Task.WhenAll(Scan(),Fetch());}};
        if(preview)SetPreview();
    }
    public void SetPreview()
    {
        drives.ItemsSource=new[]{new MacVolume("/Volumes/SWITCH","SWITCH","disk4s1","preview","msdos",128L<<30,76L<<30)};drives.SelectedIndex=0;
        packs.ItemsSource=new[]{new PackAsset(1,"HATS pack.zip","https://github.com/TechRepairs4U/HATS/releases",217676363,null,"")};packs.SelectedIndex=0;
        releaseTitle.Text="Latest HATS release";releaseDetail.Text="Release names and tags are detected automatically.";
        status.Text="Ready when you are.";install.IsEnabled=false;
    }
    void Controls(){install.IsEnabled=!PreviewMode&&!busy&&!scanning&&!fetching&&!dialogOpen&&release!=null&&drives.SelectedItem is MacVolume&&packs.SelectedItem is PackAsset;drives.IsEnabled=packs.IsEnabled=options.IsEnabled=!busy&&!dialogOpen;refreshDrive.IsEnabled=!busy&&!scanning&&!dialogOpen;refreshRelease.IsEnabled=!busy&&!fetching&&!dialogOpen;}
    void Ui(Action action){if(!closed)Dispatcher.UIThread.Post(()=>{if(!closed)action();});}
    void Log(string message){try{AppSettings.Log(message);}catch(Exception e) when(e is IOException or UnauthorizedAccessException){}Ui(()=>{logLines.Add($"{DateTime.Now:HH:mm:ss}  {message}");});}
    void Error(Exception error){Log(error.ToString());notice.Text=error.Message;notice.IsVisible=true;status.Text="Check the message above.";}
    async Task Safe(Func<Task> action){try{await action();}catch(Exception error){Error(error);}}
    async Task Scan()
    {
        if(scanning||busy)return;scanning=true;Controls();
        try{var selected=drives.SelectedItem as MacVolume;var found=await MacVolumes.ScanAsync();if(closed)return;drives.ItemsSource=found;drives.SelectedItem=found.FirstOrDefault(v=>v.Root==selected?.Root&&v.Uuid==selected.Uuid);if(drives.SelectedItem==null)driveDetail.Text=found.Length==0?"Connect an SD card formatted as FAT32 or exFAT, then Refresh.":"Choose the card you want to update.";}
        catch(Exception error){Error(error);}finally{scanning=false;Controls();}
    }
    async Task Fetch()
    {
        if(fetching||busy)return;fetching=true;release=null;packs.ItemsSource=null;releaseTitle.Text="Checking GitHub…";Controls();
        try{using var service=new ReleaseService();var latest=await service.LatestAsync();if(closed)return;release=latest;releaseTitle.Text=latest.Title;releaseDetail.Text=$"{latest.Tag}  ·  Latest published release";packs.ItemsSource=latest.Assets;if(latest.Assets.Length==1)packs.SelectedIndex=0;notice.IsVisible=false;}
        catch(Exception error){releaseTitle.Text="Couldn’t load the latest release";Error(error);}finally{fetching=false;Controls();}
    }
    async Task<bool> Dialog(string title,Control body,string? accept=null)
    {
        dialogOpen=true;Controls();var result=false;
        var dialog=new Window{Title=title,Width=560,MaxHeight=620,SizeToContent=SizeToContent.Height,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("#171020"),FontFamily=FontFamily};
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=9,HorizontalAlignment=HorizontalAlignment.Right};var close=new Button{Content=accept==null?"Done":"Cancel"};close.Click+=(_,_)=>dialog.Close();buttons.Children.Add(close);
        if(accept!=null){var ok=new Button{Content=accept,Background=Brush("#B494FF"),Foreground=Brush("#1A1029")};ok.Click+=(_,_)=>{result=true;dialog.Close();};buttons.Children.Add(ok);}
        var content=Stack(Text(title,20),body,buttons);content.Margin=new Thickness(22);content.Spacing=18;dialog.Content=new ScrollViewer{Content=content};
        try{await dialog.ShowDialog(this);return result;}finally{dialogOpen=false;Controls();}
    }
    async Task ProtectionDetails()=>await Dialog("Your games stay in place",Text("Before every install, HATS downloads and validates your latest GitHub protection list. If that fails, backup and SD changes do not start.\n\nGames, ports and mods in protected paths are not backed up, deleted or overwritten. Only recognised HATS utilities and sysmodules are updated.\n\nUnrelated folders in switch and game mods in atmosphere/contents are also protected automatically.",14));
    async Task ShowGitHub()
    {
        using var service=new ExclusionService();var snapshot=await service.LatestAsync();
        await Dialog("GitHub protection list",Stack(Text($"{snapshot.PreservePaths.Length} protected paths · checked just now",14),new TextBox{Text=string.Join('\n',snapshot.PreservePaths),IsReadOnly=true,AcceptsReturn=true,Height=290,TextWrapping=TextWrapping.NoWrap},Text("A fresh copy is fetched again just before installation. This preview is never used as a fallback.",12,true)));
    }
    async Task ChooseBackup()
    {
        var folders=await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions{Title="Choose where to save HATS backups",AllowMultiple=false});
        if(folders.Count==0)return;var path=folders[0].TryGetLocalPath()??throw new IOException("Choose a folder on your Mac.");
        settings.BackupFolder=Paths.Root(path);settings.Save();backupSummary.Text="Backups: "+Path.GetFileName(path);ToolTip.SetTip(backupSummary,path);
    }
    async Task EditProtections()
    {
        var existing=File.Exists(AppSettings.ProtectionFile)?InstallPolicy.LoadPreserved(AppSettings.ProtectionFile):[];
        var box=new TextBox{Text=string.Join('\n',existing),AcceptsReturn=true,Height=240,PlaceholderText="switch/My Game\natmosphere/contents/0100…"};
        if(!await Dialog("Extra protected folders",Stack(Text("Optional SD-relative paths, one per line. These add to GitHub and automatic protections.",13,true),box),"Save"))return;
        var paths=(box.Text??"").Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();foreach(var path in paths)Paths.ValidateRelative(path);
        Directory.CreateDirectory(AppSettings.DataFolder);File.WriteAllText(AppSettings.ProtectionFile,JsonSerializer.Serialize(new{preserve_paths=paths},new JsonSerializerOptions{WriteIndented=true}));
    }
    async Task ShowActivity()
    {
        var openBackup=new Button{Content="Open last backup",IsEnabled=lastBackup!=null};openBackup.Click+=(_,_)=>{if(lastBackup!=null)AppSettings.Open(lastBackup);};
        await Dialog("Activity & recovery",Stack(new TextBox{Text=string.Join('\n',logLines.TakeLast(250)),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=280},openBackup,Text("Logs, downloaded packs and recovery files are kept on your Mac.",12,true)));
    }
    async Task Install()
    {
        if(PreviewMode||busy||release is not {} selected||drives.SelectedItem is not MacVolume volume||packs.SelectedItem is not PackAsset asset)return;
        if(!await Dialog("Update this SD card?",Stack(Text($"{volume.Name}\n{volume.Root}\n\n{asset.Name}",15),Text($"Verified backup: {settings.BackupFolder}\n\nYour latest GitHub exclusions are required before any backup or SD changes.",13,true)),"Install HATS"))return;
        busy=true;cancellation=new();cancel.IsVisible=true;cancel.IsEnabled=true;progress.IsVisible=true;progress.Value=0;notice.IsVisible=false;Controls();
        var backup=Path.Combine(settings.BackupFolder,DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var current=await MacVolumes.InspectAsync(volume.Root,cancellation.Token);MacVolumes.SameCard(volume,current);
            var guard=new MacVolumeGuard(current);var preserved=InstallPolicy.LoadPreserved(AppSettings.ProtectionFile);
            if(Paths.Overlap(backup,volume.Root)||Paths.Overlap(AppSettings.WorkFolder,volume.Root))throw new IOException("Backups and downloads must be on your Mac, separate from the SD card.");
            AppSettings.Log("Starting HATS update for "+volume.Root); // Check log access before writing to the card.
            await Task.Run(()=>new Installer{WorkFolder=AppSettings.WorkFolder,MaximumTargetFileBytes=current.FileSystem=="msdos"?uint.MaxValue:long.MaxValue}.RunAsync(selected,asset,current.Root,backup,preserved,Log,p=>
            {
                long now=Environment.TickCount64;if(p.CanCancel&&p.Percent<100&&now-Interlocked.Read(ref lastProgress)<120)return;Interlocked.Exchange(ref lastProgress,now);
                Ui(()=>{progress.Value=p.Percent;status.Text=$"{p.Phase} · {p.Percent:N0}%";cancel.IsEnabled=p.CanCancel&&cancellation?.IsCancellationRequested!=true;});
            },cancellation.Token,guard.Validate));
            lastBackup=backup;status.Text="HATS updated. Your games are safe.";notice.Text="Update complete. Eject the SD card in Finder before removing it.";notice.Foreground=Brush("#BDDFC9");notice.IsVisible=true;
        }
        catch(OperationCanceledException){status.Text="Cancelled before SD changes.";Log("Preparation cancelled. Any completed backup files were retained.");}
        catch(Exception error){notice.Foreground=Brush("#F0BFC9");Error(error);}
        finally{if(Directory.Exists(backup))lastBackup=backup;busy=false;cancel.IsVisible=false;cancellation.Dispose();cancellation=null;Controls();}
    }
}
