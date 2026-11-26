using System;
using System.Globalization;
using System.IO;
using EngineeringPlatform.Host.Diagnostics;

namespace EngineeringPlatform.Host.Preview
{
    /// <summary>
    /// 预览图落盘与命中（设计 §3.6）。
    /// 目录：%LOCALAPPDATA%\EngineeringPlatform\cache\preview
    /// 文件名：{fingerprint8}-{w}x{h}-{extentHash}.png
    /// </summary>
    internal sealed class PreviewCache
    {
        private readonly string _directory;

        public PreviewCache(string directory)
        {
            _directory = string.IsNullOrEmpty(directory) ? DefaultDirectory() : directory;
        }

        public static string DefaultDirectory()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(Path.Combine(local, "EngineeringPlatform"), "cache"), "preview");
        }

        public string DirectoryPath
        {
            get { return _directory; }
        }

        /// <summary>确保目录存在；失败返回 false 并给出原因（sys_health.cacheWritable）。</summary>
        public bool EnsureDirectory(out string error)
        {
            error = null;
            try
            {
                if (!Directory.Exists(_directory))
                {
                    Directory.CreateDirectory(_directory);
                }
                string probe = Path.Combine(_directory, ".write-probe");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }

        /// <summary>生成落盘路径（文件名只含哈希与尺寸，不含图纸名明文）。</summary>
        public string BuildPath(string fingerprint, int width, int height, string extentHash)
        {
            string shortFingerprint = ShortFingerprint(fingerprint);
            string safeExtent = string.IsNullOrEmpty(extentHash) ? "all" : extentHash;
            string fileName = shortFingerprint + "-" + width.ToString(CultureInfo.InvariantCulture) + "x"
                + height.ToString(CultureInfo.InvariantCulture) + "-" + safeExtent + ".png";
            return Path.Combine(_directory, fileName);
        }

        /// <summary>指纹前 8 位（去掉 fp1: 前缀）。</summary>
        public static string ShortFingerprint(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint))
            {
                return "nofp0000";
            }
            string body = fingerprint.StartsWith("fp1:", StringComparison.OrdinalIgnoreCase)
                ? fingerprint.Substring(4)
                : fingerprint;
            return body.Length >= 8 ? body.Substring(0, 8) : body.PadRight(8, '0');
        }

        /// <summary>
        /// 路径是否落在缓存目录内（白名单校验，MCP 侧还会再校验一次）。
        /// 使用「展开 + FullPath + 分隔符结尾前缀」比较，防 cache2\ 命中 cache。
        /// </summary>
        public bool IsWithinDirectory(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            try
            {
                string root = Path.GetFullPath(_directory);
                if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                {
                    root += Path.DirectorySeparatorChar;
                }
                string full = Path.GetFullPath(path);
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>缓存目录可写（sys_health）。</summary>
        public bool IsWritable
        {
            get
            {
                string error;
                return EnsureDirectory(out error);
            }
        }

        /// <summary>清理过期缓存（保留最近 200 个文件）。</summary>
        public void Trim(int keepFiles)
        {
            try
            {
                if (!Directory.Exists(_directory))
                {
                    return;
                }
                string[] files = Directory.GetFiles(_directory, "*.png");
                if (files.Length <= keepFiles)
                {
                    return;
                }
                Array.Sort(files, delegate(string a, string b)
                {
                    return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
                });
                int remove = files.Length - keepFiles;
                for (int i = 0; i < remove; i++)
                {
                    try
                    {
                        File.Delete(files[i]);
                    }
                    catch (Exception)
                    {
                        // 单文件删除失败忽略
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("preview", "trim_failed", ex);
            }
        }
    }
}
