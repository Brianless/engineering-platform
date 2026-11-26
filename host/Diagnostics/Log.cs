using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace EngineeringPlatform.Host.Diagnostics
{
    /// <summary>
    /// 文件日志（设计 §7.4）。单行结构：ts|LEVEL|area|event|k=v|k=v
    /// 硬约束：
    /// 1) 绝不记录 sessionToken 明文（统一走 <see cref="Mask"/>）；
    /// 2) 绝不记录图元文字内容与完整文件路径（统一走 <see cref="SafePath"/>）；
    /// 3) 单文件 10 MB 轮转，保留 7 天。
    /// </summary>
    internal static class HostLog
    {
        private static readonly object Sync = new object();
        private static readonly List<string> Recent = new List<string>();
        private const int RecentCapacity = 500;

        private static string _dir;
        private static string _level = "INFO";
        private static long _maxBytes = 10L * 1024 * 1024;
        private static int _keepDays = 7;
        private static StreamWriter _writer;
        private static DateTime _currentDate = DateTime.MinValue;
        private static long _currentBytes;

        /// <summary>初始化日志目录与级别（幂等，可重复调用）。</summary>
        public static void Init(string dir, string level = "INFO", long maxBytes = 10L * 1024 * 1024, int keepDays = 7)
        {
            lock (Sync)
            {
                _maxBytes = maxBytes > 0 ? maxBytes : 10L * 1024 * 1024;
                _keepDays = keepDays > 0 ? keepDays : 7;
                _level = string.IsNullOrEmpty(level) ? "INFO" : level.ToUpperInvariant();
                if (string.IsNullOrEmpty(dir))
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    dir = Path.Combine(Path.Combine(local, "EngineeringPlatform"), "logs");
                }
                try
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                catch (Exception)
                {
                    // 日志目录不可写时降级为仅内存缓冲，绝不让宿主启动失败
                    _dir = null;
                    return;
                }
                _dir = dir;
                CleanupOldFiles();
            }
        }

        /// <summary>日志目录（供 sys_health 检查与面板导出使用）。</summary>
        public static string DirectoryPath
        {
            get { return _dir; }
        }

        public static bool IsWritable
        {
            get
            {
                if (string.IsNullOrEmpty(_dir))
                {
                    return false;
                }
                try
                {
                    string probe = Path.Combine(_dir, ".write-probe");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static void Debug(string area, string evt, params object[] kv)
        {
            Write("DEBUG", area, evt, kv);
        }

        public static void Info(string area, string evt, params object[] kv)
        {
            Write("INFO", area, evt, kv);
        }

        public static void Warn(string area, string evt, params object[] kv)
        {
            Write("WARN", area, evt, kv);
        }

        public static void Error(string area, string evt, params object[] kv)
        {
            Write("ERROR", area, evt, kv);
        }

        /// <summary>把异常写进日志（只写类型与短消息，不写堆栈里的绝对路径）。</summary>
        public static void Exception(string area, string evt, Exception ex)
        {
            string message = ex == null ? "null" : ex.GetType().Name + ":" + Trim(ex.Message, 300);
            Write("ERROR", area, evt, new object[] { "error", message });
        }

        /// <summary>读取最近若干条日志（面板「日志导出」与 sys_status 摘要用）。</summary>
        public static List<string> RecentLines(int limit)
        {
            lock (Sync)
            {
                int count = limit <= 0 ? Recent.Count : Math.Min(limit, Recent.Count);
                int start = Recent.Count - count;
                return new List<string>(Recent.GetRange(start, count));
            }
        }

        /// <summary>把日志目录压缩导出到指定路径（降级面板「导出日志」）。</summary>
        public static string ExportZip(string targetZipPath)
        {
            string dir = _dir;
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
            {
                throw new InvalidOperationException("日志目录不可用");
            }
            string tempDir = Path.Combine(Path.GetTempPath(), "ep-log-export-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                foreach (string file in System.IO.Directory.GetFiles(dir, "*.log"))
                {
                    File.Copy(file, Path.Combine(tempDir, Path.GetFileName(file)), true);
                }
                // 打包成 zip（FileSystem 版本没有返回值，不能放进 using）
                System.IO.Compression.ZipFile.CreateFromDirectory(tempDir, targetZipPath);
            }
            finally
            {
                try
                {
                    System.IO.Directory.Delete(tempDir, true);
                }
                catch (Exception)
                {
                    // 临时目录清理失败不影响导出结果
                }
            }
            return targetZipPath;
        }

        /// <summary>打码会话令牌（日志里永不出现明文）。</summary>
        public static string Mask(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return "null";
            }
            if (token.Length <= 4)
            {
                return "***";
            }
            return token.Substring(0, 4) + "***";
        }

        /// <summary>路径脱敏：basename + 路径哈希前 8 位（不落明文绝对路径）。</summary>
        public static string SafePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "null";
            }
            try
            {
                string baseName = Path.GetFileName(path);
                string hash = Protocol.ParamsDigest.Sha256Hex(path.ToUpperInvariant()).Substring(0, 8);
                return (baseName ?? "?") + "#" + hash;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>截断文本（日志与摘要里都不允许出现长文本内容）。</summary>
        public static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            string single = text.Replace("\r", " ").Replace("\n", " ");
            if (single.Length <= max)
            {
                return single;
            }
            return single.Substring(0, max) + "…";
        }

        private static void Write(string level, string area, string evt, object[] kv)
        {
            if (!IsEnabled(level))
            {
                return;
            }
            StringBuilder sb = new StringBuilder(160);
            sb.Append(TimeUtilNowIso()).Append('|');
            sb.Append(level).Append('|');
            sb.Append(EscapeToken(area)).Append('|');
            sb.Append(EscapeToken(evt));

            if (kv != null && kv.Length > 0)
            {
                for (int i = 0; i + 1 < kv.Length; i += 2)
                {
                    string key = Convert.ToString(kv[i], CultureInfo.InvariantCulture);
                    string value = Convert.ToString(kv[i + 1], CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }
                    sb.Append('|').Append(EscapeToken(key)).Append('=').Append(EscapeValue(value));
                }
            }

            string line = sb.ToString();
            lock (Sync)
            {
                Recent.Add(line);
                if (Recent.Count > RecentCapacity)
                {
                    Recent.RemoveAt(0);
                }
                if (string.IsNullOrEmpty(_dir))
                {
                    return;
                }
                try
                {
                    EnsureWriter();
                    if (_writer != null)
                    {
                        _writer.Write(line);
                        _writer.Write('\n');
                        _writer.Flush();
                        _currentBytes += Encoding.UTF8.GetByteCount(line) + 1;
                        if (_currentBytes > _maxBytes)
                        {
                            Rotate();
                        }
                    }
                }
                catch (Exception)
                {
                    // 写盘失败不得影响请求处理；关闭 writer 让下次重开
                    SafeCloseWriter();
                }
            }
        }

        private static bool IsEnabled(string level)
        {
            int current = LevelValue(_level);
            int incoming = LevelValue(level);
            return incoming >= current;
        }

        private static int LevelValue(string level)
        {
            if (string.Equals(level, "DEBUG", StringComparison.OrdinalIgnoreCase))
            {
                return 10;
            }
            if (string.Equals(level, "INFO", StringComparison.OrdinalIgnoreCase))
            {
                return 20;
            }
            if (string.Equals(level, "WARN", StringComparison.OrdinalIgnoreCase))
            {
                return 30;
            }
            if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
            {
                return 40;
            }
            return 20;
        }

        private static string TimeUtilNowIso()
        {
            return Protocol.TimeUtil.ToIso(DateTime.UtcNow);
        }

        private static void EnsureWriter()
        {
            DateTime today = DateTime.UtcNow.Date;
            if (_writer != null && today == _currentDate)
            {
                return;
            }
            SafeCloseWriter();
            string path = Path.Combine(_dir, "host-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _currentBytes = stream.Length;
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            _currentDate = today;
        }

        private static void Rotate()
        {
            SafeCloseWriter();
            string path = Path.Combine(_dir, "host-" + _currentDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            try
            {
                if (File.Exists(path))
                {
                    string rotated = path + "." + DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture) + ".1";
                    File.Move(path, rotated);
                }
            }
            catch (Exception)
            {
                // 轮转失败时继续写原文件（下一条会重试）
            }
            _currentBytes = 0;
        }

        private static void SafeCloseWriter()
        {
            if (_writer == null)
            {
                return;
            }
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception)
            {
                // 忽略关闭异常
            }
            _writer = null;
        }

        private static void CleanupOldFiles()
        {
            try
            {
                DateTime limit = DateTime.UtcNow.AddDays(-_keepDays);
                foreach (string file in System.IO.Directory.GetFiles(_dir, "host-*.log*"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < limit)
                        {
                            File.Delete(file);
                        }
                    }
                    catch (Exception)
                    {
                        // 单个文件删除失败不影响其它文件
                    }
                }
            }
            catch (Exception)
            {
                // 目录不存在或不可读时忽略
            }
        }

        private static string EscapeToken(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "-";
            }
            return value.Replace("|", "\\p").Replace("\r", string.Empty).Replace("\n", "\\n");
        }

        private static string EscapeValue(string value)
        {
            if (value == null)
            {
                return "null";
            }
            return EscapeToken(value).Replace("\t", " ");
        }
    }
}
