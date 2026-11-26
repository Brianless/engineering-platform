using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace EngineeringPlatform.Gateway;

/// <summary>
/// 南方 CASS 10/11 全功能命令目录与自动化交互知识库（v3.1.2）：
/// - 内置工程高频命令（土方、高程/展点、等高线/DTM、断面/里程、地籍、检查入库、GIS 转换）及避坑要点；
/// - 支持按中文关键词检索命令，并可动态补充检索本机安装的 CASS .cuix 菜单定义。
/// </summary>
internal static class CassCommandCatalog
{
    internal sealed record Entry(string Domain, string Name, string Command, string InteractionPattern, string Notes);

    private static readonly Entry[] BuiltIn =
    [
        // === 1. 土方计算（工程应用） ===
        new("土方计算", "方格网法土方计算", "fgwtf", "pick_boundary_then_mfc_dialog_then_angle",
            "先 OSMODE=0 + 缩放聚焦红线边中点按 X,Y 拾取 + sendEnter；弹窗内选数据文件须用 browse_file 点 [...] 走文件框；确认后如停在 CMDACTIVE=1 再发 sendEnter 采用 0° 水平网格"),
        new("土方计算", "方格网根据注记重新计算", "refgwtf", "pick_boundary_or_mfc", "修改图面角点高程注记后重算方格网土方"),
        new("土方计算", "三角网法土方计算(坐标文件)", "DTMTF", "mfc_dialog_with_dat", "先选计算方式 1(坐标文件) 或 2(图面高程点)，再选闭合边界线与平场标高"),
        new("土方计算", "三角网法土方计算(图上三角网)", "tstf", "pick_boundary_then_mfc", "基于图上已有 SJW 三角网计算挖填方"),
        new("土方计算", "两期间土方计算(DTM)", "twosjw", "mfc_dialog_with_two_sjw_or_dat", "对比两期原始/复测三角网或坐标文件计算挖填方"),
        new("土方计算", "等高线法土方计算", "dgxtf", "pick_contours_then_mfc", "基于封闭等高线计算场地土方"),
        new("土方计算", "区域土方量平衡", "tfbalance", "pick_boundary_then_mfc", "自动反算挖填平衡设计标高（1=坐标文件，2=图上高程点）"),
        new("土方计算", "库容计算", "kurong", "mfc_dialog", "按高程分级计算水库/坑塘库容"),
        new("土方计算", "断面法土方计算-道路/场地/任意", "transect", "mfc_dialog_and_pick", "1=道路断面 2=场地断面 3=任意断面"),
        new("土方计算", "断面法图面土方计算", "mapretf", "pick_sections", "根据已绘断面图计算总挖填方量"),
        new("土方计算", "断面法图面土方计算(输出Excel)", "mapretf_excel", "pick_sections_save_xls", "计算并导出 Excel 断面土方表"),
        new("土方计算", "二断面线间土方计算", "betweendmx", "pick_two_sections", "计算两相邻断面线间的挖填方"),
        new("土方计算", "计算表面积", "surfacearea", "pick_boundary", "1=坐标文件 2=图上高程点 3=三角网，计算三维表面积"),

        // === 2. 高程点与展点（绘图处理 / 工程应用） ===
        new("高程与展点", "展高程点", "zhkzd", "prompt_1_then_file_dialog_then_dist", "先输 1 → 弹文件框选 .dat（格式：点号,,X东,Y北,H）→ 命令行提示注记间距时发 sendEnter"),
        new("高程与展点", "展野外测点点号/代码/点位", "zhdm", "prompt_mode_then_file_dialog", "2=点号 3=代码 4=点位"),
        new("高程与展点", "高程点生成数据文件(有编码)", "LINKSJX1", "file_save_dialog", "从带 SOUTH 202101 编码的 GCD 高程点导出 .dat"),
        new("高程与展点", "高程点生成数据文件(无编码)", "gcdtodat", "file_save_dialog", "从普通图面高程点导出 .dat"),
        new("高程与展点", "注记生成数据文件", "GCDTODAT2", "file_save_dialog", "直接从高程文字注记生成 .dat"),
        new("高程与展点", "等高线生成数据文件", "datincontour", "file_save_dialog", "沿等高线离散采样生成 .dat"),
        new("高程与展点", "高程点过滤", "gcdguolv", "mfc_dialog", "按最小间距抽稀重叠高程点"),
        new("高程与展点", "高程点消隐", "gcdxy", "cmdline_or_select", "隐藏与地物压盖的高程点"),
        new("高程与展点", "修改高程", "changegcd", "cmdline", "批量加减或修改高程点数值"),
        new("高程与展点", "根据注记修改高程", "heightfromtext", "cmdline", "将高程文字同步写入点位 Z 值"),

        // === 3. 等高线与三角网（DTM） ===
        new("等高线与DTM", "建立三角网(DTM)", "LINKSJX", "mfc_dialog", "选择由数据文件或图面高程点构建 SJW 三角网，可选考虑地性线/坎高"),
        new("等高线与DTM", "图面三角网完善", "APPENDSJX", "cmdline", "在已有三角网基础上局部补充/修正"),
        new("等高线与DTM", "三角网写入文件(.sjw)", "writesjw", "file_save_dialog", "保存当前三角网到 .sjw 文件"),
        new("等高线与DTM", "三角网读出文件(.sjw)", "readsjw", "file_open_dialog", "从 .sjw 文件加载三角网"),
        new("等高线与DTM", "删三角网", "delsjx", "cmdline", "清除图面 SJW 三角网"),
        new("等高线与DTM", "绘制等高线", "dzx", "mfc_dialog", "在三角网基础上输入等高距与拟合方式生成等高线"),
        new("等高线与DTM", "绘制等深线", "dsx", "mfc_dialog", "水上测量生成等深线"),
        new("等高线与DTM", "删全部等高线", "deldgx", "cmdline", "删除全部等高线"),
        new("等高线与DTM", "查询指定点高程", "height", "pick_point", "在三角网上内插任意点高程"),

        // === 4. 断面与里程 ===
        new("断面与里程", "由纵断面线新建横断面线", "hdmcreate", "pick_alignment_then_mfc", "沿纵断面线按桩距与左右宽生成横断面线"),
        new("断面与里程", "由纵断面线生成里程文件(.hdm)", "fromzdline", "mfc_dialog_with_save", "根据纵横断面线与高程生成 .hdm 里程文件"),
        new("断面与里程", "由等高线/三角网生成里程文件", "dmfromdgx", "prompt_mode_then_mfc", "1=等高线 2=三角网"),
        new("断面与里程", "由坐标文件生成里程文件", "getlicheng", "mfc_dialog", "由 .dat 坐标文件生成断面里程"),
        new("断面与里程", "根据里程文件绘断面图", "dmt_licheng", "mfc_dialog_with_browse", "用 browse_file 选 .hdm 文件并设横纵比例尺绘断面图"),
        new("断面与里程", "根据已知坐标绘断面图", "dmt_dat", "mfc_dialog_with_browse", "直接由坐标文件绘制纵横断面图"),
        new("断面与里程", "道路设计参数文件", "roadpara", "multi_tab_mfc_dialog", "多选项卡设置道路断面设计参数"),

        // === 5. 数据编码与图幅 ===
        new("数据与图幅", "查看实体编码", "GETP", "pick_entity", "读取实体 SOUTH XData 编码"),
        new("数据与图幅", "加入实体编码", "PUTP", "pick_entity_then_mfc", "为实体写入 SOUTH 编码"),
        new("数据与图幅", "改变当前图形比例尺", "gbblc1", "cmdline", "修改 CASS 1:500/1:1000/1:2000 比例尺"),
        new("数据与图幅", "图幅网格(指定长宽)", "tfwg", "mfc_dialog", "绘制标准或自定义图幅方格网"),
        new("数据与图幅", "加方格网(公里网)", "hfgw", "mfc_dialog", "绘制坐标公里网"),
        new("数据与图幅", "标准图幅(50x50/50x40/任意)", "tfzs", "prompt_mode_then_mfc", "1=50x40 2=50x50 3=任意 4=倾斜"),

        // === 6. 地籍与面积 ===
        new("地籍与面积", "查询实体面积", "areauser", "pick_entity", "查询闭合地物面积（平方米/亩）"),
        new("地籍与面积", "计算指定范围的面积", "jsmj", "pick_boundary", "计算封闭区域面积并注记"),
        new("地籍与面积", "复合线转为权属线", "PLTOJZLINE", "pick_entity", "将普通多段线转为 CASS 宗地权属线"),
        new("地籍与面积", "界址点生成数据文件", "jzptofile", "file_save_dialog", "导出宗地界址点坐标文件"),
        new("地籍与面积", "依权属文件绘权属图", "hqst", "file_open_dialog", "由权属文件自动绘制宗地图"),

        // === 7. 质检入库与 GIS 格式互转 ===
        new("质检与GIS", "图形实体检查", "checkdwg", "multi_tab_mfc_dialog", "全面检查编码、拓扑、悬挂点、自相交等（支持 select_tab 切换选项卡）"),
        new("质检与GIS", "删除重复实体", "check_repeat", "mfc_dialog", "自动检测并清理重叠实体"),
        new("质检与GIS", "删除复合线多余点", "REMOVESAMEPOINTS", "cmdline", "清理重合/冗余多段线节点"),
        new("质检与GIS", "检查伪节点", "CheckFalseNode", "cmdline", "查找线要素伪节点"),
        new("质检与GIS", "输出 ARC/INFO SHP 格式", "casstoshp", "mfc_dialog_with_browse", "将 CASS 图导出为 Shapefile"),
        new("质检与GIS", "输入 ARC/INFO SHP 格式", "shptocass2", "mfc_dialog_with_browse", "导入 Shapefile 到 CASS"),
        new("质检与GIS", "导出 Google 地球 KML 格式", "explorttokml", "mfc_dialog_with_browse", "将图纸导出为 KML"),
        new("质检与GIS", "输出国家空间矢量 VCT 格式", "vctout", "mfc_dialog_with_browse", "导出 VCT 标准交换格式"),

        // === 8. 飞时达 FastTFT V15.x（地形离散点 / 方格网 / 三角网 / 块面 / 断面 / 边坡） ===
        new("飞时达FastTFT", "离散点文件导入(文件转离散点)", "GPFILETOLSD", "mfc_dialog_with_browse_then_msgbox",
            "弹窗「导入标高数据」：点 [218] [...] 走 browse_file 选 .dat/.txt，选模板 [230]=[I],,[X],[Y],[Z] 与标注 [227]=Z值 后点 [1] 确定；生成完成后会弹二级 AutoCAD 提示框（已成功生成N个离散点），须点其 [2] 确定关闭"),
        new("飞时达FastTFT", "属性图元转地形离散点", "GPDSGNLANDPT", "cmdline_pick_sample", "将图面高程点/文字批量转为飞时达 DX-地形离散点"),
        new("飞时达FastTFT", "绘制计算区域(含单一区块)", "TFCREATEREGION", "cmdline_trace_vertices_or_pick",
            "先输区域编号(如 1)；注意 C(选封闭线) 走 acedEntSel 拒绝 (handent) 且受视图/捕捉影响，全自动 100% 稳定做法是先 OSMODE=0，选 D(跟踪描绘) 后逐点单次发送各顶点 X,Y（严禁换行拼接）→ C(闭合) → N(单区块自动同时建 1-1 区块)"),
        new("飞时达FastTFT", "划分区块(多区块划分)", "TFCREATEBLOCK", "cmdline", "在同一区域内进一步划分多个独立土方计算区块"),
        new("飞时达FastTFT", "自动布置方格网", "TFAUTOFG", "cmdline_A_then_mfc_dialog_then_cancel",
            "输 A(所有区域) 弹「自动布置方格网」：[201]横向间距、[202]纵向间距、[206]/[207]对准点X/Y（大坐标务必设在场地附近如左下角整5m处）、[208]横向角度(0=水平)、[220]字高 → 点 [1] 确定；生成完毕会循环回区块提示，须发 cancelCommand=true 退出"),
        new("飞时达FastTFT", "采集自然标高(方格网)", "TFGATHERNATURE", "cmdline_A_then_A_then_cancel",
            "先输 A(全图地形范围) → 再输 A(所有区块) 自动插值采集自然标高；完成后循环回提示，发 cancelCommand=true 退出"),
        new("飞时达FastTFT", "输入设计标高(方格网)", "TFDESIGNIMPORT", "cmdline_pick_interior_then_mfc_then_cancel",
            "注意本命令无 A 选项且不可与槽底标高 TFSRCDBG 混淆！须发送区块内部点坐标 X,Y 弹出「设计标高输入」：[224]等高度面 + [241]统一高度 + [1]确定；完成后发 cancelCommand=true 退出"),
        new("飞时达FastTFT", "优化设计标高(土方平衡)", "TFDESIGNOPTIMIZE", "cmdline_pick_interior_then_mfc", "自动计算挖填平衡设计标高与坡面优化"),
        new("飞时达FastTFT", "输入槽底标高", "TFSRCDBG", "cmdline_pick_interior", "输入基坑/槽底相对设计标高的降深值（非平场设计标高）"),
        new("飞时达FastTFT", "计算方格土方量", "TFFGCALVOL", "mfc_dialog_then_pick_interior_then_msgbox",
            "先弹「土石方计算」设 [216]考虑松散系数 等并点 [1]确定 → 再发送区块内部点 X,Y（无 A 选项）→ 计算完成后弹结果框「土石方计算」（含填/挖/净方量），读取 Static 文本并点 [2]确定关闭，再发 cancelCommand=true"),
        new("飞时达FastTFT", "土方行列汇总", "TFFGCOLLECT", "cmdline_A_pick_block_col_row",
            "输 A(自动汇总) → 发区块内部点 X,Y → 发列汇总位置 X,Y → 发行汇总位置 X,Y，自动在图面绘制行列汇总表"),
        new("飞时达FastTFT", "土方量统计表(绘表/导出)", "TFFGSTATTABLE", "mfc_dialog_stat_draw_exit",
            "弹窗「土石方量统计表」：先点 [470]统计 填充数据 → 点 [276]绘表 并给插入点坐标（或 [275]导出）→ 点 [2]退出"),
        new("飞时达FastTFT", "方格网边坡绘制与计算", "TFDRAWSLOPEFG", "cmdline_then_mfc", "沿场区边界放坡；配对 TFBPCALVOL 计算边坡土方量"),
        new("飞时达FastTFT", "自动布置三角网(三角网法)", "TFSJW", "cmdline_and_mfc", "飞时达三角网法土方链：TFSJW → TFSJWCJZRBG(采自然) → TFSJWSRSJBG(输设计) → TFSJWJSTFL(算土方) → TFSJWHZB(汇总表)"),
        new("飞时达FastTFT", "块面/田块法计算土方量", "TFCALCULATEKM", "cmdline_and_mfc", "飞时达田块法：TFQKCREATEREGION → TFCREATEBLOCKTK → TFINPUTKM → TFCALCULATEKM → TFSTATKM"),
        new("飞时达FastTFT", "断面法布置断面线与计算", "TFHZDMX", "cmdline_and_mfc", "飞时达断面法：TFHZDMX(布断面线) → TFDMCJZG(采自然) → TFDMSRSG(输设计) → TFHZDM(断面法计算中心)"),
        new("飞时达FastTFT", "生成自然三维地形模型(DTM)", "GPDRAWYSDTM", "mfc_dialog", "生成飞时达原始自然地表三维三角面模型"),

        // === 9. Autodesk Civil 3D 2019–2026 全功能模块与 .NET API（12 大核心域 + BIM 交付） ===
        new("Civil3D测量与点", "COGO点与点编组管理(CogoPoints/PointGroups)", "CREATEPOINTS / IMPORTPOINTS", "dotnet_api_or_dialog",
            "CivilDocument.CogoPoints / PointGroups / PointFileFormat；海量地形点(>10万)推荐直接通过 TinSurface.AddVertices(Point3dCollection) 构建曲面，比逐个建 CogoPoint 快 50 倍"),
        new("Civil3D地形曲面", "TIN地形曲面创建与长边裁剪(TinSurface)", "CREATESURFACE / SURFACESTYLE", "dotnet_api",
            "TinSurface.Create(db, name) + AddVertices + BuildOptions.UseMaximumTriangleLength=true / MaximumTriangleLength(裁剪凹包假长边) + AddStandardBreaklines + Analysis.SetElevationData(高程带着色)"),
        new("Civil3D土方体积", "TIN体积曲面与土方平衡(TinVolumeSurface)", "VOLUMESDASHBOARD / CREATESURFACE", "dotnet_api_two_tx",
            "TinVolumeSurface.Create(name, baseId, compId) → GetVolumeProperties() 读 UnadjustedCutVolume / UnadjustedFillVolume / UnadjustedNetVolume；**必须分两事务**：Tx1 建 CorridorSurface 并 Rebuild，Tx2 裁剪 Datum 长边后再建体积曲面"),
        new("Civil3D平面路线", "平面路线与平曲线/缓和曲线/超高(Alignment)", "CREATEALIGNMENTLAYOUT / SUPERELEVATION", "dotnet_api",
            "Alignment.Create(cdoc, plOpts, name, siteId, layerId, styleId, labelSetId) 或 write_alignment_plan；支持 AddFixedLine / AddFreeCurveFillet / AddFreeSpiralCurveSpiral / DesignSpeeds / CalculateSuperelevation；注意 Alignment.Create 的第2参是 PolylineOptions、第3参才是 name"),
        new("Civil3D纵断面", "地面纵断面与抛物线设计纵断面(Profile)", "CREATEPROFILEFROMSURFACE / CREATEPROFILELAYOUT", "dotnet_api",
            "Profile.CreateFromSurface 采样原地面；Profile.CreateByLayout 建设计拉坡线，**必须调用 Entities.AddFreeSymmetricParabolaByPVIAndCurveLength** 生成平滑竖曲线（严禁尖角折线）；ProfileView.Create 建纵断面图并配干净网格样式与 _No Labels 防文字墙"),
        new("Civil3D装配部件", "标准横断面装配与限宽放坡(Assembly/Subassembly)", "CREATEASSEMBLY / TOOLPALETTES", "dotnet_api",
            "cdoc.AssemblyCollection.Add(name, AssemblyType.UndividedCrownedRoad, pt)；山区放坡严禁默认 1:4 的 BasicSideSlopeCutDitch，必须用 Subassembly.DaylightMaxWidth（设 CutSlope/FillSlope/MaxWidth，注意参数属性名为 p.Key 而非 p.KeyName）"),
        new("Civil3D道路模型", "三维道路走廊/区域/目标映射/曲面(Corridor)", "CREATECORRIDOR / CORRIDORPROPERTIES", "dotnet_api",
            "cdoc.CorridorCollection.Add(name, baselineName, alignId, profId) → AddRegion → SetTargets(TargetType.Surface=原始地貌) → CorridorSurfaces.Add(Datum, AddLinkCode/Top/Datum) → AddBoundary → Rebuild()"),
        new("Civil3D断面与工程量", "采样线/横断面图/材质土石方(SampleLineGroup/SectionView)", "CREATESAMPLELINES / COMPUTEMATERIALS", "dotnet_api",
            "SampleLineGroup.Create + SampleLine.CreateByStationRange 沿桩号生成横断面采样线 → SectionViewGroupCollection.Add 批量出横断面图 → QTO QuantityTakeoffCriteria 统计分段土石方与路基路面工程量"),
        new("Civil3D场地与放坡", "场地/要素线/放坡组/宗地(Site/FeatureLine/Grading/Parcel)", "CREATEFEATURELINE / CREATEGRADING", "dotnet_api",
            "Site.Create / FeatureLine.CreateFromPolyline / GradingGroup / Parcel；适用于场平、互通立交匝道三角区、弃渣场与挡墙台背放坡"),
        new("Civil3D管网水文", "重力管网/压力管网/汇水区(PipeNetwork/PressurePipe/Catchment)", "CREATEPIPENETWORK / CREATEPRESSUREPIPE", "dotnet_api",
            "Network.Create(cdoc, name) 建雨水/污水/涵洞管网（AddPipe/AddStructure）；AeccPressurePipesMgd 管理给水/燃气压力管网；AeccHydroCalcsMgd 管理汇水区与水力分析"),
        new("Civil3D桥梁交叉口", "桥梁与平面交叉口(Bridge/Intersection)", "CREATEINTERSECTION / CREATEBRIDGE", "dotnet_api",
            "CivilDocument.GetIntersectionIds() / GetBridgeIds()；交叉口自动生成路缘倒角与衔接曲面；山区高填方深谷(>18m)自动切换桥梁结构（桥面系+防撞护栏+桥墩+盖梁）"),
        new("Civil3D出图与协同", "图框组/自动分幅出图/数据快捷方式(ViewFrameGroup/DataShortcut)", "CREATEVIEWFRAMES / CREATESHEETS / MANAGEDATASHORTCUTS", "dotnet_api",
            "CivilDocument.GetViewFrameGroupIds() 实现平纵组合自动分幅出图；AeccDataShortcutMgd (DREFs) 实现路线/曲面/管网多专业动态协同引用；中文表格须建 TrueType Microsoft YaHei 文字样式防问号乱码"),
        new("Civil3D_BIM交付", "道路实体导出/PBR材质/PropertySet/IFC4.3与LandXML", "EXPORTCORRIDORTOSOLIDS / IFCINFRAEXPORT / LANDXMLOUT", "dotnet_api",
            "Corridor.ExportSolids(ExportCorridorSolidsParams, db) 导出分层真三维实体（沥青面层/水稳基层/路缘石/边沟/放坡）+ 配套三维重力挡墙/桥梁/波形护栏/路面标线 + AutoCAD Material 真实材质 + Shaded/Realistic 渲染 + PropertySet BIM属性 + IFC4.3/LandXML 交付；**性能避坑**：当原地面 TIN 超 50 万三角面时，切 3D 渲染前应优先用 Shaded 样式或仅开启 BIM+走廊+等高线显示，避免 100 万级 TIN 实时 Realistic 材质重算卡顿主线程")
    ];

    public static JsonObject Search(JsonObject args)
    {
        string query = (args["query"]?.GetValue<string>() ?? "").Trim();
        string domain = (args["domain"]?.GetValue<string>() ?? "").Trim();
        int limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 30, 1, 100);

        var matches = BuiltIn.Where(e =>
            (string.IsNullOrEmpty(domain) || e.Domain.Contains(domain, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(query) ||
             e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             e.Command.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             e.Domain.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             e.Notes.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .ToList();

        var arr = new JsonArray();
        foreach (var e in matches)
        {
            arr.Add(new JsonObject
            {
                ["domain"] = e.Domain,
                ["name"] = e.Name,
                ["command"] = e.Command,
                ["interactionPattern"] = e.InteractionPattern,
                ["notes"] = e.Notes
            });
        }

        // 若内置目录命中很少且传了 query，自动补扫本机 CASS .cuix 菜单文件
        var cuixHits = new JsonArray();
        if (!string.IsNullOrEmpty(query) && matches.Count < 5)
        {
            foreach (var (name, cmd) in SearchLocalCassCuix(query, 15))
            {
                cuixHits.Add(new JsonObject { ["name"] = name, ["command"] = cmd });
            }
        }

        return new JsonObject
        {
            ["summary"] = $"【南方 CASS + 飞时达 FastTFT + Civil 3D 全功能目录】匹配内置 {matches.Count} 条" + (cuixHits.Count > 0 ? $" + 本机 CUIX 补充 {cuixHits.Count} 条" : "") + $"（关键词='{query}'）。",
            ["commands"] = arr,
            ["cuixExtraMatches"] = cuixHits,
            ["datFormatRule"] = "南方 CASS / 飞时达通用 .dat 格式：点号,编码(可空),AutoCAD东坐标X,AutoCAD北坐标Y,高程H（如 1,,607560.080,3544468.790,69.340）",
            ["southXDataRule"] = "南方 CASS 高程点编码：RegApp='SOUTH'，1000='202101'，图层='GCD'",
            ["fastTftPipelineRule"] = "飞时达 FastTFT V15.x 零点击方格网标准链：GPFILETOLSD(导入离散点+关二级提示框) → TFCREATEREGION(OSMODE=0 + D逐点描绘闭合 + N单区块) → TFAUTOFG(A + 设对准点/间距/0° + cancel退出) → TFGATHERNATURE(A→A + cancel) → TFDESIGNIMPORT(点区块内部点 + 设统一高度 + cancel) → TFFGCALVOL(确认参数→点区块内部点→读并关闭结果框→cancel) → TFFGCOLLECT / TFFGSTATTABLE",
            ["civil3dBimPipelineRule"] = "Civil 3D 2019–2026 设计院级 + LOD 300 BIM 全自动链：TinSurface(裁剪凹包长边) → route_solver(JTG D20平纵优化+经济比选+调配) → Alignment + Profile(AddFreeSymmetricParabolaByPVIAndCurveLength) + ProfileView → Assembly(DaylightMaxWidth限宽) → Corridor(多区域+目标曲面+Datum曲面) → Tx2(Datum长边裁剪+TinVolumeSurface算土方) → Corridor.ExportSolids + 三维挡墙/桥梁/护栏/标线 + PBR Material + Realistic渲染 + PropertySet/IFC4.3 + 纬地HintCAD报表"
        };
    }

    private static List<(string Name, string Command)> SearchLocalCassCuix(string query, int maxResults)
    {
        var hits = new List<(string, string)>();
        try
        {
            var roots = new List<string>();
            string? envCass = Environment.GetEnvironmentVariable("EP_CASS_DIR");
            if (!string.IsNullOrWhiteSpace(envCass)) roots.Add(envCass.Trim());
            foreach (var drv in DriveInfo.GetDrives())
            {
                if (drv.DriveType != DriveType.Fixed || !drv.IsReady) continue;
                string r = drv.RootDirectory.FullName;
                roots.Add(Path.Combine(r, "CASS"));
                roots.Add(Path.Combine(r, "Program Files", "South"));
                roots.Add(Path.Combine(r, "Program Files (x86)", "South"));
            }
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) continue;
                foreach (string cuix in Directory.EnumerateFiles(root, "ACAD.cuix", SearchOption.AllDirectories))
                {
                    using var zf = ZipFile.OpenRead(cuix);
                    var entry = zf.GetEntry("MenuGroup.cui");
                    if (entry == null) continue;
                    using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
                    var doc = XDocument.Parse(sr.ReadToEnd());
                    foreach (var mm in doc.Descendants("MenuMacro"))
                    {
                        var mac = mm.Element("Macro");
                        string name = mac?.Element("Name")?.Value?.Trim() ?? "";
                        string cmd = (mac?.Element("Command")?.Value ?? "").Replace("^C^C", "").Replace("^P", "").Trim();
                        if (string.IsNullOrEmpty(cmd)) continue;
                        if (name.Contains(query, StringComparison.OrdinalIgnoreCase) || cmd.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            hits.Add((name, cmd));
                            if (hits.Count >= maxResults) return hits;
                        }
                    }
                }
            }
        }
        catch { }
        return hits;
    }
}
