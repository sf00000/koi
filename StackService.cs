using System;
using System.Collections.Generic;

namespace Koi
{
    // 与窗口和磁盘无关的文件堆规则。StackMembers 是新格式的权威数据，
    // Stack 继续保存路径镜像，让旧配置可读、新配置也能被旧版本打开。
    public static class StackService
    {
        public static void Normalize(ItemCfg item)
        {
            if (item == null) return;
            List<ItemCfg> members = new List<ItemCfg>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (item.StackMembers != null && item.StackMembers.Count > 0)
            {
                foreach (ItemCfg member in item.StackMembers)
                    AddUnique(members, seen, member);
            }
            else if (item.Stack != null && item.Stack.Count > 0)
            {
                string primary = PathIdentity.Normalize(item.Path);
                foreach (string path in item.Stack)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    ItemCfg member = PathIdentity.Normalize(path) == primary
                        ? CloneLeaf(item)
                        : new ItemCfg { Path = path, Name = DefaultName(path), Group = item.Group };
                    AddUnique(members, seen, member);
                }
            }
            else
            {
                item.Stack = null;
                item.StackMembers = null;
                return;
            }

            if (members.Count == 0)
            {
                item.Stack = null;
                item.StackMembers = null;
            }
            else if (members.Count == 1)
                CopyLeaf(item, members[0]);
            else
            {
                // 保留堆自己的别名；代表文件变更时同步名称与图标，避免显示旧文件。
                if (PathIdentity.Normalize(item.Path) != PathIdentity.Normalize(members[0].Path))
                {
                    item.Name = members[0].Name;
                    item.Icon = members[0].Icon;
                }
                item.Path = members[0].Path;
                SetMembers(item, members);
            }
        }

        public static bool IsStack(ItemCfg item)
        {
            if (item == null) return false;
            if (item.StackMembers == null && item.Stack != null) Normalize(item);
            return item.StackMembers != null && item.StackMembers.Count >= 2;
        }

        public static List<ItemCfg> GetMembers(ItemCfg item)
        {
            Normalize(item);
            List<ItemCfg> result = new List<ItemCfg>();
            if (IsStack(item))
                foreach (ItemCfg member in item.StackMembers) result.Add(CloneLeaf(member));
            return result;
        }

        public static bool ContainsPath(ItemCfg item, string path)
        {
            if (item == null || string.IsNullOrWhiteSpace(path)) return false;
            Normalize(item);
            string key = PathIdentity.Normalize(path);
            if (PathIdentity.Normalize(item.Path) == key) return true;
            if (IsStack(item))
                foreach (ItemCfg member in item.StackMembers)
                    if (PathIdentity.Normalize(member.Path) == key) return true;
            return false;
        }

        // 只有成功合并后，调用方才可以删除独立的拖动源。
        public static bool Merge(ItemCfg target, ItemCfg incoming)
        {
            if (target == null || incoming == null || ReferenceEquals(target, incoming) ||
                string.IsNullOrWhiteSpace(target.Path) || string.IsNullOrWhiteSpace(incoming.Path)) return false;
            Normalize(target);
            Normalize(incoming);
            if (IsStack(incoming) || ContainsPath(target, incoming.Path)) return false;
            List<ItemCfg> members = GetMembers(target);
            if (members.Count == 0) members.Add(CloneLeaf(target));
            members.Add(CloneLeaf(incoming));
            SetMembers(target, members);
            return true;
        }

        // 返回快照，调用方用返回的条目替换原堆；不依赖当前 UI 所选分组。
        public static List<ItemCfg> Unstack(ItemCfg item)
        {
            return GetMembers(item);
        }

        public static ItemCfg PullOut(ItemCfg item, string path)
        {
            if (item == null || string.IsNullOrWhiteSpace(path)) return null;
            Normalize(item);
            if (!IsStack(item)) return null;
            string key = PathIdentity.Normalize(path);
            int at = item.StackMembers.FindIndex(delegate(ItemCfg m) { return PathIdentity.Normalize(m.Path) == key; });
            if (at < 0) return null;
            ItemCfg pulled = CloneLeaf(item.StackMembers[at]);
            item.StackMembers.RemoveAt(at);
            if (item.StackMembers.Count == 1)
                CopyLeaf(item, item.StackMembers[0]);
            else
            {
                if (at == 0)
                {
                    item.Path = item.StackMembers[0].Path;
                    item.Name = item.StackMembers[0].Name;
                    item.Icon = item.StackMembers[0].Icon;
                }
                SetMembers(item, item.StackMembers);
            }
            return pulled;
        }

        // 显式移动整个文件堆，覆盖每位成员的归属；之后拆开仍在该组。
        public static void MoveToGroup(ItemCfg item, string group)
        {
            if (item == null) return;
            Normalize(item);
            item.Group = group;
            if (IsStack(item))
                foreach (ItemCfg member in item.StackMembers) member.Group = group;
        }

        public static string DisplayName(ItemCfg item)
        {
            if (item == null) return "";
            return string.IsNullOrEmpty(item.Name) ? DefaultName(item.Path) : item.Name;
        }

        static void AddUnique(List<ItemCfg> members, HashSet<string> seen, ItemCfg item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;
            if (seen.Add(PathIdentity.Normalize(item.Path))) members.Add(CloneLeaf(item));
        }

        static ItemCfg CloneLeaf(ItemCfg item)
        {
            return new ItemCfg { Path = item.Path, Name = item.Name, Icon = item.Icon, Group = item.Group };
        }

        static void CopyLeaf(ItemCfg target, ItemCfg source)
        {
            target.Path = source.Path;
            target.Name = source.Name;
            target.Icon = source.Icon;
            target.Group = source.Group;
            target.Stack = null;
            target.StackMembers = null;
        }

        static void SetMembers(ItemCfg item, List<ItemCfg> members)
        {
            item.StackMembers = members;
            item.Stack = new List<string>();
            foreach (ItemCfg member in members) item.Stack.Add(member.Path);
        }

        static string DefaultName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string name = path.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }
    }
}
