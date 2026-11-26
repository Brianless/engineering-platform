# 第三方组件与许可声明（THIRD-PARTY-NOTICES）

本仓库包含第三方开源代码（vendored 源码）与编译期外部引用。本文登记**每一份的来源、许可证、版权行、在本仓库的位置**，
并写明我们**不包含/不分发**什么。

> 维护规则：新增任何第三方代码或依赖，必须先在本文件加条目，再改代码。
> 来源台账（commit / hash / 文件数）见 `third_party/UPSTREAM.md`；改动台账见 `third_party/PATCHES.md`。

---

## 1. Civil3D-mcp（vendored 源码）

| 项 | 内容 |
|---|---|
| 项目 | **Civil3D-mcp** — https://github.com/Sacred-G/Civil3D-mcp |
| 固定 commit | `9e437a3bab6df4ba13fabc2e909e138e8ac15896` |
| 许可证 | **MIT** |
| 版权行 | `Copyright (c) 2025 lisiting01` |
| 许可证原文 | `third_party/civil3d-mcp/LICENSE`（1067 B，sha256 `dcad40f8…6374`，**一字未改**） |
| 本仓库位置 | `third_party/civil3d-mcp/`（194 文件 / 2.8 MB / 53 个 `.cs`） |
| 本仓库用途 | **第二方法来源**：不整体加载其插件（与 new-acad 插件同名 `Civil3DMcpPlugin.dll` 且同占 127.0.0.1:8080）。只按 `docs/08` §4.3 取方法级补充（按名查找 + 句柄化、串行执行门禁范式、审批流范式） |
| 是否修改 | **否**（本轮 0 处改动） |

## 2. new-acad（vendored 源码）

| 项 | 内容 |
|---|---|
| 项目 | **new-acad** — https://github.com/Jjo37/new-acad |
| 固定 commit | `1120cda4aeba9086adf762e0a6ec6bdfd91bc652`（插件版本 1.15.0） |
| 许可证 | **MIT** |
| 版权行 | `Copyright (c) 2025-2026 Jjo 阿飞` |
| 许可证原文 | `third_party/new-acad/LICENSE`（1072 B，sha256 `d54c3f10…7121`，**一字未改**） |
| 本仓库位置 | `third_party/new-acad/`（421 文件 / 6.1 MB / 99 个 `.cs`） |
| 本仓库用途 | **A′ 主线的进程内专业执行层**（`docs/08` §2.2）；其 `server/panel-relay.js` 的 `/tcp` 桥在加最小鉴权补丁后作为我方网关到插件的唯一方法通道（N03） |
| 是否修改 | **是，仅 2 处机器路径适配**：P-001（csproj 6 处 HintPath 参数化）、P-002（`tools/scan-desc-check.js` 绝对路径改相对）。**未改任何能力实现**，diff 见 `third_party/patches/paths.patch` |

### 2.1 new-acad 内嵌的上游 MIT（必须保留，已随源码保留）

`third_party/new-acad/server/mcp/` 派生自 Civil3D-mcp，其 `LICENSE`
（`third_party/new-acad/server/mcp/LICENSE`，1067 B，sha256 `dcad40f8…6374`）
与 `third_party/civil3d-mcp/LICENSE` **同 hash** —— 即同一份 MIT 原文、同一版权行 `Copyright (c) 2025 lisiting01`。
上游自己在 `third_party/new-acad/THIRD-PARTY-NOTICES.md` §1 也是这么声明的。**这份文件不得删除、不得改写。**

## 3. .NET / NuGet 依赖（随 new-acad 插件分发）

| 组件 | 版本 | 许可证 | 许可证原文归档 | 依据 |
|---|---|---|---|---|
| **UglyToad.PdfPig**（含 `.Core` / `.Fonts` / `.Tokenization` / `.Tokens` 同版本子包） | `1.7.0-custom-5` | **Apache-2.0** | ✅ `third_party/notices/UglyToad.PdfPig-1.7.0-custom-5/`（`LICENSE` 14227 B + `NOTICES.txt` 414 B） | 见下方说明 |
| **SixLabors.ImageSharp** | `2.1.9` | **Apache-2.0** | ✅ `third_party/notices/SixLabors.ImageSharp-2.1.9/`（`LICENSE` 11339 B） | nuspec `<license type="expression">Apache-2.0</license>` |

**PdfPig 的许可证依据（这一条必须看细节）**：该包 **nuspec 没有 `<license>` 元素、包内也没有 LICENSE/NOTICE**，
它 `repository` 里记的 commit `6ebcf528e098f0b88a4588e9d710a994c9c251e0` **在上游 `UglyToad/PdfPig` 查不到（404）**
—— 说明 `1.7.0-custom-5` 是某个 fork 的定制构建。我们按「派生自 Apache-2.0 的 PdfPig，从上游继承」处理，
并归档了上游 `LICENSE` + `NOTICES.txt`（含 PDFBox / Adobe Glyph List 的版权行）。
**残余风险**：若该 fork 改了许可证，本假设不成立 —— 对外分发前应向包来源方确认。完整取证见
`third_party/notices/UglyToad.PdfPig-1.7.0-custom-5/SOURCE.md`。

**ImageSharp 的版本边界**：SixLabors 自 **3.0** 起改用 *Six Labors Split License*（非 Apache-2.0）。
本项目锁 `2.1.9` 仍是 Apache-2.0；**升级大版本必须重新评估许可证**，不能沿用本归档。

## 4. Node / npm 依赖（不随本仓库分发；仅供底层 MCP server 本地调试）

`third_party/civil3d-mcp/package.json` 与 `third_party/new-acad/server/mcp/package.json` 的**直接依赖**，
许可证字段取自 npm registry 元数据（本轮实查，非凭记忆）：

| 包 | 声明许可证 | 用在哪 |
|---|---|---|
| `@modelcontextprotocol/sdk` | MIT | 两个 server |
| `cheerio` | MIT | 两个 server |
| `minisearch` | MIT | 两个 server |
| `zod` | MIT | 两个 server |
| `typescript` | **Apache-2.0** | devDependency |
| `vitest` | MIT | devDependency |
| `rimraf` | **BlueOak-1.0.0** | devDependency |
| `@anthropic-ai/mcpb` | MIT | devDependency |
| `@rolldown/binding-win32-x64-msvc` | MIT | devDependency（new-acad） |
| `@types/node` | MIT | devDependency |

> ⚠️ 上表只覆盖**直接依赖**。**传递依赖**的许可证以各包自带 LICENSE 为准 —— 本轮**未安装
> `node_modules`**（编译探测不需要 Node 侧），因此没有做完整树扫描。将来要分发 Node 侧产物时，
> 必须先 `npm ci` 再跑许可证扫描并回填本表。

## 5. Autodesk 程序集（**不包含、不分发**）

- 本仓库**不含任何 Autodesk 二进制**（`accoremgd` / `AcDbMgd` / `acmgd` / `acdbmgdbrep` /
  `AecBaseMgd` / `AeccDbMgd` / `AeccPressurePipesMgd` / `Subassembly.*` 全部只在**编译期**作为 `Private=false` 引用）。
- 本机编译引用统一归集在仓库**外**：`%LOCALAPPDATA%\EngineeringPlatform\refs\c3d2025\`（22 MB）。
- `third_party/new-acad/plugin/AcBridge-v24/C_References/` 由上游 `build-plugin.ps1` 在构建时生成（本机 19 MB），
  **已 gitignore**、不入库；它同样只是本机编译引用。
- 运行需用户自行安装并授权正版 **AutoCAD / Civil 3D**。
- **本产品非 Autodesk 官方产品**，与 Autodesk 无隶属、赞助或背书关系；不包含 Autodesk 商标素材作为自身标识。

## 6. 本项目自身

**结论（已拍板）：本项目为专有软件，保留所有权利。**

| 项 | 内容 |
|---|---|
| 许可证类型 | **专有（proprietary）** —— 不采用 MIT / Apache-2.0 / GPL 等开源许可证 |
| 权利声明 | `Copyright (c) 2026 Xu-Liang. All rights reserved.` / 保留所有权利 |
| 全文位置 | 仓库根目录 **`LICENSE`**（中文为主 + 英文对照，7 节） |
| 限制要点 | 未经著作权人**书面许可**，不得复制、修改、演绎、分发、再许可、商业使用，或用于向第三方提供服务 |
| 担保 | 按「现状」提供，不附带任何明示或默示担保；输出结果不得作为工程决策的唯一依据 |
| 第三方组件 | **不受**本专有声明约束，各自遵循原许可证（见 §1–§4）；若冲突，冲突部分以第三方许可证为准 |
| 决策记录 | 决策日期 **2026-11-22**，决策人：**项目所有者**（用户）；执行：工程师 |

**本专有声明没有、也不会给第三方组件附加任何额外限制** —— 那会违反 MIT（不得对再分发附加限制）
与 Apache-2.0（不得减少原许可权利）。根 `LICENSE` 第 3 节把这一点写成了明确条款。

---

## 7. 本轮合规自检（可复现）

```bash
# ① vendored 树内不应有 Autodesk 二进制
find third_party \( -iname "*.dll" -o -iname "*.exe" -o -iname "*.arx" -o -iname "*.dbx" \) \
  -not -path "*C_References*" -not -path "*/bin/*" -not -path "*/obj/*" -not -path "*/notices/*"
#   → 只剩构建产物与 NuGet 依赖，Autodesk 程序集 0 个

# ② 三份上游 LICENSE 与上游逐字一致（sha256 见 §1 / §2 / §2.1）
sha256sum third_party/civil3d-mcp/LICENSE third_party/new-acad/LICENSE third_party/new-acad/server/mcp/LICENSE

# ③ 版权行未被改写
head -3 third_party/civil3d-mcp/LICENSE     # Copyright (c) 2025 lisiting01
head -3 third_party/new-acad/LICENSE        # Copyright (c) 2025-2026 Jjo 阿飞

# ④ 归档的第三方许可证原文未被改动（对照各自 SOURCE.md）
sha256sum third_party/notices/UglyToad.PdfPig-1.7.0-custom-5/LICENSE \
          third_party/notices/UglyToad.PdfPig-1.7.0-custom-5/NOTICES.txt \
          third_party/notices/SixLabors.ImageSharp-2.1.9/LICENSE
#   → 4c510e16…ded409 / d7a07a10…5f6e92 / f38dc276…8425a6f

# ⑤ 根 LICENSE 是专有声明而不是开源许可证
grep -c "All rights reserved" LICENSE        # ≥ 1
grep -ci "MIT License" LICENSE               # 0
```

## 8. NuGet 依赖原文核查（遍历实际还原图，不靠印象）

方法：读 `third_party/new-acad/plugin/AcBridge-v24/src/obj/project.assets.json` 的 `libraries`，
只取 `type == "package"`，得到**实际还原的 9 个包**；再逐个查包目录里有无
`license*` / `licence*` / `notice*` / `copying*` 文件，并读 nuspec 的 `<license>` 元素。

| 包 | 版本 | nuspec license | 包内原文 | 处置 |
|---|---|---|---|---|
| `UglyToad.PdfPig` | 1.7.0-custom-5 | **未声明** | ❌ 无 | ✅ 已归档（上游 Apache-2.0 原文，含残余风险说明） |
| `UglyToad.PdfPig.Core` | 1.7.0-custom-5 | **未声明** | ❌ 无 | 同上（同一构建，共用一份归档） |
| `UglyToad.PdfPig.Fonts` | 1.7.0-custom-5 | **未声明** | ❌ 无 | 同上 |
| `UglyToad.PdfPig.Tokenization` | 1.7.0-custom-5 | **未声明** | ❌ 无 | 同上 |
| `UglyToad.PdfPig.Tokens` | 1.7.0-custom-5 | **未声明** | ❌ 无 | 同上 |
| `SixLabors.ImageSharp` | 2.1.9 | `Apache-2.0`（表达式） | ❌ 无 | ✅ 已归档（按 nuspec 记录的 commit 取原文） |
| `Microsoft.NETCore.Platforms` | 5.0.0 | `MIT` | ✅ `LICENSE.TXT` + `THIRD-PARTY-NOTICES.TXT` | 无需归档（随包分发即可） |
| `System.Runtime.CompilerServices.Unsafe` | 5.0.0 | `MIT` | ✅ 同上 | 无需归档 |
| `System.Text.Encoding.CodePages` | 5.0.0 | `MIT` | ✅ 同上 | 无需归档 |

> `System.ValueTuple 4.5.0` 只出现在 PdfPig 的 net4x 依赖组里；本项目产物是 net8.0，
> 该包未参与还原，故不在上表。

**小结**：需要归档的 2 个（PdfPig 家族、ImageSharp）已全部归档；
其余 3 个 MIT 包自带原文；**没有「取不到」的项** —— 但 PdfPig 的**许可证归属**存在残余风险（§3 已说明）。

## 9. 变更历史

| 日期 | 变更 |
|---|---|
| 2026-11-22 | 首次建立：登记两个 vendored 上游（MIT）、new-acad 内嵌上游 MIT、2 个 Apache-2.0 NuGet、10 个 npm 直接依赖、Autodesk 程序集约束；标注 2 项待复核（PdfPig custom 构建的 NOTICE、npm 传递依赖全树扫描） |
| 2026-11-22 | 用户拍板 LICENSE 策略 → §6 改为「专有 · 保留所有权利」并新增根 `LICENSE`；§3 两份 Apache-2.0 组件的许可证原文已归档到 `third_party/notices/`（含 sha256 与 PdfPig fork 残余风险）；新增 §8 依赖原文核查表（遍历 9 个实际还原包）；npm 传递依赖全树扫描仍待 `npm ci` 后执行 |
