using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace Koi
{
    public class ItemCfg
    {
        public string Name;
        public string Path;
        public string Icon; // 可选：自定义图标文件（png/jpg/ico），如 Store 应用无法自动取图标时使用
        public List<string> Stack; // 旧配置兼容：成员路径镜像，由 StackService 同步
        public List<ItemCfg> StackMembers; // 完整成员快照（叶子项），保留名称、图标与原分组
        public string Group;       // 所属分组（空 = 未分组，仅显示在"全部"视图）
    }

    public class WorkflowCfg
    {
        public string Name;
        public List<string> Paths = new List<string>(); // 逐行：文件夹/程序/文件/网址，一键依次打开
    }

    public class Config
    {
        public double IconSize = 64;
        public double Opacity = 0.72; // Dock 背景不透明度 0.25~1
        public double? Left;
        public double? Top;
        public bool Topmost = true;
        public bool AutoStart = false;
        public bool ShowNames = true;          // 图标下方常显名称（旧配置缺省视为 true）
        [XmlIgnore]
        public bool ShowNamesSpecified;        // XmlSerializer 开关：旧配置无此字段时保持 false
        public string CurrentGroup = "全部";   // 当前显示的分组
        public List<string> Recent = new List<string>();               // 最近打开（新→旧，最多 8 条）
        public List<WorkflowCfg> Workflows = new List<WorkflowCfg>(); // 一键打开的工作组合
        public List<ItemCfg> Items = new List<ItemCfg>();
    }

}
