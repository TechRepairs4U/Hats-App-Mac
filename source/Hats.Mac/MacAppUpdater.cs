using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Hats.Core;

namespace Hats.Mac;

public static class MacAppUpdater
{
    public const string BundleId="com.magicmonkei.hats-installer";
    public const string BundleName="Magic Monkei HATS.app";
    public static string CurrentBundle()
    {
        if(!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("App replacement requires macOS.");
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory!=null;directory=directory.Parent)
            if(directory.Name.EndsWith(".app",StringComparison.Ordinal))return directory.FullName;
        throw new IOException("Run HATS from its installed .app in Applications to use the updater.");
    }
    public static void CheckDestination(string bundle)
    {
        var parent=Directory.GetParent(bundle)?.FullName??throw new IOException("Invalid installed app location.");
        if(bundle.StartsWith("/Volumes/",StringComparison.Ordinal)||bundle.Contains("/AppTranslocation/",StringComparison.Ordinal))
            throw new IOException("Drag HATS to Applications, eject the disk image and open that copy before updating.");
        for(var directory=new DirectoryInfo(bundle);directory!=null;directory=directory.Parent)
            if(directory.LinkTarget!=null)throw new IOException("Move HATS to a regular Applications folder before updating.");
        var probe=Path.Combine(parent,".hats-write-check-"+Guid.NewGuid().ToString("N"));
        try{using(var stream=new FileStream(probe,FileMode.CreateNew,FileAccess.Write))stream.Flush(true);File.Delete(probe);}
        catch(UnauthorizedAccessException){throw new IOException("This Applications folder is not writable by your account. Move HATS to your own ~/Applications folder, then try again.");}
    }
    public static void ValidatePlist(string xml,Version expected)
    {
        using var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Ignore,XmlResolver=null,MaxCharactersInDocument=1048576});
        var entries=XDocument.Load(reader).Root?.Element("dict")?.Elements().ToArray()??throw new InvalidDataException("The update has no app metadata.");
        var fields=new Dictionary<string,string>();
        for(int i=0;i<entries.Length;i+=2)
        {
            if(i+1>=entries.Length||entries[i].Name!="key"||fields.ContainsKey(entries[i].Value))throw new InvalidDataException("The app metadata is ambiguous.");
            fields.Add(entries[i].Value,entries[i+1].Name=="string"?entries[i+1].Value:"");
        }
        if(fields.GetValueOrDefault("CFBundleIdentifier")!=BundleId||fields.GetValueOrDefault("CFBundleExecutable")!="MagicMonkeiHATS"||fields.GetValueOrDefault("CFBundlePackageType")!="APPL"||
           AppUpdateService.ParseVersion(fields.GetValueOrDefault("CFBundleShortVersionString")??"")!=AppUpdateService.ParseVersion(expected.ToString()))
            throw new InvalidDataException("The downloaded app's identity or version does not match the selected release.");
    }
    public static async Task<string> RunAsync(string command,IEnumerable<string> arguments,CancellationToken cancel=default)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel);deadline.CancelAfter(TimeSpan.FromMinutes(3));
        using var process=new Process{StartInfo=new(command){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true}};
        foreach(var argument in arguments)process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        try
        {
            var stdout=process.StandardOutput.ReadToEndAsync(deadline.Token);var stderr=process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);var output=await stdout;var error=await stderr;
            if(process.ExitCode!=0)throw new IOException(Path.GetFileName(command)+" failed: "+error.Trim());return output;
        }
        finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync(CancellationToken.None);}}
    }
    static async Task ValidateBundle(string bundle,Version version,CancellationToken cancel)
    {
        if(!Directory.Exists(bundle)||new DirectoryInfo(bundle).LinkTarget!=null)throw new IOException("The update does not contain a regular HATS app bundle.");
        ValidatePlist(await RunAsync("/usr/bin/plutil",["-convert","xml1","-o","-",Path.Combine(bundle,"Contents/Info.plist")],cancel),version);
        await RunAsync("/usr/bin/codesign",["--verify","--deep","--strict",bundle],cancel);
        await RunAsync("/usr/bin/lipo",[Path.Combine(bundle,"Contents/MacOS/MagicMonkeiHATS"),"-verify_arch","arm64","x86_64"],cancel);
    }
    public static async Task<string> PrepareAsync(string dmg,string installed,Version version,CancellationToken cancel=default)
    {
        if(!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException();
        CheckDestination(installed);
        var mount=Path.Combine(Path.GetDirectoryName(dmg)!,"mount-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(mount);
        var staged=Path.Combine(Path.GetDirectoryName(installed)!,".HATS-update-"+Guid.NewGuid().ToString("N")+".app");
        bool mounted=false;
        try
        {
            // Downloads keep macOS's normal first-open security checks; never clear quarantine.
            await RunAsync("/usr/bin/xattr",["-w","com.apple.quarantine",$"0081;{DateTimeOffset.UtcNow.ToUnixTimeSeconds():x};MagicMonkeiHATS;{Guid.NewGuid()}",dmg],cancel);
            await RunAsync("/usr/bin/hdiutil",["verify",dmg],cancel);
            await RunAsync("/usr/bin/hdiutil",["attach","-readonly","-nobrowse","-mountpoint",mount,dmg],cancel);mounted=true;
            var candidate=Path.Combine(mount,BundleName);await ValidateBundle(candidate,version,cancel);
            await RunAsync("/usr/bin/ditto",["--rsrc","--extattr",candidate,staged],cancel);
            // ditto preserves internal framework symlinks and code signatures.
            await ValidateBundle(staged,version,cancel);
            var quarantine=await RunAsync("/usr/bin/xattr",["-p","com.apple.quarantine",dmg],cancel);
            await RunAsync("/usr/bin/xattr",["-w","com.apple.quarantine",quarantine.Trim(),staged],cancel);
            return staged;
        }
        catch
        {
            // Only remove the fresh staging folder created by this invocation.
            if(Directory.Exists(staged))Directory.Delete(staged,true);
            throw;
        }
        finally
        {
            if(mounted){try{await RunAsync("/usr/bin/hdiutil",["detach",mount]);}catch(Exception e){AppSettings.Log("Could not eject update image: "+e.Message);}}
        }
    }
    public static string LaunchReplacement(string installed,string staged,string work,int parentPid,string? startupReport=null)
    {
        CheckDestination(installed);
        if(Path.GetDirectoryName(installed)!=Path.GetDirectoryName(staged)||!Path.GetFileName(staged).StartsWith(".HATS-update-",StringComparison.Ordinal)||new DirectoryInfo(staged).LinkTarget!=null)
            throw new IOException("The staged update is not beside the installed app.");
        var previous=Path.Combine(Path.GetDirectoryName(installed)!,"Magic Monkei HATS.previous-"+Guid.NewGuid().ToString("N")[..8]+".app");
        var script=Path.Combine(work,"replace-app.sh");var log=Path.Combine(work,"update.log");
        var identityHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(installed,"Contents/Info.plist")))).ToLowerInvariant();
        File.WriteAllText(script,ReplacementScript.Replace("\r\n","\n"));
        var start=new ProcessStartInfo("/bin/sh"){UseShellExecute=false,WorkingDirectory=work};
        foreach(var arg in new[]{script,parentPid.ToString(System.Globalization.CultureInfo.InvariantCulture),installed,staged,previous,log,startupReport??"",identityHash})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Could not start the app updater.");return log;
    }
    public const string ReplacementScript="""
        #!/bin/sh
        set -eu
        parent="$1"; current="$2"; staged="$3"; previous="$4"; log="$5"; report="$6"; expected="$7"
        exec >> "$log" 2>&1
        echo "Waiting for HATS to close."
        count=0
        while kill -0 "$parent" 2>/dev/null; do
          count=$((count + 1))
          if [ "$count" -gt 60 ]; then echo "ERROR: HATS did not close. The installed app was not changed."; exit 1; fi
          sleep 1
        done
        lock="$current.update-lock"
        if ! /bin/mkdir "$lock"; then echo "ERROR: another app update is in progress. The installed app was not changed."; exit 1; fi
        moved=0; installed=0
        rollback() {
          if [ "$moved" = 1 ]; then
            echo "ERROR: update failed; restoring previous app."
            if [ "$installed" = 1 ]; then /bin/mv "$current" "$staged"; fi
            /bin/mv "$previous" "$current"
            /usr/bin/open -n "$current" || true
          fi
          /bin/rmdir "$lock"
        }
        trap rollback EXIT
        if [ ! -d "$current" ] || [ -L "$current" ] || [ ! -d "$staged" ] || [ -L "$staged" ] || [ -e "$previous" ] || [ -L "$previous" ]; then
          echo "ERROR: an app path changed. The installed app was not changed."; exit 1
        fi
        actual=$(/usr/bin/shasum -a 256 "$current/Contents/Info.plist")
        if [ "${actual%% *}" != "$expected" ]; then echo "ERROR: the installed app changed. Check for updates again."; exit 1; fi
        /usr/bin/codesign --verify --deep --strict "$staged"
        /bin/mv "$current" "$previous"
        moved=1
        /bin/mv "$staged" "$current"
        installed=1
        if [ -n "$report" ]; then
          /usr/bin/open -n "$current" --args --startup-check "$report"
        else
          /usr/bin/open -n "$current"
        fi
        moved=0
        /bin/rmdir "$lock"
        trap - EXIT
        echo "SUCCESS: updated app opened. Previous app: $previous"
        """;
}
