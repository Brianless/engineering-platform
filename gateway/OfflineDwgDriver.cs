using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;

namespace EngineeringPlatform.Gateway;

/// <summary>
/// 离线原生 DWG/DXF 驱动（基于 ACadSharp，零 CAD 安装即可运行）：
/// 1. 支持直接读取 R14 ~ AC1032 (AutoCAD 2018–2026) DWG 与 DXF；
/// 2. 为 COM 驱动提供大图纸混合加速（已保存图纸直读磁盘，5 万图元 <1s）；
/// 3. 硬安全防线：拒绝原地覆写含未知/代理对象（天正/Civil3D/鸿业等）的既有 DWG，避免破坏专业扩展字典。
/// </summary>
internal sealed class OfflineDwgDriver
{
    public sealed record LoadedDocumentMeta(
        CadDocument Document,
        int UnsupportedNotifications,
        List<string> CustomClasses,
        int CustomClassInstances);

    public CadDocument LoadDocument(string path) => LoadDocumentWithMeta(path).Document;

    public LoadedDocumentMeta LoadDocumentWithMeta(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("dwg_or_dxf_not_found", path);
        }
        int unsupportedNotifs = 0;
        CadDocument doc;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".dxf")
        {
            using var reader = new DxfReader(path);
            reader.OnNotification += (_, e) =>
            {
                if (e.NotificationType == NotificationType.NotImplemented ||
                    (e.Message != null && e.Message.Contains("not implemented", StringComparison.OrdinalIgnoreCase)))
                {
                    unsupportedNotifs++;
                }
            };
            doc = reader.Read();
        }
        else
        {
            using var reader = new DwgReader(path);
            reader.OnNotification += (_, e) =>
            {
                if (e.NotificationType == NotificationType.NotImplemented ||
                    (e.Message != null && e.Message.Contains("not implemented", StringComparison.OrdinalIgnoreCase)))
                {
                    unsupportedNotifs++;
                }
            };
            doc = reader.Read();
        }

        var customClasses = new List<string>();
        int customInstances = 0;
        foreach (var cls in doc.Classes)
        {
            string dxfName = cls.DxfName ?? "";
            string cppName = cls.CppClassName ?? "";
            string appName = cls.ApplicationName ?? "";
            bool isCustomVertical =
                cls.WasZombie ||
                IsCustomVerticalToken(dxfName) ||
                IsCustomVerticalToken(cppName) ||
                IsCustomVerticalToken(appName);
            if (isCustomVertical)
            {
                string label = !string.IsNullOrEmpty(dxfName) ? dxfName : cppName;
                if (!string.IsNullOrEmpty(label) && !customClasses.Contains(label, StringComparer.OrdinalIgnoreCase))
                {
                    customClasses.Add(label);
                }
                if (cls.InstanceCount > 0) customInstances += cls.InstanceCount;
            }
        }

        return new LoadedDocumentMeta(doc, unsupportedNotifs, customClasses, customInstances);
    }

    private static bool IsCustomVerticalToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        return token.Contains("Aecc", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("AecDb", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("TArch", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("Tangent", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("TDb", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("Hongye", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("Civil", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("Proxy", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("Zombie", StringComparison.OrdinalIgnoreCase);
    }

    public JsonObject ReadOverview(string filePath)
    {
        var sw = Stopwatch.StartNew();
        var meta = LoadDocumentWithMeta(filePath);
        var doc = meta.Document;
        int entityCount = 0;
        int blockRefCount = 0;
        int entityProxyCount = 0;
        var usedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        bool hasBounds = false;

        foreach (var ent in doc.Entities)
        {
            entityCount++;
            usedLayers.Add(ent.Layer?.Name ?? "0");
            if (ent is Insert) blockRefCount++;
            if (IsProxyOrUnknown(ent)) entityProxyCount++;

            try
            {
                var bb = ent.GetBoundingBox();
                if (IsValidBox(bb))
                {
                    minX = Math.Min(minX, bb.Min.X);
                    minY = Math.Min(minY, bb.Min.Y);
                    minZ = Math.Min(minZ, bb.Min.Z);
                    maxX = Math.Max(maxX, bb.Max.X);
                    maxY = Math.Max(maxY, bb.Max.Y);
                    maxZ = Math.Max(maxZ, bb.Max.Z);
                    hasBounds = true;
                }
            }
            catch { }
        }

        int proxyCount = entityProxyCount + meta.CustomClassInstances;
        bool safeOverwrite = proxyCount == 0 && meta.CustomClasses.Count == 0 && meta.UnsupportedNotifications == 0;
        int totalLayerRecords = doc.Layers.Count;
        int emptyLayers = Math.Max(0, totalLayerRecords - usedLayers.Count);
        sw.Stop();

        string fileName = Path.GetFileName(filePath);
        string nameHash = CanonicalJson.Sha256Hex(Encoding.UTF8.GetBytes(fileName)).Substring(0, 8) + Path.GetExtension(filePath);
        int insUnits = (int)doc.Header.InsUnits;

        var customClassSample = new JsonArray();
        foreach (var c in meta.CustomClasses.Take(20)) customClassSample.Add(c);

        return new JsonObject
        {
            ["summary"] = $"【离线图纸总览 · ACadSharp】{fileName}｜版本 {doc.Header.VersionString}｜图元 {entityCount}｜图层 {usedLayers.Count}（空图层 {emptyLayers}）｜代理/专业对象 {proxyCount}（自定义类 {meta.CustomClasses.Count}）｜耗时 {sw.ElapsedMilliseconds}ms",
            ["backend"] = "offline_acadsharp",
            ["software"] = new JsonObject
            {
                ["name"] = "ACadSharp(Offline)",
                ["version"] = doc.Header.VersionString ?? "unknown",
                ["civilApiAvailable"] = false
            },
            ["drawing"] = new JsonObject
            {
                ["nameHash"] = nameHash,
                ["fullPath"] = filePath,
                ["readOnly"] = true,
                ["safeToOverwriteInPlace"] = safeOverwrite
            },
            ["units"] = UnitsNode(insUnits),
            ["extents"] = new JsonObject
            {
                ["min"] = hasBounds ? new JsonArray(minX, minY, minZ) : new JsonArray(0.0, 0.0, 0.0),
                ["max"] = hasBounds ? new JsonArray(maxX, maxY, maxZ) : new JsonArray(0.0, 0.0, 0.0)
            },
            ["counts"] = new JsonObject
            {
                ["entities"] = entityCount,
                ["layers"] = usedLayers.Count,
                ["layerTableRecords"] = totalLayerRecords,
                ["emptyLayers"] = emptyLayers,
                ["blocks"] = doc.BlockRecords.Count,
                ["blockReferences"] = blockRefCount,
                ["proxyEntities"] = proxyCount,
                ["customClasses"] = meta.CustomClasses.Count,
                ["unsupportedNotifications"] = meta.UnsupportedNotifications,
                ["entityLimitReached"] = false
            },
            ["customClassesSample"] = customClassSample,
            ["elapsedMs"] = sw.ElapsedMilliseconds
        };
    }

    public JsonObject ReadLayers(string filePath, string? namePattern = null, int limit = 200)
    {
        var doc = LoadDocument(filePath);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var typesByLayer = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ent in doc.Entities)
        {
            string lyr = ent.Layer?.Name ?? "0";
            counts[lyr] = counts.GetValueOrDefault(lyr) + 1;
            if (!typesByLayer.TryGetValue(lyr, out var map))
            {
                map = new Dictionary<string, int>(StringComparer.Ordinal);
                typesByLayer[lyr] = map;
            }
            string dxf = ent.ObjectName ?? ent.GetType().Name;
            map[dxf] = map.GetValueOrDefault(dxf) + 1;
        }

        var arr = new JsonArray();
        int matched = 0;
        foreach (var layer in doc.Layers.OrderByDescending(l => counts.GetValueOrDefault(l.Name)))
        {
            if (!string.IsNullOrEmpty(namePattern) && !WildcardMatch(namePattern, layer.Name)) continue;
            matched++;
            if (arr.Count >= Math.Clamp(limit, 1, 500)) continue;

            var typesNode = new JsonObject();
            if (typesByLayer.TryGetValue(layer.Name, out var tmap))
            {
                foreach (var kv in tmap) typesNode[kv.Key] = kv.Value;
            }
            arr.Add(new JsonObject
            {
                ["name"] = layer.Name,
                ["entityCount"] = counts.GetValueOrDefault(layer.Name),
                ["isOn"] = layer.IsOn,
                ["isFrozen"] = layer.Flags.HasFlag(LayerFlags.Frozen),
                ["isLocked"] = layer.Flags.HasFlag(LayerFlags.Locked),
                ["types"] = typesNode
            });
        }

        return new JsonObject
        {
            ["summary"] = $"【离线图层 · ACadSharp】共 {matched} 个图层，返回 {arr.Count} 个。",
            ["backend"] = "offline_acadsharp",
            ["total"] = matched,
            ["layers"] = arr,
            ["truncated"] = matched > arr.Count
        };
    }

    public JsonObject ExportLayerGeoJson(
        string filePath,
        List<string>? layers,
        Dictionary<string, string>? catMap,
        int limit,
        string? outPath)
    {
        var sw = Stopwatch.StartNew();
        var doc = LoadDocument(filePath);
        if (string.IsNullOrWhiteSpace(outPath))
        {
            string ws = Path.Combine(Path.GetTempPath(), "ep_workspace");
            Directory.CreateDirectory(ws);
            outPath = Path.Combine(ws, "constraints.geojson");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        }

        var features = new JsonArray();
        var byCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int scanned = 0;
        int cap = Math.Clamp(limit, 1, 100000);

        foreach (var ent in doc.Entities)
        {
            scanned++;
            if (features.Count >= cap) break;

            string lyr = ent.Layer?.Name ?? "0";
            if (layers != null && layers.Count > 0 && !layers.Any(pat => WildcardMatch(pat, lyr)))
            {
                continue;
            }

            var geom = EntityToGeoJson(ent);
            if (geom == null) continue;

            string category = ResolveCategory(lyr, catMap);
            string handleHex = ent.Handle.ToString("X", CultureInfo.InvariantCulture);

            var props = new JsonObject
            {
                ["handle"] = handleHex,
                ["layer"] = lyr,
                ["dxfName"] = ent.ObjectName ?? ent.GetType().Name,
                ["category"] = category,
                ["name"] = $"{lyr}_{handleHex}"
            };
            if (ent is Insert ins)
            {
                props["blockName"] = ins.Block?.Name ?? "";
                props["elevation"] = ins.InsertPoint.Z;
            }
            else if (ent is Point pt)
            {
                props["elevation"] = pt.Location.Z;
            }

            features.Add(new JsonObject
            {
                ["type"] = "Feature",
                ["properties"] = props,
                ["geometry"] = geom
            });
            byCategory[category] = byCategory.GetValueOrDefault(category) + 1;
        }

        var fc = new JsonObject
        {
            ["type"] = "FeatureCollection",
            ["features"] = features
        };
        string json = CanonicalJson.SerializeCompact(fc);
        File.WriteAllText(outPath, json, new UTF8Encoding(false));
        sw.Stop();

        var catObj = new JsonObject();
        foreach (var kv in byCategory) catObj[kv.Key] = kv.Value;

        return new JsonObject
        {
            ["summary"] = $"【离线 GeoJSON 导出 · ACadSharp】导出 {features.Count} 要素（扫描 {scanned} 图元）→ {outPath}｜耗时 {sw.ElapsedMilliseconds}ms",
            ["backend"] = "offline_acadsharp",
            ["outPath"] = outPath,
            ["featureCount"] = features.Count,
            ["scannedEntities"] = scanned,
            ["fileBytes"] = new FileInfo(outPath).Length,
            ["byCategory"] = catObj,
            ["elapsedMs"] = sw.ElapsedMilliseconds
        };
    }

    public JsonObject ExecuteBatchSideDatabase(JsonObject args)
    {
        var filesArr = args["files"] as JsonArray;
        if (filesArr == null || filesArr.Count == 0)
        {
            throw new ArgumentException("files_required");
        }
        string action = args["action"]?.GetValue<string>() ?? "inspect";
        string? findText = args["findText"]?.GetValue<string>();
        string replaceText = args["replaceText"]?.GetValue<string>() ?? "";
        bool saveChanges = args["saveChanges"]?.GetValue<bool>() == true;

        var sw = Stopwatch.StartNew();
        var results = new JsonArray();
        int okCount = 0, failCount = 0;

        foreach (var fNode in filesArr)
        {
            string file = fNode?.GetValue<string>() ?? "";
            var entry = new JsonObject { ["file"] = file };
            if (!File.Exists(file))
            {
                entry["ok"] = false;
                entry["error"] = "file_not_found";
                failCount++;
                results.Add(entry);
                continue;
            }

            try
            {
                var meta = LoadDocumentWithMeta(file);
                var doc = meta.Document;
                if (action.Equals("inspect", StringComparison.OrdinalIgnoreCase))
                {
                    var byType = new Dictionary<string, int>(StringComparer.Ordinal);
                    int proxyCount = meta.CustomClassInstances;
                    foreach (var ent in doc.Entities)
                    {
                        string t = ent.ObjectName ?? ent.GetType().Name;
                        byType[t] = byType.GetValueOrDefault(t) + 1;
                        if (IsProxyOrUnknown(ent)) proxyCount++;
                    }
                    var tObj = new JsonObject();
                    foreach (var kv in byType) tObj[kv.Key] = kv.Value;
                    entry["entityCount"] = doc.Entities.Count;
                    entry["layerCount"] = doc.Layers.Count;
                    entry["proxyEntities"] = proxyCount;
                    entry["customClasses"] = meta.CustomClasses.Count;
                    entry["unsupportedNotifications"] = meta.UnsupportedNotifications;
                    entry["insUnits"] = (int)doc.Header.InsUnits;
                    entry["dwgVersion"] = doc.Header.VersionString;
                    entry["byType"] = tObj;
                }
                else if (action.Equals("export_layers", StringComparison.OrdinalIgnoreCase))
                {
                    var lyrs = new JsonArray();
                    foreach (var l in doc.Layers)
                    {
                        lyrs.Add(new JsonObject
                        {
                            ["name"] = l.Name,
                            ["isOff"] = !l.IsOn,
                            ["isFrozen"] = l.Flags.HasFlag(LayerFlags.Frozen),
                            ["isLocked"] = l.Flags.HasFlag(LayerFlags.Locked)
                        });
                    }
                    entry["layers"] = lyrs;
                }
                else if (action.Equals("find_replace_text", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(findText)) throw new ArgumentException("findText_required");
                    int entityProxyCount = doc.Entities.Count(IsProxyOrUnknown);
                    int totalProxyRisk = entityProxyCount + meta.CustomClassInstances;
                    if (saveChanges && (totalProxyRisk > 0 || meta.CustomClasses.Count > 0 || meta.UnsupportedNotifications > 0))
                    {
                        // 硬安全防线：拒绝原地覆写含天正/Civil3D/鸿业代理对象或自定义类的工程图
                        throw new InvalidOperationException(
                            $"proxy_overwrite_blocked: 图纸含代理/专业自定义对象（proxy={totalProxyRisk}, customClasses={meta.CustomClasses.Count}, unsupported={meta.UnsupportedNotifications}），禁止 ACadSharp 原地覆写，请通过真实 CAD 或另存副本处理");
                    }

                    int replaced = 0;
                    foreach (var ent in doc.Entities)
                    {
                        if (ent is TextEntity txt && !string.IsNullOrEmpty(txt.Value) && txt.Value.Contains(findText))
                        {
                            txt.Value = txt.Value.Replace(findText, replaceText);
                            replaced++;
                        }
                        else if (ent is MText mtxt && !string.IsNullOrEmpty(mtxt.Value) && mtxt.Value.Contains(findText))
                        {
                            mtxt.Value = mtxt.Value.Replace(findText, replaceText);
                            replaced++;
                        }
                    }
                    entry["replacedCount"] = replaced;
                    entry["saved"] = false;
                    if (saveChanges && replaced > 0)
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext == ".dxf") DxfWriter.Write(file, doc, false);
                        else DwgWriter.Write(file, doc);
                        entry["saved"] = true;
                    }
                }
                else
                {
                    throw new InvalidOperationException("unsupported_action:" + action);
                }

                entry["ok"] = true;
                okCount++;
            }
            catch (Exception ex)
            {
                entry["ok"] = false;
                entry["error"] = $"{ex.GetType().Name}: {ex.Message}";
                failCount++;
            }
            results.Add(entry);
        }

        sw.Stop();
        return new JsonObject
        {
            ["summary"] = $"【离线批处理 · ACadSharp】动作 {action}｜共 {filesArr.Count} 份图纸，成功 {okCount} / 失败 {failCount}｜耗时 {sw.ElapsedMilliseconds}ms",
            ["backend"] = "offline_acadsharp",
            ["action"] = action,
            ["totalFiles"] = filesArr.Count,
            ["succeeded"] = okCount,
            ["failed"] = failCount,
            ["results"] = results,
            ["elapsedMs"] = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// 无 CAD 进程直接创建或向纯 DWG/DXF 追加结构化实体（基于 ACadSharp）：
    /// - 目标文件不存在时新建空白 CadDocument；
    /// - 目标文件存在时先检查代理对象/专业自定义类，触发硬防线保护 Civil 3D / 天正 / 鸿业图纸。
    /// </summary>
    public JsonObject WriteEntities(string filePath, JsonObject args)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("dwgPath_required");
        }
        var entities = args["entities"] as JsonArray;
        if (entities == null || entities.Count == 0)
        {
            throw new ArgumentException("entities_required");
        }
        if (entities.Count > 5000)
        {
            throw new ArgumentException("entities_exceed_limit:5000");
        }

        string defaultLayer = args["layer"]?.GetValue<string>() ?? "0";
        if (string.IsNullOrWhiteSpace(defaultLayer)) defaultLayer = "0";
        bool strict = args["strict"]?.GetValue<bool>() == true;

        var sw = Stopwatch.StartNew();
        CadDocument doc;
        bool createdNewFile;
        if (File.Exists(filePath))
        {
            var meta = LoadDocumentWithMeta(filePath);
            int entityProxyCount = meta.Document.Entities.Count(IsProxyOrUnknown);
            int totalProxyRisk = entityProxyCount + meta.CustomClassInstances;
            if (totalProxyRisk > 0 || meta.CustomClasses.Count > 0 || meta.UnsupportedNotifications > 0)
            {
                throw new InvalidOperationException(
                    $"proxy_overwrite_blocked: 图纸含代理/专业自定义对象（proxy={totalProxyRisk}, customClasses={meta.CustomClasses.Count}, unsupported={meta.UnsupportedNotifications}），禁止 ACadSharp 原地覆写，请通过真实 CAD 或另存新文件处理");
            }
            doc = meta.Document;
            createdNewFile = false;
        }
        else
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            doc = new CadDocument();
            createdNewFile = true;
        }

        var createdEntities = new List<Entity>();
        var failedItems = new JsonArray();
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        bool hasBounds = false;

        for (int i = 0; i < entities.Count; i++)
        {
            if (entities[i] is not JsonObject spec)
            {
                if (strict) throw new InvalidOperationException($"write_entities_strict_failed:index={i}:entity_must_be_object");
                failedItems.Add(new JsonObject { ["index"] = i, ["type"] = "unknown", ["error"] = "entity_must_be_object" });
                continue;
            }

            string rawType = (spec["type"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
            try
            {
                var (ent, canonicalType) = BuildOfflineEntity(spec, rawType);
                string lyrName = spec["layer"]?.GetValue<string>() ?? defaultLayer;
                if (string.IsNullOrWhiteSpace(lyrName)) lyrName = defaultLayer;
                ent.Layer = EnsureOfflineLayer(doc, lyrName);

                if (TryOffDouble(spec["color"], out double colorVal))
                {
                    int ci = (int)Math.Round(colorVal);
                    if (ci >= 1 && ci <= 255)
                    {
                        ent.Color = new Color((short)ci);
                    }
                }

                doc.Entities.Add(ent);
                createdEntities.Add(ent);
                byType[canonicalType] = byType.GetValueOrDefault(canonicalType) + 1;

                try
                {
                    var bb = ent.GetBoundingBox();
                    if (IsValidBox(bb))
                    {
                        minX = Math.Min(minX, bb.Min.X);
                        minY = Math.Min(minY, bb.Min.Y);
                        minZ = Math.Min(minZ, bb.Min.Z);
                        maxX = Math.Max(maxX, bb.Max.X);
                        maxY = Math.Max(maxY, bb.Max.Y);
                        maxZ = Math.Max(maxZ, bb.Max.Z);
                        hasBounds = true;
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                if (strict)
                {
                    throw new InvalidOperationException($"write_entities_strict_failed:index={i}:{ex.Message}");
                }
                failedItems.Add(new JsonObject
                {
                    ["index"] = i,
                    ["type"] = string.IsNullOrEmpty(rawType) ? "unknown" : rawType,
                    ["error"] = ex.Message
                });
            }
        }

        bool saved = false;
        if (createdEntities.Count > 0 || createdNewFile)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".dxf")
            {
                DxfWriter.Write(filePath, doc, false);
            }
            else
            {
                DwgWriter.Write(filePath, doc);
            }
            saved = true;
        }

        var handles = new JsonArray();
        for (int i = 0; i < createdEntities.Count && handles.Count < 500; i++)
        {
            handles.Add(createdEntities[i].Handle.ToString("X", CultureInfo.InvariantCulture));
        }

        var byTypeNode = new JsonObject();
        foreach (var kv in byType) byTypeNode[kv.Key] = kv.Value;
        sw.Stop();

        var res = new JsonObject
        {
            ["summary"] = $"【离线批量建实体 · ACadSharp】请求 {entities.Count}，成功 {createdEntities.Count}，失败 {failedItems.Count} → {filePath}｜耗时 {sw.ElapsedMilliseconds}ms",
            ["backend"] = "offline_acadsharp",
            ["filePath"] = filePath,
            ["createdNewFile"] = createdNewFile,
            ["requestedCount"] = entities.Count,
            ["createdCount"] = createdEntities.Count,
            ["failedCount"] = failedItems.Count,
            ["failedItems"] = failedItems,
            ["layer"] = defaultLayer,
            ["handles"] = handles,
            ["handlesTruncated"] = createdEntities.Count > handles.Count,
            ["byType"] = byTypeNode,
            ["saved"] = saved,
            ["elapsedMs"] = sw.ElapsedMilliseconds
        };
        if (hasBounds)
        {
            res["extents"] = new JsonObject
            {
                ["min"] = new JsonArray(Math.Round(minX, 6), Math.Round(minY, 6), Math.Round(minZ, 6)),
                ["max"] = new JsonArray(Math.Round(maxX, 6), Math.Round(maxY, 6), Math.Round(maxZ, 6))
            };
        }
        return res;
    }

    private static Layer EnsureOfflineLayer(CadDocument doc, string name)
    {
        if (doc.Layers.TryGetValue(name, out Layer? existing) && existing != null)
        {
            return existing;
        }
        var lyr = new Layer(name);
        doc.Layers.Add(lyr);
        return lyr;
    }

    private static (Entity Entity, string CanonicalType) BuildOfflineEntity(JsonObject spec, string rawType)
    {
        if (string.IsNullOrEmpty(rawType)) throw new ArgumentException("type_required");

        switch (rawType)
        {
            case "line":
            {
                if (!TryOffPoint3(spec, out var p1, "start", "p1", "from"))
                {
                    if (TryOffDouble(spec["x1"], out double x1) && TryOffDouble(spec["y1"], out double y1))
                        p1 = new CSMath.XYZ(x1, y1, TryOffDouble(spec["z1"], out double z1) ? z1 : 0.0);
                    else throw new ArgumentException("line_requires_start_and_end");
                }
                if (!TryOffPoint3(spec, out var p2, "end", "p2", "to"))
                {
                    if (TryOffDouble(spec["x2"], out double x2) && TryOffDouble(spec["y2"], out double y2))
                        p2 = new CSMath.XYZ(x2, y2, TryOffDouble(spec["z2"], out double z2) ? z2 : 0.0);
                    else throw new ArgumentException("line_requires_start_and_end");
                }
                return (new Line(p1, p2), "line");
            }
            case "polyline":
            case "lwpolyline":
            case "pline":
            {
                var pts = spec["points"] as JsonArray ?? spec["vertices"] as JsonArray;
                if (pts == null || pts.Count < 2) throw new ArgumentException("polyline_requires_at_least_two_points");
                double width = TryOffDouble(spec["width"], out double wv) ? wv : 0.0;
                double elev = TryOffDouble(spec["elevation"], out double ev) ? ev : 0.0;
                var pl = new LwPolyline
                {
                    IsClosed = spec["closed"]?.GetValue<bool>() == true,
                    Elevation = elev
                };
                for (int k = 0; k < pts.Count; k++)
                {
                    if (!TryOffVertex2(pts[k], out double vx, out double vy, out double bulge))
                        throw new ArgumentException($"invalid_polyline_vertex:{k}");
                    pl.Vertices.Add(new LwPolyline.Vertex(new CSMath.XY(vx, vy))
                    {
                        Bulge = bulge,
                        StartWidth = width,
                        EndWidth = width
                    });
                }
                return (pl, "polyline");
            }
            case "rectangle":
            case "rect":
            {
                double x1, y1, x2, y2;
                double elev = TryOffDouble(spec["elevation"], out double ev) ? ev : 0.0;
                if (TryOffPoint3(spec, out var a, "p1", "min", "start") && TryOffPoint3(spec, out var b, "p2", "max", "end"))
                {
                    x1 = a.X; y1 = a.Y; x2 = b.X; y2 = b.Y;
                    if (Math.Abs(elev) < 1e-12) elev = a.Z;
                }
                else if (TryOffDouble(spec["width"], out double w) && TryOffDouble(spec["height"], out double h) && w > 0 && h > 0)
                {
                    if (TryOffPoint3(spec, out var org, "origin", "p1", "min"))
                    {
                        x1 = org.X; y1 = org.Y; x2 = x1 + w; y2 = y1 + h;
                        if (Math.Abs(elev) < 1e-12) elev = org.Z;
                    }
                    else if (TryOffPoint3(spec, out var ctr, "center", "at"))
                    {
                        x1 = ctr.X - w * 0.5; y1 = ctr.Y - h * 0.5; x2 = ctr.X + w * 0.5; y2 = ctr.Y + h * 0.5;
                        if (Math.Abs(elev) < 1e-12) elev = ctr.Z;
                    }
                    else if (TryOffDouble(spec["x"], out x1) && TryOffDouble(spec["y"], out y1))
                    {
                        x2 = x1 + w; y2 = y1 + h;
                    }
                    else throw new ArgumentException("rectangle_requires_origin_or_center");
                }
                else throw new ArgumentException("rectangle_requires_p1_p2_or_origin_width_height");

                if (Math.Abs(x2 - x1) < 1e-9 || Math.Abs(y2 - y1) < 1e-9) throw new ArgumentException("rectangle_degenerate");
                var rect = new LwPolyline { IsClosed = true, Elevation = elev };
                rect.Vertices.Add(new LwPolyline.Vertex(new CSMath.XY(x1, y1)));
                rect.Vertices.Add(new LwPolyline.Vertex(new CSMath.XY(x2, y1)));
                rect.Vertices.Add(new LwPolyline.Vertex(new CSMath.XY(x2, y2)));
                rect.Vertices.Add(new LwPolyline.Vertex(new CSMath.XY(x1, y2)));
                return (rect, "rectangle");
            }
            case "circle":
            {
                if (!TryOffPoint3(spec, out var center, "center", "at") && !TryOffInlinePoint3(spec, out center))
                    throw new ArgumentException("circle_requires_center");
                if (!(TryOffDouble(spec["radius"], out double r) || TryOffDouble(spec["r"], out r)) || r <= 1e-9)
                    throw new ArgumentException("circle_requires_positive_radius");
                return (new Circle { Center = center, Radius = r }, "circle");
            }
            case "arc":
            {
                if (!TryOffPoint3(spec, out var center, "center", "at") && !TryOffInlinePoint3(spec, out center))
                    throw new ArgumentException("arc_requires_center");
                if (!(TryOffDouble(spec["radius"], out double r) || TryOffDouble(spec["r"], out r)) || r <= 1e-9)
                    throw new ArgumentException("arc_requires_positive_radius");
                if (!TryOffAngleRad(spec, "startAngle_deg", "startAngle", "startAngle_rad", out double sRad) ||
                    !TryOffAngleRad(spec, "endAngle_deg", "endAngle", "endAngle_rad", out double eRad))
                    throw new ArgumentException("arc_requires_startAngle_and_endAngle");
                return (new Arc { Center = center, Radius = r, StartAngle = sRad, EndAngle = eRad }, "arc");
            }
            case "ellipse":
            {
                if (!TryOffPoint3(spec, out var center, "center", "at") && !TryOffInlinePoint3(spec, out center))
                    throw new ArgumentException("ellipse_requires_center");
                CSMath.XYZ majorAxis;
                double ratio;
                if (TryOffPoint3(spec, out var maj, "majorAxis"))
                {
                    majorAxis = maj;
                    ratio = TryOffDouble(spec["radiusRatio"], out double rr) ? rr : 0.5;
                }
                else
                {
                    if (!(TryOffDouble(spec["majorRadius"], out double rx) || TryOffDouble(spec["rx"], out rx)) ||
                        !(TryOffDouble(spec["minorRadius"], out double ry) || TryOffDouble(spec["ry"], out ry)) ||
                        rx <= 1e-9 || ry <= 1e-9)
                        throw new ArgumentException("ellipse_requires_majorAxis_or_majorRadius_minorRadius");
                    double rot = TryOffAngleRad(spec, "rotation_deg", "rotation", "rotation_rad", out double rt) ? rt : 0.0;
                    majorAxis = new CSMath.XYZ(rx * Math.Cos(rot), rx * Math.Sin(rot), 0.0);
                    ratio = ry / rx;
                }
                double majLen = Math.Sqrt(majorAxis.X * majorAxis.X + majorAxis.Y * majorAxis.Y + majorAxis.Z * majorAxis.Z);
                if (majLen <= 1e-9 || ratio <= 1e-9) throw new ArgumentException("ellipse_invalid_axes");
                if (ratio > 1.0)
                {
                    majorAxis = new CSMath.XYZ(-majorAxis.Y * ratio, majorAxis.X * ratio, majorAxis.Z);
                    ratio = 1.0 / ratio;
                }
                double startParam = 0.0, endParam = 2.0 * Math.PI;
                if (TryOffDouble(spec["startParam"], out double sp) || TryOffAngleRad(spec, "startAngle_deg", null, "startAngle_rad", out sp))
                    startParam = sp;
                if (TryOffDouble(spec["endParam"], out double ep) || TryOffAngleRad(spec, "endAngle_deg", null, "endAngle_rad", out ep))
                    endParam = ep;
                return (new Ellipse
                {
                    Center = center,
                    MajorAxisEndPoint = majorAxis,
                    RadiusRatio = ratio,
                    StartParameter = startParam,
                    EndParameter = endParam
                }, "ellipse");
            }
            case "text":
            case "dbtext":
            {
                string content = spec["text"]?.GetValue<string>() ?? spec["content"]?.GetValue<string>() ?? spec["value"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(content)) throw new ArgumentException("text_requires_content");
                if (!TryOffPoint3(spec, out var pos, "position", "insert", "at") && !TryOffInlinePoint3(spec, out pos))
                    throw new ArgumentException("text_requires_position");
                double h = TryOffDouble(spec["height"], out double hv) && hv > 1e-9 ? hv : 2.5;
                double rot = TryOffAngleRad(spec, "rotation_deg", "rotation", "rotation_rad", out double rt) ? rt : 0.0;
                return (new TextEntity
                {
                    InsertPoint = pos,
                    Value = content,
                    Height = h,
                    Rotation = rot
                }, "text");
            }
            case "mtext":
            {
                string content = spec["text"]?.GetValue<string>() ?? spec["content"]?.GetValue<string>() ?? spec["value"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(content)) throw new ArgumentException("mtext_requires_content");
                if (!TryOffPoint3(spec, out var pos, "position", "insert", "at") && !TryOffInlinePoint3(spec, out pos))
                    throw new ArgumentException("mtext_requires_position");
                double h = TryOffDouble(spec["height"], out double hv) && hv > 1e-9 ? hv : 2.5;
                double w = TryOffDouble(spec["width"], out double wv) && wv > 0 ? wv : 0.0;
                double rot = TryOffAngleRad(spec, "rotation_deg", "rotation", "rotation_rad", out double rt) ? rt : 0.0;
                var mt = new MText
                {
                    InsertPoint = pos,
                    Value = content,
                    Height = h,
                    RectangleWidth = w
                };
                if (Math.Abs(rot) > 1e-12)
                {
                    mt.AlignmentPoint = new CSMath.XYZ(Math.Cos(rot), Math.Sin(rot), 0.0);
                }
                return (mt, "mtext");
            }
            case "point":
            case "dbpoint":
            {
                if (!TryOffPoint3(spec, out var pos, "position", "point", "at") && !TryOffInlinePoint3(spec, out pos))
                    throw new ArgumentException("point_requires_position");
                return (new Point(pos), "point");
            }
            default:
                throw new ArgumentException("unsupported_entity_type:" + rawType);
        }
    }

    private static bool TryOffDouble(JsonNode? node, out double val)
    {
        val = 0.0;
        if (node == null) return false;
        try { val = node.GetValue<double>(); return true; }
        catch
        {
            try
            {
                string? s = node.GetValue<string>();
                return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
            }
            catch { return false; }
        }
    }

    private static bool TryOffInlinePoint3(JsonObject obj, out CSMath.XYZ pt)
    {
        pt = default;
        bool hasX = TryOffDouble(obj["x"], out double x) || TryOffDouble(obj["e"], out x);
        bool hasY = TryOffDouble(obj["y"], out double y) || TryOffDouble(obj["n"], out y);
        if (!hasX || !hasY) return false;
        double z = TryOffDouble(obj["z"], out double zv) ? zv : 0.0;
        pt = new CSMath.XYZ(x, y, z);
        return true;
    }

    private static bool TryOffPoint3(JsonObject obj, out CSMath.XYZ pt, params string[] keys)
    {
        pt = default;
        foreach (string k in keys)
        {
            JsonNode? n = obj[k];
            if (n == null) continue;
            if (n is JsonArray arr && arr.Count >= 2 &&
                TryOffDouble(arr[0], out double x) && TryOffDouble(arr[1], out double y))
            {
                double z = arr.Count >= 3 && TryOffDouble(arr[2], out double zv) ? zv : 0.0;
                pt = new CSMath.XYZ(x, y, z);
                return true;
            }
            if (n is JsonObject sub && TryOffInlinePoint3(sub, out pt)) return true;
        }
        return false;
    }

    private static bool TryOffVertex2(JsonNode? node, out double x, out double y, out double bulge)
    {
        x = 0; y = 0; bulge = 0;
        if (node is JsonArray arr && arr.Count >= 2 &&
            TryOffDouble(arr[0], out x) && TryOffDouble(arr[1], out y))
        {
            if (arr.Count >= 3) TryOffDouble(arr[2], out bulge);
            return true;
        }
        if (node is JsonObject obj)
        {
            bool hasX = TryOffDouble(obj["x"], out x) || TryOffDouble(obj["e"], out x);
            bool hasY = TryOffDouble(obj["y"], out y) || TryOffDouble(obj["n"], out y);
            if (!hasX || !hasY) return false;
            TryOffDouble(obj["bulge"], out bulge);
            return true;
        }
        return false;
    }

    private static bool TryOffAngleRad(JsonObject obj, string? degKey, string? fallbackDegKey, string? radKey, out double rad)
    {
        rad = 0;
        if (degKey != null && TryOffDouble(obj[degKey], out double d)) { rad = d * Math.PI / 180.0; return true; }
        if (radKey != null && TryOffDouble(obj[radKey], out double r)) { rad = r; return true; }
        if (fallbackDegKey != null && TryOffDouble(obj[fallbackDegKey], out double fd)) { rad = fd * Math.PI / 180.0; return true; }
        return false;
    }

    private static JsonObject? EntityToGeoJson(Entity ent)
    {
        switch (ent)
        {
            case LwPolyline lwp when lwp.Vertices.Count >= 2:
            {
                var coords = new JsonArray();
                foreach (var v in lwp.Vertices)
                {
                    coords.Add(new JsonArray(v.Location.X, v.Location.Y));
                }
                bool closed = lwp.IsClosed ||
                    (Math.Abs(lwp.Vertices[0].Location.X - lwp.Vertices[^1].Location.X) < 1e-4 &&
                     Math.Abs(lwp.Vertices[0].Location.Y - lwp.Vertices[^1].Location.Y) < 1e-4);
                if (closed && coords.Count >= 3)
                {
                    EnsureClosedRing(coords);
                    return MakeGeo("Polygon", new JsonArray(coords));
                }
                return MakeGeo("LineString", coords);
            }
            case Polyline2D p2 when p2.Vertices.Count >= 2:
            {
                var coords = new JsonArray();
                foreach (var v in p2.Vertices) coords.Add(new JsonArray(v.Location.X, v.Location.Y));
                if (p2.IsClosed && coords.Count >= 3)
                {
                    EnsureClosedRing(coords);
                    return MakeGeo("Polygon", new JsonArray(coords));
                }
                return MakeGeo("LineString", coords);
            }
            case Polyline3D p3 when p3.Vertices.Count >= 2:
            {
                var coords = new JsonArray();
                foreach (var v in p3.Vertices) coords.Add(new JsonArray(v.Location.X, v.Location.Y, v.Location.Z));
                if (p3.IsClosed && coords.Count >= 3)
                {
                    EnsureClosedRing(coords);
                    return MakeGeo("Polygon", new JsonArray(coords));
                }
                return MakeGeo("LineString", coords);
            }
            case Line ln:
                return MakeGeo("LineString", new JsonArray(
                    new JsonArray(ln.StartPoint.X, ln.StartPoint.Y),
                    new JsonArray(ln.EndPoint.X, ln.EndPoint.Y)));
            case Circle c when c.Radius > 1e-6:
            {
                var ring = new JsonArray();
                for (int i = 0; i <= 24; i++)
                {
                    double a = 2.0 * Math.PI * i / 24.0;
                    ring.Add(new JsonArray(c.Center.X + c.Radius * Math.Cos(a), c.Center.Y + c.Radius * Math.Sin(a)));
                }
                return MakeGeo("Polygon", new JsonArray(ring));
            }
            case Arc arc when arc.Radius > 1e-6:
            {
                double s = arc.StartAngle, e = arc.EndAngle;
                if (e <= s) e += 2.0 * Math.PI;
                var coords = new JsonArray();
                for (int i = 0; i <= 16; i++)
                {
                    double a = s + (e - s) * i / 16.0;
                    coords.Add(new JsonArray(arc.Center.X + arc.Radius * Math.Cos(a), arc.Center.Y + arc.Radius * Math.Sin(a)));
                }
                return MakeGeo("LineString", coords);
            }
            case Point pt:
                return MakeGeo("Point", new JsonArray(pt.Location.X, pt.Location.Y, pt.Location.Z));
            case Insert ins:
                return MakeGeo("Point", new JsonArray(ins.InsertPoint.X, ins.InsertPoint.Y, ins.InsertPoint.Z));
            case Hatch h:
            {
                try
                {
                    var bb = h.GetBoundingBox();
                    if (IsValidBox(bb) && (bb.Max.X > bb.Min.X || bb.Max.Y > bb.Min.Y))
                    {
                        var ring = new JsonArray(
                            new JsonArray(bb.Min.X, bb.Min.Y),
                            new JsonArray(bb.Max.X, bb.Min.Y),
                            new JsonArray(bb.Max.X, bb.Max.Y),
                            new JsonArray(bb.Min.X, bb.Max.Y),
                            new JsonArray(bb.Min.X, bb.Min.Y));
                        return MakeGeo("Polygon", new JsonArray(ring));
                    }
                }
                catch { }
                return null;
            }
            default:
                return null;
        }
    }

    private static bool IsValidBox(CSMath.BoundingBox bb) =>
        IsFinite(bb.Min.X) && IsFinite(bb.Min.Y) &&
        IsFinite(bb.Max.X) && IsFinite(bb.Max.Y) &&
        bb.Min.X <= bb.Max.X && bb.Min.Y <= bb.Max.Y &&
        Math.Abs(bb.Min.X) < 1e15 && Math.Abs(bb.Max.X) < 1e15 &&
        Math.Abs(bb.Min.Y) < 1e15 && Math.Abs(bb.Max.Y) < 1e15;

    private static JsonObject MakeGeo(string type, JsonArray coords) => new()
    {
        ["type"] = type,
        ["coordinates"] = coords
    };

    private static void EnsureClosedRing(JsonArray coords)
    {
        if (coords.Count < 3) return;
        var first = coords[0]!.AsArray();
        var last = coords[^1]!.AsArray();
        double dx = Math.Abs(first[0]!.GetValue<double>() - last[0]!.GetValue<double>());
        double dy = Math.Abs(first[1]!.GetValue<double>() - last[1]!.GetValue<double>());
        if (dx > 1e-4 || dy > 1e-4) coords.Add(first.DeepClone());
    }

    private static bool IsProxyOrUnknown(Entity ent)
    {
        if (ent is UnknownEntity) return true;
        string name = ent.ObjectName ?? ent.GetType().Name;
        return name.Contains("PROXY", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    internal static bool WildcardMatch(string pattern, string input)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(input ?? "", regex, RegexOptions.IgnoreCase);
    }

    internal static string ResolveCategory(string layer, Dictionary<string, string>? catMap)
    {
        if (catMap != null && catMap.Count > 0)
        {
            if (catMap.TryGetValue(layer, out var direct)) return direct;
            foreach (var kv in catMap)
            {
                if (WildcardMatch(kv.Key, layer)) return kv.Value;
            }
        }
        string low = (layer ?? "").ToLowerInvariant();
        if (low.Contains("forbid") || low.Contains("nogo") || low.Contains("wetland") || low.Contains("禁") || low.Contains("保护"))
            return "forbidden";
        if (low.Contains("build") || low.Contains("house") || low.Contains("demol") || low.Contains("建") || low.Contains("房"))
            return "building";
        if (low.Contains("water") || low.Contains("river") || low.Contains("lake") || low.Contains("水") || low.Contains("河") || low.Contains("湖"))
            return "water";
        if (low.Contains("farm") || low.Contains("agri") || low.Contains("农") || low.Contains("田"))
            return "farmland";
        if (low.Contains("road") || low.Contains("highway") || low.Contains("道") || low.Contains("路"))
            return "existing_road";
        if (low.Contains("control") || low.Contains("waypoint") || low.Contains("控制"))
            return "control_point";
        return "avoid";
    }

    internal static JsonObject UnitsNode(int insUnits) => insUnits switch
    {
        1 => new JsonObject { ["insUnits"] = 1, ["code"] = "in", ["scaleToMeter"] = 0.0254 },
        2 => new JsonObject { ["insUnits"] = 2, ["code"] = "ft", ["scaleToMeter"] = 0.3048 },
        4 => new JsonObject { ["insUnits"] = 4, ["code"] = "mm", ["scaleToMeter"] = 0.001 },
        5 => new JsonObject { ["insUnits"] = 5, ["code"] = "cm", ["scaleToMeter"] = 0.01 },
        6 => new JsonObject { ["insUnits"] = 6, ["code"] = "m", ["scaleToMeter"] = 1.0 },
        _ => new JsonObject { ["insUnits"] = insUnits, ["code"] = "unitless", ["scaleToMeter"] = null }
    };
}
