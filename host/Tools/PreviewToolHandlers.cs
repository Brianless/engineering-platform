using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;

namespace EngineeringPlatform.Host.Tools
{
    /// <summary>
    /// 瞬态 3D 预览工具（v3.0 Phase 3）：
    /// 通过 TransientManager 在视口中绘制非破坏性 3D 预览图形（不修改图纸数据库、不增加 rev），
    /// 适合对比多条候选自动选线走廊；所有图形由 HostRuntime.Transients 统一托管生命周期。
    /// </summary>
    internal static class PreviewToolHandlers
    {
        public static Dictionary<string, object> Handle(ToolContext context)
        {
            string tool = context.Item.Tool;
            if (string.Equals(tool, "preview_transient", StringComparison.Ordinal))
            {
                return PreviewTransient(context);
            }
            throw new ToolFailureException(WireError.Of(ErrorCodes.E2001, "unknown_preview_tool:" + tool, "tool"));
        }

        private static Dictionary<string, object> PreviewTransient(ToolContext context)
        {
            Dictionary<string, object> p = context.Item.Params ?? new Dictionary<string, object>(StringComparer.Ordinal);
            string action = JsonCodec.GetStr(p, "action", "add");
            string group = JsonCodec.GetStr(p, "group", "default");
            TransientRegistry registry = context.Runtime.Transients;

            if (string.Equals(action, "clear", StringComparison.OrdinalIgnoreCase))
            {
                bool all = string.Equals(group, "*", StringComparison.Ordinal) ||
                           string.Equals(group, "all", StringComparison.OrdinalIgnoreCase);
                int cleared = all ? registry.ClearAll() : registry.ClearGroup(group);
                RefreshViewport(context);
                Dictionary<string, object> clearRes = new Dictionary<string, object>(StringComparer.Ordinal);
                clearRes["action"] = "clear";
                clearRes["group"] = group;
                clearRes["cleared"] = cleared;
                clearRes["activeGroups"] = ToObjDict(registry.GroupCounts());
                clearRes["summary"] = ToolResultHelper.Summary("【瞬态预览】已清除组「" + group + "」共 " + cleared + " 个瞬态图元。");
                return clearRes;
            }

            if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, int> counts = registry.GroupCounts();
                Dictionary<string, object> listRes = new Dictionary<string, object>(StringComparer.Ordinal);
                listRes["action"] = "list";
                listRes["activeGroups"] = ToObjDict(counts);
                listRes["summary"] = ToolResultHelper.Summary("【瞬态预览】当前活跃分组 " + counts.Count + " 个。");
                return listRes;
            }

            // action == "add" (默认) 或 "replace"
            if (string.Equals(action, "replace", StringComparison.OrdinalIgnoreCase))
            {
                registry.ClearGroup(group);
            }

            List<object> primitives = JsonCodec.GetList(p, "primitives");
            if (primitives == null || primitives.Count == 0)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "primitives_required", "primitives"));
            }

            List<Drawable> drawables = new List<Drawable>(primitives.Count);
            for (int i = 0; i < primitives.Count; i++)
            {
                Dictionary<string, object> item = primitives[i] as Dictionary<string, object>;
                if (item == null) continue;

                string kind = JsonCodec.GetStr(item, "type", "polyline");
                int colorIndex = JsonCodec.GetInt(item, "colorIndex", 1); // 1=Red, 2=Yellow, 3=Green, 4=Cyan, 5=Blue, 6=Magenta
                if (colorIndex < 1 || colorIndex > 255) colorIndex = 1;

                Entity ent = BuildEntity(kind, item);
                if (ent != null)
                {
                    ent.ColorIndex = colorIndex;
                    drawables.Add(ent);
                }
            }

            int added = registry.AddGroup(group, drawables);
            RefreshViewport(context);

            HostLog.Info("transient", "preview_added", "group", group, "added", added);

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["action"] = action;
            result["group"] = group;
            result["added"] = added;
            result["activeGroups"] = ToObjDict(registry.GroupCounts());
            result["summary"] = ToolResultHelper.Summary("【瞬态预览】已在分组「" + group + "」渲染 " + added + " 个瞬态图元（未修改图纸 rev）。");
            return result;
        }

        private static Entity BuildEntity(string kind, Dictionary<string, object> item)
        {
            if (string.Equals(kind, "polyline", StringComparison.OrdinalIgnoreCase))
            {
                List<object> pts = JsonCodec.GetList(item, "points");
                if (pts == null || pts.Count < 2) return null;
                Point3dCollection p3d = new Point3dCollection();
                for (int i = 0; i < pts.Count; i++)
                {
                    Point3d pt;
                    if (TryReadPoint(pts[i], out pt))
                    {
                        p3d.Add(pt);
                    }
                }
                if (p3d.Count < 2) return null;
                bool closed = JsonCodec.GetBool(item, "closed", false);
                return new Polyline3d(Poly3dType.SimplePoly, p3d, closed);
            }

            if (string.Equals(kind, "line", StringComparison.OrdinalIgnoreCase))
            {
                Point3d p0, p1;
                object raw0, raw1;
                if (item.TryGetValue("start", out raw0) && item.TryGetValue("end", out raw1) &&
                    TryReadPoint(raw0, out p0) && TryReadPoint(raw1, out p1))
                {
                    return new Line(p0, p1);
                }
                return null;
            }

            if (string.Equals(kind, "circle", StringComparison.OrdinalIgnoreCase))
            {
                Point3d center;
                object rawCenter;
                double radius;
                if (item.TryGetValue("center", out rawCenter) && TryReadPoint(rawCenter, out center) &&
                    JsonCodec.TryGetDouble(item, "radius", out radius) && radius > 1e-6)
                {
                    return new Circle(center, Vector3d.ZAxis, radius);
                }
                return null;
            }

            if (string.Equals(kind, "text", StringComparison.OrdinalIgnoreCase))
            {
                Point3d pos;
                object rawPos;
                string text = JsonCodec.GetStr(item, "text");
                double height;
                if (!JsonCodec.TryGetDouble(item, "height", out height) || height <= 0) height = 5.0;
                if (!string.IsNullOrEmpty(text) && item.TryGetValue("position", out rawPos) && TryReadPoint(rawPos, out pos))
                {
                    DBText t = new DBText();
                    t.Position = pos;
                    t.TextString = text;
                    t.Height = height;
                    return t;
                }
            }

            return null;
        }

        private static bool TryReadPoint(object raw, out Point3d pt)
        {
            pt = Point3d.Origin;
            List<object> arr = JsonCodec.AsList(raw);
            if (arr != null && arr.Count >= 2)
            {
                try
                {
                    double x = Convert.ToDouble(arr[0], CultureInfo.InvariantCulture);
                    double y = Convert.ToDouble(arr[1], CultureInfo.InvariantCulture);
                    double z = arr.Count >= 3 ? Convert.ToDouble(arr[2], CultureInfo.InvariantCulture) : 0.0;
                    pt = new Point3d(x, y, z);
                    return true;
                }
                catch (Exception) { return false; }
            }
            Dictionary<string, object> dict = raw as Dictionary<string, object>;
            if (dict != null)
            {
                double x, y, z = 0.0;
                bool hasX = JsonCodec.TryGetDouble(dict, "x", out x) || JsonCodec.TryGetDouble(dict, "e", out x);
                bool hasY = JsonCodec.TryGetDouble(dict, "y", out y) || JsonCodec.TryGetDouble(dict, "n", out y);
                JsonCodec.TryGetDouble(dict, "z", out z);
                if (hasX && hasY)
                {
                    pt = new Point3d(x, y, z);
                    return true;
                }
            }
            return false;
        }

        private static void RefreshViewport(ToolContext context)
        {
            try
            {
                if (context.Doc != null && context.Doc.Editor != null)
                {
                    context.Doc.Editor.UpdateScreen();
                }
            }
            catch (Exception)
            {
                // 无图形视口时忽略
            }
        }

        private static Dictionary<string, object> ToObjDict(Dictionary<string, int> source)
        {
            Dictionary<string, object> res = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in source)
            {
                res[kv.Key] = kv.Value;
            }
            return res;
        }
    }
}
