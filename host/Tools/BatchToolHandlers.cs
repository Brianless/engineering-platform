using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;

namespace EngineeringPlatform.Host.Tools
{
    /// <summary>
    /// 侧数据库无界面跨图批处理工具（v3.0 Phase 3）：
    /// 使用 new Database(false, true) + ReadDwgFile 在后台直接读写多张 DWG，
    /// 不在 MDI 视口打开图纸，支持 inspect / export_layers / find_replace_text。
    /// </summary>
    internal static class BatchToolHandlers
    {
        public static Dictionary<string, object> Handle(ToolContext context)
        {
            string tool = context.Item.Tool;
            if (string.Equals(tool, "batch_side_database", StringComparison.Ordinal))
            {
                return BatchSideDatabase(context);
            }
            throw new ToolFailureException(WireError.Of(ErrorCodes.E2001, "unknown_batch_tool:" + tool, "tool"));
        }

        private static Dictionary<string, object> BatchSideDatabase(ToolContext context)
        {
            Dictionary<string, object> p = context.Item.Params ?? new Dictionary<string, object>(StringComparer.Ordinal);
            List<string> files = JsonCodec.GetStringList(p, "files");
            if (files == null || files.Count == 0)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "files_required", "files"));
            }
            if (files.Count > 50)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "files_exceed_limit:50", "files"));
            }

            string action = JsonCodec.GetStr(p, "action", "inspect");
            string findText = JsonCodec.GetStr(p, "findText");
            string replaceText = JsonCodec.GetStr(p, "replaceText", string.Empty);
            bool saveChanges = JsonCodec.GetBool(p, "saveChanges", false);

            if (string.Equals(action, "find_replace_text", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(findText))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "findText_required", "findText"));
            }

            Stopwatch sw = Stopwatch.StartNew();
            List<object> results = new List<object>(files.Count);
            int okCount = 0;
            int failCount = 0;

            for (int i = 0; i < files.Count; i++)
            {
                context.ThrowIfAborted();
                string file = files[i];
                Dictionary<string, object> entry = new Dictionary<string, object>(StringComparer.Ordinal);
                entry["file"] = file;

                if (string.IsNullOrEmpty(file) || !File.Exists(file))
                {
                    entry["ok"] = false;
                    entry["error"] = "file_not_found";
                    failCount++;
                    results.Add(entry);
                    continue;
                }

                try
                {
                    using (Database sideDb = new Database(false, true))
                    {
                        sideDb.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
                        sideDb.CloseInput(true);

                        if (string.Equals(action, "inspect", StringComparison.OrdinalIgnoreCase))
                        {
                            InspectSideDb(sideDb, entry);
                        }
                        else if (string.Equals(action, "export_layers", StringComparison.OrdinalIgnoreCase))
                        {
                            ExportSideDbLayers(sideDb, entry);
                        }
                        else if (string.Equals(action, "find_replace_text", StringComparison.OrdinalIgnoreCase))
                        {
                            int replaced = ReplaceTextInSideDb(sideDb, findText, replaceText);
                            entry["replacedCount"] = replaced;
                            entry["saved"] = false;
                            if (saveChanges && replaced > 0)
                            {
                                sideDb.SaveAs(file, DwgVersion.Current);
                                entry["saved"] = true;
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException("unsupported_action:" + action);
                        }
                    }

                    entry["ok"] = true;
                    okCount++;
                }
                catch (Exception ex)
                {
                    entry["ok"] = false;
                    entry["error"] = ex.GetType().Name + ": " + ex.Message;
                    failCount++;
                }
                results.Add(entry);
            }

            sw.Stop();
            HostLog.Info("batch", "side_db_done", "action", action, "ok", okCount, "fail", failCount,
                "elapsedMs", sw.ElapsedMilliseconds);

            Dictionary<string, object> response = new Dictionary<string, object>(StringComparer.Ordinal);
            response["action"] = action;
            response["totalFiles"] = files.Count;
            response["succeeded"] = okCount;
            response["failed"] = failCount;
            response["results"] = results;
            response["elapsedMs"] = sw.ElapsedMilliseconds;
            response["summary"] = ToolResultHelper.Summary(string.Format(CultureInfo.InvariantCulture,
                "【侧数据库批处理】动作 {0}｜共 {1} 份 DWG，成功 {2} / 失败 {3}｜耗时 {4}ms",
                action, files.Count, okCount, failCount, sw.ElapsedMilliseconds));
            return response;
        }

        private static void InspectSideDb(Database db, Dictionary<string, object> entry)
        {
            int entityCount = 0;
            int layerCount = 0;
            Dictionary<string, int> byType = new Dictionary<string, int>(StringComparer.Ordinal);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId unused in lt)
                {
                    layerCount++;
                }

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    entityCount++;
                    try
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead, false);
                        if (obj != null)
                        {
                            string dxf = obj.GetRXClass().DxfName;
                            int prev;
                            byType.TryGetValue(dxf, out prev);
                            byType[dxf] = prev + 1;
                        }
                    }
                    catch (Exception) { }
                }
                tr.Commit();
            }

            Dictionary<string, object> typesObj = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in byType)
            {
                typesObj[kv.Key] = kv.Value;
            }
            entry["entityCount"] = entityCount;
            entry["layerCount"] = layerCount;
            entry["insUnits"] = (int)db.Insunits;
            entry["byType"] = typesObj;
        }

        private static void ExportSideDbLayers(Database db, Dictionary<string, object> entry)
        {
            List<object> layers = new List<object>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    LayerTableRecord ltr = tr.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
                    if (ltr != null)
                    {
                        Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                        item["name"] = ltr.Name;
                        item["isOff"] = ltr.IsOff;
                        item["isFrozen"] = ltr.IsFrozen;
                        item["isLocked"] = ltr.IsLocked;
                        layers.Add(item);
                    }
                }
                tr.Commit();
            }
            entry["layers"] = layers;
        }

        private static int ReplaceTextInSideDb(Database db, string findText, string replaceText)
        {
            int replaced = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (ent == null) continue;
                    DBText dt = ent as DBText;
                    if (dt != null && !string.IsNullOrEmpty(dt.TextString) && dt.TextString.Contains(findText))
                    {
                        dt.UpgradeOpen();
                        dt.TextString = dt.TextString.Replace(findText, replaceText);
                        replaced++;
                        continue;
                    }
                    MText mt = ent as MText;
                    if (mt != null && !string.IsNullOrEmpty(mt.Contents) && mt.Contents.Contains(findText))
                    {
                        mt.UpgradeOpen();
                        mt.Contents = mt.Contents.Replace(findText, replaceText);
                        replaced++;
                    }
                }
                tr.Commit();
            }
            return replaced;
        }
    }
}
