using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hats.Core;

namespace Hats.Mac;

public sealed class MainWindow : Window
{
    readonly AppSettings settings = AppSettings.Load();
    readonly ComboBox drives = new() { PlaceholderText="Choose your SD card", HorizontalAlignment=HorizontalAlignment.Stretch, MinHeight=32 };
    readonly ComboBox packs = new() { PlaceholderText="Choose a HATS pack", HorizontalAlignment=HorizontalAlignment.Stretch, MinHeight=32 };
    readonly TextBlock driveDetail = Text("Connect your SD card or Switch in USB storage mode.",12,true);
    readonly TextBlock releaseTitle = Text("Checking GitHub…",16);
    readonly TextBlock releaseDetail = Text("Latest release from TechRepairs4U/HATS",12,true);
    readonly TextBlock status = Text("Choose a drive and a pack to begin.",12);
    readonly TextBlock notice = Text("",12);
    readonly TextBlock backupSummary = Text("",11,true);
    readonly Button refreshDrive = new() {Content="Refresh"};
    readonly Button refreshRelease = new() {Content="Check releases"};
    readonly Button install = new() {Name="InstallButton",Content="Install HATS",Height=38,MinWidth=126,IsEnabled=false};
    readonly Button cancel = new() {Content="Cancel",Height=38,IsVisible=false};
    readonly Button options = new() {Content="Options",Height=38};
    readonly Button info = new() {Content="Info",Height=38};
    readonly Button savedLog = new() {Content="Open saved log",Padding=new Thickness(9,4)};
    readonly ProgressBar progress = new() {Minimum=0,Maximum=100,Height=3,IsVisible=false};
    readonly SelectableTextBlock logText = new() {Name="LogText",FontFamily="Menlo, Consolas, monospace",FontSize=11,TextWrapping=TextWrapping.Wrap,Foreground=Brush("#D9D0E5")};
    readonly ScrollViewer logScroll = new() {Name="LogScroll",HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    readonly DispatcherTimer timer = new() {Interval=TimeSpan.FromSeconds(15)};
    readonly List<string> logLines = [];
    HatsRelease? release;
    AppRelease? availableUpdate;
    bool busy,scanning,fetching,closed,dialogOpen,checkingUpdate,followLog,scannedOnce;
    string lastDriveSummary="",lastPhase="";
    int lastProgressBucket=-1;
    CancellationTokenSource? cancellation;
    long lastProgress;
    public bool PreviewMode { get; }
    public static Version CurrentVersion => AppUpdateService.ParseVersion(typeof(MainWindow).Assembly.GetName().Version!.ToString());
    static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
    static TextBlock Text(string text,double size,bool muted=false) => new() {Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Foreground=Brush(muted?"#AEA5C1":"#F5F0FC"),VerticalAlignment=VerticalAlignment.Center};
    static StackPanel Stack(params Control[] children) {var stack=new StackPanel {Spacing=6};foreach(var child in children)stack.Children.Add(child);return stack;}
    static Grid Row(Control left,Control right) {var row=new Grid {ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=10};row.Children.Add(left);Grid.SetColumn(right,1);row.Children.Add(right);return row;}
    static Border Card(Control content) => new(){Background=Brush("#191522"),BorderBrush=Brush("#3D334D"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(12,9),Child=content};
    static Image Art(string file,double width,double height) => new(){Source=new Bitmap(AssetLoader.Open(new Uri("avares://MagicMonkei.HATS/Assets/"+file))),Width=width,Height=height,Stretch=Stretch.Uniform};
    public MainWindow(bool preview=false)
    {
        PreviewMode=preview;Title="Magic Monkei · HATS Installer";Width=680;Height=620;MinWidth=500;MinHeight=480;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;WindowState=WindowState.Normal;
        Background=Brush("#100C18");FontFamily="Inter, -apple-system, sans-serif";
        var header=new Grid {Height=64,ClipToBounds=true};
        var artwork=Art("monkei-background.png",128*16d/9,128);artwork.HorizontalAlignment=HorizontalAlignment.Right;artwork.VerticalAlignment=VerticalAlignment.Top;artwork.Margin=new Thickness(0,-64*.12,0,0);header.Children.Add(artwork);
        var fade=new LinearGradientBrush {StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(1,0,RelativeUnit.Relative),GradientStops={new GradientStop(Color.Parse("#100C18"),0),new GradientStop(Color.Parse("#100C18"),.45),new GradientStop(Color.Parse("#00100C18"),.83)}};
        header.Children.Add(new Border {Background=fade});
        var brand=Text("MAGIC MONKEI",23);brand.FontWeight=FontWeight.SemiBold;brand.LetterSpacing=1;
        var sub=Text("HATS INSTALLER",10);sub.LetterSpacing=2.8;sub.Foreground=Brush("#C9B0FF");
        var brandStack=Stack(brand,sub);brandStack.VerticalAlignment=VerticalAlignment.Center;brandStack.Spacing=3;header.Children.Add(brandStack);
        info.Click+=async(_,_)=>await ProtectionDetails();
        var heading=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8};heading.Children.Add(Art("fetch-hats.png",27,27));heading.Children.Add(releaseTitle);
        var card=Card(Stack(Row(Text("SD card",15),refreshDrive),drives,driveDetail));
        var packCard=Card(Stack(Row(heading,refreshRelease),packs,releaseDetail));
        notice.IsVisible=false;notice.Foreground=Brush("#FF919D");
        UpdateBackupSummary();
        var body=Stack(header,notice,card,packCard,backupSummary);body.Spacing=8;body.Margin=new Thickness(16,4,16,8);
        var setup=new ScrollViewer {Content=body,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        var root=new Grid {RowDefinitions=new RowDefinitions("Auto,*,Auto")};root.Children.Add(setup);
        logScroll.Content=logText;logScroll.Margin=new Thickness(0,6,0,0);
        logScroll.LayoutUpdated+=(_,_)=>{if(followLog){followLog=false;logScroll.ScrollToEnd();}};
        var logBody=new Grid {RowDefinitions=new RowDefinitions("Auto,*")};logBody.Children.Add(Row(Text("Activity log",13),savedLog));Grid.SetRow(logScroll,1);logBody.Children.Add(logScroll);
        var logPanel=Card(logBody);logPanel.Name="LogPanel";logPanel.Margin=new Thickness(16,0,16,10);Grid.SetRow(logPanel,1);root.Children.Add(logPanel);
        savedLog.Click+=async(_,_)=>await Safe(()=>{AppSettings.Log("Opened saved activity log.");AppSettings.Open(AppSettings.LogPath);return Task.CompletedTask;});
        install.Background=Brush("#CDB9FF");install.Foreground=Brush("#1A1029");install.FontWeight=FontWeight.SemiBold;install.Padding=new Thickness(14,7);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,Spacing=7,HorizontalAlignment=HorizontalAlignment.Right};buttons.Children.Add(options);buttons.Children.Add(info);buttons.Children.Add(cancel);buttons.Children.Add(install);
        var footer=new Border {Name="Footer",Background=Brush("#171020"),BorderBrush=Brush("#3D334D"),BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(16,8),Child=Stack(progress,status,buttons)};Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
        void FitSetup(){setup.MaxHeight=Math.Max(180,Bounds.Height-footer.Bounds.Height-144);}
        SizeChanged+=(_,_)=>FitSetup();footer.SizeChanged+=(_,_)=>FitSetup();
        var menu=new MenuFlyout();
        void Item(string title,Func<Task> action) {var item=new MenuItem {Header=title};item.Click+=async(_,_)=>await Safe(action);menu.Items.Add(item);}
        Item("Backup location…",ChooseBackup);
        Item("View GitHub releases",()=>{AppSettings.Open("https://github.com/TechRepairs4U/HATS/releases");return Task.CompletedTask;});
        Item("Check for app updates…",()=>CheckUpdates(true));menu.Items.Add(new Separator());
        Item("Format SD card…",FormatCard);options.Flyout=menu;
        refreshDrive.Click+=async(_,_)=>await Scan(true);refreshRelease.Click+=async(_,_)=>await Fetch();
        drives.SelectionChanged+=(_,_)=>{if(drives.SelectedItem is MacVolume v)driveDetail.Text=$"{v.FreeBytes/1073741824d:N1} GB free of {v.TotalBytes/1073741824d:N1} GB · {v.Root}";Controls();};
        packs.SelectionChanged+=(_,_)=>Controls();install.Click+=async(_,_)=>await Install();
        cancel.Click+=(_,_)=>{cancellation?.Cancel();cancel.IsEnabled=false;status.Text="Cancelling preparation…";Log("Cancellation requested.");};
        timer.Tick+=async(_,_)=>{if(!busy&&!dialogOpen)await Scan();};
        Closing+=(_,e)=>{if(busy){e.Cancel=true;notice.Text="Keep the app open until the current task finishes. You can cancel during preparation.";notice.IsVisible=true;}};
        Closed+=(_,_)=>{closed=true;timer.Stop();};
        Opened+=async(_,_)=>
        {
            if(!PreviewMode)
            {
                var screen=Screens.ScreenFromWindow(this);if(screen!=null){Width=Math.Min(680,screen.WorkingArea.Width/screen.Scaling-32);Height=Math.Min(620,screen.WorkingArea.Height/screen.Scaling-64);}
                Log($"HATS Installer {CurrentVersion.ToString(3)} started on macOS.");Log("Backups: "+settings.BackupFolder);timer.Start();
                await Task.WhenAll(Scan(true),Fetch(),CheckUpdates(false));
            }
        };
        if(preview)SetPreview();
    }
    public void SetPreview()
    {
        drives.ItemsSource=new[]{new MacVolume("/Volumes/SWITCH","SWITCH","disk4s1","preview","msdos",128L<<30,76L<<30)};drives.SelectedIndex=0;
        packs.ItemsSource=new[]{new PackAsset(1,"HATS.MM.zip","https://github.com/TechRepairs4U/HATS/releases",217676363,null,"")};packs.SelectedIndex=0;
        releaseTitle.Text="HATS 23.0.1";releaseDetail.Text="3 ZIP packs available · Checked 05:27";status.Text="Choose a drive and a pack to begin.";install.IsEnabled=false;
        PreviewLog("HATS Installer 2.3.0 started on macOS.");PreviewLog("SD card detected: SWITCH · FAT32 · 128 GB");PreviewLog("Latest HATS release loaded. Ready when you are.");
    }
    public void PreviewLog(string message){if(!PreviewMode)throw new InvalidOperationException();AppendLog(message);}
    void Controls(){var locked=busy||dialogOpen||checkingUpdate;install.IsEnabled=!PreviewMode&&!locked&&!scanning&&!fetching&&release!=null&&drives.SelectedItem is MacVolume&&packs.SelectedItem is PackAsset;drives.IsEnabled=packs.IsEnabled=options.IsEnabled=info.IsEnabled=!locked;refreshDrive.IsEnabled=!locked&&!scanning;refreshRelease.IsEnabled=!locked&&!fetching;}
    void Ui(Action action){if(!closed)Dispatcher.UIThread.Post(()=>{if(!closed)action();});}
    void AppendLog(string message){logLines.Add($"{DateTime.Now:HH:mm:ss}  {message}");if(logLines.Count>150)logLines.RemoveRange(0,logLines.Count-150);logText.Text=string.Join('\n',logLines);followLog=true;}
    void Log(string message){try{AppSettings.Log(message);}catch(Exception e) when(e is IOException or UnauthorizedAccessException){}Ui(()=>AppendLog(message));}
    void Error(Exception error){Log(error.ToString());notice.Foreground=Brush("#FF919D");notice.Text=error.Message;notice.IsVisible=true;status.Text="Review the message above.";}
    async Task Safe(Func<Task> action){try{await action();}catch(Exception error){Error(error);}}
    void UpdateBackupSummary(){backupSummary.Text="Backups: "+(settings.BackupFolder==AppSettings.DefaultBackupFolder?"Desktop/HATSBackups":settings.BackupFolder);ToolTip.SetTip(backupSummary,settings.BackupFolder);}
    async Task Scan(bool announce=false)
    {
        if(scanning||busy)return;scanning=true;Controls();
        try
        {
            var selected=drives.SelectedItem as MacVolume;var found=await MacVolumes.ScanAsync();if(closed)return;
            drives.ItemsSource=found;drives.SelectedItem=found.FirstOrDefault(v=>v.Root==selected?.Root&&v.Uuid==selected.Uuid&&v.Device==selected.Device);
            if(!scannedOnce&&selected==null&&found.Length==1)drives.SelectedIndex=0;scannedOnce=true;
            if(drives.SelectedItem==null)driveDetail.Text=found.Length==0?"Connect an SD card formatted as FAT32 or exFAT, then Refresh.":"Choose the card you want to update.";
            var summary=string.Join("; ",found.Select(v=>v+" ("+v.Root+")"));if(announce||summary!=lastDriveSummary)Log(found.Length==0?"No supported removable SD cards found.":"Detected: "+summary);lastDriveSummary=summary;
        }
        catch(Exception error){Error(error);}finally{scanning=false;Controls();}
    }
    async Task Fetch()
    {
        if(fetching||busy)return;fetching=true;release=null;packs.ItemsSource=null;releaseTitle.Text="Checking GitHub…";Controls();Log("Checking for HATS packs…");
        try{using var service=new ReleaseService();var latest=await service.LatestAsync();if(closed)return;release=latest;releaseTitle.Text=latest.Title;releaseDetail.Text=$"{latest.Assets.Length} ZIP packs available · Checked {DateTime.Now:HH:mm}";packs.ItemsSource=latest.Assets;if(latest.Assets.Length==1)packs.SelectedIndex=0;Log($"Loaded {latest.Title}: {latest.Assets.Length} ZIP packs.");}
        catch(Exception error){releaseTitle.Text="Couldn’t load the latest release";Error(error);}finally{fetching=false;Controls();}
    }
    async Task<bool> Dialog(string title,Control body,string? accept=null)
    {
        dialogOpen=true;Controls();var result=false;
        var dialog=new Window{Title=title,Width=Math.Min(540,Math.Max(440,Bounds.Width-32)),MaxHeight=Math.Max(360,Bounds.Height-30),SizeToContent=SizeToContent.Height,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("#171020"),FontFamily=FontFamily};
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=9,HorizontalAlignment=HorizontalAlignment.Right};var close=new Button{Content=accept==null?"Done":"Cancel",IsCancel=true};close.Click+=(_,_)=>dialog.Close();buttons.Children.Add(close);
        if(accept!=null){var ok=new Button{Content=accept,Background=Brush("#CDB9FF"),Foreground=Brush("#1A1029")};ok.Click+=(_,_)=>{result=true;dialog.Close();};buttons.Children.Add(ok);}
        var content=Stack(Text(title,20),body,buttons);content.Margin=new Thickness(20);content.Spacing=15;dialog.Content=new ScrollViewer{Content=content};
        try{await dialog.ShowDialog(this);return result;}finally{dialogOpen=false;Controls();}
    }
    async Task ProtectionDetails()=>await Dialog("Info",Text($"Magic Monkei HATS Installer {CurrentVersion.ToString(3)}\n\nBefore every install, the app checks the latest protection list and creates a verified backup on your Mac. Games, ports and mods in protected paths stay in place. Only recognised HATS files are replaced. Empty SD cards are supported.\n\nApp updates are separate from HATS packs. Use Options → Check for app updates.",14));
    async Task ChooseBackup()
    {
        var folders=await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions{Title="Choose where to save HATS backups",AllowMultiple=false});
        if(folders.Count==0)return;var path=folders[0].TryGetLocalPath()??throw new IOException("Choose a folder on your Mac.");
        settings.BackupFolder=Paths.Root(path);settings.Save();UpdateBackupSummary();Log("Backup location: "+settings.BackupFolder);
    }
    async Task FormatCard()
    {
        var warning=Text("!!! CAUTION !!!\n\nFormatting erases EVERYTHING on the selected device, including games, saves, partitions and emuMMC. Formatting does not create a backup.\n\nCheck the drive label and capacity in Disk Utility before choosing Erase. Use FAT32 (MS-DOS) or exFAT for HATS. macOS may request permission to erase the device.",14);warning.Foreground=Brush("#FF747F");
        var selected=drives.SelectedItem as MacVolume;
        if(await Dialog("Format SD card",Stack(Text(selected==null?"No card selected. Choose the correct removable device in Disk Utility.":$"Selected: {selected.Name}\n{selected.Root} · {selected.TotalBytes/1073741824d:N1} GB",14),warning),"Open Disk Utility"))
        {Log("Opening Disk Utility. No formatting has been started by HATS.");AppSettings.Open("/System/Applications/Utilities/Disk Utility.app");}
    }
    async Task CheckUpdates(bool manual)
    {
        if(checkingUpdate||busy||PreviewMode||dialogOpen)return;checkingUpdate=true;Controls();
        try
        {
            Log("Checking for app updates…");using var service=new AppUpdateService();var latest=await service.LatestAsync();if(closed)return;
            availableUpdate=latest!=null&&latest.Version>CurrentVersion?latest:null;
            var message=availableUpdate!=null?$"App update {availableUpdate.DisplayVersion} is available.":latest==null?"No stable Mac app update has been published yet.":$"HATS Installer {CurrentVersion.ToString(3)} is up to date.";Log(message);
            if(!manual){if(availableUpdate!=null&&!busy){notice.Text=message+" Open Options → Check for app updates.";notice.Foreground=Brush("#C9B0FF");notice.IsVisible=true;}return;}
            if(availableUpdate==null){await Dialog("App updates",Text(message,14));return;}
            var selected=availableUpdate;
            if(await Dialog("App update available",Stack(Text($"{CurrentVersion.ToString(3)} → {selected.DisplayVersion}\n\nThe app will download and verify the update, keep the previous app for recovery, then restart. Your settings, backups and SD card are kept.",14),Text(selected.Notes.Length>1600?selected.Notes[..1600]+"…":selected.Notes,12,true)),"Update & restart"))await UpdateApp(selected);
        }
        catch(Exception error){if(manual)Error(error);else Log("App update check failed: "+error.Message);}
        finally{checkingUpdate=false;Controls();}
    }
    async Task UpdateApp(AppRelease selected)
    {
        busy=true;cancellation=new();cancel.IsVisible=true;cancel.IsEnabled=true;progress.IsVisible=true;progress.Value=0;Controls();
        try
        {
            var bundle=MacAppUpdater.CurrentBundle();MacAppUpdater.CheckDestination(bundle);
            var folder=Path.Combine(AppSettings.DataFolder,"Updates",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var download=Path.Combine(folder,AppUpdateService.AssetName);
            using var service=new AppUpdateService();AppUpdateService.Confirm(selected,await service.LatestAsync(cancellation.Token));Log("Downloading app "+selected.DisplayVersion+"…");
            var bucket=-1;await service.DownloadAsync(selected,download,p=>{Ui(()=>{progress.Value=p;status.Text=$"Downloading app update · {p:N0}%";});var next=(int)p/10;if(next!=bucket){bucket=next;Log($"App download {p:N0}%");}},cancellation.Token);
            Log("App download checksum verified. Validating the Mac app…");
            var staged=await MacAppUpdater.PrepareAsync(download,bundle,selected.Version,cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();cancel.IsEnabled=false;Log("Update verified. Restarting; the previous app is retained alongside the installed app.");
            MacAppUpdater.LaunchReplacement(bundle,staged,folder,Environment.ProcessId);busy=false;Close();
        }
        catch(OperationCanceledException){Log("App update cancelled. The installed app was not changed.");status.Text="App update cancelled.";}
        catch(Exception error){Error(error);}
        finally{busy=false;cancel.IsVisible=false;cancellation.Dispose();cancellation=null;Controls();}
    }
    async Task Install()
    {
        if(PreviewMode||busy||release is not {} selected||drives.SelectedItem is not MacVolume volume||packs.SelectedItem is not PackAsset asset)return;
        if(!await Dialog("Update this SD card?",Stack(Text($"{volume.Name}\n{volume.Root}\n\n{asset.Name}",15),Text($"Verified backup: {settings.BackupFolder}\n\nYour latest GitHub exclusions are required before any backup or SD changes.",13,true)),"Install HATS"))return;
        busy=true;cancellation=new();cancel.IsVisible=true;cancel.IsEnabled=true;progress.IsVisible=true;progress.Value=0;notice.IsVisible=false;lastPhase="";lastProgressBucket=-1;Controls();
        var backup=Path.Combine(settings.BackupFolder,DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var current=await MacVolumes.InspectAsync(volume.Root,cancellation.Token);MacVolumes.SameCard(volume,current);
            var guard=new MacVolumeGuard(current);var preserved=InstallPolicy.LoadPreserved(AppSettings.ProtectionFile);
            if(Paths.Overlap(backup,volume.Root)||Paths.Overlap(AppSettings.WorkFolder,volume.Root))throw new IOException("Backups and downloads must be on your Mac, separate from the SD card.");
            AppSettings.Log("Starting HATS update for "+volume.Root);
            Log($"Installing {asset.Name} to {volume.Name} ({volume.Root}). Backup: {backup}");
            await Task.Run(()=>new Installer{WorkFolder=AppSettings.WorkFolder,MaximumTargetFileBytes=current.FileSystem=="msdos"?uint.MaxValue:long.MaxValue}.RunAsync(selected,asset,current.Root,backup,preserved,Log,p=>
            {
                var bucket=(int)p.Percent/10;if(p.Phase!=lastPhase||bucket!=lastProgressBucket){lastPhase=p.Phase;lastProgressBucket=bucket;Log($"{p.Phase} · {p.Percent:N0}%");}
                long now=Environment.TickCount64;if(p.CanCancel&&p.Percent<100&&now-Interlocked.Read(ref lastProgress)<120)return;Interlocked.Exchange(ref lastProgress,now);
                Ui(()=>{progress.Value=p.Percent;status.Text=$"{p.Phase} · {p.Percent:N0}%";cancel.IsEnabled=p.CanCancel&&cancellation?.IsCancellationRequested!=true;});
            },cancellation.Token,guard.Validate));
            status.Text="HATS installation complete.";Log("Installation complete. Backup: "+backup);notice.Text="Eject the SD card in Finder before removing it.";notice.Foreground=Brush("#BDDFC9");notice.IsVisible=true;
        }
        catch(OperationCanceledException){status.Text="Cancelled before SD changes.";Log("Preparation cancelled. Any completed backup files were retained.");}
        catch(Exception error){Error(error);}
        finally{busy=false;cancel.IsVisible=false;cancellation.Dispose();cancellation=null;Controls();}
    }
}
