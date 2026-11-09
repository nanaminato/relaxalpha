# RelaxAlpha

面向中国商品期货期权的量化研究工作台。第一阶段以 CSV 快照为数据入口，在指定预算下筛选期权，并反算达到 2×、5×、10× 等净回收倍数所需的标的价格。

## 快速启动

需要 .NET 10 SDK、Node.js 24.15+（24.x）或 26.x。两个终端分别运行以下命令。

```powershell
dotnet restore
dotnet run --project src/RelaxAlpha.Api --no-launch-profile --urls http://127.0.0.1:5080
```

```powershell
cd web
npm ci
npm start
```

打开 [http://localhost:4200](http://localhost:4200)。在「市场总览」点击「加载模拟数据」可立即查看五条演示合约。接着在「机会排行榜」运行扫描，或从期权链进入「情景模拟器」。真实数据入口位于「数据源管理」，支持 UTF-8 CSV。

## Docker Compose

```bash
docker compose up --build -d
```

访问 [http://localhost:8080](http://localhost:8080)。默认监听回环地址，SQLite 数据保存在 Compose 卷内。设置 `RELAXALPHA_PORT` 可覆盖宿主端口。应用用于本机或可信内部研究环境，若对其他网络开放，需在网关配置身份验证、TLS 与访问控制。

切换 PostgreSQL 时，在项目根目录的 `.env` 设置 `POSTGRES_PASSWORD`，然后运行以下命令。数据库服务使用私有 Compose 网络。两个提供程序各自维护 EF 迁移，切换数据库后需重新导入所需数据。

```bash
docker compose -f compose.yaml -f compose.postgres.yaml up --build -d
```

## 构建与测试

```powershell
dotnet build RelaxAlpha.sln
dotnet test RelaxAlpha.sln
cd web
npm ci
npm run build
npm run i18n -- --output-path dist
```

- [开发、CSV 与 API 说明](docs/development.md)
- [定价口径与数据源调研](docs/research.md)
- [模拟 CSV 格式样本](samples/option-chain.demo.csv)

## 路线图

1. 第一阶段提供 SQLite/PostgreSQL、CSV、Black-76、预算扫描、情景模拟、Angular Dashboard 和 Compose 部署。SignalR 通知客户端重新读取新导入的快照。Quartz 每五分钟运行默认扫描。
2. 第二阶段接入有授权的自动行情、交易日历和合约规则，扩展历史数据管理、波动率与回测。
3. 第三阶段增加风险事件监控、AI 辅助研究和自动研究报告。

理论回报倍数无法给出实现概率。到期行权可能产生期货仓位、保证金需求和进一步损益。项目仅用于研究。
