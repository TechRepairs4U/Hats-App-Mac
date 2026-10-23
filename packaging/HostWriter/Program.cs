using Microsoft.NET.HostModel.AppHost;
if(args.Length!=3)throw new ArgumentException("Expected apphost template, destination and relative managed entry point.");
HostWriter.CreateAppHost(args[0],args[1],args[2],enableMacOSCodeSign:true);
