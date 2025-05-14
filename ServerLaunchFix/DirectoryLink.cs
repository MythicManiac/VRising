using System.IO;

namespace ServerLaunchFix
{
    /// <summary>
    /// Platform-aware wrapper that creates directory links using the appropriate mechanism
    /// (Junction Points on Windows, Symbolic Links on Wine/Linux)
    /// </summary>
    public static class DirectoryLink
    {
        /// <summary>
        /// Creates a directory link from the specified link path to the target directory.
        /// Automatically uses the appropriate mechanism based on the detected platform.
        /// </summary>
        /// <param name="linkPath">The path where the link will be created</param>
        /// <param name="targetPath">The target directory the link will point to</param>
        /// <param name="overwrite">If true, overwrites an existing link or directory</param>
        /// <exception cref="IOException">Thrown when the link could not be created</exception>
        public static void Create(string linkPath, string targetPath, bool overwrite)
        {
            // Get absolute paths
            string fullLinkPath = Path.GetFullPath(linkPath);
            string fullTargetPath = Path.GetFullPath(targetPath);

            // Use symlinks when running under Wine, junction points otherwise
            if (PlatformDetector.IsRunningOnWine())
            {
                ServerLaunchFixPlugin.Instance.Log.LogInfo($"Creating symlink from {fullLinkPath} to {fullTargetPath}");
                Symlink.Create(fullLinkPath, fullTargetPath, overwrite);
            }
            else
            {
                ServerLaunchFixPlugin.Instance.Log.LogInfo($"Creating junction point from {fullLinkPath} to {fullTargetPath}");
                JunctionPoint.Create(fullLinkPath, fullTargetPath, overwrite);
            }
        }

        /// <summary>
        /// Deletes a directory link at the specified path.
        /// </summary>
        /// <param name="linkPath">The path to the link to delete</param>
        public static void Delete(string linkPath)
        {
            string fullPath = Path.GetFullPath(linkPath);
            
            if (PlatformDetector.IsRunningOnWine())
            {
                ServerLaunchFixPlugin.Instance.Log.LogInfo($"Deleting symlink at {fullPath}");
                Symlink.Delete(fullPath);
            }
            else
            {
                ServerLaunchFixPlugin.Instance.Log.LogInfo($"Deleting junction point at {fullPath}");
                JunctionPoint.Delete(fullPath);
            }
        }

        /// <summary>
        /// Determines whether the specified path exists and is a directory link.
        /// </summary>
        /// <param name="path">The path to check</param>
        /// <returns>True if the path is a directory link, otherwise false</returns>
        public static bool Exists(string path)
        {
            string fullPath = Path.GetFullPath(path);
            
            return PlatformDetector.IsRunningOnWine() 
                ? Symlink.Exists(fullPath) 
                : JunctionPoint.Exists(fullPath);
        }

        /// <summary>
        /// Gets the target of the specified directory link.
        /// </summary>
        /// <param name="linkPath">The path to the directory link</param>
        /// <returns>The target path of the link</returns>
        /// <exception cref="IOException">Thrown when the path is not a valid link</exception>
        public static string GetTarget(string linkPath)
        {
            string fullPath = Path.GetFullPath(linkPath);
            
            return PlatformDetector.IsRunningOnWine() 
                ? Symlink.GetTarget(fullPath) 
                : JunctionPoint.GetTarget(fullPath);
        }
    }
}