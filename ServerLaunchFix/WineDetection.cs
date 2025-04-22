using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

namespace ServerLaunchFix
{
    public static class PlatformDetector
    {
        // Cache the detection result
        private static bool? _isWine = null;

        private static bool IsWineRegistryPresent()
        {
            try
            {
                // Method 1: Registry check
                using var key = Registry.CurrentUser.OpenSubKey("Software\\Wine");
                return key != null;
            }
            catch
            {
                // If registry access fails for any reason, return false
                return false;
            }
        }

        private static bool IsWineModuleLoaded()
        {
            try
            {
                using var currentProcess = Process.GetCurrentProcess();
                return (from ProcessModule module in currentProcess.Modules select module.FileName?.ToLowerInvariant() ?? "").Any(fileName => fileName.Contains("wine") || fileName.Contains("ntdll.dll.so"));
            }
            catch
            {
                // Module enumeration can fail in some environments
                return false;
            }
        }

        private static bool IsWineEnvPresent()
        {
            try
            {
                var wineVars = new[]
                {
                    "WINELOADERNOEXEC",
                    "WINEPREFIX",
                    "WINESERVER",
                    "WINELOADER",
                    "WINEDEBUG"
                };

                return wineVars.Any(var => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(var)));
            }
            catch
            {
                return false;
            }
        }
        
        public static bool IsRunningOnWine()
        {
            // Use cached result if available
            if (_isWine.HasValue)
                return _isWine.Value;
                
            // Perform detection and cache result
            _isWine = IsWineRegistryPresent() || IsWineModuleLoaded() || IsWineEnvPresent();
            return _isWine.Value;
        }
    }
}