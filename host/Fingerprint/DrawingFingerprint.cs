using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Semantic;

namespace EngineeringPlatform.Host.Fingerprint
{
    /// <summary>
    /// 图纸指纹（设计 §3.7）：fp1: + sha256(路径哈希|Handseed|模型空间对象数|关键句柄排序串|最近保存时间) 前 32 hex。
    /// 只用于缓存键 / 审计 / 漂移告警；M0 绝不因指纹变化阻断只读。
    /// 不跨命令持有 ObjectId，每次现算。
    /// </summary>
    internal static class DrawingFingerprint
    {
        public const string Prefix = "fp1:";
        private const int KeyHandleLimit = 200;

        /// <summary>计算指纹。任何异常都退化为「基于路径与时间的弱指纹」，绝不抛出。</summary>
        public static string Compute(Database db, Document doc)
        {
            List<string> parts = new List<string>(6);
            parts.Add("v1");

            string path = doc == null ? string.Empty : (doc.Name ?? string.Empty);
            parts.Add(Redactor.PathHash(path));

            long handseed = 0;
            int entityCount = 0;
            string keyHandles = string.Empty;
            string typeSignature = string.Empty;

            if (db != null)
            {
                try
                {
                    handseed = db.Handseed.Value;
                }
                catch (Exception)
                {
                    handseed = 0;
                }
                try
                {
                    List<long> handles;
                    Dictionary<string, int> typeCounts;
                    entityCount = CountModelSpaceEntities(db, out handles, out typeCounts);
                    keyHandles = JoinSortedKeyHandles(handles);
                    typeSignature = JoinSortedTypeCounts(typeCounts);
                }
                catch (Exception ex)
                {
                    HostLog.Exception("semantic", "fingerprint_scan_failed", ex);
                }
            }

            parts.Add(handseed.ToString(CultureInfo.InvariantCulture));
            parts.Add(entityCount.ToString(CultureInfo.InvariantCulture));
            parts.Add(keyHandles);
            parts.Add(typeSignature);
            parts.Add(LastWriteIso(path));

            string joined = string.Join("\u0001", parts.ToArray());
            return Prefix + ParamsDigest.Sha256Hex(joined).Substring(0, 32);
        }

        private static int CountModelSpaceEntities(Database db, out List<long> handles, out Dictionary<string, int> typeCounts)
        {
            handles = new List<long>();
            typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            int count = 0;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                ObjectId modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(db);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(modelSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    if (id.IsErased)
                    {
                        continue;
                    }
                    count++;
                    try
                    {
                        string dxf = id.ObjectClass == null ? "UNK" : (id.ObjectClass.DxfName ?? "UNK");
                        int prev;
                        typeCounts[dxf] = typeCounts.TryGetValue(dxf, out prev) ? (prev + 1) : 1;
                    }
                    catch (Exception)
                    {
                    }
                    if (handles.Count < KeyHandleLimit)
                    {
                        try
                        {
                            handles.Add(id.Handle.Value);
                        }
                        catch (Exception)
                        {
                            // 个别对象句柄取不到，忽略
                        }
                    }
                }
            }
            return count;
        }

        private static string JoinSortedTypeCounts(Dictionary<string, int> typeCounts)
        {
            if (typeCounts == null || typeCounts.Count == 0)
            {
                return "none";
            }
            List<string> keys = new List<string>(typeCounts.Keys);
            keys.Sort(StringComparer.Ordinal);
            System.Text.StringBuilder sb = new System.Text.StringBuilder(keys.Count * 16);
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(keys[i]);
                sb.Append(':');
                sb.Append(typeCounts[keys[i]].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string JoinSortedKeyHandles(List<long> handles)
        {
            if (handles == null || handles.Count == 0)
            {
                return "none";
            }
            List<long> sorted = new List<long>(handles);
            sorted.Sort();
            System.Text.StringBuilder sb = new System.Text.StringBuilder(sorted.Count * 9);
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(sorted[i].ToString("X", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string LastWriteIso(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "0";
            }
            try
            {
                if (!File.Exists(path))
                {
                    return "0";
                }
                return File.GetLastWriteTimeUtc(path).ToString("O", CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "0";
            }
        }
    }
}
