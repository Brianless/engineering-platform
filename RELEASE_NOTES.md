# Engineering Platform v1.0.0 发布说明

**发布日期**：2026-11-26  
**版本号**：`1.0.0`（网关、双目标宿主插件、安装程序全组件统一）

---

## 一、核心交付能力

1. **统一多驱动 MCP 网关（`ep-mcp.exe` v1.0.0，42 个 MCP 工具）**
   - 纯 C# `.NET 8` 单文件自包含，同时支持 **Streamable HTTP** 与 **`--stdio`**，零 Python / Node.js 依赖。
   - **一网关三驱动自动路由**：满血插件（NamedPipe）/ 通用免插件 COM（AutoCAD 2010–2026、中望、浩辰）/ 离线原生 ACadSharp（DWG/DXF），支持多开 CAD `targetPid` / `hostInstanceId` 精准路由与 PID 去重。
   - `sys_capabilities.tools` 与 `tools/list` 42 工具强制 100% 同源对齐。
   - 内置 **南方 CASS 10/11 + 飞时达 FastTFT V15.x + Autodesk Civil 3D 2019–2026 全部 12 大模块与 BIM 规范目录**（`ui_cass_commands`），以及 Win32 + 进程内双引擎 MFC 弹窗驱动。

2. **原生批量建实体与实景回执（全三驱动打通）**
   - 新增 **`write_entities`** 工具：单事务支持创建 `1..5000` 个原生实体（`line` / `polyline` / `rectangle` / `circle` / `arc` / `ellipse` / `text` / `mtext` / `point`），自动建图层与设 ACI 颜色、自动归一化椭圆长短轴比、同步返回精确创建数/句柄/包围盒/失败明细。
   - 升级 **`write_command`** 延迟 `Application.Idle` 实景回执：LISP 自动 `vl-catch-all-apply` 捕获真实报错，非 LISP 自动捕获 `LASTPROMPT`，返回 `verified` / `beforeEntityCount` / `afterEntityCount` / `entityDelta` / `lispError`。
   - 全部在线写工具补齐 `DocumentLock` 显式文档锁（彻底杜绝 `eLockViolation`），并统一 `net48` `JavaScriptSerializer` 数组解析（兼容 `object[]` / `List<object>` / `IList`）。

3. **高精度语义预览与实时图纸范围**
   - `read_preview` 支持 `LINE`、真开口 `ARC`、精确 `CIRCLE`、任意旋转/局部 `ELLIPSE` 与真字体 `TEXT` / `MTEXT` 渲染，彻底告别外接矩形框退化。
   - `SemanticExtractor` 在未截断时实时累加实体并集包围盒，删改图元后预览范围即时收缩/扩张，无需额外 `ZOOM _E`。
   - `DrawingFingerprint` 纳入模型空间实体类型计数，图元类型变更即刷新指纹。

4. **全版本 CAD / Civil 3D 宿主插件与 Roslyn 引擎（`EngineeringHost.dll` v1.0.0）**
   - 覆盖 AutoCAD 2017–2026 与 Civil 3D 2019–2026（`net48` + `net8.0-windows`）。
   - 完整感知 Civil 3D 曲面、体积曲面、路线、纵断面、道路、装配、采样线编组、管网、点编组、场地等原生对象；`write_alignment_plan` 自动生成平滑抛物线竖曲线。
   - `write_eval_csharp` 支持 `.NET 8` Roslyn 与 `.NET 4.8` CodeDom 双引擎，自动去重程序集与补齐 `System.Runtime`/`netstandard` 门面引用。

5. **JTG D20-2017 自动选线与经济造价引擎（`route_solver/`）**
   - 支持 20–100 km/h 规范预设、16 邻域等高线贴坡 A*、HiGHS 全局凸 LP 拉坡、最大坡长/缓和坡段自动插入、连续 3 km 均坡 `<= 5.5%`、隧道 `<= 3.0%`、平包竖核验、概算单价库与土石方调配模型。

6. **一体化安装与热更新体验（`EngineeringPlatform-Setup.exe`）**
   - 单页现代化 WinForms 向导 + 静默 CLI，精准写入 `DemandLoad` 与 `Profiles\<name>\Variables\TRUSTEDPATHS`（零启动安全弹窗）。
   - 支持 Windows NTFS 占用热重命名替换，旧进程占用时仍可无缝落地新版二进制。

---

## 二、全量验证结果

- `tests/run_all.py`（11 套件）：**1,563 passed / 0 failed**（含 `static_audit 408/408`、`parity 178/178`、`gateway_n04 294/294`、`mcp_stdio 164/164`）
- `tests/test_v3_e2e.py`：**PASS**（42 工具 + `sys_capabilities` 精确对齐 + HTTP 安全 + NamedPipe/SQLite）
- `tests/test_v31_multidriver.py`：**PASS**（离线 ACadSharp 9 类实体生成 DXF/DWG + 回读校验 + COM 诊断 + Auto 路由）
- `tests/qa/qa_write_domain.py`：**36 passed / 0 failed**
- `route_solver/tests`：**3/3 PASS**
- 真机验证：AutoCAD 2020（R23.1）、南方 CASS 11、飞时达 FastTFT V15.2、Civil 3D 2025 闭环通过。
