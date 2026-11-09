using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Koi.Tests
{
    public static class FolderTests
    {
        public static void Run()
        {
            BoundedResultsAndCacheExpiry();
            CancellationDoesNotPopulateCache();
            ErrorsAreRetryable();
            ConcurrentScansAreBounded();
            RealDirectoryReturnsNewestFiles();
            OfflineFolderCanBecomeAvailable();
        }

        sealed class FakeReader : IFolderReader
        {
            public int Calls;
            public int FileCount = 20;
            public bool Fail;
            public Action DuringFiles;
            public IEnumerable<string> Directories(string path)
            {
                Interlocked.Increment(ref Calls);
                if (Fail) throw new IOException("offline");
                for (int i = 0; i < 100; i++) yield return path + "/directory" + i;
            }
            public IEnumerable<FolderFile> Files(string path)
            {
                for (int i = 0; i < FileCount; i++)
                {
                    if (DuringFiles != null) DuringFiles();
                    yield return new FolderFile { Path = path + "/file" + i, ModifiedUtc = new DateTime(2020, 1, 1).AddMinutes(i) };
                }
            }
        }

        static void BoundedResultsAndCacheExpiry()
        {
            FakeReader reader = new FakeReader { FileCount = FolderService.FileScanLimit + 50 };
            DateTime now = DateTime.UtcNow;
            FolderService service = new FolderService(reader, delegate { return now; });
            FolderSnapshot result = service.ReadAsync("test-folder", CancellationToken.None).GetAwaiter().GetResult();
            TestAssert.Equal(FolderService.DirectoryLimit, result.Directories.Count);
            TestAssert.True(result.MoreDirectories && result.FileScanLimited);
            TestAssert.Equal(6, result.Files.Count);
            TestAssert.Equal("test-folder/file4999", result.Files[0].Path);
            TestAssert.True(result.Files[0].ModifiedUtc > result.Files[5].ModifiedUtc);
            service.ReadAsync("test-folder", CancellationToken.None).GetAwaiter().GetResult();
            TestAssert.Equal(1, reader.Calls);
            now = now.AddSeconds(11);
            service.ReadAsync("test-folder", CancellationToken.None).GetAwaiter().GetResult();
            TestAssert.Equal(2, reader.Calls);
        }

        static void CancellationDoesNotPopulateCache()
        {
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                FakeReader reader = new FakeReader { DuringFiles = cancellation.Cancel };
                FolderService service = new FolderService(reader);
                TestAssert.Throws<OperationCanceledException>(delegate
                { service.ReadAsync("cancel", cancellation.Token).GetAwaiter().GetResult(); });
                reader.DuringFiles = null;
                service.ReadAsync("cancel", CancellationToken.None).GetAwaiter().GetResult();
                TestAssert.Equal(2, reader.Calls, "取消的结果不能污染缓存");
            }
        }

        static void ErrorsAreRetryable()
        {
            FakeReader reader = new FakeReader { Fail = true };
            FolderService service = new FolderService(reader);
            TestAssert.True(service.ReadAsync("offline", CancellationToken.None).GetAwaiter().GetResult().Error != null);
            reader.Fail = false;
            TestAssert.True(service.ReadAsync("offline", CancellationToken.None).GetAwaiter().GetResult().Error == null);
            TestAssert.Equal(2, reader.Calls);
        }

        sealed class BlockingReader : IFolderReader
        {
            public readonly CountdownEvent Started = new CountdownEvent(2);
            public readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            public int Calls;
            public IEnumerable<string> Directories(string path)
            {
                Interlocked.Increment(ref Calls);
                Started.Signal();
                if (!Release.Wait(5000)) throw new TimeoutException();
                return new string[0];
            }
            public IEnumerable<FolderFile> Files(string path) { return new FolderFile[0]; }
        }

        static void ConcurrentScansAreBounded()
        {
            BlockingReader reader = new BlockingReader();
            FolderService service = new FolderService(reader);
            Task<FolderSnapshot> first = service.ReadAsync("first", CancellationToken.None);
            Task<FolderSnapshot> second = service.ReadAsync("second", CancellationToken.None);
            try
            {
                TestAssert.True(reader.Started.Wait(3000), "两个后台读取应能并发启动");
                using (CancellationTokenSource cancellation = new CancellationTokenSource())
                {
                    Task<FolderSnapshot> third = service.ReadAsync("third", cancellation.Token);
                    cancellation.Cancel();
                    TestAssert.Throws<OperationCanceledException>(delegate { third.GetAwaiter().GetResult(); });
                    TestAssert.Equal(2, reader.Calls, "第三个请求应可在等待阶段取消，而不进入文件系统");
                }
            }
            finally
            {
                reader.Release.Set();
                Task.WaitAll(first, second);
                reader.Started.Dispose();
                reader.Release.Dispose();
            }
        }

        static void RealDirectoryReturnsNewestFiles()
        {
            string directory = Path.Combine(Path.GetTempPath(), "koi-folder-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                Directory.CreateDirectory(Path.Combine(directory, "项目.v1"));
                for (int i = 0; i < 12; i++)
                {
                    string file = Path.Combine(directory, "file" + i + ".txt");
                    File.WriteAllText(file, "test");
                    File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1).AddHours(i));
                }
                FolderSnapshot result = new FolderService().ReadAsync(directory, CancellationToken.None).GetAwaiter().GetResult();
                TestAssert.True(result.Error == null);
                TestAssert.Equal(1, result.Directories.Count);
                TestAssert.Equal(6, result.Files.Count);
                TestAssert.Equal("file11.txt", Path.GetFileName(result.Files[0].Path));
                TestAssert.Equal("file6.txt", Path.GetFileName(result.Files[5].Path));
            }
            finally { Directory.Delete(directory, true); }
        }

        static void OfflineFolderCanBecomeAvailable()
        {
            bool online = false;
            FolderService service = new FolderService(isDirectory: delegate { return online; });
            TestAssert.True(!service.IsDirectoryAsync("network-folder", CancellationToken.None).GetAwaiter().GetResult());
            online = true;
            TestAssert.True(service.IsDirectoryAsync("network-folder", CancellationToken.None).GetAwaiter().GetResult(),
                "启动时离线不能永久缓存为非目录");
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                TestAssert.Throws<OperationCanceledException>(delegate
                { service.IsDirectoryAsync("network-folder", cancellation.Token).GetAwaiter().GetResult(); });
            }
        }
    }
}
