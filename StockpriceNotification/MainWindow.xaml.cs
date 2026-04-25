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
        private readonly ObservableCollection<StockItem> _stocks = new ObservableCollection<StockItem>();
        private readonly HttpClient _http = new HttpClient();
        private CancellationTokenSource? _cts;
        private bool _isRunning = false;
        private StockItem? _selectedItem = null;

        private readonly Dictionary<string, string> _alertedState = new Dictionary<string, string>();
        private readonly Dictionary<string, Queue<(DateTime time, double price)>> _priceHistory
            = new Dictionary<string, Queue<(DateTime, double)>>();
        private int _intervalSeconds = 30;

        private static readonly string SavePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "StockMonitor", "settings.json");

        public MainWindow()
        {
            InitializeComponent();
            _http.Timeout = TimeSpan.FromSeconds(10);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            _stocks.CollectionChanged += (_, __) => RefreshStockPanel();
            LoadSettings();
        }

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

        // ─── Cookie / Crumb 管理 ──────────────────────────────────────────────
        private string _crumb  = "";
        private string _cookie = "";
        private readonly SemaphoreSlim _crumbLock = new SemaphoreSlim(1, 1);

        private async Task EnsureCrumbAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_crumb)) return;
            await _crumbLock.WaitAsync(ct);
            try
            {
                if (!string.IsNullOrEmpty(_crumb)) return;

                // Step1: Yahoo Finance にアクセスして cookie を取得
                using var handler = new HttpClientHandler { UseCookies = false };
                using var client  = new HttpClient(handler);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

                var resp1 = await client.GetAsync("https://finance.yahoo.com", ct);
                var cookieList = new List<string>();
                if (resp1.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    foreach (var c in setCookies)
                        cookieList.Add(c.Split(';')[0]);
                _cookie = string.Join("; ", cookieList);

                // Step2: crumb を取得
                var req2 = new HttpRequestMessage(HttpMethod.Get,
                    "https://query1.finance.yahoo.com/v1/test/getcrumb");
                req2.Headers.TryAddWithoutValidation("Cookie", _cookie);
                req2.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                var resp2 = await client.SendAsync(req2, ct);
                _crumb = (await resp2.Content.ReadAsStringAsync()).Trim();
            }
            finally
            {
                _crumbLock.Release();
            }
        }

        private void AlertMode_Changed(object sender, RoutedEventArgs e)
        {
            if (PriceModePanel == null || PercentModePanel == null) return;
            bool priceMode = RadioPrice.IsChecked == true;
            PriceModePanel.Visibility   = priceMode ? Visibility.Visible : Visibility.Collapsed;
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

            StockCountText.Text = string.Format("{0} 銘柄", _stocks.Count);
        }

        private TextBlock MakeGroupHeader(string title, int count)
        {
            return new TextBlock
            {
                Style = (Style)FindResource("GroupHeader"),
                Text  = string.Format("{0}  ({1})", title, count)
            };
        }

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
            card.MouseLeftButtonUp += (_, __) =>
                SelectItem(_selectedItem == item ? null : item);

            var outer = new StackPanel();

            // Row1: 銘柄名 + 価格
            var row1 = new Grid();
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftTop = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            var nameBlock = new TextBlock { Foreground = Brushes.WhiteSmoke, FontSize = 14, FontWeight = FontWeights.Bold };
            nameBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DisplayName") { Source = item });

            var exchangeBadge = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background   = new SolidColorBrush(Color.FromRgb(30, 40, 60)),
                Padding      = new Thickness(5, 1, 5, 1),
                Margin       = new Thickness(6, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var exchText = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(88, 166, 255)) };
            exchText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Exchange") { Source = item });
            exchangeBadge.Child = exchText;

            nameRow.Children.Add(nameBlock);
            nameRow.Children.Add(exchangeBadge);

            var statusBlock = new TextBlock { FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
            statusBlock.SetBinding(TextBlock.TextProperty,       new System.Windows.Data.Binding("StatusMessage") { Source = item });
            statusBlock.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("StatusColor")   { Source = item, Converter = new ColorStringToBrushConverter() });

            leftTop.Children.Add(nameRow);
            leftTop.Children.Add(statusBlock);

            var rightTop = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
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
            outer.Children.Add(new Border
            {
                Height     = 1,
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                Margin     = new Thickness(0, 8, 0, 8)
            });

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
            outer.Children.Add(new Border
            {
                Height     = 1,
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                Margin     = new Thickness(0, 8, 0, 6)
            });

            // Row3: 52週レンジ
            var rangeLabelStack = new StackPanel { Orientation = Orientation.Horizontal };
            rangeLabelStack.Children.Add(new TextBlock { Text = "52週安値 ", Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)), FontSize = 10 });
            var w52LowTb = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(248, 81, 73)) };
            w52LowTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Week52Low") { Source = item });
            rangeLabelStack.Children.Add(w52LowTb);
            rangeLabelStack.Children.Add(new TextBlock { Text = "  〜  52週高値 ", Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)), FontSize = 10 });
            var w52HighTb = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(63, 185, 80)) };
            w52HighTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Week52High") { Source = item });
            rangeLabelStack.Children.Add(w52HighTb);
            outer.Children.Add(rangeLabelStack);

            card.Child = outer;
            return card;
        }

        private StackPanel MakeDetailCell(string label, StockItem item, string bindPath)
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

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_stocks.Count == 0)
            {
                MessageBox.Show("銘柄を1つ以上追加してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (int.TryParse(IntervalInput.Text, out var secs) && secs >= 10)
                _intervalSeconds = secs;
            else
            {
                _intervalSeconds = 30;
                IntervalInput.Text = "30";
            }
            IntervalHint.Text = string.Format("{0}秒ごとに全銘柄を取得します", _intervalSeconds);

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
                LastUpdateText.Text = string.Format("最終更新: {0:HH:mm:ss}", DateTime.Now));
        }

        private async Task FetchSingleAsync(string symbol, CancellationToken ct)
        {
            try
            {
                // v8 chart は crumb 不要（テストで v8/q1 が成功済み）
                var url = string.Format(
                    "https://query1.finance.yahoo.com/v8/finance/chart/{0}?interval=1d&range=1d&includePrePost=true",
                    Uri.EscapeDataString(symbol));

                var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    UpdateDisplay(symbol, null, null, null, string.Format("HTTP {0}", (int)resp.StatusCode));
                    return;
                }

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var meta = doc.RootElement
                    .GetProperty("chart")
                    .GetProperty("result")[0]
                    .GetProperty("meta");

                double price     = meta.GetProperty("regularMarketPrice").GetDouble();
                double prevClose = meta.GetProperty("chartPreviousClose").GetDouble();

                // 市場状態
                string marketState = "";
                if (meta.TryGetProperty("marketState", out var ms))
                    marketState = ms.GetString() ?? "";

                double displayPrice = price;
                string priceLabel   = MarketStateLabel(marketState);

                // プレ・アフター価格があれば優先
                if (marketState == "PRE" && meta.TryGetProperty("preMarketPrice", out var pre) && pre.ValueKind == JsonValueKind.Number)
                    displayPrice = pre.GetDouble();
                else if (marketState == "POST" && meta.TryGetProperty("postMarketPrice", out var post) && post.ValueKind == JsonValueKind.Number)
                    displayPrice = post.GetDouble();

                // 銘柄名
                string name = symbol;
                if (meta.TryGetProperty("shortName", out var sn) && !string.IsNullOrEmpty(sn.GetString()))
                    name = sn.GetString()!;
                else if (meta.TryGetProperty("instrumentType", out _) && meta.TryGetProperty("exchangeName", out var exn))
                    name = symbol; // fallback

                double changePct = prevClose != 0.0
                    ? (displayPrice - prevClose) / prevClose * 100.0
                    : 0.0;

                // v8 meta から取れる詳細情報を詰める
                var detail = new StockDetail();
                detail.MarketState = priceLabel;
                bool isJp = symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase);
                string unit = isJp ? "円" : "$";
                string fmt  = isJp ? "N0" : "F2";

                if (meta.TryGetProperty("regularMarketDayHigh", out var hi))
                    detail.DayHigh = unit + hi.GetDouble().ToString(fmt);
                if (meta.TryGetProperty("regularMarketDayLow", out var lo))
                    detail.DayLow  = unit + lo.GetDouble().ToString(fmt);
                if (meta.TryGetProperty("regularMarketOpen", out var op))
                    detail.Open    = unit + op.GetDouble().ToString(fmt);
                if (meta.TryGetProperty("regularMarketVolume", out var vol))
                {
                    long v = vol.GetInt64();
                    detail.Volume = isJp
                        ? v.ToString("N0") + "株"
                        : v >= 1_000_000 ? (v / 1_000_000.0).ToString("F1") + "M株"
                        : v >= 1_000     ? (v / 1_000.0).ToString("F0") + "K株"
                        : v.ToString("N0") + "株";
                }
                if (meta.TryGetProperty("exchangeName", out var exName))
                    detail.Exchange = exName.GetString() ?? "--";
                if (meta.TryGetProperty("currency", out var cur))
                    detail.Currency = cur.GetString() ?? "--";

                RecordPriceHistory(symbol, displayPrice);
                UpdateDisplay(symbol, displayPrice, prevClose, changePct, null, name, detail);
                await CheckAlertAsync(symbol, displayPrice, prevClose, changePct, name, ct);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                var msg = ex.Message.Length > 50 ? ex.Message.Substring(0, 50) + "…" : ex.Message;
                UpdateDisplay(symbol, null, null, null, msg);
            }
        }

        private string MarketStateLabel(string state)
        {
            switch (state)
            {
                case "REGULAR": return "取引中";
                case "PRE":     return "プレマーケット";
                case "POST":    return "アフターマーケット";
                case "CLOSED":  return "取引終了";
                default:        return state;
            }
        }

        private double GetRaw(JsonElement el, string key)
        {
            if (el.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("raw", out var r))
                    return r.GetDouble();
                if (v.ValueKind == JsonValueKind.Number)
                    return v.GetDouble();
            }
            return 0.0;
        }

        private StockDetail ParseDetail(JsonElement root, string symbol)
        {
            var d = new StockDetail();
            bool isJp = symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase);
            string fmt  = isJp ? "N0" : "F2";
            string unit = isJp ? "円" : "$";

            if (root.TryGetProperty("price", out var pr))
            {
                d.Open      = FormatNum(pr, "regularMarketOpen",    fmt, unit);
                d.DayHigh   = FormatNum(pr, "regularMarketDayHigh", fmt, unit);
                d.DayLow    = FormatNum(pr, "regularMarketDayLow",  fmt, unit);
                d.Volume    = FormatVolume(pr, "regularMarketVolume", isJp);
                d.MarketCap = FormatLargeNum(pr, "marketCap", isJp);
                if (pr.TryGetProperty("exchangeName",   out var ex))  d.Exchange = ex.GetString()  ?? "--";
                if (pr.TryGetProperty("currencySymbol", out var cur)) d.Currency = cur.GetString() ?? "--";
            }

            if (root.TryGetProperty("summaryDetail", out var sd))
            {
                d.PeRatio    = FormatNum(sd, "trailingPE",       "F1", "x");
                d.Week52High = FormatNum(sd, "fiftyTwoWeekHigh", fmt,  unit);
                d.Week52Low  = FormatNum(sd, "fiftyTwoWeekLow",  fmt,  unit);
            }

            return d;
        }

        private string FormatNum(JsonElement el, string key, string fmt, string suffix)
        {
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object
                && v.TryGetProperty("raw", out var raw))
            {
                double val    = raw.GetDouble();
                string numStr = val.ToString(fmt);
                return suffix == "x" ? numStr + suffix : suffix + numStr;
            }
            return "--";
        }

        private string FormatVolume(JsonElement el, string key, bool isJp)
        {
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object
                && v.TryGetProperty("raw", out var raw))
            {
                long vol = raw.GetInt64();
                if (isJp)           return vol.ToString("N0") + "株";
                if (vol >= 1_000_000) return (vol / 1_000_000.0).ToString("F1") + "M株";
                if (vol >= 1_000)     return (vol / 1_000.0).ToString("F0") + "K株";
                return vol.ToString("N0") + "株";
            }
            return "--";
        }

        private string FormatLargeNum(JsonElement el, string key, bool isJp)
        {
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object
                && v.TryGetProperty("raw", out var raw))
            {
                double val  = raw.GetDouble();
                string unit = isJp ? "" : "$";
                string suf  = "";
                double disp = val;

                if (isJp)
                {
                    if      (val >= 1_000_000_000_000) { disp = val / 1_000_000_000_000.0; suf = "兆円"; }
                    else if (val >= 100_000_000)        { disp = val / 100_000_000.0;       suf = "億円"; }
                    else                                { suf = "円"; }
                    return disp.ToString(suf.StartsWith("兆") || suf.StartsWith("億") ? "F1" : "N0") + suf;
                }
                else
                {
                    if      (val >= 1_000_000_000) { disp = val / 1_000_000_000.0; suf = "B"; }
                    else if (val >= 1_000_000)     { disp = val / 1_000_000.0;     suf = "M"; }
                    return unit + disp.ToString(suf.Length > 0 ? "F1" : "N0") + suf;
                }
            }
            return "--";
        }

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
                    item.DisplayName = string.Format("{0}  ({1})", name, symbol);

                if (price.HasValue)
                {
                    bool isJp = symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase);
                    item.PriceText = isJp
                        ? string.Format("¥{0:N0}", price.Value)
                        : string.Format("${0:F2}", price.Value);

                    if (changePct.HasValue)
                    {
                        var sign = changePct.Value >= 0 ? "▲" : "▼";
                        double diff = prevClose.HasValue ? price.Value - prevClose.Value : 0;
                        string diffStr = isJp
                            ? string.Format("{0:+0;-0;0}円", diff)
                            : string.Format("{0:+0.00;-0.00;0.00}ドル", diff);
                        item.ChangeText  = string.Format("{0} {1:F2}%  ({2})", sign, Math.Abs(changePct.Value), diffStr);
                        item.ChangeColor = changePct.Value >= 0 ? "#3FB950" : "#F85149";
                        item.PriceColor  = changePct.Value > 0 ? "#3FB950" : changePct.Value < 0 ? "#F85149" : "#F0F6FC";
                    }

                    item.StatusMessage = string.IsNullOrEmpty(detail?.MarketState) ? "取得成功" : detail.MarketState;
                    item.StatusColor   = detail?.MarketState == "取引中"   ? "#3FB950"
                                       : detail?.MarketState == "取引終了" ? "#8B949E"
                                       : "#E3B341";
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

        private void RecordPriceHistory(string symbol, double price)
        {
            if (!_priceHistory.ContainsKey(symbol))
                _priceHistory[symbol] = new Queue<(DateTime, double)>();
            var q = _priceHistory[symbol];
            q.Enqueue((DateTime.Now, price));
            while (q.Count > 3600) q.Dequeue();
        }

        private async Task CheckAlertAsync(
            string symbol, double price, double prevClose,
            double changePct, string name, CancellationToken ct)
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
                double v;
                if (priceMode)
                {
                    if (double.TryParse(AlertHighPriceInput.Text, out v)) highPrice = v;
                    if (double.TryParse(AlertLowPriceInput.Text,  out v)) lowPrice  = v;
                }
                else
                {
                    if (double.TryParse(AlertHighPctInput.Text, out v)) highPct = v;
                    if (double.TryParse(AlertLowPctInput.Text,  out v)) lowPct  = v;
                }
                if (int.TryParse(SurgeSecondsInput.Text,    out var ss) && ss > 0) surgeSeconds = ss;
                if (double.TryParse(SurgeUpPctInput.Text,   out v)) surgeUp   = v;
                if (double.TryParse(SurgeDownPctInput.Text, out v)) surgeDown = v;
                if (double.TryParse(VolumeRatioInput.Text,  out v)) volRatio  = v;
                if (double.TryParse(VolumeMinInput.Text,    out v)) volMin    = v;
            });

            if (!enabled || string.IsNullOrEmpty(webhook)) return;

            bool hitHigh = priceMode
                ? (highPrice.HasValue && price >= highPrice.Value)
                : (highPct.HasValue   && changePct >= highPct.Value);
            bool hitLow = priceMode
                ? (lowPrice.HasValue && price <= lowPrice.Value)
                : (lowPct.HasValue   && changePct <= lowPct.Value);

            string? msg = null;

            if (hitHigh)
            {
                var key = symbol + "_high";
                if (!_alertedState.ContainsKey(key))
                {
                    bool isJp = symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase);
                    string priceStr = isJp ? string.Format("{0:N0}円", price) : string.Format("{0:F2}ドル", price);
                    string condStr  = priceMode
                        ? string.Format("上限価格 {0:N0}{1}", highPrice!.Value, isJp ? "円" : "ドル")
                        : string.Format("上昇率 +{0:F2}%", highPct!.Value);
                    msg = string.Format("🚀 **{0}** が条件達成！\n現在値: **{1}**  前日比: **▲{2:F2}%**\n条件: {3}",
                        name, priceStr, Math.Abs(changePct), condStr);
                    _alertedState[key] = "alerted";
                    _alertedState.Remove(symbol + "_low");
                }
            }
            else if (hitLow)
            {
                var key = symbol + "_low";
                if (!_alertedState.ContainsKey(key))
                {
                    bool isJp = symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase);
                    string priceStr = isJp ? string.Format("{0:N0}円", price) : string.Format("{0:F2}ドル", price);
                    string condStr  = priceMode
                        ? string.Format("下限価格 {0:N0}{1}", lowPrice!.Value, isJp ? "円" : "ドル")
                        : string.Format("下落率 {0:F2}%", lowPct!.Value);
                    msg = string.Format("📉 **{0}** が条件達成！\n現在値: **{1}**  前日比: **▼{2:F2}%**\n条件: {3}",
                        name, priceStr, Math.Abs(changePct), condStr);
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

            await CheckSurgeAsync(symbol, name, price, surgeSeconds, surgeUp, surgeDown, webhook, ct);
        }

        private async Task CheckSurgeAsync(
            string symbol, string name, double currentPrice,
            int windowSeconds, double? surgeUpPct, double? surgeDownPct,
            string webhook, CancellationToken ct)
        {
            if (surgeUpPct == null && surgeDownPct == null) return;
            if (!_priceHistory.ContainsKey(symbol)) return;

            var q      = _priceHistory[symbol];
            var cutoff = DateTime.Now.AddSeconds(-windowSeconds);
            var baseline = q.Where(p => p.time >= cutoff).OrderBy(p => p.time).FirstOrDefault();
            if (baseline.price == 0) return;

            double surgePct = (currentPrice - baseline.price) / baseline.price * 100.0;
            string? surgeMsg = null;

            if (surgeUpPct.HasValue && surgePct >= surgeUpPct.Value)
            {
                var key = symbol + "_surge_up";
                if (!_alertedState.ContainsKey(key))
                {
                    bool isJp = symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase);
                    string priceStr = isJp ? string.Format("{0:N0}円", currentPrice) : string.Format("{0:F2}ドル", currentPrice);
                    surgeMsg = string.Format("🔥 **{0}** 急騰検知！\n直近{1}秒で **+{2:F2}%** 上昇\n現在値: **{3}**",
                        name, windowSeconds, surgePct, priceStr);
                    _alertedState[key] = "alerted";
                    _alertedState.Remove(symbol + "_surge_down");
                }
            }
            else if (surgeDownPct.HasValue && surgePct <= surgeDownPct.Value)
            {
                var key = symbol + "_surge_down";
                if (!_alertedState.ContainsKey(key))
                {
                    bool isJp = symbol.EndsWith(".T", StringComparison.OrdinalIgnoreCase);
                    string priceStr = isJp ? string.Format("{0:N0}円", currentPrice) : string.Format("{0:F2}ドル", currentPrice);
                    surgeMsg = string.Format("📉 **{0}** 急落検知！\n直近{1}秒で **{2:F2}%** 下落\n現在値: **{3}**",
                        name, windowSeconds, surgePct, priceStr);
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

        private async Task SendDiscordAsync(string url, string message, CancellationToken ct)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { content = message });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                await _http.PostAsync(url, content, ct);
                Dispatcher.Invoke(() => SetStatus("通知送信済", "#E3B341"));
                _ = Task.Delay(5000).ContinueWith(_ =>
                    Dispatcher.Invoke(() => { if (_isRunning) SetStatus("監視中", "#3FB950"); }));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    MessageBox.Show(string.Format("Discord送信失敗:\n{0}", ex.Message),
                        "エラー", MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }

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

        // ─── 日経平均テスト取得 ───────────────────────────────────────────────
        private async void TestNikkei_Click(object sender, RoutedEventArgs e)
        {
            NikkeiPriceText.Text  = "取得中…";
            NikkeiChangeText.Text = "";

            // 複数エンドポイントを順番に試す（どれが動くか確認用）
            var endpoints = new (string label, string url)[]
            {
                ("v8/q1",
                 "https://query1.finance.yahoo.com/v8/finance/chart/%5EN225?interval=1d&range=1d"),
                ("v8/q2",
                 "https://query2.finance.yahoo.com/v8/finance/chart/%5EN225?interval=1d&range=1d"),
                ("v11/q1",
                 string.Format("https://query1.finance.yahoo.com/v11/finance/quoteSummary/%5EN225?modules=price&crumb={0}",
                     Uri.EscapeDataString(_crumb))),
                ("v11/q2",
                 string.Format("https://query2.finance.yahoo.com/v11/finance/quoteSummary/%5EN225?modules=price&crumb={0}",
                     Uri.EscapeDataString(_crumb))),
            };

            var results = new System.Text.StringBuilder();

            foreach (var ep in endpoints)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, ep.url);
                    if (!string.IsNullOrEmpty(_cookie))
                        req.Headers.TryAddWithoutValidation("Cookie", _cookie);

                    var resp = await _http.SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();

                    results.AppendLine(string.Format("[{0}] HTTP {1}", ep.label, (int)resp.StatusCode));

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

                        NikkeiPriceText.Text       = string.Format("{0:N0}", price);
                        NikkeiChangeText.Text       = string.Format("{0}{1:F0}({2:F2}%)", sign, Math.Abs(chg), Math.Abs(chgPct));
                        NikkeiChangeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

                        MessageBox.Show(
                            string.Format("✅ 成功エンドポイント: {0}\n\n日経平均: {1:N0}\n前日比: {2}{3:F0} ({4:F2}%)\n\n全結果:\n{5}",
                                ep.label, price, sign, Math.Abs(chg), Math.Abs(chgPct), results),
                            "テスト結果", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    results.AppendLine(string.Format("[{0}] 例外: {1}", ep.label,
                        ex.Message.Length > 50 ? ex.Message.Substring(0, 50) : ex.Message));
                }
            }

            NikkeiPriceText.Text = "失敗";
            MessageBox.Show("全エンドポイント失敗:\n\n" + results, "テスト失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            SaveSettings();
            _cts?.Cancel();
            _http.Dispose();
        }

        private void SetStatus(string text, string hexColor)
        {
            var c = (Color)ColorConverter.ConvertFromString(hexColor);
            StatusText.Text       = text;
            StatusText.Foreground = new SolidColorBrush(c);
            StatusDot.Fill        = new SolidColorBrush(c);
        }
    }

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
