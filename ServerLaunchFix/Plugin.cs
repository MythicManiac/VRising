using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.IL2CPP;
using UnityEngine;
using HarmonyLib;
using ProjectM.Shared;

namespace ServerLaunchFix
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class ServerLaunchFixPlugin : BasePlugin
    {
        public static ServerLaunchFixPlugin Instance;
        private Harmony _harmony;
        public override void Load()
        {
            if (ServerLaunchFix.IsClient)
            {
                _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
                _harmony.PatchAll();
            }

            Instance = this;
        }

        public override bool Unload()
        {
            _harmony?.UnpatchSelf();
            return true;
        }
    }

    class ServerLaunchFix
    {
        public static readonly ServerLaunchFix Instance = new();
        private readonly Stack<Dictionary<string, string>> _environmentStack = new();

        [DllImport("kernel32.dll")]
        static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, SymbolicLink dwFlags);
        private enum SymbolicLink
        {
            File = 0,
            Directory = 1
        }

        private const string DoorstopConfig = @"
[UnityDoorstop]
# Specifies whether assembly executing is enabled
enabled=true
# Specifies the path (absolute, or relative to the game's exe) to the DLL/EXE that should be executed by Doorstop
targetAssembly=BepInEx\core\BepInEx.IL2CPP.dll
# Specifies whether Unity's output log should be redirected to <current folder>\output_log.txt
redirectOutputLog=false

[MonoBackend]
runtimeLib=..\mono\MonoBleedingEdge\EmbedRuntime\mono-2.0-sgen.dll
configDir=..\mono\MonoBleedingEdge\etc
corlibDir=..\mono\Managed
# Specifies whether the mono soft debugger is enabled
debugEnabled=false
# Specifies whether the mono soft debugger should suspend the process and wait for the remote debugger
debugSuspend=false
# Specifies the listening address the soft debugger
debugAddress=127.0.0.1:10000
";
        private const string BepInExConfig = @"
[Logging.Console]

## Enables showing a console for log output.
# Setting type: Boolean
# Default value: false
Enabled = false
";

        public void PushDoorstopEnvironment()
        {
            var doorstopEnv = new Dictionary<string, string>();
            foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>())
            {
                if (!key.StartsWith("DOORSTOP_")) continue;
                doorstopEnv[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, null);
            }
            _environmentStack.Push(doorstopEnv);
        }

        public void PopDoorstopEnvironment()
        {
            var doorstopEnv = _environmentStack.Pop();
            foreach (var (key, val) in doorstopEnv)
            {
                Environment.SetEnvironmentVariable(key, val);
            }
        }

        public static bool IsClient => Application.productName == "VRising";

        public static bool IsServerExe(string filePath)
        {
            return Path.GetFileName(filePath) == "VRisingServer.exe";
        }

        private static string PrepareServerBepInEx()
        {
            var profileDir = Path.GetDirectoryName(Paths.BepInExRootPath);
            if (profileDir == null)
            {
                ServerLaunchFixPlugin.Instance.Log.LogError("Unable to find BepInEx install dir!");
                ServerLaunchFixPlugin.Instance.Log.LogError("Server mods might not work");
                return null;
            }

            var serverBepInExDir = Path.Combine(profileDir, "BepInEx_Server");
            if (!Directory.Exists(serverBepInExDir))
            {
                Directory.CreateDirectory(serverBepInExDir);
            }

            foreach (var entry in Directory.GetDirectories(Paths.BepInExRootPath))
            {
                var name = Path.GetFileName(entry);
                var destination = Path.Combine(serverBepInExDir, name);
                if (name is "cache" or "config" or "unhollowed")
                {
                    RecursiveCopyIfNewer(entry, destination);
                }
                else
                {
                    JunctionPoint.Create(Path.GetFullPath(destination), Path.GetFullPath(entry), true);
                }
            }

            File.WriteAllText(Path.Combine(serverBepInExDir, "config", "BepInEx.cfg"), BepInExConfig);
            return Path.Combine(serverBepInExDir, "core", "BepInEx.IL2CPP.dll");
        }

        private static void RecursiveCopyIfNewer(string source, string destination)
        {
            if (!Directory.Exists(destination))
                Directory.CreateDirectory(destination);

            foreach (var entry in Directory.GetFiles(source))
            {
                var target = Path.Combine(destination, Path.GetFileName(entry));
                if (File.Exists(target))
                    if (new FileInfo(entry).LastWriteTime <= new FileInfo(target).LastWriteTime)
                        continue;
                File.Copy(entry, target, true);
            }
            foreach (var entry in Directory.GetDirectories(source))
            {
                RecursiveCopyIfNewer(entry, Path.Combine(destination, Path.GetFileName(entry)));
            }
        }

        public static void PrepareBuiltInServer()
        {
            if (!IsClient) return;

            const string doorstopFilename = "winhttp.dll";
            var doorstopPath = Path.Combine(Paths.GameRootPath, doorstopFilename);
            if (!File.Exists(doorstopPath))
            {
                ServerLaunchFixPlugin.Instance.Log.LogError("Doorstop not found, unable to copy to server!");
                ServerLaunchFixPlugin.Instance.Log.LogError("Server mods might not work");
                return;
            }

            var serverDir = Path.Combine(Paths.GameRootPath, "VRising_Server");
            if (!Directory.Exists(serverDir))
            {
                ServerLaunchFixPlugin.Instance.Log.LogError("Built-in server not found, unable to configure mods!");
                ServerLaunchFixPlugin.Instance.Log.LogError("Server mods might not work");
                return;
            }

            File.Copy(doorstopPath, Path.Combine(serverDir, doorstopFilename), true);
            File.WriteAllText(Path.Combine(serverDir, "doorstop_config.ini"), DoorstopConfig);
        }

        public static string BuildServerLaunchExtraArgs()
        {
            var doorstopTarget = PrepareServerBepInEx();
            if (doorstopTarget != null)
            {
                return $" --doorstop-enable true --doorstop-target \"{doorstopTarget}\"";
            }

            return "";
        }
    }

    [HarmonyPatch(typeof(StunProcess_PInvoke), nameof(StunProcess_PInvoke.Start))]
    static class ProcessLaunchPatch
    {
        static void Prefix(string fileName, ref string arguments, string dir, bool hidden, bool redirectStandardInput)
        {
            if (ServerLaunchFix.IsServerExe(fileName))
            {
                ServerLaunchFix.PrepareBuiltInServer();
                ServerLaunchFix.Instance.PushDoorstopEnvironment();
                arguments += ServerLaunchFix.BuildServerLaunchExtraArgs();
            }
        }

        static void Postfix(string fileName, ref string arguments, string dir, bool hidden, bool redirectStandardInput)
        {
            if (ServerLaunchFix.IsServerExe(fileName))
            {
                ServerLaunchFix.Instance.PopDoorstopEnvironment();
            }
        }
    }
}
