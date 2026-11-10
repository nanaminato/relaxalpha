import { computed, signal, type Signal } from '@angular/core';
import type { Quote, Side } from './market';
import { exchangeName, productName } from './product-names';

interface BrowseFilters {
  search: string;
  product: string;
  side: Side | '';
}

const defaults: BrowseFilters = {search: '', product: '', side: ''};
const collator = new Intl.Collator('zh-CN', {numeric: true});
// 与 productName 共用对象缓存，筛选与分组时不必重复拼串。
const productKeys = new WeakMap<Quote, string>();
export const productKey = (quote: Quote) => {
  const cached = productKeys.get(quote);
  if (cached !== undefined) return cached;
  const key = quote.exchange + ':' + productName(quote);
  productKeys.set(quote, key);
  return key;
};

// text 需为大写。浏览筛选与模拟器合约查找共用同一套匹配规则。
export const matchesQuery = (quote: Quote, text: string) =>
  !text || [quote.symbol, quote.underlying, quote.exchange, productName(quote), exchangeName(quote.exchange)]
    .some(label => label.toUpperCase().includes(text));

export function browseList<T>(items: Signal<readonly T[]>, quoteOf: (item: T) => Quote, pageSize: number) {
  const filters = signal(defaults);
  const requestedPage = signal(0);
  const products = computed(() => {
    const distinct = new Map<string, string>();
    for (const item of items()) {
      const quote = quoteOf(item);
      const key = productKey(quote);
      if (!distinct.has(key)) distinct.set(key, productName(quote));
    }
    return [...distinct].map(([key, name]) => ({key, name}))
      .sort((a, b) => collator.compare(a.name, b.name) || a.key.localeCompare(b.key));
  });
  const filtered = computed(() => {
    const {search, product, side} = filters();
    const text = search.trim().toUpperCase();
    if (!text && !product && !side) return items();
    return items().filter(item => {
      const quote = quoteOf(item);
      if (product && productKey(quote) !== product) return false;
      if (side && quote.side !== side) return false;
      return matchesQuery(quote, text);
    });
  });
  const pages = computed(() => Array.from({length: Math.ceil(filtered().length / pageSize)}, (_, index) => index));
  const page = computed(() => Math.min(requestedPage(), Math.max(0, pages().length - 1)));
  const rows = computed(() => filtered().slice(page() * pageSize, (page() + 1) * pageSize));
  const range = computed(() => {
    const start = page() * pageSize;
    return {first: rows().length ? start + 1 : 0, last: start + rows().length, total: filtered().length};
  });
  const active = computed(() => Boolean(filters().search || filters().product || filters().side));

  return {
    filters, products, filtered, pages, page, rows, range, active,
    goTo(index: number) { requestedPage.set(Math.max(0, Math.min(index, pages().length - 1))); },
    set<K extends keyof BrowseFilters>(key: K, value: BrowseFilters[K]) {
      filters.update(previous => ({...previous, [key]: value}));
      requestedPage.set(0);
    },
    clear() {
      filters.set(defaults);
      requestedPage.set(0);
    },
    reconcile(resetPage = false) {
      const selected = filters().product;
      if (selected && !products().some(p => p.key === selected)) {
        filters.update(previous => ({...previous, product: ''}));
        resetPage = true;
      }
      requestedPage.set(resetPage ? 0 : page());
    }
  };
}
