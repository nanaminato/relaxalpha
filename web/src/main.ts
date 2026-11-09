import { DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { bootstrapApplication } from '@angular/platform-browser';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Batch, DailyStatus, MarketApi, MarketUniverse, Quality, Quote, Scan, Settings, Simulation, SourceKind } from './market';

const tabs = [
  {id: 'overview', number: '01', name: $localize`市场总览`},
  {id: 'chain', number: '02', name: $localize`期权链`},
  {id: 'ranking', number: '03', name: $localize`机会排行榜`},
  {id: 'simulator', number: '04', name: $localize`情景模拟器`},
  {id: 'strategy', number: '05', name: $localize`策略配置`},
  {id: 'sources', number: '06', name: $localize`数据源管理`},
] as const;
type Tab = typeof tabs[number]['id'];

const defaults: Settings = {
  budget: 1000, targetMultiple: 5, minDays: 30, maxDays: 90, side: null, minIv: 0, maxIv: 5,
  minVolume: 10, minOpenInterest: 50, maxSpreadRatio: 0.3, maxAgeDays: 3, shock: 0.1
};

@Component({
  selector: 'ra-app',
  imports: [FormsModule, DatePipe, DecimalPipe, PercentPipe],
  templateUrl: './dashboard.html'
})
class Dashboard {
  private readonly api = inject(MarketApi);
  private readonly hub = new HubConnectionBuilder().withUrl('/hubs/market')
    .withAutomaticReconnect().configureLogging(LogLevel.Warning).build();
  private refreshVersion = 0;

  readonly tabs = tabs;
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
  readonly search = signal('');
  readonly page = signal(0);
  readonly filtered = computed(() => {
    const text = this.search().trim().toUpperCase();
    return this.quotes().filter(q => [q.symbol, q.underlying, q.exchange].some(code => code.includes(text)));
  });
  readonly chainPage = computed(() => this.filtered().slice(this.page() * 30, (this.page() + 1) * 30));
  readonly pageCount = computed(() => Math.max(1, Math.ceil(this.filtered().length / 30)));
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
    this.selected.set(null);
    this.simulation.set(null);
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
      this.page.set(Math.min(this.page(), this.pageCount() - 1));
    } catch (error) {
      if (version === this.refreshVersion) this.marketError.set(this.errorText(error));
    } finally {
      if (version === this.refreshVersion) this.marketLoading.set(false);
    }
  }

  searchChain(value: string) {
    this.search.set(value);
    this.page.set(0);
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
    if (!quote) return;
    this.futuresPrice = quote.futuresPrice;
    this.remainingDays = 0;
    this.volatility = quote.iv ?? 0.25;
  }

  openSimulation(quote: Quote) {
    this.selectQuote(quote);
    this.navigate('simulator');
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
