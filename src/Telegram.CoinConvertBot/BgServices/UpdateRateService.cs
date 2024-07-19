using Flurl;
using Flurl.Http;
using FreeSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.CoinConvertBot.BgServices.Base;
using Telegram.CoinConvertBot.Domains.Tables;
using Telegram.CoinConvertBot.Helper;

namespace Telegram.CoinConvertBot.BgServices
{
    public class UpdateRateService : BaseScheduledService
    {
        const string baseUrl = "https://www.okx.com";
        const string User_Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/104.0.0.0 Safari/537.36";
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<UpdateRateService> _logger;
        private readonly FlurlClient client;

        public UpdateRateService(
            IConfiguration configuration,
            IServiceProvider serviceProvider,
            ILogger<UpdateRateService> logger) : base("更新汇率", TimeSpan.FromMinutes(1), logger)
        {
            _configuration = configuration;
            _serviceProvider = serviceProvider;
            _logger = logger;
            var WebProxy = configuration.GetValue<string>("WebProxy");
            client = new FlurlClient();
            client.Settings.Timeout = TimeSpan.FromSeconds(15);
            if (!string.IsNullOrEmpty(WebProxy))
            {
                client.Settings.HttpClientFactory = new ProxyHttpClientFactory(WebProxy);
            }
        }

        protected override async Task ExecuteAsync()
        {
            var list = new List<TokenRate>();
            _logger.LogInformation("------------------{tips}------------------", "开始更新汇率");
            using IServiceScope scope = _serviceProvider.CreateScope();
            var _repository = scope.ServiceProvider.GetRequiredService<IBaseRepository<TokenRate>>();

            // 设置默认固定汇率
            decimal fixedRate = 0m;  // 可以在这里修改固定汇率

            if (fixedRate > 0)
            {
                // 使用固定汇率
                list.Add(new TokenRate
                {
                    Id = $"USDT_{Currency.TRX}",
                    Currency = Currency.USDT,
                    ConvertCurrency = Currency.TRX,
                    LastUpdateTime = DateTime.Now,
                    Rate = fixedRate,
                    ReverseRate = 1m / fixedRate,
                });
                _logger.LogInformation("使用固定汇率，USDT -> TRX = {Rate}", fixedRate);
            }
            else
            {
                // 获取配置中的实时汇率
                var rate = _configuration.GetValue("TrxRate", 0m);
                if (rate > 0)
                {
                    list.Add(new TokenRate
                    {
                        Id = $"USDT_{Currency.TRX}",
                        Currency = Currency.USDT,
                        ConvertCurrency = Currency.TRX,
                        LastUpdateTime = DateTime.Now,
                        Rate = rate,
                        ReverseRate = 1m / rate,
                    });
                }
                else
                {
                    // 从OKX获取汇率
                    await FetchRateFromOkx(list);
                    // 如果从OKX获取汇率失败，则尝试从币安获取
                    if (!list.Any())
                    {
                        await FetchRateFromBinance(list);
                    }
                }
            }

            foreach (var item in list)
            {
                _logger.LogInformation("更新汇率，{a} -> {b} = {c}", item.Currency, item.ConvertCurrency, item.Rate);
                await _repository.InsertOrUpdateAsync(item);
            }
            _logger.LogInformation("------------------{tips}------------------", "结束更新汇率");
        }

        private async Task FetchRateFromOkx(List<TokenRate> list)
        {
            var side = "buy";
            try
            {
                var convert1 = await baseUrl
                    .WithClient(client)
                    .WithHeaders(new { User_Agent })
                    .AppendPathSegment("v2/asset/quick/exchange/quote")
                    .PostJsonAsync(new
                    {
                        side,
                        baseCcy = Currency.TRX.ToString(),
                        quoteCcy = Currency.USDT.ToString(),
                        rfqSz = 1,
                        rfqSzCcy = Currency.USDT.ToString(),
                    })
                    .ReceiveJson<Root>();
                if (convert1.code == 0)
                {
                    list.Add(new TokenRate
                    {
                        Id = $"USDT_{Currency.TRX}",
                        Currency = Currency.USDT,
                        ConvertCurrency = Currency.TRX,
                        LastUpdateTime = DateTime.Now,
                        Rate = convert1.data.askBaseSz,
                        ReverseRate = convert1.data.askPx,
                    });
                    _logger.LogInformation("OKX汇率，USDT -> TRX = {Rate}", convert1.data.askBaseSz);
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning("TRX -> USDT 汇率获取失败！错误信息：{msg}", e?.InnerException?.Message + "; " + e?.Message);
            }
        }

        private async Task FetchRateFromBinance(List<TokenRate> list)
        {
            try
            {
                string binanceBaseUrl = "https://api.binance.com";
                var response = await binanceBaseUrl
                    .AppendPathSegment("/api/v3/ticker/price")
                    .SetQueryParams(new { symbol = "TRXUSDT" })
                    .GetJsonAsync<BinanceRateResponse>();

                if (response != null && decimal.TryParse(response.price, out decimal binanceRate) && binanceRate > 0)
                {
                    decimal reverseRate = 1m / binanceRate; // 计算汇率的倒数
                    list.Add(new TokenRate
                    {
                        Id = $"USDT_{Currency.TRX}",
                        Currency = Currency.USDT,
                        ConvertCurrency = Currency.TRX,
                        LastUpdateTime = DateTime.Now,
                        Rate = reverseRate, // 使用倒数作为汇率
                        ReverseRate = binanceRate,
                    });
                    _logger.LogInformation("币安汇率，USDT -> TRX = {Rate}", reverseRate); // 使用倒数值记录日志
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning("从币安获取TRX -> USDT 汇率失败！错误信息：{msg}", e.Message);
            }
        }
    }

    class Datum
    {
        public Currency baseCcy { get; set; }
        public Currency quoteCcy { get; set; }
        public decimal askPx { get; set; }
        public decimal askQuoteSz { get; set; }
        public decimal askBaseSz { get; set; }
    }

    class BinanceRateResponse
    {
        public string symbol { get; set; }
        public string price { get; set; }
    }

    class Root
    {
        public int code { get; set; }
        public Datum data { get; set; }
        public string detailMsg { get; set; }
        public string error_code { get; set; }
        public string error_message { get; set; }
        public string msg { get; set; }
    }

    enum OkxSide
    {
        Buy,
        Sell
    }
}
