using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Semantic;

namespace EngineeringPlatform.Host.Preview
{
    /// <summary>图例项（颜色 → 图层/类型 → 数量）。</summary>
    internal sealed class LegendEntry
    {
        public string Color = "#1f77b4";
        public string Label = string.Empty;
        public int Count;

        public Dictionary<string, object> ToDict()
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["color"] = Color;
            dict["label"] = Label;
            dict["count"] = Count;
            return dict;
        }
    }

    /// <summary>渲染结果。</summary>
    internal sealed class PreviewResult
    {
        public string PreviewPath = string.Empty;
        public int Bytes;
        public int Width;
        public int Height;
        public double[] Min = new double[3];
        public double[] Max = new double[3];
        public List<LegendEntry> Legend = new List<LegendEntry>();
        public bool Oversize;
        public bool FontFallback;
        public string Note = "结构示意图，非出图成果";
    }

    /// <summary>
    /// 结构示意图渲染（设计 §3.6）。
    /// 明确选择：M0 不调用 PlotEngine / GraphicsInterface 做真实渲染，
    /// 而是用 System.Drawing 把语义模型里的几何画成 PNG——确定性、零外部依赖、几十毫秒，
    /// 且不会因为打印设备/PC3/CTB 配置在无人交互的主线程里卡住（E1004 的主要来源之一）。
    /// 注意字段 note 恒为「结构示意图，非出图成果」。
    /// </summary>
    internal sealed class PreviewRenderer
    {
        /// <summary>8 色调色板（不用真实 ACI 颜色，避免黑底白线反色问题）。</summary>
        private static readonly string[] Palette =
        {
            "#1f77b4", "#ff7f0e", "#2ca02c", "#d62728",
            "#9467bd", "#8c564b", "#e377c2", "#17becf"
        };

        private const int MaxDrawOps = 20000;
        private const int MaxImageBytes = 1024 * 1024;

        private readonly PreviewCache _cache;

        public PreviewRenderer(PreviewCache cache)
        {
            _cache = cache ?? new PreviewCache(null);
        }

        /// <summary>渲染 PNG。异常统一转 E5001（磁盘/权限）。</summary>
        public PreviewResult Render(SemanticModel model, double[] extent, int width, int height,
            List<string> highlightHandles, List<WireWarning> warnings)
        {
            PreviewResult result = new PreviewResult();
            result.Width = width;
            result.Height = height;

            double x0;
            double y0;
            double x1;
            double y1;
            ResolveExtent(model, extent, out x0, out y0, out x1, out y1);
            result.Min[0] = x0;
            result.Min[1] = y0;
            result.Min[2] = 0D;
            result.Max[0] = x1;
            result.Max[1] = y1;
            result.Max[2] = 0D;

            string error;
            if (!_cache.EnsureDirectory(out error))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E5001, "preview_write_failed:" + error));
            }

            string extentHash = ExtentHash(x0, y0, x1, y1);
            result.PreviewPath = _cache.BuildPath(model == null ? null : model.Fingerprint, width, height, extentHash);

            Bitmap bitmap = null;
            try
            {
                bitmap = Draw(model, x0, y0, x1, y1, width, height, highlightHandles, result, warnings);
                SavePng(bitmap, result.PreviewPath);
                result.Bytes = (int)new FileInfo(result.PreviewPath).Length;

                if (result.Bytes > MaxImageBytes)
                {
                    // 按 0.8 比例重试一次
                    int retryWidth = Math.Max(200, (int)(width * 0.8));
                    int retryHeight = Math.Max(200, (int)(height * 0.8));
                    bitmap.Dispose();
                    bitmap = Draw(model, x0, y0, x1, y1, retryWidth, retryHeight, highlightHandles, result, warnings);
                    SavePng(bitmap, result.PreviewPath);
                    result.Width = retryWidth;
                    result.Height = retryHeight;
                    result.Bytes = (int)new FileInfo(result.PreviewPath).Length;

                    if (result.Bytes > MaxImageBytes)
                    {
                        result.Oversize = true;
                        HostLog.Warn("preview", "oversize", "bytes", result.Bytes, "limit", MaxImageBytes);
                    }
                }
            }
            catch (ToolFailureException)
            {
                throw;
            }
            catch (Exception ex)
            {
                HostLog.Exception("preview", "render_failed", ex);
                throw new ToolFailureException(WireError.Of(ErrorCodes.E5001, "preview_render_failed"));
            }
            finally
            {
                if (bitmap != null)
                {
                    bitmap.Dispose();
                }
            }

            _cache.Trim(200);
            return result;
        }

        private static void ResolveExtent(SemanticModel model, double[] extent,
            out double x0, out double y0, out double x1, out double y1)
        {
            if (extent != null && extent.Length == 4 && extent[2] > extent[0] && extent[3] > extent[1])
            {
                x0 = extent[0];
                y0 = extent[1];
                x1 = extent[2];
                y1 = extent[3];
                return;
            }

            if (model != null && model.HasExtents && model.Max[0] > model.Min[0] && model.Max[1] > model.Min[1])
            {
                x0 = model.Min[0];
                y0 = model.Min[1];
                x1 = model.Max[0];
                y1 = model.Max[1];
                return;
            }

            x0 = 0D;
            y0 = 0D;
            x1 = 100D;
            y1 = 100D;
        }

        private Bitmap Draw(SemanticModel model, double x0, double y0, double x1, double y1,
            int width, int height, List<string> highlightHandles, PreviewResult result, List<WireWarning> warnings)
        {
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.InterpolationMode = InterpolationMode.Low;

                double drawingWidth = Math.Max(x1 - x0, 1e-9);
                double drawingHeight = Math.Max(y1 - y0, 1e-9);
                double margin = Math.Min(width, height) * 0.06;
                double scale = Math.Min((width - margin * 2) / drawingWidth, (height - margin * 2.6) / drawingHeight);
                if (scale <= 0D || double.IsInfinity(scale))
                {
                    scale = 1D;
                }

                double padX = (width - drawingWidth * scale) / 2D;
                double padY = (height - drawingHeight * scale) / 2D + margin * 0.35;
                // 世界坐标 (x0, y1) 映射到画布左上 (padX, padY)，(x1, y0) 映射到画布右下
                double offsetX = padX - x0 * scale;
                double offsetY = padY + y1 * scale;

                Dictionary<string, Pen> pens = new Dictionary<string, Pen>(StringComparer.Ordinal);
                Dictionary<string, int> typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

                HashSet<string> highlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (highlightHandles != null)
                {
                    for (int i = 0; i < highlightHandles.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(highlightHandles[i]))
                        {
                            highlight.Add(highlightHandles[i]);
                        }
                    }
                }

                // 字体提前选定（供实体文字 TEXT/MTEXT 渲染与图例共用）
                string fontName = PickFont(result, warnings);

                int drawOps = 0;
                if (model != null && model.Entities != null)
                {
                    for (int i = 0; i < model.Entities.Count && drawOps < MaxDrawOps; i++)
                    {
                        ObjectRef obj = model.Entities[i];
                        if (obj == null)
                        {
                            continue;
                        }

                        // 只画可见图层上的对象（不可见对象不参与预览）
                        if (!obj.Visible)
                        {
                            continue;
                        }

                        int count;
                        typeCounts.TryGetValue(obj.DxfName, out count);
                        typeCounts[obj.DxfName] = count + 1;

                        Pen pen = PenFor(pens, obj.DxfName);
                        if (highlight.Contains(obj.Handle))
                        {
                            pen = new Pen(Color.FromArgb(255, 220, 0), 3f);
                        }

                        drawOps += DrawObject(graphics, obj, pen, fontName, scale, offsetX, offsetY, x0, y0, x1, y1, width, height);
                    }
                }

                // 图例（取数量最多的 8 类）
                result.Legend = BuildLegend(typeCounts);

                using (Font titleFont = new Font(fontName, 13f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font smallFont = new Font(fontName, 10f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(60, 60, 60)))
                {
                    string fingerprintShort = PreviewCache.ShortFingerprint(model == null ? null : model.Fingerprint);
                    string title = (model == null ? "未命名图纸" : model.DrawingBaseName)
                        + "  " + fingerprintShort + "  " + (result.FontFallback ? "READ-ONLY PREVIEW" : "只读预览");
                    graphics.DrawString(title, titleFont, textBrush, new PointF((float)margin, (float)(margin * 0.4)));

                    // 比例尺（右下）
                    string scaleText = BuildScaleBar(x1 - x0, model);
                    SizeF scaleSize = graphics.MeasureString(scaleText, smallFont);
                    graphics.DrawString(scaleText, smallFont, textBrush,
                        new PointF((float)(width - margin - scaleSize.Width), (float)(height - margin - scaleSize.Height)));

                    // 水印（底部居中）
                    string watermark = result.FontFallback
                        ? "SCHEMATIC ONLY - NOT A DELIVERABLE"
                        : "结构示意图 · 非出图成果 · 请以正式图纸为准";
                    SizeF watermarkSize = graphics.MeasureString(watermark, smallFont);
                    graphics.DrawString(watermark, smallFont, new SolidBrush(Color.FromArgb(120, 120, 120)),
                        new PointF((float)((width - watermarkSize.Width) / 2D), (float)(height - margin * 0.55)));

                    // 图例（左上依次下排）
                    float marginF = (float)margin;
                    float legendY = marginF * 0.4f + 20f;
                    for (int i = 0; i < result.Legend.Count; i++)
                    {
                        LegendEntry entry = result.Legend[i];
                        using (SolidBrush brush = new SolidBrush(ParseColor(entry.Color)))
                        {
                            graphics.FillRectangle(brush, marginF, legendY, 12f, 12f);
                        }
                        graphics.DrawString(entry.Label + " (" + entry.Count + ")", smallFont, textBrush,
                            new PointF(marginF + 16f, legendY - 1f));
                        legendY += 15f;
                    }
                }

                foreach (KeyValuePair<string, Pen> pair in pens)
                {
                    pair.Value.Dispose();
                }

                // 边框
                using (Pen border = new Pen(Color.FromArgb(200, 200, 200), 1f))
                {
                    graphics.DrawRectangle(border, 0.5f, 0.5f, width - 1.5f, height - 1.5f);
                }
            }
            return bitmap;
        }

        private static int DrawObject(Graphics graphics, ObjectRef obj, Pen pen, string fontName,
            double scale, double offsetX, double offsetY, double x0, double y0, double x1, double y1,
            int width, int height)
        {
            try
            {
                // LINE：用起终点精确绘制
                if (string.Equals(obj.DxfName, "LINE", StringComparison.OrdinalIgnoreCase))
                {
                    List<object> start = GetPoint(obj, "start");
                    List<object> end = GetPoint(obj, "end");
                    if (start != null && end != null)
                    {
                        PointF p1 = Transform(ToDouble(start[0]), ToDouble(start[1]), scale, offsetX, offsetY);
                        PointF p2 = Transform(ToDouble(end[0]), ToDouble(end[1]), scale, offsetX, offsetY);
                        if (InBounds(p1, width, height) || InBounds(p2, width, height))
                        {
                            graphics.DrawLine(pen, p1, p2);
                            return 1;
                        }
                        return 0;
                    }
                }

                // ARC：用真实圆心、半径与起止角绘制开口圆弧（严禁画成闭合整圆）
                if (string.Equals(obj.DxfName, "ARC", StringComparison.OrdinalIgnoreCase))
                {
                    List<object> arcCenter = GetPoint(obj, "center");
                    object rObj;
                    object saObj;
                    object eaObj;
                    if (arcCenter != null && arcCenter.Count >= 2 &&
                        obj.Geometry.TryGetValue("radius", out rObj) &&
                        obj.Geometry.TryGetValue("startAngle_deg", out saObj) &&
                        obj.Geometry.TryGetValue("endAngle_deg", out eaObj))
                    {
                        double rad = ToDouble(rObj);
                        double rPx = rad * scale;
                        if (rPx >= 0.5D && rPx < Math.Max(width, height) * 50D)
                        {
                            PointF cp = Transform(ToDouble(arcCenter[0]), ToDouble(arcCenter[1]), scale, offsetX, offsetY);
                            float diam = (float)(2D * rPx);
                            float arcLeft = cp.X - (float)rPx;
                            float arcTop = cp.Y - (float)rPx;
                            if (arcLeft <= width && arcTop <= height && arcLeft + diam >= 0 && arcTop + diam >= 0)
                            {
                                double startDeg = ToDouble(saObj);
                                double endDeg = ToDouble(eaObj);
                                double sweepCcw = (endDeg - startDeg) % 360D;
                                if (sweepCcw <= 1e-6D) sweepCcw += 360D;
                                // AutoCAD 逆时针(Y向上) → GDI+ 顺时针(Y向下)：起点=-endDeg，顺时针扫掠=+sweepCcw
                                float gdiStart = (float)(-endDeg);
                                float gdiSweep = (float)sweepCcw;
                                graphics.DrawArc(pen, arcLeft, arcTop, diam, diam, gdiStart, gdiSweep);
                                return 1;
                            }
                            return 0;
                        }
                    }
                }

                // CIRCLE：优先用真实圆心与半径精确绘制
                if (string.Equals(obj.DxfName, "CIRCLE", StringComparison.OrdinalIgnoreCase))
                {
                    List<object> cirCenter = GetPoint(obj, "center");
                    object rObj;
                    if (cirCenter != null && cirCenter.Count >= 2 && obj.Geometry.TryGetValue("radius", out rObj))
                    {
                        double rPx = ToDouble(rObj) * scale;
                        if (rPx >= 0.5D && rPx < Math.Max(width, height) * 50D)
                        {
                            PointF cp = Transform(ToDouble(cirCenter[0]), ToDouble(cirCenter[1]), scale, offsetX, offsetY);
                            float diam = (float)(2D * rPx);
                            float cLeft = cp.X - (float)rPx;
                            float cTop = cp.Y - (float)rPx;
                            if (cLeft <= width && cTop <= height && cLeft + diam >= 0 && cTop + diam >= 0)
                            {
                                graphics.DrawEllipse(pen, cLeft, cTop, diam, diam);
                                return 1;
                            }
                            return 0;
                        }
                    }
                }

                // ELLIPSE：按真实中心、长半轴、短半轴与参数起止角绘制椭圆/椭圆弧（支持任意旋转，不退化成外接矩形）
                if (string.Equals(obj.DxfName, "ELLIPSE", StringComparison.OrdinalIgnoreCase))
                {
                    List<object> eCenter = GetPoint(obj, "center");
                    List<object> majorAxis = GetPoint(obj, "majorAxis");
                    List<object> minorAxis = GetPoint(obj, "minorAxis");
                    if (eCenter != null && eCenter.Count >= 2 && majorAxis != null && majorAxis.Count >= 2)
                    {
                        double ecx = ToDouble(eCenter[0]);
                        double ecy = ToDouble(eCenter[1]);
                        double ax = ToDouble(majorAxis[0]);
                        double ay = ToDouble(majorAxis[1]);
                        double bx;
                        double by;
                        if (minorAxis != null && minorAxis.Count >= 2)
                        {
                            bx = ToDouble(minorAxis[0]);
                            by = ToDouble(minorAxis[1]);
                        }
                        else
                        {
                            object ratioObj;
                            double ratio = obj.Geometry.TryGetValue("radiusRatio", out ratioObj) ? ToDouble(ratioObj) : 0.5D;
                            bx = -ay * ratio;
                            by = ax * ratio;
                        }
                        object spObj;
                        object epObj;
                        double sp = obj.Geometry.TryGetValue("startParam", out spObj) ? ToDouble(spObj) : 0D;
                        double ep = obj.Geometry.TryGetValue("endParam", out epObj) ? ToDouble(epObj) : (2D * Math.PI);
                        if (ep <= sp + 1e-6D)
                        {
                            ep = sp + 2D * Math.PI;
                        }
                        const int segs = 64;
                        PointF[] pts = new PointF[segs + 1];
                        bool anyVisible = false;
                        for (int k = 0; k <= segs; k++)
                        {
                            double t = sp + (ep - sp) * k / (double)segs;
                            double cosT = Math.Cos(t);
                            double sinT = Math.Sin(t);
                            double wx = ecx + ax * cosT + bx * sinT;
                            double wy = ecy + ay * cosT + by * sinT;
                            PointF pf = Transform(wx, wy, scale, offsetX, offsetY);
                            pts[k] = pf;
                            if (InBounds(pf, width, height))
                            {
                                anyVisible = true;
                            }
                        }
                        if (anyVisible)
                        {
                            graphics.DrawLines(pen, pts);
                            return 1;
                        }
                        return 0;
                    }
                }

                // Q-19：TEXT / MTEXT 按真实位置、字高与旋转角绘制真实文字（不再退化成空心矩形框）
                if (string.Equals(obj.DxfName, "TEXT", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(obj.DxfName, "MTEXT", StringComparison.OrdinalIgnoreCase))
                {
                    List<object> pos = GetPoint(obj, "position");
                    string textVal = null;
                    object textObj;
                    if (obj.Geometry.TryGetValue("text", out textObj) && textObj != null)
                    {
                        textVal = Convert.ToString(textObj, CultureInfo.InvariantCulture);
                    }
                    if (string.IsNullOrEmpty(textVal) && obj.Texts != null && obj.Texts.Count > 0)
                    {
                        textVal = obj.Texts[0];
                    }
                    if (pos != null && pos.Count >= 2 && !string.IsNullOrEmpty(textVal))
                    {
                        PointF tp = Transform(ToDouble(pos[0]), ToDouble(pos[1]), scale, offsetX, offsetY);
                        object hObj;
                        double worldH = obj.Geometry.TryGetValue("height", out hObj) ? ToDouble(hObj) : 2.5D;
                        if (worldH <= 1e-6D) worldH = 2.5D;
                        float fontPx = (float)Math.Max(8.0D, Math.Min(96.0D, worldH * scale));
                        object rotObj;
                        double rotDeg = obj.Geometry.TryGetValue("rotation_deg", out rotObj) ? ToDouble(rotObj) : 0D;
                        bool isMText = string.Equals(obj.DxfName, "MTEXT", StringComparison.OrdinalIgnoreCase);

                        if (tp.X >= -width && tp.X <= width * 2f && tp.Y >= -height && tp.Y <= height * 2f)
                        {
                            GraphicsState saved = graphics.Save();
                            try
                            {
                                graphics.TranslateTransform(tp.X, tp.Y);
                                if (Math.Abs(rotDeg) > 1e-3D)
                                {
                                    graphics.RotateTransform((float)(-rotDeg));
                                }
                                using (Font entFont = new Font(fontName, fontPx, FontStyle.Regular, GraphicsUnit.Pixel))
                                using (SolidBrush entBrush = new SolidBrush(pen.Color))
                                {
                                    // DBText.Position 在 CAD 中是基线左下角，GDI+ Y 轴向下故上移 ~1.12 倍字高；MText.Location 为左上角
                                    float drawY = isMText ? 0f : (-fontPx * 1.12f);
                                    graphics.DrawString(textVal, entFont, entBrush, new PointF(0f, drawY));
                                }
                            }
                            finally
                            {
                                graphics.Restore(saved);
                            }
                            return 1;
                        }
                        return 0;
                    }
                }

                if (!obj.HasExtents)
                {
                    return 0;
                }

                PointF min = Transform(obj.Min[0], obj.Min[1], scale, offsetX, offsetY);
                PointF max = Transform(obj.Max[0], obj.Max[1], scale, offsetX, offsetY);
                float left = Math.Min(min.X, max.X);
                float top = Math.Min(min.Y, max.Y);
                float rectWidth = Math.Abs(max.X - min.X);
                float rectHeight = Math.Abs(max.Y - min.Y);

                // 完全在画布外则跳过
                if (left > width || top > height || left + rectWidth < 0 || top + rectHeight < 0)
                {
                    return 0;
                }

                if (string.Equals(obj.DxfName, "CIRCLE", StringComparison.OrdinalIgnoreCase))
                {
                    if (rectWidth >= 1f && rectHeight >= 1f)
                    {
                        graphics.DrawEllipse(pen, left, top, rectWidth, rectHeight);
                        return 1;
                    }
                }

                // 其余类型用包围盒矩形示意（结构示意图的合理简化）
                if (rectWidth >= 1.5f && rectHeight >= 1.5f)
                {
                    graphics.DrawRectangle(pen, left, top, rectWidth, rectHeight);
                    return 1;
                }

                // 极小的对象画一个小十字，避免完全看不见
                float cx = left + rectWidth / 2f;
                float cy = top + rectHeight / 2f;
                graphics.DrawLine(pen, cx - 2f, cy, cx + 2f, cy);
                graphics.DrawLine(pen, cx, cy - 2f, cx, cy + 2f);
                return 1;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static List<object> GetPoint(ObjectRef obj, string key)
        {
            object raw;
            if (!obj.Geometry.TryGetValue(key, out raw))
            {
                return null;
            }
            return JsonCodec.AsList(raw);
        }

        private static double ToDouble(object value)
        {
            if (value is double)
            {
                return (double)value;
            }
            if (value is int)
            {
                return (int)value;
            }
            if (value is long)
            {
                return (long)value;
            }
            double parsed;
            if (value != null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return 0D;
        }

        private static PointF Transform(double x, double y, double scale, double offsetX, double offsetY)
        {
            return new PointF((float)(x * scale + offsetX), (float)(-(y * scale) + offsetY));
        }

        private static bool InBounds(PointF point, int width, int height)
        {
            return point.X >= -width && point.X <= width * 2 && point.Y >= -height && point.Y <= height * 2;
        }

        private static Pen PenFor(Dictionary<string, Pen> pens, string dxfName)
        {
            Pen pen;
            string key = string.IsNullOrEmpty(dxfName) ? "UNKNOWN" : dxfName;
            if (pens.TryGetValue(key, out pen))
            {
                return pen;
            }
            int index = 0;
            for (int i = 0; i < key.Length; i++)
            {
                index = (index * 31 + key[i]) & 0x7FFFFFFF;
            }
            Color color = ParseColor(Palette[index % Palette.Length]);
            pen = new Pen(color, 1f);
            pens[key] = pen;
            return pen;
        }

        private static List<LegendEntry> BuildLegend(Dictionary<string, int> typeCounts)
        {
            List<KeyValuePair<string, int>> items = new List<KeyValuePair<string, int>>(typeCounts);
            items.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            List<LegendEntry> legend = new List<LegendEntry>();
            for (int i = 0; i < items.Count && i < 8; i++)
            {
                LegendEntry entry = new LegendEntry();
                entry.Label = items[i].Key;
                entry.Count = items[i].Value;
                string key = items[i].Key;
                int index = 0;
                for (int j = 0; j < key.Length; j++)
                {
                    index = (index * 31 + key[j]) & 0x7FFFFFFF;
                }
                entry.Color = Palette[index % Palette.Length];
                legend.Add(entry);
            }
            return legend;
        }

        private static string BuildScaleBar(double drawingWidth, SemanticModel model)
        {
            double raw = drawingWidth / 5D;
            double nice = NiceNumber(raw);
            string unit = model == null ? "unitless" : model.UnitCode;
            string value = nice >= 1D
                ? Math.Round(nice, 0).ToString("0", CultureInfo.InvariantCulture)
                : Math.Round(nice, 3).ToString("0.###", CultureInfo.InvariantCulture);
            return "比例尺 ≈ " + value + " " + unit;
        }

        private static double NiceNumber(double value)
        {
            if (value <= 0D || double.IsNaN(value) || double.IsInfinity(value))
            {
                return 1D;
            }
            double exponent = Math.Floor(Math.Log10(value));
            double fraction = value / Math.Pow(10D, exponent);
            double niceFraction;
            if (fraction < 1.5D)
            {
                niceFraction = 1D;
            }
            else if (fraction < 3D)
            {
                niceFraction = 2D;
            }
            else if (fraction < 7D)
            {
                niceFraction = 5D;
            }
            else
            {
                niceFraction = 10D;
            }
            return niceFraction * Math.Pow(10D, exponent);
        }

        /// <summary>选中文字体：微软雅黑 → 黑体 → 宋体；都不可用则英文并回退告警。</summary>
        private static string PickFont(PreviewResult result, List<WireWarning> warnings)
        {
            string[] candidates = { "Microsoft YaHei", "SimHei", "SimSun" };
            try
            {
                using (InstalledFontCollection collection = new InstalledFontCollection())
                {
                    HashSet<string> installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    FontFamily[] families = collection.Families;
                    for (int i = 0; i < families.Length; i++)
                    {
                        installed.Add(families[i].Name);
                    }
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        if (installed.Contains(candidates[i]))
                        {
                            return candidates[i];
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 字体枚举失败按回退处理
            }

            result.FontFallback = true;
            if (warnings != null)
            {
                warnings.Add(WireWarning.Of(ErrorCodes.W1006, "font_fallback:no_cjk_font"));
            }
            return "Arial";
        }

        private static void SavePng(Bitmap bitmap, string path)
        {
            bitmap.Save(path, ImageFormat.Png);
        }

        private static Color ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#')
            {
                return Color.Black;
            }
            try
            {
                int r = int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                int g = int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                int b = int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return Color.FromArgb(255, r, g, b);
            }
            catch (Exception)
            {
                return Color.Black;
            }
        }

        /// <summary>范围哈希（文件名用）。</summary>
        public static string ExtentHash(double x0, double y0, double x1, double y1)
        {
            string text = x0.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                          y0.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                          x1.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                          y1.ToString("0.###", CultureInfo.InvariantCulture);
            return ParamsDigest.Sha256Hex(text).Substring(0, 8);
        }
    }
}
