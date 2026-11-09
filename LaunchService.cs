using System;
using System.Diagnostics;
using System.IO;

namespace Koi
{
    // Platform operations are injected so launch decisions can be tested without WPF or Win32.
    internal interface ILaunchPlatform
    {
        ShortcutInfo ResolveShortcut(string path);
        ActivationResult Activate(string executablePath);
        void Start(ProcessStartInfo startInfo);
    }

    internal sealed class ShortcutInfo
    {
        public string TargetPath;
        public string Arguments;
        public string WorkingDirectory;
    }

    internal enum ActivationStatus { NotRunning, Activated, Unavailable }

    internal sealed class ActivationResult
    {
        public readonly ActivationStatus Status;
        public readonly string Error;
        public ActivationResult(ActivationStatus status, string error = null)
        {
            Status = status;
            Error = error;
        }
    }

    internal sealed class LaunchResult
    {
        public readonly bool Succeeded;
        public readonly bool ReusedWindow;
        public readonly string Error;
        public LaunchResult(bool succeeded, bool reusedWindow, string error = null)
        {
            Succeeded = succeeded;
            ReusedWindow = reusedWindow;
            Error = error;
        }
    }

    internal static class LaunchPolicy
    {
        public static string CleanPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string clean = path.Trim();
            if (clean.Length >= 2 && clean[0] == '"' && clean[clean.Length - 1] == '"')
                clean = clean.Substring(1, clean.Length - 2).Trim();
            // Do not expand percent-encoded URLs as environment variables.
            Uri uri;
            if (Uri.TryCreate(clean, UriKind.Absolute, out uri) && !uri.IsFile) return clean;
            return Environment.ExpandEnvironmentVariables(clean);
        }

        public static bool IsExecutable(string path)
        {
            return HasFileExtension(path, ".exe");
        }

        static bool HasFileExtension(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            Uri uri;
            if (Uri.TryCreate(path, UriKind.Absolute, out uri) && !uri.IsFile) return false;
            return string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsShortcut(string path)
        {
            return HasFileExtension(path, ".lnk");
        }

        public static bool CanReuseShortcut(ShortcutInfo shortcut)
        {
            if (shortcut == null || !IsExecutable(shortcut.TargetPath)
                || !string.IsNullOrWhiteSpace(shortcut.Arguments)) return false;
            if (string.IsNullOrWhiteSpace(shortcut.WorkingDirectory)) return true;
            string targetDirectory = Path.GetDirectoryName(CleanPath(shortcut.TargetPath));
            return !string.IsNullOrEmpty(targetDirectory)
                && PathIdentity.Normalize(targetDirectory) == PathIdentity.Normalize(shortcut.WorkingDirectory);
        }

        public static ProcessStartInfo PowerShellAt(string directory)
        {
            // The path is data passed as a working directory, never PowerShell source code.
            return new ProcessStartInfo("powershell.exe", "-NoExit")
            {
                WorkingDirectory = directory,
                UseShellExecute = false
            };
        }

        // Windows command-line quoting, including trailing backslashes in a directory root.
        public static string QuoteArgument(string value)
        {
            if (value == null) value = "";
            System.Text.StringBuilder result = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
            return result.ToString();
        }
    }

    internal sealed class LaunchService
    {
        readonly ILaunchPlatform platform;
        readonly Action<string> recordRecent;

        public LaunchService(ILaunchPlatform platform, Action<string> recordRecent)
        {
            if (platform == null) throw new ArgumentNullException("platform");
            this.platform = platform;
            this.recordRecent = recordRecent;
        }

        public LaunchResult Launch(string path)
        {
            try
            {
                string originalPath = LaunchPolicy.CleanPath(path);
                if (originalPath.Length == 0) return new LaunchResult(false, false, "路径为空。");
                string reuseTarget = LaunchPolicy.IsExecutable(originalPath) ? originalPath : null;
                if (LaunchPolicy.IsShortcut(originalPath))
                {
                    ShortcutInfo shortcut = null;
                    try { shortcut = platform.ResolveShortcut(originalPath); } catch { }
                    if (LaunchPolicy.CanReuseShortcut(shortcut)) reuseTarget = LaunchPolicy.CleanPath(shortcut.TargetPath);
                }
                if (reuseTarget != null)
                {
                    ActivationResult activation = platform.Activate(reuseTarget);
                    if (activation == null) return new LaunchResult(false, false, "无法确认已有程序的窗口状态。");
                    if (activation.Status == ActivationStatus.Activated) return Success(originalPath, true);
                    if (activation.Status == ActivationStatus.Unavailable)
                        return new LaunchResult(false, false, activation.Error ?? "已有程序无法切到前台，请从任务栏切换。");
                }
                // In particular, start the original .lnk, preserving arguments and working directory.
                platform.Start(new ProcessStartInfo(originalPath) { UseShellExecute = true });
                return Success(originalPath, false);
            }
            catch (Exception ex) { return new LaunchResult(false, false, ex.Message); }
        }

        public LaunchResult LaunchCommand(ProcessStartInfo startInfo, string recentPath)
        {
            try
            {
                if (startInfo == null) throw new ArgumentNullException("startInfo");
                platform.Start(startInfo);
                return Success(recentPath, false);
            }
            catch (Exception ex) { return new LaunchResult(false, false, ex.Message); }
        }

        LaunchResult Success(string path, bool reused)
        {
            // A persistence failure must not turn a successful start into a retry/duplicate launch.
            if (recordRecent != null && !string.IsNullOrEmpty(path))
                try { recordRecent(path); } catch { }
            return new LaunchResult(true, reused);
        }
    }
}
