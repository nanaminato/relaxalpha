import { DatePipe, DecimalPipe, NgTemplateOutlet, PercentPipe } from '@angular/common';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { bootstrapApplication } from '@angular/platform-browser';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Batch, DailyStatus, LiveQuoteBatch, MarketApi, MarketUniverse, Opportunity, Quality, Quote, Scan, Settings, Simulation, SourceKind } from './market';
import { browseList, matchesQuery, productKey } from './browse-list';
import { exchangeName, productName } from './product-names';

const tabs = [
  {id: 'overview', number: '01', name: $localize`市场总览`},
  {id: 'chain', number: '02', name: $localize`期权链`},
  {id: 'ranking', number: '03', name: $localize`机会排行榜`},
  {id: 'simulator', number: '04', name: $localize`情景模拟器`},
  {id: 'strategy', number: '05', name: $localize`策略配置`},
  {id: 'sources', number: '06', name: $localize`数据源管理`},
] as const;
type Tab = typeof tabs[number]['id'];
type SortDirection = 'ascending' | 'descending';

interface RankingGroup {
  key: string;
  quote: Quote;
  items: Opportunity[];
}

type RankingRow = {key: string; kind: 'group'; group: RankingGroup; expanded: boolean}
  | {key: number; kind: 'contract'; item: Opportunity};

const rankingColumns = [
  {column: 'cost', title: $localize`单手资金`, detail: $localize`本金风险`, value: (item: Opportunity) => item.quote.entryCost},
  {column: 'change', title: $localize`目标涨跌幅`, detail: $localize`目标到期价`, value: (item: Opportunity) => item.target.change},
  {column: 'multiple', title: $localize`情景净回收倍数`, detail: $localize`情景盈亏`, value: (item: Opportunity) => item.shockScenario.multiple}
] as const;

type RankingColumn = (typeof rankingColumns)[number]['column'];

// 表头文案与排序取值由同一张表派生，新增列只需改上面的定义。
const rankingValue = Object.fromEntries(rankingColumns.map(column => [column.column, column.value])) as
  Record<RankingColumn, (item: Opportunity) => number | null>;

// 默认策略由 GET /api/scans/defaults 提供，避免前后端各写一份默认值。这里只是加载完成前的中性占位。
const placeholder: Settings = {
  budget: 0, targetMultiple: 0, minDays: 0, maxDays: 0, side: null, minIv: 0, maxIv: 0,
  minVolume: 0, minOpenInterest: 0, maxSpreadRatio: 0, maxAgeDays: 0, shock: 0
};

// 服务端默认值可能带请求专用字段（universe/simulated），按表单模型的键裁剪后再落到表单。
const only = <T extends object>(template: T, source: T): T =>
  Object.fromEntries(Object.keys(template).map(key => [key, source[key as keyof T]])) as T;

// 模拟器下拉一次最多渲染的合约数，避免上万条 option 拖慢页面。
const MaxContractOptions = 200;

@Component({
  selector: 'ra-app',
  imports: [FormsModule, DatePipe, DecimalPipe, PercentPipe, NgTemplateOutlet],
  templateUrl: './dashboard.html'
})
class Dashboard {
  private readonly api = inject(MarketApi);
  private readonly hub = new HubConnectionBuilder().withUrl('/hubs/market')
    .withAutomaticReconnect().configureLogging(LogLevel.Warning).build();
  private refreshVersion = 0;
  private liveVersion = 0;
  private pendingQuoteId: number | null = null;

  readonly tabs = tabs;
  readonly exchangeName = exchangeName;
  readonly productName = productName;
  readonly active = signal<Tab>('overview');
  readonly title = computed(() => tabs.find(tab => tab.id === this.active())!.name);
  readonly universe = signal<MarketUniverse>('OfficialDaily');
  readonly official = computed(() => this.universe() === 'OfficialDaily');
  readonly universes: {id: MarketUniverse; label: string}[] = [
    {id: 'OfficialDaily', label: $localize`官方日行情`},
    {id: 'Imported', label: $localize`导入行情`},
    {id: 'Simulated', label: $localize`模拟数据`}
  ];
  readonly connected = signal(false);
  readonly busy = signal(false);
  readonly marketLoading = signal(true);
  readonly error = signal('');
  readonly marketError = signal('');
  readonly notice = signal('');
  readonly quotes = signal<Quote[]>([]);
  readonly batches = signal<Batch[]>([]);
  readonly scheduled = signal<Scan | null>(null);
  readonly daily = signal<DailyStatus[]>([]);
  readonly dailyFailed = computed(() => this.daily().filter(feed => feed.failure));
  readonly scan = signal<Scan | null>(null);
  readonly simulation = signal<Simulation | null>(null);
  readonly selected = signal<Quote | null>(null);
  readonly live = signal<LiveQuoteBatch | null>(null);
  readonly liveBusy = signal(false);
  readonly liveError = signal('');
  readonly liveStates = {
    connecting: $localize`连接中`, ready: $localize`已连接`, reconnecting: $localize`重连中`
  };
  readonly chain = browseList(this.quotes, quote => quote, 30);
  readonly rankingSort = signal<{column: RankingColumn; direction: SortDirection} | null>(null);
  readonly rankingGrouped = signal(false);
  readonly reachesOnly = signal(false);
  readonly expandedProducts = signal<ReadonlySet<string>>(new Set());
  readonly rankingHeaders = rankingColumns;
  readonly ranking = browseList(computed(() => {
    const items = (this.scan()?.opportunities ?? []).filter(item => !this.reachesOnly() || item.reachesTarget);
    const sort = this.rankingSort();
    if (!sort) return items;
    const value = rankingValue[sort.column];
    const sign = sort.direction === 'ascending' ? 1 : -1;
    return [...items].sort((left, right) => {
      const a = value(left);
      const b = value(right);
      if (a === b) return 0;
      if (a === null) return 1;
      if (b === null) return -1;
      return (a - b) * sign;
    });
  }), item => item.quote, 25);
  readonly rankingGroups = computed(() => {
    const groups = new Map<string, RankingGroup>();
    for (const item of this.ranking.filtered()) {
      const key = productKey(item.quote);
      const existing = groups.get(key);
      if (existing) existing.items.push(item);
      else groups.set(key, {key, quote: item.quote, items: [item]});
    }
    return [...groups.values()];
  });
  readonly rankingDisplayRows = computed<RankingRow[]>(() => {
    const contractRow = (item: Opportunity): RankingRow => ({key: item.quote.id, kind: 'contract', item});
    if (!this.rankingGrouped()) return this.ranking.rows().map(contractRow);

    const expandedProducts = this.expandedProducts();
    return this.rankingGroups().flatMap(group => {
      const expanded = expandedProducts.has(group.key);
      const header: RankingRow = {key: group.key, kind: 'group', group, expanded};
      return expanded ? [header, ...group.items.map(contractRow)] : [header];
    });
  });
  readonly simulationOrigin = signal<'chain' | 'ranking' | null>(null);
  readonly canQuote = (quote: Quote) => quote.entryCost != null && quote.quality !== 'Crossed';
  readonly quotable = computed(() => this.quotes().filter(this.canQuote));
  readonly contractFilter = signal('');
  readonly contractMatches = computed(() => {
    const text = this.contractFilter().trim().toUpperCase();
    const quotable = this.quotable();
    const matched = text ? quotable.filter(quote => matchesQuery(quote, text)) : quotable;
    const items = matched.slice(0, MaxContractOptions);
    const selected = this.selected();
    if (selected && quotable.some(item => item.id === selected.id) && !items.some(item => item.id === selected.id))
      items.unshift(selected);
    return {items, total: matched.length};
  });
  readonly recentHits = computed(() => {
    if (!this.official()) return [];
    const scanned = this.scan();
    const latest = scanned?.settings.universe === this.universe() ? scanned : this.scheduled();
    return latest?.opportunities.slice(0, 3) ?? [];
  });
  readonly underlyings = computed(() => new Set(this.quotes().map(q => q.exchange + '/' + q.underlying)).size);
  readonly twoSided = computed(() => this.quotes().filter(q => q.quality === 'TwoSided').length);
  readonly latestQuote = computed(() => this.quotes().reduce((latest, q) =>
    Date.parse(q.observedAtUtc) > Date.parse(latest || '1970-01-01') ? q.observedAtUtc : latest, ''));

  readonly kinds: Record<SourceKind, string> = {
    UserProvided: $localize`用户提供（未验证）`, ExchangePublic: $localize`交易所公开（申报）`,
    Licensed: $localize`已授权（申报）`, Simulated: $localize`模拟数据`
  };
  readonly qualities: Record<Quality, string> = {
    TwoSided: $localize`双边报价`, Indicative: $localize`参考报价`, Crossed: $localize`盘口倒挂`,
    DailySettlement: $localize`日结算参考`
  };
  readonly sides = {Call: $localize`看涨`, Put: $localize`看跌`};
  readonly defaults = signal<Settings | null>(null);
  readonly settingsReady = signal(false);
  settings: Settings = {...placeholder};
  source = '';
  kind: SourceKind = 'UserProvided';
  observedAt = '';
  file: File | null = null;
  futuresPrice = 0;
  remainingDays = 0;
  volatility = 0.25;
  targets = '2, 5, 10';

  constructor() {
    const destroy = inject(DestroyRef);
    this.restoreState();
    const onPopState = () => {
      this.restoreState();
      void this.refresh();
    };
    window.addEventListener('popstate', onPopState);
    // Ctrl/Cmd + Enter：模拟器里计算情景，其他页面运行扫描。
    const onKeyDown = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.key !== 'Enter') return;
      event.preventDefault();
      if (this.active() === 'simulator') void this.runSimulation();
      else void this.runScan();
    };
    window.addEventListener('keydown', onKeyDown);
    this.hub.on('MarketUpdated', () => void this.refresh());
    this.hub.onreconnecting(() => this.connected.set(false));
    this.hub.onclose(() => this.connected.set(false));
    this.hub.onreconnected(() => {
      this.connected.set(true);
      void this.refresh();
    });
    void this.hub.start().then(() => this.connected.set(true), () => this.connected.set(false));
    destroy.onDestroy(() => {
      window.removeEventListener('popstate', onPopState);
      window.removeEventListener('keydown', onKeyDown);
      void this.hub.stop();
    });
    void this.refresh();
  }

  navigate(tab: Tab) {
    this.active.set(tab);
    this.error.set('');
    this.notice.set('');
    this.syncState(true);
  }

  changeUniverse(universe: MarketUniverse) {
    if (universe === this.universe()) return;
    this.selectUniverse(universe);
    this.syncState(true);
    void this.refresh();
  }

  private selectUniverse(universe: MarketUniverse) {
    if (universe === this.universe()) return;
    this.universe.set(universe);
    this.quotes.set([]);
    this.scan.set(null);
    this.chain.clear();
    this.ranking.clear();
    this.expandedProducts.set(new Set());
    this.simulationOrigin.set(null);
    this.selectQuote(null);
  }

  async refresh() {
    const version = ++this.refreshVersion;
    this.marketLoading.set(true);
    this.marketError.set('');
    if (!this.settingsReady()) void this.loadSettings();
    try {
      const [quotes, batches, scheduled, daily] = await Promise.all([
        this.api.market(this.universe()), this.api.sources(), this.api.scheduled(), this.api.daily()
      ]);
      if (version !== this.refreshVersion) return;
      this.quotes.set(quotes);
      this.batches.set(batches);
      this.scheduled.set(scheduled);
      this.daily.set(daily);
      this.applyPendingQuote(quotes);
      this.chain.reconcile();
    } catch (error) {
      if (version === this.refreshVersion) this.marketError.set(this.errorText(error));
    } finally {
      if (version === this.refreshVersion) this.marketLoading.set(false);
    }
  }

  sortRanking(column: RankingColumn) {
    const current = this.rankingSort();
    const direction = current?.column === column && current.direction === 'ascending' ? 'descending' : 'ascending';
    this.rankingSort.set({column, direction});
    this.ranking.goTo(0);
  }

  rankingDirection(column: RankingColumn) {
    const sort = this.rankingSort();
    return sort?.column === column ? sort.direction : null;
  }

  toggleProduct(key: string) {
    this.expandedProducts.update(previous => {
      const expanded = new Set(previous);
      if (expanded.has(key)) expanded.delete(key);
      else expanded.add(key);
      return expanded;
    });
  }

  toggleReaches() {
    this.reachesOnly.update(value => !value);
    this.ranking.goTo(0);
  }

  setAllProductsExpanded(expanded: boolean) {
    this.expandedProducts.set(new Set(expanded ? this.rankingGroups().map(group => group.key) : []));
  }

  resetSettings() {
    const defaults = this.defaults();
    if (defaults) this.settings = only(placeholder, defaults);
  }

  async runScan() {
    this.navigate('ranking');
    await this.perform(async () => {
      this.scan.set(null);
      if (Object.entries(this.settings).some(([key, value]) => key !== 'side' &&
          (typeof value !== 'number' || !Number.isFinite(value))))
        throw new Error($localize`请填写全部数字筛选条件。`);
      this.scan.set(await this.api.scan(this.settings, this.universe()));
      this.ranking.reconcile(true);
    });
  }

  sourceLabel(kind: SourceKind, tradingDate: string | null) {
    return tradingDate ? $localize`官方日行情` : this.kinds[kind];
  }

  dailyState(feed: DailyStatus) {
    if (!feed.tradingDate) return $localize`超出已配置的交易日历`;
    if (feed.batchId) return $localize`已入库`;
    if (!feed.enabled) return $localize`采集已关闭`;
    if (feed.failure) return $localize`采集失败，等待重试`;
    return $localize`等待入库`;
  }

  selectById(id: number | null) {
    this.selectQuote(this.quotable().find(q => q.id === id) ?? null);
  }

  selectQuote(quote: Quote | null) {
    this.selected.set(quote);
    this.simulation.set(null);
    ++this.liveVersion;
    this.live.set(null);
    this.liveError.set('');
    this.liveBusy.set(false);
    if (!quote) {
      this.syncState(false);
      return;
    }
    this.futuresPrice = quote.futuresPrice;
    this.remainingDays = 0;
    this.volatility = quote.iv ?? 0.25;
    this.syncState(false);
  }

  openSimulation(quote: Quote) {
    const origin = this.active();
    this.simulationOrigin.set(origin === 'chain' || origin === 'ranking' ? origin : null);
    this.selectQuote(quote);
    this.navigate('simulator');
  }

  async loadLive() {
    const quote = this.selected();
    if (!quote || quote.kind === 'Simulated' || this.liveBusy()) return;

    const version = ++this.liveVersion;
    this.liveError.set('');
    this.liveBusy.set(true);
    try {
      for (let attempt = 0; attempt < 8; attempt++) {
        if (version !== this.liveVersion || this.active() !== 'simulator') return;
        const result = await this.api.live(quote.id);
        if (version !== this.liveVersion || this.active() !== 'simulator') return;
        this.live.set(result);
        if (result.quotes.every(q => q.state !== 'pending')) return;
        if (attempt < 7) await new Promise(resolve => setTimeout(resolve, 1000));
      }
    } catch (error) {
      if (version === this.liveVersion) this.liveError.set(this.errorText(error));
    } finally {
      if (version === this.liveVersion) this.liveBusy.set(false);
    }
  }

  useLiveUnderlying() {
    const quote = this.live()?.quotes[1];
    const age = quote?.quoteTime ? Date.now() - Date.parse(quote.quoteTime) : Infinity;
    if (this.live()?.state !== 'ready' || !quote?.lastPrice || quote.stale || age > 120_000 || age < -5000) {
      this.liveError.set($localize`请先刷新并取得有效的标的最新价。`);
      return;
    }
    this.futuresPrice = quote.lastPrice;
    this.simulation.set(null);
    this.liveError.set('');
  }

  async runSimulation() {
    const quote = this.selected();
    if (!quote) return;
    await this.perform(async () => {
      this.simulation.set(null);
      if (![this.futuresPrice, this.remainingDays, this.volatility].every(Number.isFinite))
        throw new Error($localize`请填写标的价格、剩余天数和波动率。`);
      const targets = this.targets.split(/[,，]/).map(value => Number(value.trim()));
      this.simulation.set(await this.api.simulate(quote.id, this.futuresPrice, this.remainingDays, this.volatility, targets));
    });
  }

  selectFile(event: Event) {
    this.file = (event.target as HTMLInputElement).files?.[0] ?? null;
  }

  async upload() {
    const file = this.file;
    if (!file) return;
    await this.perform(async () => {
      if (!this.observedAt || !this.source.trim()) throw new Error($localize`请填写数据来源及观测时间。`);
      const batch = await this.api.import(file, this.source.trim(), this.kind, new Date(this.observedAt).toISOString());
      this.selectUniverse(batch.kind === 'Simulated' ? 'Simulated' : 'Imported');
      this.notice.set($localize`导入批次包含 ${batch.count}:count: 条行情。`);
      await this.refresh();
    });
  }

  async loadDemo() {
    await this.perform(async () => {
      await this.api.demo();
      this.selectUniverse('Simulated');
      this.navigate('chain');
      this.notice.set($localize`已加载模拟期权链。演示合约仅用于验证研究流程。`);
      await this.refresh();
    });
  }

  private async loadSettings() {
    try {
      const defaults = await this.api.defaults();
      this.defaults.set(defaults);
      this.settings = only(placeholder, defaults);
      this.settingsReady.set(true);
    } catch (error) {
      // 保持占位值并提示；下次刷新会重试。
      this.error.set(this.errorText(error));
    }
  }

  // 视图状态写进 hash：刷新后可回到原处，链接也能直接分享（#ranking?universe=Imported&id=12）。
  private restoreState() {
    const [tab, query = ''] = location.hash.replace(/^#/, '').split('?');
    if (this.tabs.some(item => item.id === tab)) this.active.set(tab as Tab);
    const params = new URLSearchParams(query);
    const universe = params.get('universe');
    if (this.universes.some(item => item.id === universe)) this.universe.set(universe as MarketUniverse);
    const id = Number(params.get('id'));
    this.pendingQuoteId = Number.isInteger(id) && id > 0 ? id : null;
    if (this.pendingQuoteId === null) this.selected.set(null);
  }

  private syncState(push: boolean) {
    const params = new URLSearchParams();
    if (this.universe() !== 'OfficialDaily') params.set('universe', this.universe());
    const selected = this.selected();
    if (selected) params.set('id', String(selected.id));
    const query = params.toString();
    const hash = `#${this.active()}${query ? '?' + query : ''}`;
    if (location.hash === hash) return;
    if (push) history.pushState(null, '', hash);
    else history.replaceState(null, '', hash);
  }

  private applyPendingQuote(quotes: Quote[]) {
    const id = this.pendingQuoteId;
    this.pendingQuoteId = null;
    if (id === null) return;
    const quote = quotes.find(item => item.id === id);
    if (quote) this.selectQuote(quote);
  }

  daysLeft(quote: Quote) {
    return Math.max(0, Math.round((Date.parse(quote.expiryUtc) - Date.now()) / 86_400_000));
  }

  readonly exportChain = () => this.download('option-chain', [
    ['交易所', '品种', '合约', '方向', '到期 (UTC)', '标的期货价', '行权价', '单手资金', '参考 IV', '成交量', '持仓量', '质量', '来源'],
    ...this.chain.filtered().map(quote => [exchangeName(quote.exchange), productName(quote), quote.symbol,
      this.sides[quote.side], quote.expiryUtc, quote.futuresPrice, quote.strike, quote.entryCost, quote.iv,
      quote.volume, quote.openInterest, this.qualities[quote.quality], quote.source])
  ]);

  readonly exportRanking = () => this.download('opportunities', [
    ['品种', '合约', '方向', '到期 (UTC)', '单手资金', '目标期货价', '目标涨跌幅', '情景倍数', '情景盈亏', '相对价差', '参考 IV', '成交量', '持仓量', '来源'],
    ...this.ranking.filtered().map(item => [productName(item.quote), item.quote.symbol, this.sides[item.quote.side],
      item.quote.expiryUtc, item.quote.entryCost, item.target.futuresPrice, item.target.change,
      item.shockScenario.multiple, item.shockScenario.profit, item.spreadRatio, item.quote.iv,
      item.quote.volume, item.quote.openInterest, item.quote.source])
  ]);

  private download(name: string, rows: (string | number | null | undefined)[][]) {
    const body = rows.map(row => row.map(cell => this.csvCell(cell)).join(',')).join('\r\n');
    const url = URL.createObjectURL(new Blob(['\uFEFF', body], {type: 'text/csv;charset=utf-8'}));
    const link = document.createElement('a');
    link.href = url;
    link.download = `${name}-${new Date().toISOString().slice(0, 10)}.csv`;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  private csvCell(value: string | number | null | undefined) {
    if (value === null || value === undefined) return '';
    const text = String(value);
    return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
  }

  private async perform(action: () => Promise<void>) {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    try { await action(); }
    catch (error) { this.error.set(this.errorText(error)); }
    finally { this.busy.set(false); }
  }

  private errorText(error: unknown) {
    if (error instanceof HttpErrorResponse)
      return error.error?.detail || $localize`请求失败（HTTP ${error.status}:status:）。`;
    return error instanceof Error ? error.message : $localize`请求失败，请重试。`;
  }
}

void bootstrapApplication(Dashboard, {
  providers: [provideHttpClient()]
});
