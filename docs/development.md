# 开发说明

## 模块和技术版本

| 模块 | 责任 |
| --- | --- |
| `src/RelaxAlpha.Domain` | 行情批次、期权合约、质量状态、单手成本 |
| `src/RelaxAlpha.Quant` | Black-76、Greeks、隐含波动率、目标情景 |
| `src/RelaxAlpha.Application` | 行情查询、扫描、模拟用例与数据接口 |
| `src/RelaxAlpha.Infrastructure` | 官方日行情和 CSV 数据源、EF Core 持久化与两套迁移 |
| `src/RelaxAlpha.Worker` | Quartz 日行情采集与计划扫描，保存进程内最近结果 |
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

## 官方日行情采集与离线调试

Compose 默认启用联网采集，直接运行 API 时默认关闭。任务启动时和之后每小时检查一次北京时间的上一交易日，跳过周末及已发布的休市日。内置日历覆盖 2026 年；后续年度可根据上期所公告配置 `ExchangeDaily:CalendarThrough` 和 `ExchangeDaily:AdditionalClosures`，例如通过环境变量 `ExchangeDaily__CalendarThrough`、`ExchangeDaily__AdditionalClosures__0` 传入 ISO 日期。超出覆盖日期时停止采集并在界面提示。 [上期所休市安排](https://www.shfe.com.cn/services/calenderandholidays/holiday/)

采集以「数据源 + 交易日」持久化任务记录，并在请求前声明本次尝试。每个交易日成功入库后，无论刷新、扫描、重启还是同数据库的并发实例，均复用本地数据。未完成任务至少间隔六小时才重试，每份有效原文件收到后立即保存，重试只补尚未保存的文件。采集状态可在「数据源管理」或 `GET /api/sources/daily` 查看。开发、测试与业务查询均可在断网时使用。

要复用已下载的原始文件，在启动 API 前设置 `ExchangeDaily__Enabled=true` 和 `ExchangeDaily__OfflineDirectory` 为文件所在目录。离线模式只读文件，遇到缺失或无效文件会记录失败。Compose 联网模式优先读取项目下的 `.exchange-cache/`，只有缺少本地文件才请求官网。直接运行 API 可用 `ExchangeDaily__SeedDirectory` 指向同一目录。将同一个交易日的文件按以下名称保存。表中 `YYYYMMDD` 为北京时间交易日。

| 文件名 | 上期所官网路径 |
| --- | --- |
| `futures-YYYYMMDD.dat` | `/data/tradedata/future/dailydata/kxYYYYMMDD.dat` |
| `options-YYYYMMDD.dat` | `/data/tradedata/option/dailydata/kxYYYYMMDD.dat` |
| `contracts-YYYYMMDD.dat` | `/data/busiparamdata/option/ContractBaseInfoYYYYMMDD.dat` |
| `fees-YYYYMMDD.dat` | `/data/busiparamdata/option/SettlementYYYYMMDD.dat` |

保留数据库卷即可保留原文件及尝试记录；每套独立数据库各有自己的缓存。测试使用代码内样本和假的 HTTP 处理程序。原始官方文件不纳入版本控制。 [上期所日周数据](https://www.shfe.com.cn/reports/tradedata/) · [业务参数汇总](https://www.shfe.com.cn/reports/businessdata/prmsummary/)

## 天勤接入

天勤 SDK 3.10.4 运行在单个 Python 3.13 进程内。进程持有一个 `TqApi` 实例，持续执行 `wait_update`。用户在模拟器查询时，.NET 将选中的期权及标的合约交给该进程订阅。并发和重复查询复用会话、订阅和内存快照。会话异常退出后按 5–120 秒退避重连，并恢复所请求的合约。行情权限由天勤账户决定。 [天勤 SDK 官方文档](https://tqsdk-python.readthedocs.io/en/stable/reference/tqsdk.api.html)

在仓库根目录用 PowerShell 7 初始化本地凭据，再启动可选 Compose 服务。

```powershell
./scripts/Set-TqSdkCredentials.ps1
docker compose -f compose.yaml -f compose.tqsdk.yaml up --build -d
```

脚本掩码读取凭据，保存至 `.secrets/tq_username` 和 `.secrets/tq_password`。该目录从 Git 和 Docker 构建上下文排除，凭据仅通过运行时只读挂载提供给 Python 容器。Linux 用户可限制宿主机目录访问权限（目录 `0700`，两个文件 `0444` 以供容器内非 root 用户读取）。更新凭据后重启 `tq-feed`。使用 PostgreSQL 时可同时叠加 `compose.postgres.yaml`。

打开模拟器，选择真实合约后点击「读取天勤行情」。面板显示天勤原始行情时间、最新价、买一卖一、成交量与持仓量。行情超过两分钟会标记为旧行情；取得有效标的最新价后可手动填入情景。官方日行情仍是默认扫描和权利金估算的来源，天勤服务仅提供盘口快照。

`GET /api/live/status` 可查看会话状态、已请求合约数和会话代数。 `POST /api/live/quotes/{id}` 接受空 JSON 对象并查询本地期权 ID。Python HTTP 端口仅供 Compose 服务网络访问。进程级订阅上限默认 128，使用 `RELAXALPHA_TQ_CAPACITY` 调整（2–512）；达到上限时现有合约仍可查询，新合约返回 429。当前 SDK 订阅随会话保留。离线验证可在 `market-feed` 下运行 `python -m unittest -v`。

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
| `GET /api/market?universe=OfficialDaily` | 默认读取上一交易日官方期权链及 IV。也可选 `Imported` 或 `Simulated` |
| `GET /api/sources` | 最近 100 个入库批次 |
| `GET /api/sources/daily` | 本地采集状态、目标交易日和重试时间 |
| `POST /api/sources/import?source=...&kind=...&observedAt=...` | 请求体为 CSV，URL 编码查询参数，观测时间用 ISO 8601 |
| `POST /api/sources/demo` | 发送 JSON 请求体 `{}`，显式载入模拟数据 |
| `POST /api/scans` | JSON 策略扫描，传 `{}` 使用默认条件 |
| `GET /api/scans/scheduled` | Quartz 最近一次默认扫描，尚未完成时返回空结果 |
| `GET /api/scans/defaults` | 默认策略定义，前端表单初始值与「恢复默认」以此为准 |
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
  "universe": "OfficialDaily"
}
```

当前策略在页面会话内编辑并提交，表单初始值与「恢复默认」读取 `GET /api/scans/defaults`，默认值只在后端定义一份。Quartz 每五分钟以默认策略扫描官方日行情，结果保存在内存，重启后重新计算。旧调用可继续通过 `simulated=false` 显式读取导入行情，或通过 `simulated=true` 读取模拟行情。
