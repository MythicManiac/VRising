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
using BepInEx.Unity.IL2CPP;

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
# General options for Unity Doorstop
[General]

# Enable Doorstop?
enabled = true

# Path to the assembly to load and execute
# NOTE: The entrypoint must be of format `static void Doorstop.Entrypoint.Start()`
target_assembly = BepInEx\core\BepInEx.Unity.IL2CPP.dll

# If true, Unity's output log is redirected to <current folder>\output_log.txt
redirect_output_log = false

# If enabled, DOORSTOP_DISABLE env var value is ignored
# USE THIS ONLY WHEN ASKED TO OR YOU KNOW WHAT THIS MEANS
ignore_disable_switch = false

# Options specific to running under Unity Mono runtime
[UnityMono]

# Overrides default Mono DLL search path
# Sometimes it is needed to instruct Mono to seek its assemblies from a different path
# (e.g. mscorlib is stripped in original game)
# This option causes Mono to seek mscorlib and core libraries from a different folder before Managed
# Original Managed folder is added as a secondary folder in the search path
dll_search_path_override =

# If true, Mono debugger server will be enabled
debug_enabled = false

# When debug_enabled is true, this option specifies whether Doorstop should initialize the debugger server
# If you experience crashes when starting the debugger on debug UnityPlayer builds, try setting this to false
debug_start_server = true

# When debug_enabled is true, specifies the address to use for the debugger server
debug_address = 127.0.0.1:10000

# If true and debug_enabled is true, Mono debugger server will suspend the game execution until a debugger is attached
debug_suspend = false

# Options sepcific to running under Il2Cpp runtime
[Il2Cpp]

# Path to coreclr.dll that contains the CoreCLR runtime
coreclr_path = ..\dotnet\coreclr.dll

# Path to the directory containing the managed core libraries for CoreCLR (mscorlib, System, etc.)
corlib_dir = ..\dotnet
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
                if (name is "cache" or "config" or "interop")
                {
                    RecursiveCopyIfNewer(entry, destination);
                }
                else
                {
                    JunctionPoint.Create(Path.GetFullPath(destination), Path.GetFullPath(entry), true);
                }
            }

            File.WriteAllText(Path.Combine(serverBepInExDir, "config", "BepInEx.cfg"), BepInExConfig);
            return Path.Combine(serverBepInExDir, "core", "BepInEx.Unity.IL2CPP.dll");
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
                return $" --doorstop-enable true --doorstop-target-assembly \"{doorstopTarget}\"";
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
