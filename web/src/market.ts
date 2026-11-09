import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type Side = 'Call' | 'Put';
export type SourceKind = 'UserProvided' | 'ExchangePublic' | 'Licensed' | 'Simulated';
export type Quality = 'TwoSided' | 'Indicative' | 'Crossed';

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
}
export interface Batch {
  id: string;
  source: string;
  kind: SourceKind;
  observedAtUtc: string;
  importedAtUtc: string;
  count: number;
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
export interface Scan {
  asOfUtc: string;
  settings: Settings;
  evaluated: number;
  matched: number;
  opportunities: {
    quote: Quote;
    spreadRatio: number;
    target: Target;
    shockScenario: Scenario;
    reachesTarget: boolean;
  }[];
}
export interface Simulation {
  scenario: Scenario;
  targets: { multiple: number; condition: Target | null }[];
}

@Injectable({ providedIn: 'root' })
export class MarketApi {
  private readonly http = inject(HttpClient);
  market(simulated: boolean) {
    return firstValueFrom(this.http.get<Quote[]>('/api/market', {params: {simulated}}));
  }
  sources() {
    return firstValueFrom(this.http.get<Batch[]>('/api/sources'));
  }
  scheduled() {
    return firstValueFrom(this.http.get<Scan | null>('/api/scans/scheduled'));
  }
  scan(settings: Settings, simulated: boolean) {
    return firstValueFrom(this.http.post<Scan>('/api/scans', {...settings, simulated}));
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
