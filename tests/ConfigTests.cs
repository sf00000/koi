using System;
using System.Collections.Generic;
using System.IO;

namespace Koi.Tests
{
    public static class ConfigTests
    {
        public static void Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "koi-config-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                RoundTripAndRecovery(directory);
                CorruptFilesAreArchived(directory);
                FailedSerializationKeepsSavedFile(directory);
                OldAndMalformedFieldsAreNormalized(directory);
                PathsPreserveRootsAndUrlCase(directory);
            }
            finally { Directory.Delete(directory, true); }
        }

        static void RoundTripAndRecovery(string directory)
        {
            string path = Path.Combine(directory, "roundtrip.xml");
            ConfigStore store = new ConfigStore(path);
            TestAssert.Equal(0, store.Load().Value.Items.Count);
            Config value = new Config { ShowNames = false, CurrentGroup = "论文" };
            value.Items.Add(new ItemCfg { Name = "论文", Path = Path.Combine(directory, "paper.pdf"), Group = "论文" });
            value.Workflows.Add(new WorkflowCfg { Name = "工作", Paths = new List<string> { "https://example.com/Case" } });
            store.Save(value);
            value.IconSize = 80;
            store.Save(value);
            Config read = store.Load().Value;
            TestAssert.Equal(80.0, read.IconSize);
            TestAssert.True(!read.ShowNames, "显式关闭名称应可往返保存");
            TestAssert.Equal("论文", read.CurrentGroup);
            TestAssert.Equal("https://example.com/Case", read.Workflows[0].Paths[0]);
            string backup = File.ReadAllText(path + ".bak");
            File.WriteAllText(path, "<broken");
            ConfigLoadResult recovered = store.Load();
            TestAssert.True(recovered.RecoveredFromBackup);
            TestAssert.Equal(64.0, recovered.Value.IconSize);
            store.Save(recovered.Value);
            TestAssert.Equal(backup, File.ReadAllText(path + ".bak"), "恢复后第一次保存不能拿损坏主配置覆盖有效备份");
            TestAssert.Equal(64.0, new ConfigStore(path).Load().Value.IconSize);
        }

        static void CorruptFilesAreArchived(string directory)
        {
            string path = Path.Combine(directory, "corrupt.xml");
            File.WriteAllText(path, "broken primary");
            File.WriteAllText(path + ".bak", "broken backup");
            ConfigStore store = new ConfigStore(path);
            ConfigLoadResult result = store.Load();
            TestAssert.True(result.Warning != null);
            TestAssert.True(store.CanSave);
            TestAssert.True(!File.Exists(path));
            TestAssert.Equal(2, Directory.GetFiles(directory, "corrupt.xml*.corrupt-*").Length);
            store.Save(result.Value);
            TestAssert.True(File.Exists(path));
        }

        static void FailedSerializationKeepsSavedFile(string directory)
        {
            string path = Path.Combine(directory, "atomic.xml");
            ConfigStore store = new ConfigStore(path);
            Config config = new Config();
            store.Save(config);
            string saved = File.ReadAllText(path);
            config.Items.Add(new ItemCfg { Name = "invalid\0character", Path = "file.txt" });
            TestAssert.Throws<InvalidOperationException>(delegate { store.Save(config); });
            TestAssert.Equal(saved, File.ReadAllText(path));
            TestAssert.Equal(0, Directory.GetFiles(directory, "atomic.xml.tmp-*").Length);
        }

        static void OldAndMalformedFieldsAreNormalized(string directory)
        {
            string path = Path.Combine(directory, "legacy.xml");
            File.WriteAllText(path, "<Config><IconSize>NaN</IconSize><Opacity>INF</Opacity>" +
                "<Left>NaN</Left><Top>-INF</Top><CurrentGroup>不存在</CurrentGroup>" +
                "<Items xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><ItemCfg xsi:nil=\"true\"/>" +
                "<ItemCfg><Path> </Path></ItemCfg></Items></Config>");
            Config loaded = new ConfigStore(path).Load().Value;
            TestAssert.Equal(64.0, loaded.IconSize);
            TestAssert.Equal(0.72, loaded.Opacity);
            TestAssert.True(loaded.Left == null && loaded.Top == null);
            TestAssert.True(loaded.ShowNames);
            TestAssert.Equal("全部", loaded.CurrentGroup);
            TestAssert.Equal(0, loaded.Items.Count);
            loaded.Recent = new List<string> { "", null, path, path.ToUpperInvariant() };
            loaded.Workflows = new List<WorkflowCfg> { null, new WorkflowCfg { Paths = null } };
            ConfigStore.Normalize(loaded);
            TestAssert.Equal(1, loaded.Recent.Count);
            TestAssert.Equal(1, loaded.Workflows.Count);
            TestAssert.Equal(0, loaded.Workflows[0].Paths.Count);
            loaded.Items.Add(new ItemCfg { StackMembers = new List<ItemCfg>
            {
                new ItemCfg { Path = "recoverable-a.txt", Group = "恢复" },
                new ItemCfg { Path = "recoverable-b.txt", Group = "恢复" }
            } });
            ConfigStore.Normalize(loaded);
            TestAssert.Equal(1, loaded.Items.Count, "应先从完整成员恢复缺失的代表路径，不能丢弃整个堆");
            TestAssert.Equal("recoverable-a.txt", loaded.Items[0].Path);
        }

        static void PathsPreserveRootsAndUrlCase(string directory)
        {
            TestAssert.Equal(PathIdentity.Normalize(directory), PathIdentity.Normalize(directory + Path.DirectorySeparatorChar));
            string root = Path.GetPathRoot(directory);
            TestAssert.Equal(root.ToLowerInvariant(), PathIdentity.Normalize(root));
            TestAssert.True(PathIdentity.Normalize("https://example.com/Case") != PathIdentity.Normalize("https://example.com/case"));
            TestAssert.Equal(PathIdentity.Normalize(Path.Combine(directory, "a", "..", "b")), PathIdentity.Normalize(Path.Combine(directory, "b")));
        }
    }
}
