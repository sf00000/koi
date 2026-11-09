using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Koi.Tests
{
    internal static class LaunchTests
    {
        sealed class FakePlatform : ILaunchPlatform
        {
            public ShortcutInfo Shortcut;
            public ActivationResult Activation = new ActivationResult(ActivationStatus.NotRunning);
            public Exception ResolveError, ActivationError, StartError;
            public int ResolveCalls, ActivationCalls, StartCalls;
            public string ActivatedPath;
            public ProcessStartInfo LastStart;

            public ShortcutInfo ResolveShortcut(string path)
            {
                ResolveCalls++;
                if (ResolveError != null) throw ResolveError;
                return Shortcut;
            }
            public ActivationResult Activate(string path)
            {
                ActivationCalls++;
                ActivatedPath = path;
                if (ActivationError != null) throw ActivationError;
                return Activation;
            }
            public void Start(ProcessStartInfo startInfo)
            {
                StartCalls++;
                LastStart = startInfo;
                if (StartError != null) throw StartError;
            }
        }

        static readonly string Executable = Path.Combine(Path.GetTempPath(), "koi-launch-test", "app.exe");
        static readonly string ShortcutPath = Path.Combine(Path.GetTempPath(), "koi-launch-test", "app.lnk");

        public static void Run()
        {
            SuccessfulReuseRecordsOriginalPath();
            FailedActivationNeverStartsAgain();
            NewProcessRecordsOnlyAfterStart();
            ShortcutLaunchRetainsShellSemantics();
            SafeShortcutReusesResolvedTarget();
            UnresolvableShortcutStillUsesShell();
            NonExecutablePathsUseOneLaunchPolicy();
            TerminalPathIsNeverPowerShellSource();
            ExplicitCommandsRecordOnlyOnSuccess();
            InvalidInputAndPersistenceFailure();
        }

        static void SuccessfulReuseRecordsOriginalPath()
        {
            FakePlatform platform = new FakePlatform { Activation = new ActivationResult(ActivationStatus.Activated) };
            List<string> recent = new List<string>();
            LaunchResult result = new LaunchService(platform, recent.Add).Launch(Executable);
            TestAssert.True(result.Succeeded && result.ReusedWindow);
            TestAssert.Equal(0, platform.StartCalls);
            TestAssert.Equal(1, recent.Count);
            TestAssert.Equal(Executable, recent[0]);
        }

        static void FailedActivationNeverStartsAgain()
        {
            FakePlatform platform = new FakePlatform
            {
                Activation = new ActivationResult(ActivationStatus.Unavailable, "foreground denied")
            };
            List<string> recent = new List<string>();
            LaunchService service = new LaunchService(platform, recent.Add);
            LaunchResult denied = service.Launch(Executable);
            TestAssert.True(!denied.Succeeded);
            TestAssert.Equal("foreground denied", denied.Error);
            TestAssert.Equal(0, platform.StartCalls);
            TestAssert.Equal(0, recent.Count);
            platform.ActivationError = new InvalidOperationException("window enumeration failed");
            TestAssert.True(!service.Launch(Executable).Succeeded);
            TestAssert.Equal(0, platform.StartCalls, "An uncertain running state must never start a duplicate.");
            TestAssert.Equal(0, recent.Count);
        }

        static void NewProcessRecordsOnlyAfterStart()
        {
            FakePlatform platform = new FakePlatform();
            List<string> recent = new List<string>();
            LaunchService service = new LaunchService(platform, recent.Add);
            LaunchResult result = service.Launch("\"" + Executable + "\"");
            TestAssert.True(result.Succeeded && !result.ReusedWindow);
            TestAssert.Equal(Executable, platform.LastStart.FileName);
            TestAssert.True(platform.LastStart.UseShellExecute);
            TestAssert.Equal(1, recent.Count);
            platform.StartError = new InvalidOperationException("file missing");
            TestAssert.True(!service.Launch(Executable).Succeeded);
            TestAssert.Equal(1, recent.Count, "A failed launch must not enter recent history.");
        }

        static void ShortcutLaunchRetainsShellSemantics()
        {
            FakePlatform platform = new FakePlatform
            {
                Shortcut = new ShortcutInfo { TargetPath = Executable, Arguments = "--profile research" }
            };
            List<string> recent = new List<string>();
            LaunchService service = new LaunchService(platform, recent.Add);
            TestAssert.True(service.Launch(ShortcutPath).Succeeded);
            TestAssert.Equal(0, platform.ActivationCalls);
            TestAssert.Equal(ShortcutPath, platform.LastStart.FileName);
            TestAssert.Equal("", platform.LastStart.Arguments);
            TestAssert.True(platform.LastStart.UseShellExecute);
            TestAssert.Equal(ShortcutPath, recent[0]);

            platform.Shortcut.Arguments = "";
            platform.Shortcut.WorkingDirectory = Path.Combine(Path.GetTempPath(), "different-project");
            TestAssert.True(service.Launch(ShortcutPath).Succeeded);
            TestAssert.Equal(0, platform.ActivationCalls, "A shortcut's project directory is part of its launch semantics.");
            TestAssert.Equal(ShortcutPath, platform.LastStart.FileName);
            TestAssert.Equal("", platform.LastStart.WorkingDirectory);
        }

        static void SafeShortcutReusesResolvedTarget()
        {
            FakePlatform platform = new FakePlatform
            {
                Shortcut = new ShortcutInfo { TargetPath = Executable, WorkingDirectory = Path.GetDirectoryName(Executable) },
                Activation = new ActivationResult(ActivationStatus.Activated)
            };
            List<string> recent = new List<string>();
            LaunchResult result = new LaunchService(platform, recent.Add).Launch(ShortcutPath);
            TestAssert.True(result.Succeeded && result.ReusedWindow);
            TestAssert.Equal(Executable, platform.ActivatedPath);
            TestAssert.Equal(0, platform.StartCalls);
            TestAssert.Equal(ShortcutPath, recent[0], "History must retain the shortcut, not rewrite it to the executable.");
            platform.Shortcut.WorkingDirectory = "";
            platform.Activation = new ActivationResult(ActivationStatus.NotRunning);
            TestAssert.True(new LaunchService(platform, null).Launch(ShortcutPath).Succeeded);
            TestAssert.Equal(ShortcutPath, platform.LastStart.FileName, "The no-process fallback must still start the original link.");
        }

        static void UnresolvableShortcutStillUsesShell()
        {
            FakePlatform platform = new FakePlatform { ResolveError = new InvalidOperationException("COM unavailable") };
            TestAssert.True(new LaunchService(platform, null).Launch(ShortcutPath).Succeeded);
            TestAssert.Equal(0, platform.ActivationCalls);
            TestAssert.Equal(ShortcutPath, platform.LastStart.FileName);
        }

        static void NonExecutablePathsUseOneLaunchPolicy()
        {
            string[] paths = { Path.GetDirectoryName(Executable), Path.ChangeExtension(Executable, ".pdf"),
                "https://example.test/Download/App.exe", "https://example.test/links/app.lnk", "shell:AppsFolder\\Example!App" };
            FakePlatform platform = new FakePlatform();
            List<string> recent = new List<string>();
            LaunchService service = new LaunchService(platform, recent.Add);
            foreach (string path in paths)
            {
                TestAssert.True(service.Launch(path).Succeeded);
                TestAssert.Equal(path, platform.LastStart.FileName);
            }
            TestAssert.Equal(paths.Length, recent.Count);
            TestAssert.Equal(0, platform.ActivationCalls);
            TestAssert.Equal(0, platform.ResolveCalls, "URLs ending in .lnk must not be parsed as local shortcuts.");
        }

        static void TerminalPathIsNeverPowerShellSource()
        {
            string directory = "C:\\Projects\\O'Brien; Write-Output harmless";
            ProcessStartInfo info = LaunchPolicy.PowerShellAt(directory);
            TestAssert.Equal("powershell.exe", info.FileName);
            TestAssert.Equal("-NoExit", info.Arguments);
            TestAssert.Equal(directory, info.WorkingDirectory);
            TestAssert.True(!info.UseShellExecute);
            TestAssert.Equal("\"C:\\\\\"", LaunchPolicy.QuoteArgument("C:\\"));
            TestAssert.Equal("\"a b\"", LaunchPolicy.QuoteArgument("a b"));
            TestAssert.Equal("\"a\\\"b\"", LaunchPolicy.QuoteArgument("a\"b"));
        }

        static void ExplicitCommandsRecordOnlyOnSuccess()
        {
            FakePlatform platform = new FakePlatform();
            List<string> recent = new List<string>();
            LaunchService service = new LaunchService(platform, recent.Add);
            ProcessStartInfo info = LaunchPolicy.PowerShellAt(Path.GetTempPath());
            TestAssert.True(service.LaunchCommand(info, info.WorkingDirectory).Succeeded);
            TestAssert.Equal(1, recent.Count);
            platform.StartError = new InvalidOperationException("cannot start terminal");
            TestAssert.True(!service.LaunchCommand(info, info.WorkingDirectory).Succeeded);
            TestAssert.Equal(1, recent.Count);
        }

        static void InvalidInputAndPersistenceFailure()
        {
            FakePlatform platform = new FakePlatform();
            LaunchService service = new LaunchService(platform, delegate { throw new IOException("save failed"); });
            TestAssert.True(!service.Launch("  ").Succeeded);
            TestAssert.Equal(0, platform.StartCalls);
            TestAssert.True(service.Launch(Executable).Succeeded, "A history failure does not undo a successful launch.");
            TestAssert.Equal(1, platform.StartCalls);
        }
    }
}
