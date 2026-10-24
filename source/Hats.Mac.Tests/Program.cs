using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Controls;
using Hats.Core;
using Hats.Mac;
using Hats.Mac.Tests;
using System.Text;
using System.Xml.Linq;

var output=Path.GetFullPath(Environment.GetEnvironmentVariable("HATS_TEST_ROOT")??Path.Combine(AppContext.BaseDirectory,"fixtures"));Directory.CreateDirectory(output);
if(args.Contains("--render"))
{
    AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions {UseHeadlessDrawing=false}).SetupWithoutStarting();
    var window=new MainWindow(preview:true);window.Show();Dispatcher.UIThread.RunJobs();
    if(window.WindowState!=WindowState.Normal||window.Width!=680||window.Height!=620)throw new Exception("Expected compact normal window.");
    var scroll=window.GetVisualDescendants().OfType<ScrollViewer>().Single(x=>x.Name=="LogScroll");
    var footer=window.GetVisualDescendants().OfType<Border>().Single(x=>x.Name=="Footer");
    foreach(var size in new[]{(680,620),(500,480),(820,700)})
    {
        window.Width=size.Item1;window.Height=size.Item2;Dispatcher.UIThread.RunJobs();
        using(var frame=window.CaptureRenderedFrame()??throw new Exception("No UI frame rendered."))frame.Save(Path.Combine(output,$"macos-preview-{size.Item1}x{size.Item2}.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        if(scroll.Bounds.Height<65||footer.TranslatePoint(default,window)!.Value.Y+footer.Bounds.Height>window.Bounds.Height+1)throw new Exception("Log or footer does not fit.");
        var install=window.GetVisualDescendants().OfType<Button>().Single(x=>x.Name=="InstallButton");var point=install.TranslatePoint(default,window)!.Value;
        if(point.X<0||point.X+install.Bounds.Width>window.Bounds.Width||point.Y+install.Bounds.Height>window.Bounds.Height)throw new Exception("Install button clipped.");
    }
    window.Width=680;window.Height=620;
    for(int i=0;i<170;i++)window.PreviewLog($"Example activity {i}: verifying downloaded files and preparing the backup.");
    Dispatcher.UIThread.RunJobs();using(var frame=window.CaptureRenderedFrame()){}Dispatcher.UIThread.RunJobs();
    scroll.ScrollToHome();window.PreviewLog("Latest activity: verification completed.");Dispatcher.UIThread.RunJobs();
    using(var frame=window.CaptureRenderedFrame()!)frame.Save(Path.Combine(output,"macos-log-open.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    Dispatcher.UIThread.RunJobs();
    if(scroll.Offset.Y<scroll.Extent.Height-scroll.Viewport.Height-2)throw new Exception("Log did not follow the latest activity.");
    if(window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(x=>x.Name=="LogText").Text!.Split('\n').Length!=150)throw new Exception("Log history is not bounded.");
    window.Close();Console.WriteLine("PASS compact UI at 680x620, 500x480 and 820x700; expanded log and automatic scrolling");return 0;
}
void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
void Rejected(Action action){try{action();}catch(Exception e) when(e is IOException or InvalidDataException){return;}throw new Exception("Expected rejection.");}
string Plist(string? changed=null,object? replacement=null)
{
    var fields=new Dictionary<string,object>{{"MountPoint","/Volumes/SWITCH"},{"DeviceIdentifier","disk4s1"},{"VolumeUUID","CARD-1234"},{"FilesystemType","msdos"},{"FileSystemPersonality","MS-DOS FAT32"},{"VirtualOrPhysical","Physical"},{"Internal",false},{"RemovableMedia",true},{"ReadOnlyVolume",false},{"ReadOnlyMedia",false},{"TotalSize",128L<<30},{"VolumeFreeSpace",76L<<30},{"VolumeName","SWITCH"}};
    if(changed!=null){if(replacement==null)fields.Remove(changed);else fields[changed]=replacement;}
    return new XElement("plist",new XElement("dict",fields.SelectMany(p=>new[]{new XElement("key",p.Key),p.Value is bool b?new XElement(b?"true":"false"):p.Value is long l?new XElement("integer",l):new XElement("string",p.Value)}))).ToString();
}
var volume=MacVolumes.Parse(Plist());Assert(volume.Root=="/Volumes/SWITCH"&&volume.FreeBytes==76L<<30,"FAT32 card parse failed.");
foreach(var pair in new (string Key,object? Value)[]{("MountPoint","/"),("MountPoint","/Volumes/SWITCH/switch"),("ReadOnlyMedia",true),("ReadOnlyVolume",true),("ReadOnlyVolume",null),("FilesystemType","apfs"),("VolumeUUID",null),("VirtualOrPhysical","Virtual"),("DeviceIdentifier","disk;bad"),("FileSystemPersonality","MS-DOS FAT16")})Rejected(()=>MacVolumes.Parse(Plist(pair.Key,pair.Value)));
// Generate an explicit internal/non-removable fixture without relying on whitespace.
var internalDoc=XDocument.Parse(Plist("Internal",true));var removable=internalDoc.Descendants("key").Single(k=>k.Value=="RemovableMedia");removable.NextNode!.ReplaceWith(new XElement("false"));Rejected(()=>MacVolumes.Parse(internalDoc.ToString()));
Rejected(()=>MacVolumes.SameCard(volume,volume with{Uuid="different"}));Rejected(()=>MacVolumes.SameCard(volume,volume with{Device="disk5s1"}));
var bytes=new byte[2168];BitConverter.GetBytes(12345678L).CopyTo(bytes,48);BitConverter.GetBytes((uint)0x1000).CopyTo(bytes,64);Encoding.UTF8.GetBytes("msdos").CopyTo(bytes,72);Encoding.UTF8.GetBytes("/Volumes/SWITCH").CopyTo(bytes,88);Encoding.UTF8.GetBytes("/dev/disk4s1").CopyTo(bytes,1112);
var identity=MountIdentity.Decode(bytes);Assert(identity.Device=="/dev/disk4s1"&&identity.Fsid==12345678L,"Darwin statfs64 decoding failed.");identity.EnsureSame(identity);Rejected(()=>identity.EnsureSame(identity with{Fsid=88}));Rejected(()=>identity.EnsureSame(identity with{Flags=1}));Rejected(()=>identity.EnsureSame(identity with{Mount="/"}));
if(OperatingSystem.IsMacOS()){var root=MacVolumeGuard.Read("/");Assert(root.Mount=="/"&&root.FileSystem.Length>0,"Native macOS mount ABI failed.");Console.WriteLine("PASS native macOS mount read (no SD writes)");}
if(!OperatingSystem.IsWindows()){Assert(Paths.MountForPath("/Volumes/SWITCH/a",new[]{"/","/Volumes/SWITCH"})=="/Volumes/SWITCH","SD free-space mount selection");Assert(Paths.MountForPath("/Volumes/SWITCH2/a",new[]{"/","/Volumes/SWITCH"})=="/","Mount prefix boundary");}
Console.WriteLine("PASS Mac volume, identity, read-only and mount-boundary checks");
var sizeCase=Path.Combine(output,"fat32-file-limit-"+Guid.NewGuid().ToString("N"));var sizeSource=Path.Combine(sizeCase,"source");var sizeSd=Path.Combine(sizeCase,"sd");var sizeBackup=Path.Combine(sizeCase,"backup");
Directory.CreateDirectory(Path.Combine(sizeSource,"atmosphere"));Directory.CreateDirectory(Path.Combine(sizeSource,"bootloader"));Directory.CreateDirectory(Path.Combine(sizeSource,"switch"));Directory.CreateDirectory(sizeSd);File.WriteAllText(Path.Combine(sizeSource,"atmosphere/package3"),"too large for injected limit");
Rejected(()=>new Installer{MaximumTargetFileBytes=1}.Install(sizeSource,sizeSd,sizeBackup));Assert(!Directory.Exists(sizeBackup)&&!Directory.EnumerateFileSystemEntries(sizeSd).Any(),"File size failure changed SD or started backup.");
Console.WriteLine("PASS FAT32 file size preflight stops before backup or changes");
var result=await Regression.Run(args);if(result!=0)return result;
int packArg=Array.IndexOf(args,"--real-pack");
if(packArg>=0)
{
    var paths=ExclusionService.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Data/protection-test-fixture.json")));
    var fixture=Path.Combine(output,"all-protections-"+Guid.NewGuid().ToString("N"));var pack=Path.Combine(fixture,"pack");ArchiveService.Extract(args[packArg+1],pack);var source=InstallPolicy.FindPack(pack);var sd=Path.Combine(fixture,"sd");Directory.CreateDirectory(sd);
    void Write(string root,string relative,string value){var p=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,value);}
    foreach(var p in paths){var file=Path.HasExtension(p)?p:p+"/protected-probe.bin";Write(sd,file,"user data");Write(source,file,"incoming collision");}
    var plan=InstallPolicy.Plan(source,sd,paths);
    foreach(var p in paths){var probe=Path.HasExtension(p)?p:p+"/protected-probe.bin";Assert(!plan.Copy.Any(x=>Paths.Under(x,probe)||Paths.Under(probe,x))&&!plan.Replace.Any(x=>Paths.Under(x,probe)||Paths.Under(probe,x)),"Protected probe would change: "+p);}
    Assert(plan.Sysmodules.Length==16,"Full protection list blocks sysmodules.");Console.WriteLine($"PASS all {paths.Length} scanned paths excluded from copy, backup and deletion");
}
return 0;

