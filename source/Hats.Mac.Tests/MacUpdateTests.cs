using System.Xml.Linq;
using Hats.Core;
using Hats.Mac;

static class MacUpdateTests
{
    public static void Register(Action<string,Action> test)
    {
        void Assert(bool value){if(!value)throw new Exception("Mac updater check failed.");}
        void Reject(Action action){try{action();}catch(Exception e) when(e is IOException or InvalidDataException){return;}throw new Exception("Expected rejection.");}
        var xml=new XElement("plist",new XElement("dict",new XElement("key","CFBundleIdentifier"),new XElement("string",MacAppUpdater.BundleId),new XElement("key","CFBundleExecutable"),new XElement("string","MagicMonkeiHATS"),new XElement("key","CFBundlePackageType"),new XElement("string","APPL"),new XElement("key","CFBundleShortVersionString"),new XElement("string","2.3.0"))).ToString();
        test("Mac updater validates bundle identity and matching release version",()=>{
            MacAppUpdater.ValidatePlist(xml,new Version(2,3,0,0));
            Reject(()=>MacAppUpdater.ValidatePlist(xml,new Version(2,3,1,0)));
            Reject(()=>MacAppUpdater.ValidatePlist(xml.Replace(MacAppUpdater.BundleId,"other.app"),new Version(2,3,0)));
            Reject(()=>MacAppUpdater.ValidatePlist(xml.Replace("MagicMonkeiHATS","otherBinary"),new Version(2,3,0)));
            Reject(()=>MacAppUpdater.ValidatePlist(xml.Replace("APPL","BNDL"),new Version(2,3,0)));
            Reject(()=>MacAppUpdater.ValidatePlist(xml.Replace("</dict>","<key>CFBundleIdentifier</key><string>other</string></dict>"),new Version(2,3,0)));
        });
        test("Backups default to Desktop/HATSBackups",()=>Assert(AppSettings.DefaultBackupFolder==Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Desktop","HATSBackups")));
        if(OperatingSystem.IsMacOS())foreach(var fail in new[]{false,true})test(fail?"Failed Mac update launch restores previous bundle":"Mac bundle replacement keeps previous app and handles quoted paths",()=>{
            var root=Path.Combine(Environment.GetEnvironmentVariable("HATS_TEST_ROOT")??Path.GetTempPath(),"update test ' " + Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            var current=Path.Combine(root,"Current.app");var staged=Path.Combine(root,"Staged.app");var previous=Path.Combine(root,"Previous.app");Directory.CreateDirectory(current);Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(current,"version"),"old");File.WriteAllText(Path.Combine(staged,"version"),"new");
            Directory.CreateDirectory(Path.Combine(current,"Contents"));File.WriteAllText(Path.Combine(current,"Contents/Info.plist"),"old metadata");
            var identityHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("old metadata"))).ToLowerInvariant();
            var launch=Path.Combine(root,"launch.sh");File.WriteAllText(launch,fail?"#!/bin/sh\nexit 1\n":"#!/bin/sh\nexit 0\n");
            MacAppUpdater.RunAsync("/bin/chmod",["+x",launch]).GetAwaiter().GetResult();
            string Quote(string value)=>"'"+value.Replace("'","'\"'\"'")+"'";
            // Substitute only external dependencies; execute the production transaction script.
            var script=Path.Combine(root,"replace.sh");File.WriteAllText(script,MacAppUpdater.ReplacementScript.Replace("/usr/bin/open",Quote(launch)).Replace("/usr/bin/codesign","/usr/bin/true").Replace("\r\n","\n"));
            var args=new[]{script,"99999999",current,staged,previous,Path.Combine(root,"update.log"),"",identityHash};
            if(fail)Reject(()=>MacAppUpdater.RunAsync("/bin/sh",args).GetAwaiter().GetResult());else MacAppUpdater.RunAsync("/bin/sh",args).GetAwaiter().GetResult();
            Assert(File.ReadAllText(Path.Combine(current,"version"))==(fail?"old":"new"));
            if(fail)Assert(File.ReadAllText(Path.Combine(staged,"version"))=="new"&&!Directory.Exists(previous));
            else Assert(File.ReadAllText(Path.Combine(previous,"version"))=="old"&&!Directory.Exists(staged));
        });
        if(OperatingSystem.IsMacOS())test("Mac updater blocks disk images and translocated app destinations",()=>{
            Reject(()=>MacAppUpdater.CheckDestination("/Volumes/HATS/Magic Monkei HATS.app"));
            Reject(()=>MacAppUpdater.CheckDestination("/private/var/folders/test/AppTranslocation/123/d/HATS.app"));
        });
    }
}

