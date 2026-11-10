import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type Side = 'Call' | 'Put';
export type SourceKind = 'UserProvided' | 'ExchangePublic' | 'Licensed' | 'Simulated';
export type Quality = 'TwoSided' | 'Indicative' | 'Crossed' | 'DailySettlement';
export type MarketUniverse = 'OfficialDaily' | 'Imported' | 'Simulated';

export interface Quote {
  id: number;
  exchange: string;
  symbol: string;
  underlying: string;
  expiryUtc: string;
  side: Side;
  strike: number;
  futuresPrice: number;
  multiplier: number;
  openFee: number;
  closeFee: number;
  exerciseFee: number;
  bid: number | null;
  ask: number | null;
  premium: number | null;
  entryCost: number | null;
  volume: number;
  openInterest: number;
  quality: Quality;
  source: string;
  kind: SourceKind;
  observedAtUtc: string;
  iv: number | null;
  settlementPrice: number | null;
  tradingDate: string | null;
}
export interface Batch {
  id: string;
  source: string;
  kind: SourceKind;
  observedAtUtc: string;
  importedAtUtc: string;
  count: number;
  tradingDate: string | null;
}
export interface DailyStatus {
  source: string;
  name: string;
  enabled: boolean;
  offline: boolean;
  tradingDate: string | null;
  batchId: string | null;
  lastAttemptUtc: string | null;
  retryAfterUtc: string | null;
  failure: string | null;
}
export interface Settings {
  budget: number;
  targetMultiple: number;
  minDays: number;
  maxDays: number;
  side: Side | null;
  minIv: number;
  maxIv: number;
  minVolume: number;
  minOpenInterest: number;
  maxSpreadRatio: number;
  maxAgeDays: number;
  shock: number;
  // 仅出现在 /api/scans 响应中，表示该次扫描使用的行情范围。
  universe?: MarketUniverse | null;
}
export interface Target {
  futuresPrice: number;
  change: number;
}
export interface Scenario {
  valuation: {
    price: number;
    delta: number | null;
    gamma: number | null;
    thetaPerDay: number | null;
    vegaPerPoint: number | null;
  };
  exitFee: number;
  exitValue: number;
  profit: number;
  multiple: number;
}
export interface Opportunity {
  quote: Quote;
  spreadRatio: number | null;
  target: Target;
  shockScenario: Scenario;
  reachesTarget: boolean;
}
export interface Scan {
  asOfUtc: string;
  settings: Settings;
  evaluated: number;
  matched: number;
  opportunities: Opportunity[];
}
export interface LiveQuote {
  symbol: string;
  state: 'pending' | 'ready' | 'unavailable';
  quoteTime?: string;
  stale?: boolean;
  lastPrice?: number | null;
  bid?: number | null;
  ask?: number | null;
  volume?: number | null;
  openInterest?: number | null;
}

export interface LiveQuoteBatch {
  source: 'TqSdk';
  state: 'connecting' | 'ready' | 'reconnecting';
  quotes: LiveQuote[];
}

export interface Simulation {
  scenario: Scenario;
  targets: { multiple: number; condition: Target | null }[];
}

@Injectable({ providedIn: 'root' })
export class MarketApi {
  private readonly http = inject(HttpClient);
  market(universe: MarketUniverse) {
    return firstValueFrom(this.http.get<Quote[]>('/api/market', {params: {universe}}));
  }
  sources() {
    return firstValueFrom(this.http.get<Batch[]>('/api/sources'));
  }
  daily() {
    return firstValueFrom(this.http.get<DailyStatus[]>('/api/sources/daily'));
  }
  scheduled() {
    return firstValueFrom(this.http.get<Scan | null>('/api/scans/scheduled'));
  }
  defaults() {
    return firstValueFrom(this.http.get<Settings>('/api/scans/defaults'));
  }
  scan(settings: Settings, universe: MarketUniverse) {
    return firstValueFrom(this.http.post<Scan>('/api/scans', {...settings, universe}));
  }
  live(quoteId: number) {
    return firstValueFrom(this.http.post<LiveQuoteBatch>(`/api/live/quotes/${quoteId}`, {}));
  }
  simulate(quoteId: number, futuresPrice: number, remainingDays: number, volatility: number, targets: number[]) {
    return firstValueFrom(this.http.post<Simulation>('/api/simulate', {
      quoteId, futuresPrice, remainingDays, volatility, targets
    }));
  }
  import(file: File, source: string, kind: SourceKind, observedAt: string) {
    return firstValueFrom(this.http.post<Batch>('/api/sources/import', file, {
      headers: {'Content-Type': 'text/csv'}, params: {source, kind, observedAt}
    }));
  }
  demo() {
    return firstValueFrom(this.http.post<Batch>('/api/sources/demo', {}));
  }
}
