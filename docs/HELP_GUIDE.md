# Engineering Platform v1.0.0 使用与维护指南

> 本文档面向 **工程设计人员（使用）** 与 **开发/运维人员（维护与二次扩展）**，覆盖安装配置、MCP 接入、典型工程闭环、全量可配置参数、故障排查与源码维护规范。

---

## 一、快速安装与部署

### 1.1 一键图形安装（推荐）
1. **先保存图纸并关闭正在运行的 AutoCAD / Civil 3D**（防止旧版 DLL 占用）。
2. 双击运行 **`EngineeringPlatform-Setup.exe`**。
3. 勾选目标 CAD（默认自动识别本机所有 Autodesk AutoCAD / Civil 3D / 中望 / 浩辰版本）：
   - **AutoCAD 2017–2024 / Civil 3D 2019–2024** → 自动分配 `.NET 4.8` 满血插件；
   - **AutoCAD / Civil 3D 2025+** → 自动分配 `.NET 8 + Roslyn` 满血插件；
   - **AutoCAD 2010–2016 / 中望 / 浩辰** → 自动分配免插件通用 COM 驱动。
4. 点击 **「一键应用配置」**，全部组件自动部署到 `%LOCALAPPDATA%\EngineeringPlatform\`：
   ```text
   %LOCALAPPDATA%\EngineeringPlatform\
   ├── bin\ep-mcp.exe                 # 统一多驱动 MCP 网关（v1.0.0 已签名）
   ├── host\net48\EngineeringHost.dll # AutoCAD 2017–2024 / Civil 3D 2019–2024 插件
   ├── host\net8\EngineeringHost.dll  # AutoCAD / Civil 3D 2025+ 插件（含 Roslyn）
   ├── route_solver\                  # JTG D20-2017 自动选线与经济造价引擎
   ├── installer\                     # 签名证书与无头自检脚本
   ├── config.json                    # 写通道与审批策略配置
   ├── cad-targets.json               # 已配置 CAD 目标清单
   ├── mcp-client-config.json         # MCP 客户端即拷即用配置（HTTP + stdio）
   ├── mcp-token.txt                  # 自动生成的 256-bit Bearer Token
   └── 使用与维护指南.md               # 本文档离线副本
   ```

### 1.2 静默 CLI 安装（批量分发）
```powershell
# 查看本机全部 CAD 及运行态 PID（支持跨提权识别管理员 CAD）
.\EngineeringPlatform-Setup.exe --list --json

# 仅配置全部满血插件版 CAD（2017–2026）
.\EngineeringPlatform-Setup.exe --plugin-only

# 配置全部 CAD 并启用开机自启 HTTP 网关 + 导入用户根证书
.\EngineeringPlatform-Setup.exe --all --autostart --trust-root

# 演练预览（不修改任何文件或注册表）
.\EngineeringPlatform-Setup.exe --plugin-only --dry-run

# 一键干净卸载全部 CAD 加载配置
.\EngineeringPlatform-Setup.exe --uninstall
```

---

## 二、MCP 客户端接入指南

点击安装程序底部 **「复制 MCP 配置」** 或直接读取 `%LOCALAPPDATA%\EngineeringPlatform\mcp-client-config.json`：

- **stdio 模式（推荐给本地 AI 客户端）**：
  ```json
  {
    "command": "%LOCALAPPDATA%\\EngineeringPlatform\\bin\\ep-mcp.exe",
    "args": ["--stdio", "--backend", "auto"]
  }
  ```
- **Streamable HTTP 模式（多客户端共享 / 后台常驻）**：
  - 启动网关：`ep-mcp.exe --port 18080 --backend auto`
  - 接入地址：`http://127.0.0.1:18080/mcp`，Header 携带 `Authorization: Bearer <mcp-token.txt 内容>`。
- **多开 CAD 精准路由**：先调 `sys_list_instances` 获取所有运行中 CAD 的 `pid` / `hostInstanceId`，之后任意工具传 `targetPid` 即可精准指向指定窗口。

---

## 三、典型工程全自动工作流

1. **南方 CASS 10/11（土方 / 等高线 / 断面 / GIS）**
   - 调 `ui_cass_commands` 查命令名与避坑提示；
   - 交互命令配合 `ui_read_cmdline` + `write_command(allowDuringCommand=true)`；
   - 遇到 MFC 弹窗使用 `ui_wait_dialog` / `ui_list_dialogs` + `ui_interact_dialog`（选文件必须用 `browse_file` 点击 `[...]` 按钮走二级文件框）。
2. **飞时达 FastTFT V15.x（方格网 / 三角网 / 块面 / 断面）**
   - 标准链：`GPFILETOLSD` → `TFCREATEREGION`（`OSMODE=0` + `D` 逐点描绘闭合）→ `TFAUTOFG` → `TFGATHERNATURE` → `TFDESIGNIMPORT` → `TFFGCALVOL` → `TFFGCOLLECT / TFFGSTATTABLE`。
3. **Autodesk Civil 3D 2019–2026（设计院级选线 + LOD 300 BIM）**
   - 地形曲面：`export_surface_grid` 无损导出大地坐标 `.npz`（导入海量点后须裁剪凹包长边 `MaximumTriangleLength`）。
   - 自动选线与经济比选：运行 `%LOCALAPPDATA%\EngineeringPlatform\route_solver`（需安装 `pip install -r route_solver\requirements.txt`），输出 3 方案 JTG D20-2017 平纵几何、缓和坡段、LP 土石方调配与概算比选表。
   - 原生建模与 BIM：通过 `write_alignment_plan` / `write_eval_csharp` 生成路线、抛物线纵断面、限宽放坡装配（`DaylightMaxWidth`）、三维道路、两事务体积曲面、分层 PBR 材质三维桥梁/挡墙/护栏/路面标线与纬地 HintCAD `.xls` 报表。

---

## 四、零硬编码配置与环境变量总表

本平台全部运行时路径、端口、超时、单价与证书均支持外部覆盖，**无需改源码**：

| 变量 / 配置项 | 默认值 | 作用说明 |
| :--- | :--- | :--- |
| `EP_MCP_PORT` / `--port` | `18080` | HTTP 网关监听端口 |
| `EP_MCP_TOKEN` / `--token` | 自动生成 256-bit 随机令牌 | Bearer 鉴权令牌 |
| `EP_BACKEND` / `--backend` | `auto` | 驱动模式：`auto` / `plugin` / `com` / `offline` |
| `EP_TOOL_TIMEOUT_MS` | `60000` (60s) | 普通插件工具超时时间（范围 `5000–600000` ms） |
| `EP_HEAVY_TIMEOUT_MS` | `180000` (180s) | 重计算工具（`write_eval_csharp` / `export_surface_grid` 等）超时时间 |
| `EP_INSTANCE_DIR` / `--instance-dir` | `%LOCALAPPDATA%\EngineeringPlatform\instances` | 插件实例发现目录 |
| `EP_PROJECT_DB` / `--db` | `%LOCALAPPDATA%\EngineeringPlatform\projects\default.db` | SQLite 结果与审计数据库 |
| `EP_CASS_DIR` | 自动遍历所有本地磁盘 `CASS` / `Program Files\South` | 自定义南方 CASS 安装目录（`.cuix` 检索） |
| `HINTCAD_TEMPLATE_DIR` | 自动遍历所有本地磁盘 `HintCAD\模板\表格` | 自定义纬地 HintCAD 表格模板目录 |
| `route_solver --config <json>` | 内置 JTG D20 与公路概算默认单价 | 自定义选线权重、设计车速、限宽与 `unit_prices` 概算单价库 |
| `EP_CODESIGN_SUBJECT` / `-CertSubject` | `CN=EngineeringPlatform Dev` | `build_setup.ps1` 正式发版时切换 CA 代码签名证书主题 |
| `EP_CODESIGN_TIMESTAMP` / `-TimestampUrl` | 空 | 正式发版时指定 RFC3161 时间戳服务器 |
| `EP_ACAD2024_ROOT` / `EP_ACAD2025_ROOT` | 自动探测 `C:\Program Files\Autodesk\AutoCAD 2024/2025` | 源码编译时指定本机或 CI 环境的 AutoCAD/Civil 3D SDK 目录 |

---

## 五、常见故障排查（Troubleshooting）

| 现象 / 错误码 | 根因 | 解决方法 |
| :--- | :--- | :--- |
| **`E1003 cmdactive=0x1`** | CAD 命令行正停在交互命令中 | 调用 `ui_read_cmdline` 查看提示；或发 `write_command(cancelCommand=true)` 退出 |
| **`E1004 idle_not_pumping`** | 弹出模态对话框，或百万级 TIN 切 `Realistic` 渲染占用主线程 | 先调 `ui_list_dialogs` 处理弹窗；百万级 TIN 切 3D 时优先用 `Shaded` 或暂时隐藏原始 TIN 层 |
| **`C1002` COM 无法挂载** | CAD 以管理员权限启动而 `ep-mcp.exe` 为普通权限 | 保持两者权限一致（均以普通用户启动，或均以管理员启动） |
| **安装器提示 DLL 被占用** | 目标 AutoCAD / Civil 3D 正在运行并锁定旧版 `EngineeringHost.dll` | 保存图纸并关闭该 CAD 后重新点击「一键应用配置」 |
| **AutoCAD 启动弹安全加载警告** | 新电脑尚未信任代码签名证书或 `TRUSTEDPATHS` | 运行 `EngineeringPlatform-Setup.exe --plugin-only --trust-root`（或正式版换 CA 签名证书） |
| **表格中文显示 `???`** | AutoCAD 默认 `Standard` 字体无中文字库 | 建 TrueType `Microsoft YaHei` 文字样式后再写表 |

---

## 六、开发与可维护性架构说明

### 6.1 模块分层与硬约束
```text
engineering-platform/
├── protocol/tools.schema.json   # 唯一工具契约真源（Host/Gateway 均内嵌编译）
├── host/                        # CAD 进程内插件（C# 7.3，双目标 net48 + net8.0-windows）
│   ├── Pipe/                    # 严禁引用 Autodesk.*（静态审查硬红线）
│   ├── Protocol/                # 分帧、握手、参数校验、ToolRouter
│   ├── Semantic/                # 语义识别 + Civil3dReader（全反射兼容 2019–2026）
│   ├── Tools/                   # Read / Write / Export / Preview / Batch 处理器
│   ├── Roslyn/                  # .NET 8 内存 C# 编译与 Shadow Dry-Run
│   └── Win32/                   # 进程内后台线程 MFC 模态弹窗桥
├── gateway/                     # 单体 C# MCP 网关（.NET 8，零 Python 依赖）
│   ├── BackendRouter.cs         # 三驱动路由与 PID 去重
│   ├── PipeHostClient.cs        # Driver 1: NamedPipe
│   ├── ComCadDriver.cs          # Driver 2: STA 后期绑定 COM
│   ├── OfflineDwgDriver.cs      # Driver 3: ACadSharp 离线（含代理防覆写门禁）
│   ├── Win32DialogDriver.cs     # 进程外 MFC 弹窗驱动
│   └── CassCommandCatalog.cs    # CASS + FastTFT + Civil 3D 12 模块命令知识库
├── route_solver/                # Python JTG D20-2017 自动选线 + 经济造价 + LP 调配
├── setup/                       # 一体化安装程序（WinForms + CLI + payload.zip 融合打包）
└── tests/                       # 1,556 项协议/契约/静态审查 + E2E + 多驱动 + 独立 QA
```

### 6.2 二次开发红线与验证命令
1. **新增/修改 MCP 工具**：必须同步对齐 `protocol/tools.schema.json`、`host/Protocol/ToolCatalog.cs`、`host/Tools/*ToolHandlers.cs`、`mockhost/mock_host.py`，修改后必须通过 `python tests/run_all.py`（含 `parity 178/178` 与 `static_audit 405/405`）。
2. **`host/` 编码红线**：保持 C# 7.3 兼容（确保 `.NET 4.8` 与 `.NET 8` 双目标编译），禁止 `DateTime.Now`（统一用 `DateTime.UtcNow`）、禁止在 `host/Pipe/**` 引用 `Autodesk.*`、禁止复制任何 Autodesk DLL 到产物目录。
3. **一键构建与全量回归**：
   ```powershell
   # 使用正式 CA 证书构建（或省略参数走默认开发证书）
   powershell -ExecutionPolicy Bypass -File setup\build_setup.ps1 -CertSubject "CN=YourCompany" -TimestampUrl "http://timestamp.digicert.com"

   # 全量回归门禁
   python tests\run_all.py
   python tests\test_v3_e2e.py
   python tests\test_v31_multidriver.py
   python -m unittest discover -s route_solver\tests -v
   ```
