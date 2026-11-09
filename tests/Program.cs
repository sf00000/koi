using System;
using System.Diagnostics;

namespace Koi.Tests
{
    internal static class Program
    {
        public static int Main()
        {
            Action[] suites = { StackTests.Run, LaunchTests.Run, ConfigTests.Run, FolderTests.Run };
            int failures = 0;
            foreach (Action suite in suites)
            {
                string name = suite.Method.DeclaringType.Name;
                Stopwatch elapsed = Stopwatch.StartNew();
                try
                {
                    suite();
                    Console.WriteLine("PASS {0} ({1} ms)", name, elapsed.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine("FAIL {0}: {1}", name, ex);
                }
            }
            Console.WriteLine("{0} suites, {1} assertions, {2} failures.", suites.Length, TestAssert.Count, failures);
            return failures == 0 ? 0 : 1;
        }
    }
}
