using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace StockMonitor
{
    public partial class MainWindow : Window
    {
        // ─── 共有 HttpClient（シングルトン） ──────────────────────────────────
        // HttpClient は使い捨てにすると接続枯渇が起きるため static で共有する
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient(new HttpClientHandler
            {
                UseCookies            = false,
                AllowAutoRedirect     = true,
                MaxAutomaticRedirections = 3
            });
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept", "application/json, text/html, */*");
            return client;
        }

        // ─── フィールド ───────────────────────────────────────────────────────
        private readonly ObservableCollection<StockItem> _stocks = new();
        private CancellationTokenSource? _cts;
        private bool _isRunning = false;
        private StockItem? _selectedItem = null;

        // アラート重複防止
        private readonly Dictionary<string, string> _alertedState = new();

        // 急騰・急落用価格履歴
        private readonly Dictionary<string, Queue<(DateTime time, double price)>> _priceHistory = new();

        // 出来高急増用: 前日出来高キャッシュ (symbol -> previousVolume)
        private readonly Dictionary<string, long> _prevDayVolume = new();

        private int _intervalSeconds = 30;

        private static readonly string SavePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "StockMonitor", "settings.json");

        // ─── Cookie / Crumb 管理 ──────────────────────────────────────────────
        private string _crumb  = "";
        private string _cookie = "";
        private readonly SemaphoreSlim _crumbLock = new(1, 1);

        // ─── 初期化 ───────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            _stocks.CollectionChanged += (_, _) => RefreshStockPanel();
            LoadSettings();
        }

        // ─── 設定保存・読込 ───────────────────────────────────────────────────
        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SavePath)) return;
                var json = File.ReadAllText(SavePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("webhook", out var w))
                    WebhookInput.Text = w.GetString() ?? "";
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
                var json = JsonSerializer.Serialize(new { webhook = WebhookInput.Text.Trim() });
                File.WriteAllText(SavePath, json);
            }
            catch { }
        }

        private void WebhookInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            SaveSettings();
            WebhookSaveStatus.Text = "✓ 保存済み";
        }

        private void ClearWebhook_Click(object sender, RoutedEventArgs e)
        {
            WebhookInput.Text = "";
            WebhookSaveStatus.Text = "";
            SaveSettings();
        }

        // ─── Cookie / Crumb 取得 ─────────────────────────────────────────────
        private async Task EnsureCrumbAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_crumb)) return;
            await _crumbLock.WaitAsync(ct);
            try
            {
                if (!string.IsNullOrEmpty(_crumb)) return;

                // Step1: Yahoo Finance にアクセスして cookie を取得
                var req1 = new HttpRequestMessage(HttpMethod.Get, "https://finance.yahoo.com");
                var resp1 = await _http.SendAsync(req1, ct);
                var cookieList = new List<string>();
                if (resp1.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    foreach (var c in setCookies)
                        cookieList.Add(c.Split(';')[0]);
                _cookie = string.Join("; ", cookieList);

                // Step2: crumb 取得
                var req2 = new HttpRequestMessage(HttpMethod.Get,
                    "https://query1.finance.yahoo.com/v1/test/getcrumb");
                if (!string.IsNullOrEmpty(_cookie))
                    req2.Headers.TryAddWithoutValidation("Cookie", _cookie);
                var resp2 = await _http.SendAsync(req2, ct);
                _crumb = (await resp2.Content.ReadAsStringAsync(ct)).Trim();
            }
            finally
            {
                _crumbLock.Release();
            }
        }

        // ─── UIイベント ───────────────────────────────────────────────────────
        private void AlertMode_Changed(object sender, RoutedEventArgs e)
        {
            if (PriceModePanel == null || PercentModePanel == null) return;
            bool priceMode = RadioPrice.IsChecked == true;
            PriceModePanel.Visibility   = priceMode ? Visibility.Visible  : Visibility.Collapsed;
            PercentModePanel.Visibility = priceMode ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AddButton_Click(object sender, RoutedEventArgs e) => AddTicker();
        private void TickerInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) AddTicker();
        }

        private void AddTicker()
        {
            var raw = TickerInput.Text.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(raw)) return;

            string symbol;
            if (raw.Contains('.'))
                symbol = raw;
            else if (Regex.IsMatch(raw, @"^\d{3,5}$"))
                symbol = raw + ".T";
            else if (Regex.IsMatch(raw, @"^\d{3}[A-Z]$")  ||
                     Regex.IsMatch(raw, @"^\d[A-Z]\d{2}$") ||
                     Regex.IsMatch(raw, @"^\d[A-Z]\d[A-Z]$"))
                symbol = raw + ".T";
            else
                symbol = raw;

            if (_stocks.Any(s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("すでに追加済みです。", "重複", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _stocks.Add(new StockItem { Symbol = symbol, DisplayName = symbol });
            TickerInput.Text = string.Empty;
            TickerInput.Focus();

            if (_isRunning)
                _ = FetchSingleAsync(symbol, CancellationToken.None);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedItem == null) return;
            var sym = _selectedItem.Symbol;
            _stocks.Remove(_selectedItem);
            _alertedState.Remove(sym + "_high");
            _alertedState.Remove(sym + "_low");
            _alertedState.Remove(sym + "_surge_up");
            _alertedState.Remove(sym + "_surge_down");
            _alertedState.Remove(sym + "_volume");
            _priceHistory.Remove(sym);
            _prevDayVolume.Remove(sym);
            SelectItem(null);
        }

        private void SelectItem(StockItem? item)
        {
            if (_selectedItem != null && _selectedItem != item)
                _selectedItem.IsSelected = false;
            _selectedItem = item;
            if (_selectedItem != null)
                _selectedItem.IsSelected = true;
            DeleteButton.IsEnabled = (_selectedItem != null);
        }

        // ─── 銘柄パネル構築 ───────────────────────────────────────────────────
        private void RefreshStockPanel()
        {
            if (_selectedItem != null && !_stocks.Contains(_selectedItem))
                SelectItem(null);

            StockPanel.Children.Clear();

            var jpStocks = _stocks.Where(s => s.Symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase)).ToList();
            var usStocks = _stocks.Where(s => !s.Symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase)).ToList();

            if (jpStocks.Count > 0)
            {
                StockPanel.Children.Add(MakeGroupHeader("🇯🇵 国内株（東証）", jpStocks.Count));
                foreach (var item in jpStocks)
                    StockPanel.Children.Add(MakeStockCard(item));
            }

            if (usStocks.Count > 0)
            {
                StockPanel.Children.Add(MakeGroupHeader("🇺🇸 米国株・その他", usStocks.Count));
                foreach (var item in usStocks)
                    StockPanel.Children.Add(MakeStockCard(item));
            }

            StockCountText.Text = $"{_stocks.Count} 銘柄";
        }

        private TextBlock MakeGroupHeader(string title, int count) =>
            new() { Style = (Style)FindResource("GroupHeader"), Text = $"{title}  ({count})" };

        private Border MakeStockCard(StockItem item)
        {
            var card = new Border
            {
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(14, 12, 14, 12),
                Margin          = new Thickness(0, 0, 0, 6),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand
            };
            card.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderColor") { Source = item, Converter = new ColorStringToBrushConverter() });
            card.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("BackgroundColor") { Source = item, Converter = new ColorStringToBrushConverter() });
            card.MouseLeftButtonUp += (_, _) => SelectItem(_selectedItem == item ? null : item);

            var outer = new StackPanel();

            // Row1: 銘柄名 + 価格
            var row1 = new Grid();
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftTop  = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameRow  = new StackPanel { Orientation = Orientation.Horizontal };
            var nameBlock = new TextBlock { Foreground = Brushes.WhiteSmoke, FontSize = 14, FontWeight = FontWeights.Bold };
            nameBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DisplayName") { Source = item });

            var exchangeBadge = new Border
            {
                CornerRadius      = new CornerRadius(3),
                Background        = new SolidColorBrush(Color.FromRgb(30, 40, 60)),
                Padding           = new Thickness(5, 1, 5, 1),
                Margin            = new Thickness(6, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var exchText = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(88, 166, 255)) };
            exchText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Exchange") { Source = item });
            exchangeBadge.Child = exchText;

            var statusBlock = new TextBlock { FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
            statusBlock.SetBinding(TextBlock.TextProperty,       new System.Windows.Data.Binding("StatusMessage") { Source = item });
            statusBlock.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("StatusColor")   { Source = item, Converter = new ColorStringToBrushConverter() });

            nameRow.Children.Add(nameBlock);
            nameRow.Children.Add(exchangeBadge);
            leftTop.Children.Add(nameRow);
            leftTop.Children.Add(statusBlock);

            var rightTop   = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var priceBlock = new TextBlock { FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right };
            priceBlock.SetBinding(TextBlock.TextProperty,       new System.Windows.Data.Binding("PriceText")  { Source = item });
            priceBlock.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("PriceColor") { Source = item, Converter = new ColorStringToBrushConverter() });

            var changeBlock = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
            changeBlock.SetBinding(TextBlock.TextProperty,       new System.Windows.Data.Binding("ChangeText")  { Source = item });
            changeBlock.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("ChangeColor") { Source = item, Converter = new ColorStringToBrushConverter() });

            rightTop.Children.Add(priceBlock);
            rightTop.Children.Add(changeBlock);

            Grid.SetColumn(leftTop,  0);
            Grid.SetColumn(rightTop, 1);
            row1.Children.Add(leftTop);
            row1.Children.Add(rightTop);
            outer.Children.Add(row1);

            // 区切り線
            outer.Children.Add(Divider());

            // Row2: 詳細グリッド
            var detailGrid = new UniformGrid { Columns = 3, Rows = 2 };
            detailGrid.Children.Add(MakeDetailCell("始値",    item, "OpenText"));
            detailGrid.Children.Add(MakeDetailCell("高値",    item, "HighText"));
            detailGrid.Children.Add(MakeDetailCell("安値",    item, "LowText"));
            detailGrid.Children.Add(MakeDetailCell("出来高",  item, "VolumeText"));
            detailGrid.Children.Add(MakeDetailCell("時価総額", item, "MarketCap"));
            detailGrid.Children.Add(MakeDetailCell("PER",     item, "PeRatio"));
            outer.Children.Add(detailGrid);

            // 区切り線
            outer.Children.Add(Divider());

            // Row3: 52週レンジ
            var rangeStack = new StackPanel { Orientation = Orientation.Horizontal };
            rangeStack.Children.Add(MakeRangeLabel("52週安値 "));
            var w52Low = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(248, 81, 73)) };
            w52Low.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Week52Low") { Source = item });
            rangeStack.Children.Add(w52Low);
            rangeStack.Children.Add(MakeRangeLabel("  〜  52週高値 "));
            var w52High = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(63, 185, 80)) };
            w52High.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Week52High") { Source = item });
            rangeStack.Children.Add(w52High);
            outer.Children.Add(rangeStack);

            card.Child = outer;
            return card;
        }

        private static Border Divider() => new()
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Margin     = new Thickness(0, 8, 0, 8)
        };

        private static TextBlock MakeRangeLabel(string text) => new()
        {
            Text       = text,
            Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
            FontSize   = 10
        };

        private static StackPanel MakeDetailCell(string label, StockItem item, string bindPath)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 2, 8, 4) };
            cell.Children.Add(new TextBlock
            {
                Text       = label,
                FontSize   = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158))
            });
            var val = new TextBlock { FontSize = 12, Foreground = Brushes.WhiteSmoke, FontWeight = FontWeights.SemiBold };
            val.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(bindPath) { Source = item });
            cell.Children.Add(val);
            return cell;
        }

        // ─── 監視スタート/ストップ ────────────────────────────────────────────
        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_stocks.Count == 0)
            {
                MessageBox.Show("銘柄を1つ以上追加してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _intervalSeconds = int.TryParse(IntervalInput.Text, out var secs) && secs >= 10 ? secs : 30;
            IntervalInput.Text = _intervalSeconds.ToString();
            IntervalHint.Text = $"{_intervalSeconds}秒ごとに全銘柄を取得します";

            _isRunning = true;
            _cts = new CancellationTokenSource();
            StartButton.IsEnabled = false;
            StopButton.IsEnabled  = true;
            SetStatus("監視中", "#3FB950");
            _ = MonitorLoopAsync(_cts.Token);
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _isRunning = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled  = false;
            SetStatus("停止中", "#F85149");
        }

        private async Task MonitorLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await FetchAllAsync(ct);
                try { await Task.Delay(_intervalSeconds * 1000, ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        private async Task FetchAllAsync(CancellationToken ct)
        {
            var symbols = _stocks.Select(s => s.Symbol).ToList();
            await Task.WhenAll(symbols.Select(sym => FetchSingleAsync(sym, ct)));
            Dispatcher.Invoke(() =>
                LastUpdateText.Text = $"最終更新: {DateTime.Now:HH:mm:ss}");
        }

        // ─── 株価取得（v8 chart + v11 quoteSummary 組み合わせ） ──────────────
        private async Task FetchSingleAsync(string symbol, CancellationToken ct)
        {
            try
            {
                // ── v8 chart で基本情報取得（crumb不要） ──
                var chartUrl = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?interval=1d&range=1d&includePrePost=true";
                var chartResp = await _http.GetAsync(chartUrl, ct);
                if (!chartResp.IsSuccessStatusCode)
                {
                    UpdateDisplay(symbol, null, null, null, $"HTTP {(int)chartResp.StatusCode}");
                    return;
                }

                var chartJson = await chartResp.Content.ReadAsStringAsync(ct);
                using var chartDoc = JsonDocument.Parse(chartJson);
                var meta = chartDoc.RootElement
                    .GetProperty("chart")
                    .GetProperty("result")[0]
                    .GetProperty("meta");

                double price     = meta.GetProperty("regularMarketPrice").GetDouble();
                double prevClose = meta.GetProperty("chartPreviousClose").GetDouble();

                string marketState = meta.TryGetProperty("marketState", out var ms) ? (ms.GetString() ?? "") : "";
                string priceLabel  = MarketStateLabel(marketState);

                double displayPrice = price;
                if (marketState == "PRE"  && meta.TryGetProperty("preMarketPrice",  out var pre)  && pre.ValueKind  == JsonValueKind.Number)
                    displayPrice = pre.GetDouble();
                else if (marketState == "POST" && meta.TryGetProperty("postMarketPrice", out var post) && post.ValueKind == JsonValueKind.Number)
                    displayPrice = post.GetDouble();

                string name = symbol;
                if (meta.TryGetProperty("shortName", out var sn) && !string.IsNullOrEmpty(sn.GetString()))
                    name = sn.GetString()!;

                double changePct = prevClose != 0.0
                    ? (displayPrice - prevClose) / prevClose * 100.0
                    : 0.0;

                bool   isJp = symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase);
                string unit = isJp ? "円" : "$";
                string fmt  = isJp ? "N0" : "F2";

                // v8 meta から取れる基本詳細
                var detail = new StockDetail { MarketState = priceLabel };
                if (meta.TryGetProperty("regularMarketDayHigh",   out var hi))  detail.DayHigh = unit + hi.GetDouble().ToString(fmt);
                if (meta.TryGetProperty("regularMarketDayLow",    out var lo))  detail.DayLow  = unit + lo.GetDouble().ToString(fmt);
                if (meta.TryGetProperty("regularMarketOpen",      out var op))  detail.Open    = unit + op.GetDouble().ToString(fmt);
                if (meta.TryGetProperty("exchangeName",            out var exn)) detail.Exchange = exn.GetString() ?? "--";
                if (meta.TryGetProperty("currency",                out var cur)) detail.Currency = cur.GetString() ?? "--";

                long currentVolume = 0;
                if (meta.TryGetProperty("regularMarketVolume", out var vol))
                {
                    currentVolume = vol.GetInt64();
                    detail.Volume = FormatVolume(currentVolume, isJp);
                }

                // ── v11 quoteSummary で追加情報（MarketCap / PER / 52週高安値）取得 ──
                await TryFetchQuoteSummaryAsync(symbol, detail, isJp, fmt, unit, ct);

                RecordPriceHistory(symbol, displayPrice);
                UpdateDisplay(symbol, displayPrice, prevClose, changePct, null, name, detail);
                await CheckAlertAsync(symbol, displayPrice, prevClose, changePct, currentVolume, name, ct);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                var msg = ex.Message.Length > 60 ? ex.Message[..60] + "…" : ex.Message;
                UpdateDisplay(symbol, null, null, null, msg);
            }
        }

        /// <summary>
        /// v11 quoteSummary エンドポイントから MarketCap / PER / 52週高安値 を取得して detail に詰める。
        /// 失敗しても例外を投げずに無視する（基本情報は v8 で取得済みのため）。
        /// </summary>
        private async Task TryFetchQuoteSummaryAsync(
            string symbol, StockDetail detail, bool isJp, string fmt, string unit, CancellationToken ct)
        {
            try
            {
                // crumb が未取得なら取得を試みる
                if (string.IsNullOrEmpty(_crumb))
                    await EnsureCrumbAsync(ct);

                var summaryUrl = $"https://query1.finance.yahoo.com/v11/finance/quoteSummary/{Uri.EscapeDataString(symbol)}" +
                                 $"?modules=summaryDetail,price&crumb={Uri.EscapeDataString(_crumb)}";

                var req = new HttpRequestMessage(HttpMethod.Get, summaryUrl);
                if (!string.IsNullOrEmpty(_cookie))
                    req.Headers.TryAddWithoutValidation("Cookie", _cookie);

                var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("quoteSummary", out var qs)) return;
                if (!qs.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array) return;
                var result = results[0];

                if (result.TryGetProperty("price", out var pr))
                {
                    double mc = GetRaw(pr, "marketCap");
                    if (mc > 0) detail.MarketCap = FormatLargeNum(mc, isJp);

                    double pe = GetRaw(pr, "trailingPE");
                    if (pe > 0) detail.PeRatio = pe.ToString("F1") + "x";
                }

                if (result.TryGetProperty("summaryDetail", out var sd))
                {
                    double w52h = GetRaw(sd, "fiftyTwoWeekHigh");
                    double w52l = GetRaw(sd, "fiftyTwoWeekLow");
                    if (w52h > 0) detail.Week52High = unit + w52h.ToString(fmt);
                    if (w52l > 0) detail.Week52Low  = unit + w52l.ToString(fmt);

                    // 前日出来高をキャッシュ（出来高急増判定用）
                    double avgVol = GetRaw(sd, "averageVolume");
                    if (avgVol > 0)
                        _prevDayVolume[symbol] = (long)avgVol;
                }
            }
            catch { /* quoteSummary は補助情報なので失敗しても続行 */ }
        }

        private static string MarketStateLabel(string state) => state switch
        {
            "REGULAR" => "取引中",
            "PRE"     => "プレマーケット",
            "POST"    => "アフターマーケット",
            "CLOSED"  => "取引終了",
            _         => state
        };

        // JSON から raw 値を取得するヘルパー
        private static double GetRaw(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var v)) return 0.0;
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("raw", out var r))
                return r.GetDouble();
            if (v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
            return 0.0;
        }

        private static string FormatVolume(long vol, bool isJp)
        {
            if (isJp)              return vol.ToString("N0") + "株";
            if (vol >= 1_000_000) return (vol / 1_000_000.0).ToString("F1") + "M株";
            if (vol >= 1_000)     return (vol / 1_000.0).ToString("F0") + "K株";
            return vol.ToString("N0") + "株";
        }

        private static string FormatLargeNum(double val, bool isJp)
        {
            if (isJp)
            {
                if (val >= 1_000_000_000_000) return (val / 1_000_000_000_000.0).ToString("F1") + "兆円";
                if (val >= 100_000_000)       return (val / 100_000_000.0).ToString("F1") + "億円";
                return val.ToString("N0") + "円";
            }
            else
            {
                if (val >= 1_000_000_000) return "$" + (val / 1_000_000_000.0).ToString("F1") + "B";
                if (val >= 1_000_000)     return "$" + (val / 1_000_000.0).ToString("F1") + "M";
                return "$" + val.ToString("N0");
            }
        }

        // ─── 表示更新 ─────────────────────────────────────────────────────────
        private void UpdateDisplay(
            string symbol, double? price, double? prevClose, double? changePct,
            string? errorMsg, string? name = null, StockDetail? detail = null)
        {
            Dispatcher.Invoke(() =>
            {
                var item = _stocks.FirstOrDefault(
                    s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
                if (item == null) return;

                if (!string.IsNullOrEmpty(name) && name != symbol)
                    item.DisplayName = $"{name}  ({symbol})";

                if (price.HasValue)
                {
                    bool isJp = symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase);
                    item.PriceText = isJp ? $"¥{price.Value:N0}" : $"${price.Value:F2}";

                    if (changePct.HasValue)
                    {
                        string sign  = changePct.Value >= 0 ? "▲" : "▼";
                        double diff  = prevClose.HasValue ? price.Value - prevClose.Value : 0;
                        string diffStr = isJp
                            ? $"{diff:+0;-0;0}円"
                            : $"{diff:+0.00;-0.00;0.00}ドル";
                        item.ChangeText  = $"{sign} {Math.Abs(changePct.Value):F2}%  ({diffStr})";
                        item.ChangeColor = changePct.Value >= 0 ? "#3FB950" : "#F85149";
                        item.PriceColor  = changePct.Value > 0 ? "#3FB950"
                                         : changePct.Value < 0 ? "#F85149" : "#F0F6FC";
                    }

                    item.StatusMessage = string.IsNullOrEmpty(detail?.MarketState) ? "取得成功" : detail.MarketState;
                    item.StatusColor   = detail?.MarketState == "取引中"   ? "#3FB950"
                                       : detail?.MarketState == "取引終了" ? "#8B949E" : "#E3B341";
                    item.BorderColor   = item.IsSelected ? "#58A6FF" : "#30363D";

                    if (detail != null)
                    {
                        item.OpenText   = detail.Open;
                        item.HighText   = detail.DayHigh;
                        item.LowText    = detail.DayLow;
                        item.VolumeText = detail.Volume;
                        item.MarketCap  = detail.MarketCap;
                        item.PeRatio    = detail.PeRatio;
                        item.Week52High = detail.Week52High;
                        item.Week52Low  = detail.Week52Low;
                        item.Exchange   = detail.Exchange;
                        item.Currency   = detail.Currency;
                    }
                }
                else
                {
                    item.PriceText     = "エラー";
                    item.PriceColor    = "#8B949E";
                    item.StatusMessage = errorMsg ?? "取得失敗";
                    item.StatusColor   = "#F85149";
                    item.BorderColor   = item.IsSelected ? "#58A6FF" : "#6E3232";
                }
            });
        }

        // ─── 価格履歴記録 ────────────────────────────────────────────────────
        private void RecordPriceHistory(string symbol, double price)
        {
            if (!_priceHistory.ContainsKey(symbol))
                _priceHistory[symbol] = new Queue<(DateTime, double)>();
            var q = _priceHistory[symbol];
            q.Enqueue((DateTime.Now, price));
            while (q.Count > 3600) q.Dequeue();
        }

        // ─── アラート判定 ─────────────────────────────────────────────────────
        private async Task CheckAlertAsync(
            string symbol, double price, double prevClose,
            double changePct, long currentVolume, string name, CancellationToken ct)
        {
            bool enabled = false;
            bool priceMode = true;
            double? highPrice = null, lowPrice = null;
            double? highPct = null, lowPct = null;
            int surgeSeconds = 60;
            double? surgeUp = null, surgeDown = null;
            double? volRatio = null, volMin = null;
            string webhook = "";

            Dispatcher.Invoke(() =>
            {
                enabled   = EnableAlertCheck.IsChecked == true;
                priceMode = RadioPrice.IsChecked == true;
                webhook   = WebhookInput.Text.Trim();
                if (priceMode)
                {
                    if (double.TryParse(AlertHighPriceInput.Text, out var v)) highPrice = v;
                    if (double.TryParse(AlertLowPriceInput.Text,  out var v2)) lowPrice = v2;
                }
                else
                {
                    if (double.TryParse(AlertHighPctInput.Text, out var v))  highPct = v;
                    if (double.TryParse(AlertLowPctInput.Text,  out var v2)) lowPct  = v2;
                }
                if (int.TryParse(SurgeSecondsInput.Text, out var ss) && ss > 0) surgeSeconds = ss;
                if (double.TryParse(SurgeUpPctInput.Text,   out var su)) surgeUp   = su;
                if (double.TryParse(SurgeDownPctInput.Text, out var sd)) surgeDown = sd;
                if (double.TryParse(VolumeRatioInput.Text,  out var vr)) volRatio  = vr;
                if (double.TryParse(VolumeMinInput.Text,    out var vm)) volMin    = vm;
            });

            if (!enabled || string.IsNullOrEmpty(webhook)) return;

            bool hitHigh = priceMode
                ? (highPrice.HasValue && price >= highPrice.Value)
                : (highPct.HasValue   && changePct >= highPct.Value);
            bool hitLow = priceMode
                ? (lowPrice.HasValue && price <= lowPrice.Value)
                : (lowPct.HasValue   && changePct <= lowPct.Value);

            bool isJp = symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase);
            string priceStr = isJp ? $"{price:N0}円" : $"{price:F2}ドル";
            string? msg = null;

            if (hitHigh)
            {
                var key = symbol + "_high";
                if (!_alertedState.ContainsKey(key))
                {
                    string condStr = priceMode
                        ? $"上限価格 {highPrice!.Value:N0}{(isJp ? "円" : "ドル")}"
                        : $"上昇率 +{highPct!.Value:F2}%";
                    msg = $"🚀 **{name}** が条件達成！\n現在値: **{priceStr}**  前日比: **▲{Math.Abs(changePct):F2}%**\n条件: {condStr}";
                    _alertedState[key] = "alerted";
                    _alertedState.Remove(symbol + "_low");
                }
            }
            else if (hitLow)
            {
                var key = symbol + "_low";
                if (!_alertedState.ContainsKey(key))
                {
                    string condStr = priceMode
                        ? $"下限価格 {lowPrice!.Value:N0}{(isJp ? "円" : "ドル")}"
                        : $"下落率 {lowPct!.Value:F2}%";
                    msg = $"📉 **{name}** が条件達成！\n現在値: **{priceStr}**  前日比: **▼{Math.Abs(changePct):F2}%**\n条件: {condStr}";
                    _alertedState[key] = "alerted";
                    _alertedState.Remove(symbol + "_high");
                }
            }
            else
            {
                _alertedState.Remove(symbol + "_high");
                _alertedState.Remove(symbol + "_low");
            }

            if (msg != null)
                await SendDiscordAsync(webhook, msg, ct);

            // 急騰・急落チェック
            await CheckSurgeAsync(symbol, name, price, surgeSeconds, surgeUp, surgeDown, webhook, ct);

            // 出来高急増チェック（実装）
            await CheckVolumeAsync(symbol, name, currentVolume, volRatio, volMin, webhook, ct);
        }

        private async Task CheckSurgeAsync(
            string symbol, string name, double currentPrice,
            int windowSeconds, double? surgeUpPct, double? surgeDownPct,
            string webhook, CancellationToken ct)
        {
            if (surgeUpPct == null && surgeDownPct == null) return;
            if (!_priceHistory.ContainsKey(symbol)) return;

            var q        = _priceHistory[symbol];
            var cutoff   = DateTime.Now.AddSeconds(-windowSeconds);
            var baseline = q.Where(p => p.time >= cutoff).OrderBy(p => p.time).FirstOrDefault();
            if (baseline.price == 0) return;

            double surgePct = (currentPrice - baseline.price) / baseline.price * 100.0;
            bool isJp = symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase);
            string priceStr = isJp ? $"{currentPrice:N0}円" : $"{currentPrice:F2}ドル";
            string? surgeMsg = null;

            if (surgeUpPct.HasValue && surgePct >= surgeUpPct.Value)
            {
                var key = symbol + "_surge_up";
                if (!_alertedState.ContainsKey(key))
                {
                    surgeMsg = $"🔥 **{name}** 急騰検知！\n直近{windowSeconds}秒で **+{surgePct:F2}%** 上昇\n現在値: **{priceStr}**";
                    _alertedState[key] = "alerted";
                    _alertedState.Remove(symbol + "_surge_down");
                }
            }
            else if (surgeDownPct.HasValue && surgePct <= surgeDownPct.Value)
            {
                var key = symbol + "_surge_down";
                if (!_alertedState.ContainsKey(key))
                {
                    surgeMsg = $"📉 **{name}** 急落検知！\n直近{windowSeconds}秒で **{surgePct:F2}%** 下落\n現在値: **{priceStr}**";
                    _alertedState[key] = "alerted";
                    _alertedState.Remove(symbol + "_surge_up");
                }
            }
            else
            {
                _alertedState.Remove(symbol + "_surge_up");
                _alertedState.Remove(symbol + "_surge_down");
            }

            if (surgeMsg != null)
                await SendDiscordAsync(webhook, surgeMsg, ct);
        }

        /// <summary>
        /// 出来高急増アラート。
        /// 当日出来高 >= 平均出来高 × 倍率 かつ 当日出来高 >= 最低出来高（万株）のときに通知。
        /// </summary>
        private async Task CheckVolumeAsync(
            string symbol, string name, long currentVolume,
            double? ratioThreshold, double? minVolume10k,
            string webhook, CancellationToken ct)
        {
            if (ratioThreshold == null && minVolume10k == null) return;
            if (currentVolume <= 0) return;

            // 平均出来高が取得できていない場合はスキップ
            if (!_prevDayVolume.TryGetValue(symbol, out long avgVolume) || avgVolume <= 0) return;

            double ratio = (double)currentVolume / avgVolume;

            bool hitRatio = ratioThreshold.HasValue && ratio >= ratioThreshold.Value;
            bool hitMin   = minVolume10k.HasValue   && currentVolume >= (long)(minVolume10k.Value * 10_000);
            bool triggered = hitRatio && (!minVolume10k.HasValue || hitMin)
                          || (!ratioThreshold.HasValue && hitMin);

            if (!triggered)
            {
                _alertedState.Remove(symbol + "_volume");
                return;
            }

            var key = symbol + "_volume";
            if (_alertedState.ContainsKey(key)) return;

            bool isJp = symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase);
            string volStr = FormatVolume(currentVolume, isJp);
            string msg    = $"📊 **{name}** 出来高急増！\n当日出来高: **{volStr}**（平均比 **{ratio:F1}倍**）";
            _alertedState[key] = "alerted";
            await SendDiscordAsync(webhook, msg, ct);
        }

        // ─── Discord 送信 ─────────────────────────────────────────────────────
        private async Task SendDiscordAsync(string url, string message, CancellationToken ct)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { content = message });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                await _http.PostAsync(url, content, ct);
                Dispatcher.Invoke(() => SetStatus("通知送信済", "#E3B341"));
                _ = Task.Delay(5000, ct).ContinueWith(_ =>
                    Dispatcher.Invoke(() => { if (_isRunning) SetStatus("監視中", "#3FB950"); }),
                    TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    MessageBox.Show($"Discord送信失敗:\n{ex.Message}", "エラー",
                        MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }

        // ─── テストボタン ─────────────────────────────────────────────────────
        private async void TestWebhook_Click(object sender, RoutedEventArgs e)
        {
            var url = WebhookInput.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("Webhook URLを入力してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await SendDiscordAsync(url, "✅ **株価モニター** — Webhook テスト成功！", CancellationToken.None);
            MessageBox.Show("テストメッセージを送信しました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void TestNikkei_Click(object sender, RoutedEventArgs e)
        {
            NikkeiPriceText.Text  = "取得中…";
            NikkeiChangeText.Text = "";

            var endpoints = new (string label, string url)[]
            {
                ("v8/q1", "https://query1.finance.yahoo.com/v8/finance/chart/%5EN225?interval=1d&range=1d"),
                ("v8/q2", "https://query2.finance.yahoo.com/v8/finance/chart/%5EN225?interval=1d&range=1d"),
                ("v11/q1", $"https://query1.finance.yahoo.com/v11/finance/quoteSummary/%5EN225?modules=price&crumb={Uri.EscapeDataString(_crumb)}"),
                ("v11/q2", $"https://query2.finance.yahoo.com/v11/finance/quoteSummary/%5EN225?modules=price&crumb={Uri.EscapeDataString(_crumb)}"),
            };

            var results = new StringBuilder();
            foreach (var ep in endpoints)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, ep.url);
                    if (!string.IsNullOrEmpty(_cookie))
                        req.Headers.TryAddWithoutValidation("Cookie", _cookie);

                    var resp = await _http.SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();
                    results.AppendLine($"[{ep.label}] HTTP {(int)resp.StatusCode}");
                    if (!resp.IsSuccessStatusCode) continue;

                    double price = 0, prevClose = 0;
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("chart", out var chart))
                    {
                        var meta = chart.GetProperty("result")[0].GetProperty("meta");
                        price     = meta.GetProperty("regularMarketPrice").GetDouble();
                        prevClose = meta.GetProperty("chartPreviousClose").GetDouble();
                    }
                    else if (root.TryGetProperty("quoteSummary", out var qs))
                    {
                        var pr = qs.GetProperty("result")[0].GetProperty("price");
                        price     = GetRaw(pr, "regularMarketPrice");
                        prevClose = GetRaw(pr, "regularMarketPreviousClose");
                    }

                    if (price > 0)
                    {
                        double chg    = price - prevClose;
                        double chgPct = prevClose > 0 ? chg / prevClose * 100.0 : 0;
                        string sign   = chg >= 0 ? "▲" : "▼";
                        string color  = chg >= 0 ? "#3FB950" : "#F85149";

                        NikkeiPriceText.Text       = $"{price:N0}";
                        NikkeiChangeText.Text       = $"{sign}{Math.Abs(chg):F0}({Math.Abs(chgPct):F2}%)";
                        NikkeiChangeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

                        MessageBox.Show(
                            $"✅ 成功: {ep.label}\n\n日経平均: {price:N0}\n前日比: {sign}{Math.Abs(chg):F0} ({Math.Abs(chgPct):F2}%)\n\n全結果:\n{results}",
                            "テスト結果", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    var s = ex.Message.Length > 50 ? ex.Message[..50] : ex.Message;
                    results.AppendLine($"[{ep.label}] 例外: {s}");
                }
            }

            NikkeiPriceText.Text = "失敗";
            MessageBox.Show("全エンドポイント失敗:\n\n" + results, "テスト失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ─── ウィンドウクローズ ───────────────────────────────────────────────
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            SaveSettings();
            _cts?.Cancel();
        }

        private void SetStatus(string text, string hexColor)
        {
            var c = (Color)ColorConverter.ConvertFromString(hexColor);
            StatusText.Text       = text;
            StatusText.Foreground = new SolidColorBrush(c);
            StatusDot.Fill        = new SolidColorBrush(c);
        }
    }

    // ─── StockItem（MVVM用モデル） ────────────────────────────────────────────
    public class StockItem : INotifyPropertyChanged
    {
        private string _symbol          = "";
        private string _displayName     = "";
        private string _priceText       = "---";
        private string _priceColor      = "#F0F6FC";
        private string _changeText      = "";
        private string _changeColor     = "#8B949E";
        private string _statusMessage   = "待機中";
        private string _statusColor     = "#8B949E";
        private string _borderColor     = "#30363D";
        private string _backgroundColor = "#0D1117";
        private bool   _isSelected      = false;
        private string _openText    = "--";
        private string _highText    = "--";
        private string _lowText     = "--";
        private string _volumeText  = "--";
        private string _marketCap   = "--";
        private string _peRatio     = "--";
        private string _week52High  = "--";
        private string _week52Low   = "--";
        private string _exchange    = "--";
        private string _currency    = "--";

        public string Symbol        { get => _symbol;        set { _symbol = value;        OnPropertyChanged(); } }
        public string DisplayName   { get => _displayName;   set { _displayName = value;   OnPropertyChanged(); } }
        public string PriceText     { get => _priceText;     set { _priceText = value;     OnPropertyChanged(); } }
        public string PriceColor    { get => _priceColor;    set { _priceColor = value;    OnPropertyChanged(); } }
        public string ChangeText    { get => _changeText;    set { _changeText = value;    OnPropertyChanged(); } }
        public string ChangeColor   { get => _changeColor;   set { _changeColor = value;   OnPropertyChanged(); } }
        public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }
        public string StatusColor   { get => _statusColor;   set { _statusColor = value;   OnPropertyChanged(); } }
        public string OpenText      { get => _openText;      set { _openText = value;      OnPropertyChanged(); } }
        public string HighText      { get => _highText;      set { _highText = value;      OnPropertyChanged(); } }
        public string LowText       { get => _lowText;       set { _lowText = value;       OnPropertyChanged(); } }
        public string VolumeText    { get => _volumeText;    set { _volumeText = value;    OnPropertyChanged(); } }
        public string MarketCap     { get => _marketCap;     set { _marketCap = value;     OnPropertyChanged(); } }
        public string PeRatio       { get => _peRatio;       set { _peRatio = value;       OnPropertyChanged(); } }
        public string Week52High    { get => _week52High;    set { _week52High = value;    OnPropertyChanged(); } }
        public string Week52Low     { get => _week52Low;     set { _week52Low = value;     OnPropertyChanged(); } }
        public string Exchange      { get => _exchange;      set { _exchange = value;      OnPropertyChanged(); } }
        public string Currency      { get => _currency;      set { _currency = value;      OnPropertyChanged(); } }

        public string BorderColor
        {
            get => _borderColor;
            set { _borderColor = value; OnPropertyChanged(); }
        }

        public string BackgroundColor
        {
            get => _backgroundColor;
            set { _backgroundColor = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected     = value;
                BorderColor     = value ? "#58A6FF" : "#30363D";
                BackgroundColor = value ? "#1C2333" : "#0D1117";
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ─── StockDetail（一時データ転送用） ─────────────────────────────────────
    public class StockDetail
    {
        public string Open        { get; set; } = "--";
        public string DayHigh     { get; set; } = "--";
        public string DayLow      { get; set; } = "--";
        public string Volume      { get; set; } = "--";
        public string MarketCap   { get; set; } = "--";
        public string PeRatio     { get; set; } = "--";
        public string Week52High  { get; set; } = "--";
        public string Week52Low   { get; set; } = "--";
        public string Exchange    { get; set; } = "--";
        public string Currency    { get; set; } = "--";
        public string MarketState { get; set; } = "";
    }

    // ─── WPFバインディング用 色文字列→Brushコンバーター ──────────────────────
    public class ColorStringToBrushConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter,
                              System.Globalization.CultureInfo culture)
        {
            try
            {
                if (value is string s && !string.IsNullOrEmpty(s))
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));
            }
            catch { }
            return Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter,
                                  System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }
}
