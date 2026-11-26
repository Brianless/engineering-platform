using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Semantic;

namespace EngineeringPlatform.Host.Tools
{
    /// <summary>
    /// 空间数据无损导出工具（v3.0 Phase 1）：
    /// - export_surface_grid：将 Civil 3D TinSurface 真实大地坐标高程格网无损导出为 .npz / .json；
    /// - export_layer_geojson：将指定图层的多段线/线/圆/块参照/点/填充导出为 RFC 7946 GeoJSON。
    /// 直接服务于外置 route_solver 自动选线引擎，不做坐标脱敏与范围截断。
    /// </summary>
    internal static class ExportToolHandlers
    {
        public static Dictionary<string, object> Handle(ToolContext context)
        {
            string tool = context.Item.Tool;
            if (string.Equals(tool, "export_surface_grid", StringComparison.Ordinal))
            {
                return ExportSurfaceGrid(context);
            }
            if (string.Equals(tool, "export_layer_geojson", StringComparison.Ordinal))
            {
                return ExportLayerGeoJson(context);
            }
            throw new ToolFailureException(WireError.Of(ErrorCodes.E2001, "unknown_export_tool:" + tool, "tool"));
        }

        public static string DefaultWorkspaceDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ep_workspace");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        // ---------------- export_surface_grid ----------------

        private static Dictionary<string, object> ExportSurfaceGrid(ToolContext context)
        {
            if (context.Doc == null || context.Db == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_open_document"));
            }

            Dictionary<string, object> p = context.Item.Params ?? new Dictionary<string, object>(StringComparer.Ordinal);
            string wantedSurface = JsonCodec.GetStr(p, "surface");
            double resolution;
            if (!JsonCodec.TryGetDouble(p, "resolution", out resolution) || resolution <= 0.0)
            {
                resolution = 5.0;
            }
            int maxGridDim = JsonCodec.GetInt(p, "maxGridDim", 400);
            if (maxGridDim < 10) maxGridDim = 10;
            if (maxGridDim > 2000) maxGridDim = 2000;

            string format = JsonCodec.GetStr(p, "format", "npz");
            string outPath = JsonCodec.GetStr(p, "outPath");
            if (string.IsNullOrEmpty(outPath))
            {
                string ext = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? ".json" : ".npz";
                outPath = Path.Combine(DefaultWorkspaceDir(), "terrain" + ext);
            }

            double[] customExtent = ToolResultHelper.NumberArray(p, "extent", 4);

            Stopwatch sw = Stopwatch.StartNew();
            object civilDoc = CivilApiProbe.GetCivilDocument(context.Doc);
            if (civilDoc == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E2001,
                    "civil_api_unavailable_for_surface_export"));
            }

            string surfaceName = null;
            string surfaceHandle = null;
            double minX = 0, minY = 0, maxX = 0, maxY = 0;
            double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;
            double[] xCoords = null;
            double[] yCoords = null;
            double[,] grid = null;
            int validCells = 0;
            int totalCells = 0;

            using (Transaction tr = context.Db.TransactionManager.StartTransaction())
            {
                DBObject targetSurface = FindSurface(civilDoc, tr, wantedSurface, out surfaceName, out surfaceHandle);
                if (targetSurface == null)
                {
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E1001,
                        string.IsNullOrEmpty(wantedSurface)
                            ? "no_civil_surface_in_drawing"
                            : "surface_not_found:" + wantedSurface,
                        "surface"));
                }

                // 获取真实大地坐标范围
                bool hasBounds = false;
                if (customExtent != null)
                {
                    minX = Math.Min(customExtent[0], customExtent[2]);
                    minY = Math.Min(customExtent[1], customExtent[3]);
                    maxX = Math.Max(customExtent[0], customExtent[2]);
                    maxY = Math.Max(customExtent[1], customExtent[3]);
                    hasBounds = (maxX > minX && maxY > minY);
                }
                else
                {
                    object general = InvokeZero(targetSurface, "GetGeneralProperties");
                    if (general != null)
                    {
                        double gx0, gy0, gx1, gy1;
                        if (TryReadDoubleMember(general, "MinimumCoordinateX", out gx0) &&
                            TryReadDoubleMember(general, "MinimumCoordinateY", out gy0) &&
                            TryReadDoubleMember(general, "MaximumCoordinateX", out gx1) &&
                            TryReadDoubleMember(general, "MaximumCoordinateY", out gy1) &&
                            gx1 > gx0 && gy1 > gy0)
                        {
                            minX = gx0; minY = gy0; maxX = gx1; maxY = gy1;
                            hasBounds = true;
                        }
                    }
                    if (!hasBounds)
                    {
                        Entity ent = targetSurface as Entity;
                        if (ent != null)
                        {
                            try
                            {
                                Extents3d ext = ent.GeometricExtents;
                                minX = ext.MinPoint.X; minY = ext.MinPoint.Y;
                                maxX = ext.MaxPoint.X; maxY = ext.MaxPoint.Y;
                                hasBounds = (maxX > minX && maxY > minY);
                            }
                            catch (Exception)
                            {
                                hasBounds = false;
                            }
                        }
                    }
                }

                if (!hasBounds)
                {
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E1001,
                        "surface_bounds_empty:" + surfaceName, "surface"));
                }

                // 自动调整步长防止维度超过 maxGridDim
                double spanX = maxX - minX;
                double spanY = maxY - minY;
                double effRes = resolution;
                int nx = Math.Max(2, (int)Math.Floor(spanX / effRes) + 1);
                int ny = Math.Max(2, (int)Math.Floor(spanY / effRes) + 1);
                if (nx > maxGridDim || ny > maxGridDim)
                {
                    double scaleX = spanX / (maxGridDim - 1);
                    double scaleY = spanY / (maxGridDim - 1);
                    effRes = Math.Max(effRes, Math.Max(scaleX, scaleY));
                    nx = Math.Max(2, (int)Math.Floor(spanX / effRes) + 1);
                    ny = Math.Max(2, (int)Math.Floor(spanY / effRes) + 1);
                }

                xCoords = new double[nx];
                yCoords = new double[ny];
                for (int c = 0; c < nx; c++)
                {
                    xCoords[c] = (c == nx - 1) ? maxX : (minX + c * effRes);
                }
                for (int r = 0; r < ny; r++)
                {
                    yCoords[r] = (r == ny - 1) ? maxY : (minY + r * effRes);
                }

                MethodInfo findElev = targetSurface.GetType().GetMethod(
                    "FindElevationAtXY",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(double), typeof(double) },
                    null);
                if (findElev == null)
                {
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E2001,
                        "surface_missing_FindElevationAtXY:" + targetSurface.GetType().Name));
                }

                grid = new double[ny, nx];
                totalCells = ny * nx;
                object[] invokeArgs = new object[2];
                for (int r = 0; r < ny; r++)
                {
                    if ((r & 7) == 0)
                    {
                        context.ThrowIfAborted();
                    }
                    invokeArgs[1] = yCoords[r];
                    for (int c = 0; c < nx; c++)
                    {
                        invokeArgs[0] = xCoords[c];
                        try
                        {
                            object zObj = findElev.Invoke(targetSurface, invokeArgs);
                            double z = Convert.ToDouble(zObj, CultureInfo.InvariantCulture);
                            if (double.IsNaN(z) || double.IsInfinity(z))
                            {
                                grid[r, c] = double.NaN;
                            }
                            else
                            {
                                grid[r, c] = z;
                                validCells++;
                                if (z < minZ) minZ = z;
                                if (z > maxZ) maxZ = z;
                            }
                        }
                        catch (Exception)
                        {
                            // 点位在曲面边界外时会抛 PointNotOnEntityException，按 NaN (nodata) 处理
                            grid[r, c] = double.NaN;
                        }
                    }
                }

                resolution = effRes;
                tr.Commit();
            }

            string outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                WriteTerrainJson(outPath, surfaceName, grid, xCoords, yCoords);
            }
            else
            {
                NpzWriter.WriteTerrainNpz(outPath, grid, xCoords, yCoords);
            }

            sw.Stop();
            long fileSize = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            HostLog.Info("export", "surface_grid_done",
                "surface", surfaceName, "nx", xCoords.Length, "ny", yCoords.Length,
                "valid", validCells, "total", totalCells, "elapsedMs", sw.ElapsedMilliseconds);

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["surface"] = surfaceName;
            result["handle"] = surfaceHandle;
            result["outPath"] = outPath;
            result["format"] = format;
            result["fileBytes"] = fileSize;
            result["nx"] = xCoords.Length;
            result["ny"] = yCoords.Length;
            result["resolution"] = Math.Round(resolution, 4);
            result["validCells"] = validCells;
            result["totalCells"] = totalCells;
            result["extent"] = new List<object> { minX, minY, maxX, maxY };
            result["minElevation"] = validCells > 0 ? (object)Math.Round(minZ, 4) : null;
            result["maxElevation"] = validCells > 0 ? (object)Math.Round(maxZ, 4) : null;
            result["elapsedMs"] = sw.ElapsedMilliseconds;
            result["summary"] = ToolResultHelper.Summary(string.Format(CultureInfo.InvariantCulture,
                "【曲面导出】曲面「{0}」→ {1}｜格网 {2}×{3}（分辨率 {4:0.##}m，有效像元 {5}/{6}）｜耗时 {7}ms",
                surfaceName, outPath, xCoords.Length, yCoords.Length, resolution,
                validCells, totalCells, sw.ElapsedMilliseconds));
            return result;
        }

        private static void WriteTerrainJson(string outPath, string surfaceName,
            double[,] grid, double[] xCoords, double[] yCoords)
        {
            int ny = grid.GetLength(0);
            int nx = grid.GetLength(1);
            List<object> rows = new List<object>(ny);
            for (int r = 0; r < ny; r++)
            {
                List<object> row = new List<object>(nx);
                for (int c = 0; c < nx; c++)
                {
                    double z = grid[r, c];
                    row.Add(double.IsNaN(z) ? null : (object)Math.Round(z, 4));
                }
                rows.Add(row);
            }
            List<object> xs = new List<object>(nx);
            for (int c = 0; c < nx; c++) xs.Add(Math.Round(xCoords[c], 4));
            List<object> ys = new List<object>(ny);
            for (int r = 0; r < ny; r++) ys.Add(Math.Round(yCoords[r], 4));

            Dictionary<string, object> payload = new Dictionary<string, object>(StringComparer.Ordinal);
            payload["surface"] = surfaceName;
            payload["x_coords"] = xs;
            payload["y_coords"] = ys;
            payload["elevation"] = rows;
            File.WriteAllText(outPath, JsonCodec.Serialize(payload), System.Text.Encoding.UTF8);
        }

        // ---------------- export_layer_geojson ----------------

        private static Dictionary<string, object> ExportLayerGeoJson(ToolContext context)
        {
            if (context.Doc == null || context.Db == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_open_document"));
            }

            Dictionary<string, object> p = context.Item.Params ?? new Dictionary<string, object>(StringComparer.Ordinal);
            List<string> layers = JsonCodec.GetStringList(p, "layers");
            Dictionary<string, object> catMapRaw = JsonCodec.GetDict(p, "layerCategoryMap");
            Dictionary<string, string> catMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (catMapRaw != null)
            {
                foreach (KeyValuePair<string, object> kv in catMapRaw)
                {
                    if (kv.Value is string)
                    {
                        catMap[kv.Key] = (string)kv.Value;
                    }
                }
            }
            int limit = JsonCodec.GetInt(p, "limit", 20000);
            if (limit < 1) limit = 1;
            if (limit > 100000) limit = 100000;

            string outPath = JsonCodec.GetStr(p, "outPath");
            if (string.IsNullOrEmpty(outPath))
            {
                outPath = Path.Combine(DefaultWorkspaceDir(), "constraints.geojson");
            }

            Stopwatch sw = Stopwatch.StartNew();
            List<object> features = new List<object>();
            Dictionary<string, int> byCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int scanned = 0;

            using (Transaction tr = context.Db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(context.Db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if ((scanned & 255) == 0)
                    {
                        context.ThrowIfAborted();
                    }
                    scanned++;
                    if (features.Count >= limit)
                    {
                        break;
                    }

                    Entity ent;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (ent == null)
                    {
                        continue;
                    }

                    string layer = ent.Layer ?? "0";
                    if (layers != null && layers.Count > 0 && !MatchesAnyPattern(layer, layers))
                    {
                        continue;
                    }

                    Dictionary<string, object> geometry = EntityToGeoJsonGeometry(ent, tr);
                    if (geometry == null)
                    {
                        continue;
                    }

                    string category = ResolveCategory(layer, catMap);
                    string handle = ent.Handle.Value.ToString("X", CultureInfo.InvariantCulture);

                    Dictionary<string, object> props = new Dictionary<string, object>(StringComparer.Ordinal);
                    props["handle"] = handle;
                    props["layer"] = layer;
                    props["dxfName"] = ent.GetRXClass().DxfName;
                    props["category"] = category;
                    props["name"] = layer + "_" + handle;

                    BlockReference blk = ent as BlockReference;
                    if (blk != null)
                    {
                        props["blockName"] = blk.Name;
                        props["elevation"] = blk.Position.Z;
                    }
                    DBPoint pt = ent as DBPoint;
                    if (pt != null)
                    {
                        props["elevation"] = pt.Position.Z;
                    }

                    Dictionary<string, object> feature = new Dictionary<string, object>(StringComparer.Ordinal);
                    feature["type"] = "Feature";
                    feature["properties"] = props;
                    feature["geometry"] = geometry;
                    features.Add(feature);

                    int prev;
                    byCategory.TryGetValue(category, out prev);
                    byCategory[category] = prev + 1;
                }
                tr.Commit();
            }

            Dictionary<string, object> fc = new Dictionary<string, object>(StringComparer.Ordinal);
            fc["type"] = "FeatureCollection";
            fc["features"] = features;

            string outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }
            string json = JsonCodec.Serialize(fc);
            File.WriteAllText(outPath, json, System.Text.Encoding.UTF8);

            sw.Stop();
            Dictionary<string, object> catSummary = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in byCategory)
            {
                catSummary[kv.Key] = kv.Value;
            }

            HostLog.Info("export", "layer_geojson_done",
                "features", features.Count, "scanned", scanned, "elapsedMs", sw.ElapsedMilliseconds);

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["outPath"] = outPath;
            result["featureCount"] = features.Count;
            result["scannedEntities"] = scanned;
            result["fileBytes"] = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            result["byCategory"] = catSummary;
            result["elapsedMs"] = sw.ElapsedMilliseconds;
            result["summary"] = ToolResultHelper.Summary(string.Format(CultureInfo.InvariantCulture,
                "【图层 GeoJSON 导出】导出 {0} 个要素（扫描 {1} 图元）→ {2}｜耗时 {3}ms",
                features.Count, scanned, outPath, sw.ElapsedMilliseconds));
            return result;
        }

        private static Dictionary<string, object> EntityToGeoJsonGeometry(Entity ent, Transaction tr)
        {
            Polyline pl = ent as Polyline;
            if (pl != null && pl.NumberOfVertices >= 2)
            {
                List<object> coords = new List<object>(pl.NumberOfVertices + 1);
                for (int i = 0; i < pl.NumberOfVertices; i++)
                {
                    Point3d pt = pl.GetPoint3dAt(i);
                    coords.Add(new List<object> { pt.X, pt.Y });
                }
                bool isClosed = pl.Closed || IsRingClosed(pl.GetPoint3dAt(0), pl.GetPoint3dAt(pl.NumberOfVertices - 1));
                if (isClosed && coords.Count >= 3)
                {
                    EnsureClosedRing(coords);
                    return MakeGeoJson("Polygon", new List<object> { coords });
                }
                return MakeGeoJson("LineString", coords);
            }

            Polyline3d pl3 = ent as Polyline3d;
            if (pl3 != null)
            {
                List<object> coords = new List<object>();
                foreach (ObjectId vId in pl3)
                {
                    PolylineVertex3d v = tr.GetObject(vId, OpenMode.ForRead, false) as PolylineVertex3d;
                    if (v != null)
                    {
                        coords.Add(new List<object> { v.Position.X, v.Position.Y, v.Position.Z });
                    }
                }
                if (coords.Count >= 2)
                {
                    if (pl3.Closed && coords.Count >= 3)
                    {
                        EnsureClosedRing(coords);
                        return MakeGeoJson("Polygon", new List<object> { coords });
                    }
                    return MakeGeoJson("LineString", coords);
                }
            }

            Polyline2d pl2 = ent as Polyline2d;
            if (pl2 != null)
            {
                List<object> coords = new List<object>();
                foreach (ObjectId vId in pl2)
                {
                    Vertex2d v = tr.GetObject(vId, OpenMode.ForRead, false) as Vertex2d;
                    if (v != null)
                    {
                        coords.Add(new List<object> { v.Position.X, v.Position.Y });
                    }
                }
                if (coords.Count >= 2)
                {
                    if (pl2.Closed && coords.Count >= 3)
                    {
                        EnsureClosedRing(coords);
                        return MakeGeoJson("Polygon", new List<object> { coords });
                    }
                    return MakeGeoJson("LineString", coords);
                }
            }

            Line line = ent as Line;
            if (line != null)
            {
                return MakeGeoJson("LineString", new List<object>
                {
                    new List<object> { line.StartPoint.X, line.StartPoint.Y },
                    new List<object> { line.EndPoint.X, line.EndPoint.Y }
                });
            }

            Circle circle = ent as Circle;
            if (circle != null && circle.Radius > 1e-6)
            {
                List<object> ring = new List<object>(25);
                for (int i = 0; i <= 24; i++)
                {
                    double ang = (2.0 * Math.PI * i) / 24.0;
                    ring.Add(new List<object>
                    {
                        circle.Center.X + circle.Radius * Math.Cos(ang),
                        circle.Center.Y + circle.Radius * Math.Sin(ang)
                    });
                }
                return MakeGeoJson("Polygon", new List<object> { ring });
            }

            Arc arc = ent as Arc;
            if (arc != null && arc.Radius > 1e-6)
            {
                double startA = arc.StartAngle;
                double endA = arc.EndAngle;
                if (endA <= startA) endA += 2.0 * Math.PI;
                int segs = 16;
                List<object> coords = new List<object>(segs + 1);
                for (int i = 0; i <= segs; i++)
                {
                    double a = startA + (endA - startA) * i / segs;
                    coords.Add(new List<object>
                    {
                        arc.Center.X + arc.Radius * Math.Cos(a),
                        arc.Center.Y + arc.Radius * Math.Sin(a)
                    });
                }
                return MakeGeoJson("LineString", coords);
            }

            DBPoint dbPt = ent as DBPoint;
            if (dbPt != null)
            {
                return MakeGeoJson("Point", new List<object> { dbPt.Position.X, dbPt.Position.Y, dbPt.Position.Z });
            }

            BlockReference br = ent as BlockReference;
            if (br != null)
            {
                return MakeGeoJson("Point", new List<object> { br.Position.X, br.Position.Y, br.Position.Z });
            }

            // Hatch：用外包矩形作为保守多边形（避免复杂边界异常）
            Hatch hatch = ent as Hatch;
            if (hatch != null)
            {
                try
                {
                    Extents3d ext = hatch.GeometricExtents;
                    List<object> ring = new List<object>
                    {
                        new List<object> { ext.MinPoint.X, ext.MinPoint.Y },
                        new List<object> { ext.MaxPoint.X, ext.MinPoint.Y },
                        new List<object> { ext.MaxPoint.X, ext.MaxPoint.Y },
                        new List<object> { ext.MinPoint.X, ext.MaxPoint.Y },
                        new List<object> { ext.MinPoint.X, ext.MinPoint.Y }
                    };
                    return MakeGeoJson("Polygon", new List<object> { ring });
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return null;
        }

        private static Dictionary<string, object> MakeGeoJson(string type, object coordinates)
        {
            Dictionary<string, object> g = new Dictionary<string, object>(StringComparer.Ordinal);
            g["type"] = type;
            g["coordinates"] = coordinates;
            return g;
        }

        private static bool IsRingClosed(Point3d a, Point3d b)
        {
            return Math.Abs(a.X - b.X) < 1e-4 && Math.Abs(a.Y - b.Y) < 1e-4;
        }

        private static void EnsureClosedRing(List<object> coords)
        {
            if (coords.Count < 3) return;
            List<object> first = coords[0] as List<object>;
            List<object> last = coords[coords.Count - 1] as List<object>;
            if (first != null && last != null && first.Count >= 2 && last.Count >= 2)
            {
                double dx = Math.Abs(Convert.ToDouble(first[0], CultureInfo.InvariantCulture) - Convert.ToDouble(last[0], CultureInfo.InvariantCulture));
                double dy = Math.Abs(Convert.ToDouble(first[1], CultureInfo.InvariantCulture) - Convert.ToDouble(last[1], CultureInfo.InvariantCulture));
                if (dx > 1e-4 || dy > 1e-4)
                {
                    coords.Add(new List<object>(first));
                }
            }
        }

        private static bool MatchesAnyPattern(string layer, List<string> patterns)
        {
            for (int i = 0; i < patterns.Count; i++)
            {
                if (LayerMapping.WildcardMatch(patterns[i], layer))
                {
                    return true;
                }
            }
            return false;
        }

        private static string ResolveCategory(string layer, Dictionary<string, string> catMap)
        {
            if (catMap != null && catMap.Count > 0)
            {
                string direct;
                if (catMap.TryGetValue(layer, out direct))
                {
                    return direct;
                }
                foreach (KeyValuePair<string, string> kv in catMap)
                {
                    if (LayerMapping.WildcardMatch(kv.Key, layer))
                    {
                        return kv.Value;
                    }
                }
            }
            string low = (layer ?? string.Empty).ToLowerInvariant();
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

        private static DBObject FindSurface(object civilDoc, Transaction tr, string wanted,
            out string name, out string handle)
        {
            name = null;
            handle = null;
            object rawIds = InvokeZero(civilDoc, "GetSurfaceIds") ?? GetMember(civilDoc, "Surfaces");
            IEnumerable enumerable = rawIds as IEnumerable;
            if (enumerable == null)
            {
                return null;
            }
            foreach (object item in enumerable)
            {
                ObjectId id = item is ObjectId ? (ObjectId)item : ObjectId.Null;
                if (id.IsNull || id.IsErased) continue;
                DBObject obj;
                try
                {
                    obj = tr.GetObject(id, OpenMode.ForRead, false);
                }
                catch (Exception)
                {
                    continue;
                }
                if (obj == null) continue;
                string objName = Convert.ToString(GetMember(obj, "Name"), CultureInfo.InvariantCulture) ?? string.Empty;
                string objHandle = id.Handle.Value.ToString("X", CultureInfo.InvariantCulture);

                if (string.IsNullOrEmpty(wanted) ||
                    string.Equals(objName, wanted, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(objHandle, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    name = objName;
                    handle = objHandle;
                    return obj;
                }
            }
            return null;
        }

        private static object GetMember(object target, string name)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
                {
                    return prop.GetValue(target, null);
                }
            }
            catch (Exception) { }
            return null;
        }

        private static object InvokeZero(object target, string name)
        {
            if (target == null) return null;
            try
            {
                MethodInfo m = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m != null && m.ReturnType != typeof(void))
                {
                    return m.Invoke(target, null);
                }
            }
            catch (Exception) { }
            return null;
        }

        private static bool TryReadDoubleMember(object target, string name, out double value)
        {
            value = 0.0;
            object raw = GetMember(target, name);
            if (raw == null) return false;
            try
            {
                value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
