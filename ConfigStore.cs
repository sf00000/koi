using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace Koi
{
    public sealed class ConfigLoadResult
    {
        public Config Value;
        public string Warning;
        public bool RecoveredFromBackup;
    }

    // Persistence has no WPF dependency; callers decide how to present recovery warnings.
    public sealed class ConfigStore
    {
        readonly string file;
        readonly Action<string> log;
        bool preserveBackup;
        public bool CanSave { get; private set; }

        public ConfigStore(string file, Action<string> log = null)
        {
            this.file = Path.GetFullPath(file);
            this.log = log;
            CanSave = true;
        }

        public ConfigLoadResult Load()
        {
            CanSave = true;
            preserveBackup = false;
            Config value = TryRead(file);
            if (value != null) return new ConfigLoadResult { Value = Normalize(value) };
            value = TryRead(file + ".bak");
            if (value != null)
            {
                // A corrupt primary must never replace the valid backup on the next save.
                preserveBackup = true;
                return new ConfigLoadResult { Value = Normalize(value), RecoveredFromBackup = true };
            }
            string warning = null;
            if (File.Exists(file) || File.Exists(file + ".bak"))
            {
                try
                {
                    string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
                    foreach (string path in new[] { file, file + ".bak" })
                        if (File.Exists(path)) File.Move(path, path + ".corrupt-" + stamp);
                    warning = "配置及备份无法读取，原文件已归档为 .corrupt-*，本次以默认设置启动。";
                }
                catch (Exception ex)
                {
                    CanSave = false;
                    warning = "配置无法读取且归档失败，已暂停保存以保护原文件：" + ex.Message;
                }
                if (log != null) log(warning);
            }
            return new ConfigLoadResult { Value = new Config(), Warning = warning };
        }

        static Config TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                XmlReaderSettings settings = new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(path, settings))
                    return new XmlSerializer(typeof(Config)).Deserialize(reader) as Config;
            }
            catch (Exception) { return null; }
        }

        public void Save(Config config)
        {
            if (!CanSave) throw new InvalidOperationException("配置保护已启用，禁止覆盖原文件。");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                config.ShowNamesSpecified = true;
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Framework's Serialize(Stream, ...) can escape invalid XML control
                    // characters instead of rejecting them. Never replace a readable file
                    // with XML that our reader cannot load on the next application start.
                    XmlWriterSettings settings = new XmlWriterSettings
                    {
                        CheckCharacters = true,
                        Indent = true,
                        Encoding = new System.Text.UTF8Encoding(false),
                        CloseOutput = false
                    };
                    using (XmlWriter writer = XmlWriter.Create(stream, settings))
                        new XmlSerializer(typeof(Config)).Serialize(writer, config);
                    stream.Flush(true);
                }
                if (File.Exists(file)) File.Replace(temporary, file, preserveBackup ? null : file + ".bak");
                else File.Move(temporary, file);
                preserveBackup = false;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public static Config Normalize(Config config)
        {
            config.IconSize = Finite(config.IconSize) ? Math.Max(32, Math.Min(112, config.IconSize)) : 64;
            config.Opacity = Finite(config.Opacity) && config.Opacity >= 0.25 ? Math.Min(1, config.Opacity) : 0.72;
            if (!config.ShowNamesSpecified) config.ShowNames = true;
            if (config.Left.HasValue && !Finite(config.Left.Value)) config.Left = null;
            if (config.Top.HasValue && !Finite(config.Top.Value)) config.Top = null;
            if (config.Items == null) config.Items = new List<ItemCfg>();
            config.Items.RemoveAll(delegate(ItemCfg item) { return item == null; });
            foreach (ItemCfg item in config.Items) StackService.Normalize(item);
            config.Items.RemoveAll(delegate(ItemCfg item) { return string.IsNullOrWhiteSpace(item.Path); });
            if (config.Recent == null) config.Recent = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            config.Recent.RemoveAll(delegate(string path) { return string.IsNullOrWhiteSpace(path) || !seen.Add(PathIdentity.Normalize(path)); });
            if (config.Recent.Count > 8) config.Recent.RemoveRange(8, config.Recent.Count - 8);
            if (config.Workflows == null) config.Workflows = new List<WorkflowCfg>();
            config.Workflows.RemoveAll(delegate(WorkflowCfg workflow) { return workflow == null; });
            foreach (WorkflowCfg workflow in config.Workflows)
            {
                if (workflow.Paths == null) workflow.Paths = new List<string>();
                workflow.Paths.RemoveAll(delegate(string path) { return string.IsNullOrWhiteSpace(path); });
            }
            if (string.IsNullOrEmpty(config.CurrentGroup) || (config.CurrentGroup != "全部" &&
                !config.Items.Exists(delegate(ItemCfg item) { return item.Group == config.CurrentGroup; })))
                config.CurrentGroup = "全部";
            return config;
        }

        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
