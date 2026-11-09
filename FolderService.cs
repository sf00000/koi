using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Koi
{
    public sealed class FolderFile
    {
        public string Path;
        public DateTime ModifiedUtc;
    }

    public sealed class FolderSnapshot
    {
        public readonly List<string> Directories = new List<string>();
        public readonly List<FolderFile> Files = new List<FolderFile>();
        public bool MoreDirectories;
        public bool FileScanLimited;
        public string Error;
    }

    public interface IFolderReader
    {
        IEnumerable<string> Directories(string path);
        IEnumerable<FolderFile> Files(string path);
    }

    public sealed class FileSystemFolderReader : IFolderReader
    {
        public IEnumerable<string> Directories(string path) { return Directory.EnumerateDirectories(path); }

        public IEnumerable<FolderFile> Files(string path)
        {
            foreach (string file in Directory.EnumerateFiles(path))
            {
                FolderFile entry = null;
                try
                {
                    FileInfo info = new FileInfo(file);
                    info.Refresh();
                    if (info.Exists) entry = new FolderFile { Path = file, ModifiedUtc = info.LastWriteTimeUtc };
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                // Null still counts toward the scan limit when metadata cannot be read.
                yield return entry;
            }
        }
    }

    public sealed class FolderService
    {
        public const int DirectoryLimit = 40;
        public const int RecentLimit = 6;
        public const int FileScanLimit = 5000;
        const int CacheLimit = 16;
        readonly IFolderReader reader;
        readonly Func<DateTime> now;
        readonly Func<string, bool> isDirectory;
        // Cancellation cannot interrupt an OS/network filesystem call. Bound active scans.
        readonly SemaphoreSlim gate = new SemaphoreSlim(2);
        readonly Dictionary<string, CachedFolder> cache = new Dictionary<string, CachedFolder>();
        readonly object cacheLock = new object();

        sealed class CachedFolder
        {
            public FolderSnapshot Snapshot;
            public DateTime Expires;
        }

        public FolderService(IFolderReader reader = null, Func<DateTime> now = null, Func<string, bool> isDirectory = null)
        {
            this.reader = reader ?? new FileSystemFolderReader();
            this.now = now ?? delegate { return DateTime.UtcNow; };
            this.isDirectory = isDirectory ?? Directory.Exists;
        }

        // Recheck initially unavailable folders without blocking the hover/UI thread.
        public async Task<bool> IsDirectoryAsync(string path, CancellationToken cancellation)
        {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                bool exists = await Task.Run(delegate { return isDirectory(path); }, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return exists;
            }
            finally { gate.Release(); }
        }

        public async Task<FolderSnapshot> ReadAsync(string path, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string key = PathIdentity.Normalize(path);
            FolderSnapshot snapshot = Cached(key);
            if (snapshot != null) return snapshot;
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                snapshot = Cached(key);
                if (snapshot != null) return snapshot;
                snapshot = await Task.Run(delegate { return Read(path, cancellation); }, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (snapshot.Error == null)
                {
                    lock (cacheLock)
                    {
                        if (cache.Count >= CacheLimit)
                        {
                            string oldest = null;
                            foreach (KeyValuePair<string, CachedFolder> pair in cache)
                                if (oldest == null || pair.Value.Expires < cache[oldest].Expires) oldest = pair.Key;
                            if (oldest != null) cache.Remove(oldest);
                        }
                        cache[key] = new CachedFolder { Snapshot = snapshot, Expires = now().AddSeconds(10) };
                    }
                }
                return snapshot;
            }
            finally { gate.Release(); }
        }

        FolderSnapshot Cached(string key)
        {
            lock (cacheLock)
            {
                CachedFolder value;
                if (!cache.TryGetValue(key, out value)) return null;
                if (now() < value.Expires) return value.Snapshot;
                cache.Remove(key);
                return null;
            }
        }

        FolderSnapshot Read(string path, CancellationToken cancellation)
        {
            FolderSnapshot result = new FolderSnapshot();
            try
            {
                foreach (string directory in reader.Directories(path))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (result.Directories.Count == DirectoryLimit) { result.MoreDirectories = true; break; }
                    result.Directories.Add(directory);
                }
                result.Directories.Sort(StringComparer.OrdinalIgnoreCase);
                int scanned = 0;
                foreach (FolderFile file in reader.Files(path))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++scanned > FileScanLimit) { result.FileScanLimited = true; break; }
                    if (file == null) continue;
                    result.Files.Add(file);
                    result.Files.Sort(delegate(FolderFile a, FolderFile b)
                    {
                        int order = b.ModifiedUtc.CompareTo(a.ModifiedUtc);
                        return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
                    });
                    if (result.Files.Count > RecentLimit) result.Files.RemoveAt(RecentLimit);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.Error = "无法读取目录：" + ex.Message; }
            return result;
        }
    }
}
