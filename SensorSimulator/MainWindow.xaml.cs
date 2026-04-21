using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SensorSimulator;

public record SensorMessage(string Id, double Val, long Timestamp);

public partial class MainWindow : Window
{
    private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 20000,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        EnableMultipleHttp2Connections = true
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private CancellationTokenSource? _cts;
    private Channel<SensorMessage>? _messageChannel;

    private long _successCount = 0;
    private long _failCount = 0;
    private long _tpsCounter = 0;
    private long _droppedCounter = 0;

    private readonly DispatcherTimer _uiTimer;
    private readonly ConcurrentQueue<double> _chartData = new();
    private const int MaxChartPoints = 300;
    private double _canvasWidth;
    private double _canvasHeight;
    private int _channelCapacity;

    // 保存当前的数值范围，用于图表渲染
    private double _currentMinVal = 0;
    private double _currentMaxVal = 100;

    private readonly ConcurrentDictionary<int, DeviceState> _deviceStates = new();

    public MainWindow()
    {
        InitializeComponent();
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
    }

    private class DeviceState
    {
        public double LastValue { get; set; } = double.NaN; // 初始化为NaN，等待第一次赋值
        public double Amplitude { get; set; } = 20.0;
        public double PhaseOffset { get; set; } = 0;
    }

    private async void BtnTestConn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var response = await _httpClient.GetAsync(TxtHost.Text);
            TxtConnStatus.Text = response.IsSuccessStatusCode ? "✓ 连接成功" : $"✗ HTTP {response.StatusCode}";
            TxtConnStatus.Foreground = response.IsSuccessStatusCode ? Brushes.Green : Brushes.Red;
        }
        catch (Exception)
        {
            TxtConnStatus.Text = "无法连接";
            TxtConnStatus.Foreground = Brushes.Red;
        }
    }

    private async void BtnToggleStart_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
            BtnToggleStart.Content = "启动压测";
            BtnToggleStart.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078D7"));
            return;
        }

        int deviceCount = (int)SldDeviceCount.Value;
        int intervalMs = int.Parse(TxtInterval.Text);
        int delayMs = int.Parse(TxtDelay.Text);
        int mode = CboDataMode.SelectedIndex;
        string endpoint = TxtHost.Text;

        // 解析数值范围并赋给全局变量供 Chart 使用
        _currentMinVal = double.Parse(TxtMinVal.Text);
        _currentMaxVal = double.Parse(TxtMaxVal.Text);
        if (_currentMinVal >= _currentMaxVal)
        {
            MessageBox.Show("最小值必须小于最大值！", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BoundedChannelFullMode fullMode = CboBackpressureMode.SelectedIndex switch
        {
            1 => BoundedChannelFullMode.DropOldest,
            2 => BoundedChannelFullMode.DropWrite,
            _ => BoundedChannelFullMode.Wait
        };

        Interlocked.Exchange(ref _successCount, 0);
        Interlocked.Exchange(ref _failCount, 0);
        Interlocked.Exchange(ref _droppedCounter, 0);
        _chartData.Clear();
        _deviceStates.Clear();

        _cts = new CancellationTokenSource();
        _channelCapacity = Math.Max(5000, deviceCount * (1000 / Math.Max(1, intervalMs)) * 3);

        var channelOptions = new BoundedChannelOptions(_channelCapacity)
        {
            FullMode = fullMode,
            SingleReader = false,
            SingleWriter = false
        };
        _messageChannel = Channel.CreateBounded<SensorMessage>(channelOptions);

        _ = Task.Run(() => GenerateDataAsync(deviceCount, intervalMs, mode, _currentMinVal, _currentMaxVal, _cts.Token));

        int maxConcurrency = Math.Max(1000, deviceCount * 3);
        _ = Task.Run(() => ConsumeAndSendAsync(endpoint, delayMs, maxConcurrency, _cts.Token));

        BtnToggleStart.Content = "停止压测";
        BtnToggleStart.Background = Brushes.Red;
    }

    private async Task GenerateDataAsync(int deviceCount, int intervalMs, int mode, double minVal, double maxVal, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        var sw = Stopwatch.StartNew();

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                double t = sw.Elapsed.TotalSeconds;

                Parallel.For(1, deviceCount + 1, new ParallelOptions { CancellationToken = token }, i =>
                {
                    var state = _deviceStates.GetOrAdd(i, _ => new DeviceState());

                    if (double.IsNaN(state.LastValue))
                        state.LastValue = minVal + (maxVal - minVal) / 2.0;

                    double val = mode switch
                    {
                        // 0. 纯随机：在 Min 和 Max 之间随机
                        0 => minVal + Random.Shared.NextDouble() * (maxVal - minVal),

                        // 1. 布朗运动：修复卡死极值的逻辑
                        1 => CalculateBrownianMotion(state, minVal, maxVal),

                        // 2. 正弦波：自适应范围
                        2 => CalculateHeterogeneousSine(t, state, minVal, maxVal),

                        _ => minVal
                    };

                    var msg = new SensorMessage(i.ToString(), val, now);

                    if (_messageChannel!.Reader.Count >= _channelCapacity)
                    {
                        Interlocked.Increment(ref _droppedCounter);
                    }

                    if (!_messageChannel.Writer.TryWrite(msg))
                    {
                        if (_messageChannel.Reader.Count < _channelCapacity)
                        {
                            Interlocked.Increment(ref _droppedCounter);
                        }
                    }

                    if (i == 1)
                    {
                        _chartData.Enqueue(val);
                        if (_chartData.Count > MaxChartPoints)
                            _chartData.TryDequeue(out _);
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _messageChannel?.Writer.Complete();
        }
    }

    private double CalculateBrownianMotion(DeviceState state, double minVal, double maxVal)
    {
        double range = maxVal - minVal;
        double step = (Random.Shared.NextDouble() - 0.5) * (range * 0.1);

        double nextValue = state.LastValue + step;

        if (nextValue < minVal)
        {
            nextValue = minVal + (minVal - nextValue);
        }
        else if (nextValue > maxVal)
        {
            nextValue = maxVal - (nextValue - maxVal);
        }

        state.LastValue = Math.Clamp(nextValue, minVal, maxVal);
        return state.LastValue;
    }

    private double CalculateHeterogeneousSine(double t, DeviceState state, double minVal, double maxVal)
    {
        double range = maxVal - minVal;
        double baseLine = minVal + range / 2.0;

        if (Random.Shared.NextDouble() < 0.05)
        {
            state.Amplitude = (Random.Shared.NextDouble() * 0.4 + 0.1) * range;
            state.PhaseOffset += Random.Shared.NextDouble() * Math.PI;
        }

        double angularFreq = 2.0;
        double val = (state.Amplitude * Math.Sin(angularFreq * t + state.PhaseOffset)) + baseLine;

        val = Math.Clamp(val, minVal, maxVal);
        state.LastValue = val;
        return val;
    }

    private async Task ConsumeAndSendAsync(string endpoint, int delayMs, int maxConcurrency, CancellationToken token)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxConcurrency,
            CancellationToken = token
        };

        try
        {
            await Parallel.ForEachAsync(_messageChannel!.Reader.ReadAllAsync(token), options, async (msg, ct) =>
            {
                try
                {
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, ct);
                    }

                    var response = await _httpClient.PostAsJsonAsync(endpoint, msg, ct);

                    if (response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref _successCount);
                        Interlocked.Increment(ref _tpsCounter);
                    }
                    else
                    {
                        Interlocked.Increment(ref _failCount);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref _failCount);
                }
            });
        }
        catch (OperationCanceledException) { }
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        long tps = Interlocked.Exchange(ref _tpsCounter, 0);

        TxtTps.Text = tps.ToString("N0");
        TxtSuccess.Text = Interlocked.Read(ref _successCount).ToString("N0");
        TxtFail.Text = Interlocked.Read(ref _failCount).ToString("N0");
        TxtDropped.Text = Interlocked.Read(ref _droppedCounter).ToString("N0");

        if (_messageChannel != null)
        {
            int currentBacklog = _messageChannel.Reader.Count;
            TxtBacklog.Text = currentBacklog.ToString("N0");

            TxtBacklog.Foreground = currentBacklog >= _channelCapacity * 0.8
                ? Brushes.Red
                : Brushes.DarkOrange;
        }
        else
        {
            TxtBacklog.Text = "0";
            TxtBacklog.Foreground = Brushes.Gray;
        }

        RenderChart();
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _canvasWidth = e.NewSize.Width;
        _canvasHeight = e.NewSize.Height;
    }

    private void RenderChart()
    {
        if (_canvasWidth == 0 || _canvasHeight == 0 || _chartData.IsEmpty) return;

        var points = new PointCollection();
        var dataArray = _chartData.ToArray();

        if (dataArray.Length == 0) return;

        double stepX = _canvasWidth / (MaxChartPoints - 1);

        double minY = _currentMinVal;
        double maxY = _currentMaxVal;

        if (maxY <= minY) maxY = minY + 1;

        for (int i = 0; i < dataArray.Length; i++)
        {
            double x = i * stepX;
            double normalizedY = (dataArray[i] - minY) / (maxY - minY);
            double y = _canvasHeight - (normalizedY * _canvasHeight);

            y = Math.Clamp(y, 0, _canvasHeight);

            points.Add(new Point(x, y));
        }

        Device1Chart.Points = points;
    }
}