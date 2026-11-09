using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace Koi.Tests
{
    public static class StackTests
    {
        public static void Run()
        {
            LegacyXmlMigratesWithoutLosingPrimaryMetadata();
            MergeAndUnstackPreserveEachMembersMetadata();
            PullingPrimaryUpdatesTheRepresentativeAndCollapsesCorrectly();
            MovingAStackChangesEveryMembersGroup();
            NewXmlRoundTripRetainsMetadataAndLegacyPaths();
            DuplicateAndNestedMergesAreRejected();
            NormalizationRemovesInvalidAndDuplicateMembers();
            UnknownPullDoesNotChangeTheStack();
            ReturnedMembersAreIndependentSnapshots();
        }

        static ItemCfg Item(string name, string path, string group)
        {
            return new ItemCfg { Name = name, Path = path, Icon = path + ".png", Group = group };
        }

        static void LegacyXmlMigratesWithoutLosingPrimaryMetadata()
        {
            const string xml = "<Config><Items><ItemCfg><Name>自定义论文名</Name>" +
                "<Path>C:\\Docs\\a.pdf</Path><Icon>C:\\Icons\\a.png</Icon><Group>论文</Group>" +
                "<Stack><string>C:\\Docs\\a.pdf</string><string>C:\\Docs\\b.pdf</string></Stack>" +
                "</ItemCfg></Items></Config>";
            Config config;
            using (StringReader reader = new StringReader(xml))
                config = (Config)new XmlSerializer(typeof(Config)).Deserialize(reader);
            ItemCfg stack = config.Items[0];
            StackService.Normalize(stack);
            TestAssert.True(StackService.IsStack(stack), "旧路径列表迁移后仍是文件堆");
            TestAssert.Equal(2, stack.StackMembers.Count);
            TestAssert.Equal("自定义论文名", stack.StackMembers[0].Name);
            TestAssert.Equal("C:\\Icons\\a.png", stack.StackMembers[0].Icon);
            TestAssert.Equal("论文", stack.StackMembers[0].Group);
            TestAssert.Equal("论文", stack.StackMembers[1].Group, "旧格式未知归属继承堆的分组");
            TestAssert.Equal("b", stack.StackMembers[1].Name);
            TestAssert.Equal(2, stack.Stack.Count, "继续保留兼容旧版本的路径镜像");
        }

        static void MergeAndUnstackPreserveEachMembersMetadata()
        {
            ItemCfg a = Item("论文甲", @"C:\Docs\a.pdf", "论文");
            ItemCfg b = Item("笔记乙", @"C:\Docs\b.txt", "开发");
            TestAssert.True(StackService.Merge(a, b));
            b.Name = "后续修改不能影响堆中快照";
            List<ItemCfg> leaves = StackService.Unstack(a);
            TestAssert.Equal(2, leaves.Count);
            TestAssert.Equal("论文甲", leaves[0].Name);
            TestAssert.Equal("笔记乙", leaves[1].Name);
            TestAssert.Equal(@"C:\Docs\b.txt.png", leaves[1].Icon);
            TestAssert.Equal("论文", leaves[0].Group);
            TestAssert.Equal("开发", leaves[1].Group);
            TestAssert.True(leaves[0].Stack == null && leaves[0].StackMembers == null);
            TestAssert.True(leaves[1].Stack == null && leaves[1].StackMembers == null);
        }

        static void PullingPrimaryUpdatesTheRepresentativeAndCollapsesCorrectly()
        {
            ItemCfg a = Item("甲", @"C:\Docs\a.pdf", "A组");
            ItemCfg b = Item("乙", @"C:\Docs\b.pdf", "B组");
            ItemCfg c = Item("丙", @"C:\Docs\c.pdf", null);
            StackService.Merge(a, b);
            StackService.Merge(a, c);
            ItemCfg pulled = StackService.PullOut(a, @"c:\docs\A.PDF");
            TestAssert.Equal("甲", pulled.Name);
            TestAssert.Equal("A组", pulled.Group);
            TestAssert.Equal(b.Path, a.Path);
            TestAssert.Equal(b.Name, a.Name);
            TestAssert.Equal(b.Icon, a.Icon, "首项拖出后不能继续显示原文件自定义图标");
            TestAssert.Equal(2, a.Stack.Count);
            TestAssert.Equal(b.Path, a.Stack[0]);
            ItemCfg pulledB = StackService.PullOut(a, b.Path);
            TestAssert.Equal("B组", pulledB.Group);
            TestAssert.True(!StackService.IsStack(a));
            TestAssert.True(a.Stack == null && a.StackMembers == null);
            TestAssert.Equal(c.Name, a.Name);
            TestAssert.Equal(c.Icon, a.Icon);
            TestAssert.Equal(c.Group, a.Group, "退化成单个文件时恢复该文件原有分组（包括未分组）");
        }

        static void MovingAStackChangesEveryMembersGroup()
        {
            ItemCfg a = Item("甲", @"C:\Docs\a.pdf", "论文");
            StackService.Merge(a, Item("乙", @"C:\Docs\b.pdf", null));
            StackService.MoveToGroup(a, "归档");
            TestAssert.Equal("归档", a.Group);
            foreach (ItemCfg member in StackService.Unstack(a)) TestAssert.Equal("归档", member.Group);
            StackService.MoveToGroup(a, null);
            foreach (ItemCfg member in StackService.Unstack(a)) TestAssert.Equal<string>(null, member.Group);
            ItemCfg pulled = StackService.PullOut(a, @"C:\Docs\a.pdf");
            TestAssert.Equal<string>(null, pulled.Group);
            TestAssert.Equal<string>(null, a.Group);
        }

        static void NewXmlRoundTripRetainsMetadataAndLegacyPaths()
        {
            ItemCfg a = Item("原始名", @"C:\Docs\a.pdf", "论文");
            StackService.Merge(a, Item("第二篇", @"C:\Docs\b.pdf", "开发"));
            a.Name = "文件堆别名";
            XmlSerializer serializer = new XmlSerializer(typeof(ItemCfg));
            string xml;
            using (StringWriter writer = new StringWriter())
            {
                serializer.Serialize(writer, a);
                xml = writer.ToString();
            }
            TestAssert.True(xml.Contains("<Stack>"));
            TestAssert.True(xml.Contains("<StackMembers>"));
            ItemCfg restored;
            using (StringReader reader = new StringReader(xml)) restored = (ItemCfg)serializer.Deserialize(reader);
            StackService.Normalize(restored);
            TestAssert.Equal("文件堆别名", restored.Name);
            List<ItemCfg> members = StackService.Unstack(restored);
            TestAssert.Equal("原始名", members[0].Name, "重命名堆不能覆盖其成员名称");
            TestAssert.Equal("第二篇", members[1].Name);
            TestAssert.Equal("开发", members[1].Group);
            TestAssert.Equal(@"C:\Docs\b.pdf.png", members[1].Icon);
            TestAssert.Equal(members[1].Path, restored.Stack[1]);
        }

        static void DuplicateAndNestedMergesAreRejected()
        {
            ItemCfg a = Item("甲", @"C:\Docs\a.pdf", "论文");
            TestAssert.True(!StackService.Merge(a, Item("重复", @"c:\docs\A.pdf", "其他")));
            TestAssert.True(!StackService.IsStack(a));
            TestAssert.Equal("甲", a.Name);
            StackService.Merge(a, Item("乙", @"C:\Docs\b.pdf", "论文"));
            TestAssert.True(!StackService.Merge(a, Item("重复乙", @"c:\DOCS\b.pdf", null)));
            TestAssert.Equal(2, a.Stack.Count);
            TestAssert.True(StackService.ContainsPath(a, @"c:\docs\B.pdf"));
            ItemCfg c = Item("丙", @"C:\Docs\c.pdf", null);
            StackService.Merge(c, Item("丁", @"C:\Docs\d.pdf", null));
            TestAssert.True(!StackService.Merge(a, c));
            TestAssert.Equal(2, c.Stack.Count);
            TestAssert.True(!StackService.Merge(a, a));
        }

        static void NormalizationRemovesInvalidAndDuplicateMembers()
        {
            ItemCfg only = Item("幸存者", @"C:\Docs\a.pdf", "保留");
            ItemCfg stack = new ItemCfg
            {
                Path = only.Path,
                Name = "坏堆",
                StackMembers = new List<ItemCfg> { null, only, new ItemCfg { Path = " " }, Item("重复", @"c:\docs\A.pdf", null) }
            };
            StackService.Normalize(stack);
            TestAssert.True(!StackService.IsStack(stack));
            TestAssert.Equal(only.Name, stack.Name);
            TestAssert.Equal(only.Icon, stack.Icon);
            TestAssert.Equal(only.Group, stack.Group);
            TestAssert.True(stack.StackMembers == null && stack.Stack == null);
        }

        static void UnknownPullDoesNotChangeTheStack()
        {
            ItemCfg a = Item("甲", @"C:\Docs\a.pdf", "论文");
            StackService.Merge(a, Item("乙", @"C:\Docs\b.pdf", "开发"));
            TestAssert.True(StackService.PullOut(a, @"C:\Docs\missing.pdf") == null);
            TestAssert.Equal(2, a.Stack.Count);
            TestAssert.Equal("甲", a.Name);
            TestAssert.True(StackService.PullOut(a, null) == null);
        }

        static void ReturnedMembersAreIndependentSnapshots()
        {
            ItemCfg a = Item("甲", @"C:\Docs\a.pdf", "论文");
            StackService.Merge(a, Item("乙", @"C:\Docs\b.pdf", "开发"));
            List<ItemCfg> members = StackService.GetMembers(a);
            members[0].Name = "被修改的快照";
            members[1].Group = "变更";
            members.Clear();
            TestAssert.Equal("甲", a.StackMembers[0].Name);
            TestAssert.Equal("开发", a.StackMembers[1].Group);
            TestAssert.Equal(2, a.StackMembers.Count);
        }
    }
}
