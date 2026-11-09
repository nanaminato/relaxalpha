import type { Quote } from './market';

const exchanges: Readonly<Record<string, string>> = {
  SHFE: $localize`上海期货交易所`,
  INE: $localize`上海国际能源交易中心`,
  DCE: $localize`大连商品交易所`,
  CZCE: $localize`郑州商品交易所`,
  GFEX: $localize`广州期货交易所`
};

// Product codes are scoped to the exchange. The underlying identifies the product for every strike.
const products: Readonly<Record<string, Readonly<Record<string, string>>>> = {
  SHFE: {
    CU: $localize`铜`, AL: $localize`铝`, ZN: $localize`锌`, PB: $localize`铅`, NI: $localize`镍`,
    SN: $localize`锡`, AO: $localize`氧化铝`, AD: $localize`铸造铝合金`, AU: $localize`黄金`,
    AG: $localize`白银`, RB: $localize`螺纹钢`, HC: $localize`热轧卷板`, SS: $localize`不锈钢`,
    FU: $localize`燃料油`, BU: $localize`石油沥青`, BR: $localize`丁二烯橡胶`,
    RU: $localize`天然橡胶`, SP: $localize`纸浆`, OP: $localize`胶版印刷纸`
  },
  INE: {
    SC: $localize`原油`, LU: $localize`低硫燃料油`, NR: $localize`20号胶`, BC: $localize`国际铜`
  },
  DCE: {
    A: $localize`黄大豆1号`, B: $localize`黄大豆2号`, M: $localize`豆粕`, Y: $localize`豆油`,
    P: $localize`棕榈油`, C: $localize`玉米`, CS: $localize`玉米淀粉`, JD: $localize`鸡蛋`,
    LH: $localize`生猪`, I: $localize`铁矿石`, J: $localize`焦炭`, JM: $localize`焦煤`,
    L: $localize`聚乙烯`, V: $localize`聚氯乙烯`, PP: $localize`聚丙烯`,
    EG: $localize`乙二醇`, EB: $localize`苯乙烯`, PG: $localize`液化石油气`
  },
  CZCE: {
    SR: $localize`白糖`, CF: $localize`棉花`, MA: $localize`甲醇`, TA: $localize`精对苯二甲酸（PTA）`,
    RM: $localize`菜籽粕`, OI: $localize`菜籽油`, PK: $localize`花生`, SH: $localize`烧碱`,
    PX: $localize`对二甲苯`, PF: $localize`短纤`, SA: $localize`纯碱`, SF: $localize`硅铁`,
    SM: $localize`锰硅`, FG: $localize`玻璃`, PR: $localize`瓶片`, AP: $localize`苹果`,
    CJ: $localize`红枣`, UR: $localize`尿素`
  },
  GFEX: {
    SI: $localize`工业硅`, LC: $localize`碳酸锂`, PS: $localize`多晶硅`,
    PT: $localize`铂`, PD: $localize`钯`
  }
};

export function exchangeName(exchange: string): string {
  return exchanges[exchange] ?? exchange;
}

export function productName(quote: Pick<Quote, 'exchange' | 'underlying' | 'kind'>): string {
  const underlying = quote.underlying.toUpperCase();
  const prefix = quote.exchange + '.';
  const code = underlying.startsWith(prefix) ? underlying.slice(prefix.length) : underlying;
  const contract = quote.kind === 'Simulated' ? code.replace(/^DEMO-/, '') : code;
  const product = /^[A-Z]{1,3}(?=\d|$)/.exec(contract)?.[0];
  return (product ? products[quote.exchange]?.[product] : undefined) ?? quote.underlying;
}
