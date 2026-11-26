# protocol/ —— 宿主与 MCP 的唯一契约源

本目录四个文件是「管道协议 + 工具目录」的**唯一真相源**。C# 宿主与 Python MCP 各自实现一份，**任何一方改字段都必须同时改这里，并双端同步**。

| 文件 | 内容 | 谁消费 |
|---|---|---|
| `envelope.schema.json` | 分帧后的帧载荷：请求封套 / 响应封套 / 错误对象 / 告警对象 / 握手帧引用 | Host `Protocol/Envelope.cs`、MCP `pipeclient.py` |
| `handshake.schema.json` | `hello` / `hello_ack` / `bye` 三类帧，含完整示例 | Host `Protocol/Handshake.cs`、MCP `pipeclient.handshake()` |
| `errors.json` | 错误码与告警码表（E#### / C#### / W####），含 `retriable` 与处理建议 | 双端 `ErrorCodes.cs` / `errors` 映射 |
| `tools.schema.json` | 工具目录：16 个只读工具的名称、描述规范、`inputSchema`、`outputSchema`、`annotations`、域名读写分类 | Host `Protocol/ToolCatalog.cs`、MCP `tools_catalog.py` |

## 1. 分帧

**MCP ↔ Host（Windows 命名管道）**：4 字节**小端** `uint32` 长度前缀 + UTF-8 JSON 载荷。

```
+----------+---------------------------+
| len: u32 |  payload: len bytes UTF-8 |
+----------+---------------------------+
```

- 一帧 = 一个 JSON **object**（不接受数组、不接受批量）。
- `len ∈ (0, 1048576]`；`len == 0` 视为协议违规，立即关连接。
- `len > 1 MiB`：接收方**必须先把这 len 字节读完丢弃**（否则流错位），再回 `E1001 detail=message_too_large` 的错误帧（`opId:null`），连接保持可用。
- 载荷必须是严格 JSON：解析后必须到达 EOF，尾随垃圾视为违规。

**WorkBuddy ↔ MCP（stdio）**：**行分隔**（一行一条 JSON，UTF-8，消息内不得含裸换行，单行上限 8 MiB）。这是 MCP 规范对 stdio 传输的强制要求，与本目录无关，但两处不要混淆。

### 为什么管道不用行分隔

1. 能在分配缓冲**之前**判定超限（1 MiB 是硬约束）；
2. 二进制安全，为后续预览字节/文件通道留路而不改协议版本；
3. 半包/粘包处理确定（`read_exact(4)` → `read_exact(n)`）；
4. C# `BinaryReader` 与 Python `struct.unpack('<I')` 天生对称，无需自写换行扫描。

## 2. 命名风格（全局唯一，不许有第二种）

- **JSON 字段：camelCase**（`protocolVersion`、`hostInstanceId`、`opId`、`targetFingerprint`、`insUnits`、`readOnlyHint`）。与方案第 13.2 节字段表、MCP 协议字段一致。
- **工具名：snake_case**（`read_overview`），因为它是「域名_动作」标识，不是字段。
- **枚举值：小写 snake**（`verified`、`write_channel_disabled_m0`、`duplicate_point`）。
- **时间**：UTC，`YYYY-MM-DDTHH:MM:SS.mmmZ`，字段名带 `Utc` 后缀。
- **单位**：长度用图纸单位（同响应给 `units{insUnits,code,scaleToMeter}`）；角度 `_deg`；毫秒 `_ms`。schema 里每个数值字段必须在 `description` 写明单位。

## 3. 版本与兼容

- 协议 `1.0`。**主版本必须相等**，客户端次版本可 ≤ 宿主次版本。
- 不兼容 → `E4001`，连接关闭；仅次版本超前 → 允许连接 + `W1002`，`writeEnabled` 恒 `false`。
- M0 **写入通道硬关闭**：`writeEnabled` 在 schema 中为 `const false`，任何版本都不允许放开。写域工具调用一律 `E2003 detail=write_channel_disabled_m0`。
- `toolCatalogHash = "sha256:" + sha256(tools.schema.json 文件字节).hex[:16]`，握手时交换；不一致记 `W1007`（不阻断）。

## 4. 摘要 + 结果引用（方案 13.6 的落地）

每个工具的结果对象**必须**含：

- `summary`：≤ 400 字符自然语言摘要（给用户与模型直接看）；
- `resultRef`：`{tool, opId, fingerprint, storedAt?, detailTool?}`，详情通过它去项目库或下钻工具取；
- 可选 `truncated` / `drilldown[]`：截断时必须同时给出可直接执行的 `{tool, params, why}`。

MCP 层再包一层：`content[0]` = 摘要文本（≤1200 字符），`content[1]` = 含 `resultRef` 的 JSON 文本，`read_preview` 再追加 `content[2]` = image。

## 5. 错误分层

| 层 | 码 | 用途 |
|---|---|---|
| 宿主 | `E####` | CAD 侧拒绝或失败（参数、门禁、忙碌、模态、能力缺口、版本、磁盘） |
| MCP 链路 | `C####` | 连不上宿主、实例不唯一、路径越界、配置无效 |
| 告警 | `W####` | 不阻断但必须让模型知道（Civil 缺口、指纹漂移、截断、目录不一致） |

`tools/call` 的约定：宿主 `E####` 包成 **`isError: true` 的成功响应**（模型能读到错误码并自我修正）；只有 JSON-RPC 层故障才用 `-32xxx`。

## 6. 变更流程（谁都不许单方面改）

1. 改本目录文件 → 2. 同步改 C# `Protocol/*` 与 Python `tools_catalog.py` → 3. 跑 `python -m unittest discover tests`（含双端 `paramsDigest` 与 `toolCatalogHash` 一致性断言）→ 4. 协议版本号按语义化调整（破坏性变更升次版本并记录在本文档「变更记录」）→ 5. 更新 `protocolVersion` 常量（双端各一份，CI 断言相等）。

## 7. 变更记录

| 版本 | 日期 | 变更 |
|---|---|---|
| 1.0 | 2026-11-21 | 首版：M0 只读链路，16 个工具，封套 11 字段，管道四字节分帧 |
| 1.0（rev.2） | 2026-11-21 | Civil 3D 实测后补齐契约（向后兼容，协议版本不变）：① `hello_ack` 新增 `hostProduct`（AutoCAD/Civil3D）与 `hostRelease`（R24.3 等），`hostVersion` 语义保持「插件版本号」不变；② `capabilities.civilApi` 新增 `product` / `release` / `writeSupported`；③ `read_overview` 输出新增 `civil3d` 段（detected / counts / 五类对象清单 / attributeProbe），原生对象 `confidence=1.0`；④ `tools.schema.json#/$defs` 新增 `Basis`（识别依据）定义 |

### 关于 `hostVersion` 与 `hostRelease` 的分工（勿混用）

- `hostVersion` = **插件**版本（如 `0.1.0+net48`）。实例发现文件 `%LOCALAPPDATA%\EngineeringPlatform\instances\*.json` 与 `mcp/instances.py` 都按此语义解析，**不得**改成 CAD 版本。
- `hostRelease` = **CAD 发行版本**（`R24.3` / `R23.0` / `R24.1`），来自 `ACADVER`，用于版本矩阵与可行性判断。

