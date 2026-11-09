# 开发说明

## 模块和技术版本

| 模块 | 责任 |
| --- | --- |
| `src/RelaxAlpha.Domain` | 行情批次、期权合约、质量状态、单手成本 |
| `src/RelaxAlpha.Quant` | Black-76、Greeks、隐含波动率、目标情景 |
| `src/RelaxAlpha.Application` | 行情查询、扫描、模拟用例与数据接口 |
| `src/RelaxAlpha.Infrastructure` | CSV 数据源、EF Core 持久化与两套迁移 |
| `src/RelaxAlpha.Worker` | Quartz 计划扫描，保存进程内最近结果 |
| `src/RelaxAlpha.Api` | ASP.NET Core REST、SignalR、依赖装配和静态前端托管 |
| `web` | Angular Dashboard、策略表单、情景模拟与数据管理 |
| `tests/RelaxAlpha.Tests` | xUnit 定价、目标反算、SQLite 迁移和导入/筛选测试 |

单个 ASP.NET Core 进程托管 API、SignalR 和 Quartz，容器中同源提供 Angular 静态文件。本地开发时 Angular 代理将 `/api` 和 `/hubs` 转给 5080 端口。后端为 .NET 10 LTS、EF Core 10.0.12、Npgsql EF 10.0.3、Quartz 3.20.1、Math.NET Numerics 5.0.0。前端为 Angular 22.2、TypeScript 6.0、SignalR 客户端 10.0，使用 npm 锁文件。中文界面用 Angular `i18n` 与 `$localize` 标记可翻译文本，日英译文可通过 XLIFF 接入。错误详情由 API 返回中文。

## 数据库配置和迁移

启动应用时自动应用所选数据库的迁移。SQLite 默认连接为 `Data Source=relaxalpha.db`。使用自建 PostgreSQL 时，设置 `Database__Provider=Postgres` 以及 `ConnectionStrings__Market`。PostgreSQL 库需事先创建，首次启动的数据库角色需有建表和建索引权限。

开发模型发生变更时，分别生成并评审两套迁移。

```powershell
dotnet tool restore
dotnet ef migrations add ChangeNameSqlite --project src/RelaxAlpha.Infrastructure --startup-project src/RelaxAlpha.Api --context SqliteMarketDatabase --output-dir Migrations/Sqlite
dotnet ef migrations add ChangeNamePostgres --project src/RelaxAlpha.Infrastructure --startup-project src/RelaxAlpha.Api --context PostgresMarketDatabase --output-dir Migrations/Postgres
```

## CSV 数据契约

以 `text/csv` 上传 UTF-8 文件，可含 BOM。每批限制 4 MiB、20,000 行。支持带引号的 CSV 字段。字段名不区分大小写，可调整列顺序。同一批次的交易所与合约组合必须唯一。格式错误时拒绝整个批次并返回错误位置。

```csv
exchange,symbol,underlying,expiry,side,strike,futures_price,multiplier,open_fee,close_fee,exercise_fee,bid,ask,volume,open_interest
DCE,DEMO-M-C-3400,DEMO-M,2027-01-07T15:00:00+08:00,C,3400,3100,10,2,2,2,17,20,130,710
```

| 字段 | 说明 |
| --- | --- |
| `exchange` | `SHFE`、`INE`、`DCE`、`CZCE` 或 `GFEX` |
| `symbol` / `underlying` | 期权和标的期货代码，存储为大写 |
| `expiry` | 实际到期时点，需含秒和时区，例如 `2027-01-07T15:00:00+08:00` |
| `side` | `C`/`Call` 或 `P`/`Put` |
| `strike` / `futures_price` | 行权价、同批观测的标的期货价，均须为正 |
| `multiplier` | 交易单位，报价乘以此值后得到每手金额 |
| `open_fee` / `close_fee` / `exercise_fee` | 每手费用估算，人民币元 |
| `bid` / `ask` | 同时点买一、卖一报价。缺失留空，0 视为无有效报价 |
| `volume` / `open_interest` | 成交量和持仓量，非负整数，单位为手 |

上传时另填来源名称、来源类型和批次观测时点。请以实际合约规则填写到期时点、乘数与费用，金额使用小数点且无千分位。来源类型 `ExchangePublic`、`Licensed`、`UserProvided` 由导入者申报。模拟数据使用 `Simulated`，`DEMO-` 前缀的合约只允许归入该类。样本文件使用固定日期，仅用于验证文件格式。可通过「加载模拟数据」获得相对当前日期的样本。

相同来源名称、类型、观测时点与内容哈希的重复提交返回原批次。批次保留原始观测时间和入库时间。当前行情按合约取最近观测，观测时间相同时取后入库记录，真实与模拟数据分别取最新。行情列表展示参考报价和盘口倒挂，默认扫描只接受有正买卖价且无倒挂的报价。交易所公开日结数据若缺买卖价，可用于链表查看，无法满足盘口筛选。

## API

| 方法与路径 | 用途 |
| --- | --- |
| `GET /api/health` | 应用启动检查 |
| `GET /api/market?simulated=false` | 当前未到期期权链及 IV |
| `GET /api/sources` | 最近 100 个导入批次 |
| `POST /api/sources/import?source=...&kind=...&observedAt=...` | 请求体为 CSV，URL 编码查询参数，观测时间用 ISO 8601 |
| `POST /api/sources/demo` | 发送 JSON 请求体 `{}`，显式载入模拟数据 |
| `POST /api/scans` | JSON 策略扫描，传 `{}` 使用默认条件 |
| `GET /api/scans/scheduled` | Quartz 最近一次默认扫描，尚未完成时返回空结果 |
| `POST /api/simulate` | 接受 `quoteId`、`futuresPrice`、`remainingDays`、`volatility`、可选 `targets` |
| `/hubs/market` | SignalR Hub，成功导入后发送 `MarketUpdated` 批次通知 |

扫描请求示例。百分比参数按小数传入。

```json
{
  "budget": 1000,
  "targetMultiple": 5,
  "minDays": 30,
  "maxDays": 90,
  "side": null,
  "minIv": 0,
  "maxIv": 5,
  "minVolume": 10,
  "minOpenInterest": 50,
  "maxSpreadRatio": 0.3,
  "maxAgeDays": 3,
  "shock": 0.1,
  "simulated": false
}
```

当前策略在页面会话内编辑并提交。Quartz 每五分钟以默认策略扫描非模拟行情，结果保存在内存，重启后重新计算。扩展采集时可实现应用层数据源接口，继续使用批次、来源分类和质量规则。
