import { DatePipe, DecimalPipe, NgTemplateOutlet, PercentPipe } from '@angular/common';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { bootstrapApplication } from '@angular/platform-browser';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Batch, DailyStatus, LiveQuoteBatch, MarketApi, MarketUniverse, Opportunity, Quality, Quote, Scan, Settings, Simulation, SourceKind } from './market';
import { browseList, productKey } from './browse-list';
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
type RankingColumn = 'cost' | 'change' | 'multiple';
type SortDirection = 'ascending' | 'descending';

interface RankingGroup {
  key: string;
  quote: Quote;
  items: Opportunity[];
}

type RankingRow = {key: string; kind: 'group'; group: RankingGroup; expanded: boolean}
  | {key: number; kind: 'contract'; item: Opportunity};

const rankingValue: Record<RankingColumn, (item: Opportunity) => number | null> = {
  cost: item => item.quote.entryCost,
  change: item => item.target.change,
  multiple: item => item.shockScenario.multiple
};

const defaults: Settings = {
  budget: 1000, targetMultiple: 5, minDays: 30, maxDays: 90, side: null, minIv: 0, maxIv: 5,
  minVolume: 10, minOpenInterest: 50, maxSpreadRatio: 0.3, maxAgeDays: 3, shock: 0.1
};

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
  readonly daily = signal<DailyStatus | null>(null);
  readonly dailyMessage = computed(() => {
    const daily = this.daily();
    if (!daily) return $localize`正在读取日行情状态…`;
    if (!daily.tradingDate) return $localize`当前日期超出已配置的交易日历。`;
    if (daily.batchId) return $localize`已入库 · ${daily.tradingDate}:date:`;
    if (!daily.enabled) return $localize`采集已关闭 · 目标交易日 ${daily.tradingDate}:date:`;
    if (daily.failure) return $localize`采集失败，等待下次重试 · ${daily.tradingDate}:date:`;
    return $localize`等待入库 · ${daily.tradingDate}:date:`;
  });
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
  readonly expandedProducts = signal<ReadonlySet<string>>(new Set());
  readonly rankingHeaders = [
    {column: 'cost', title: $localize`单手资金`, detail: $localize`本金风险`},
    {column: 'change', title: $localize`目标涨跌幅`, detail: $localize`目标到期价`},
    {column: 'multiple', title: $localize`情景净回收倍数`, detail: $localize`情景盈亏`}
  ] as const;
  readonly ranking = browseList(computed(() => {
    const items = this.scan()?.opportunities ?? [];
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
  readonly quotable = computed(() => this.quotes().filter(q => (q.settlementPrice ?? q.ask ?? 0) > 0 && q.quality !== 'Crossed'));
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
  settings = {...defaults};
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
    this.hub.on('MarketUpdated', () => void this.refresh());
    this.hub.onreconnecting(() => this.connected.set(false));
    this.hub.onclose(() => this.connected.set(false));
    this.hub.onreconnected(() => {
      this.connected.set(true);
      void this.refresh();
    });
    void this.hub.start().then(() => this.connected.set(true), () => this.connected.set(false));
    destroy.onDestroy(() => void this.hub.stop());
    void this.refresh();
  }

  navigate(tab: Tab) {
    this.active.set(tab);
    this.error.set('');
    this.notice.set('');
  }

  changeUniverse(universe: MarketUniverse) {
    if (universe === this.universe()) return;
    this.selectUniverse(universe);
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
    try {
      const [quotes, batches, scheduled, daily] = await Promise.all([
        this.api.market(this.universe()), this.api.sources(), this.api.scheduled(), this.api.daily()
      ]);
      if (version !== this.refreshVersion) return;
      this.quotes.set(quotes);
      this.batches.set(batches);
      this.scheduled.set(scheduled);
      this.daily.set(daily);
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

  setAllProductsExpanded(expanded: boolean) {
    this.expandedProducts.set(new Set(expanded ? this.rankingGroups().map(group => group.key) : []));
  }

  resetSettings() {
    this.settings = {...defaults};
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
    if (!quote) return;
    this.futuresPrice = quote.futuresPrice;
    this.remainingDays = 0;
    this.volatility = quote.iv ?? 0.25;
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
