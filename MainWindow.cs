using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tray = System.Windows.Forms;

internal sealed class MainWindow : Window
{
    private static readonly Brush Muted = ColorBrush(185, 184, 174);
    private static readonly Brush TextInk = ColorBrush(241, 239, 233);
    private static readonly Brush Amber = ColorBrush(255, 193, 91);
    private static readonly Brush Rule = ColorBrush(73, 74, 65);
    private readonly Border panel;
    private readonly bool trayStart;
    private readonly EventWaitHandle openSignal;
    private readonly string preview;
    private readonly int previewDpi;
    private readonly Tray.NotifyIcon tray;
    private readonly Tray.ContextMenuStrip trayMenu;
    private readonly DispatcherTimer timer;
    private readonly TextBlock model, power, range, status;
    private readonly TextBlock[] metrics = new TextBlock[5];
    private readonly ProgressBar[] bars = new ProgressBar[2];
    private readonly Button apply, restore, enable, save;
    private readonly Button[] presets;
    private readonly int[] presetWatts = { 350, 450, 550 };
    private readonly ComboBox selector;
    private readonly Slider slider;
    private readonly Canvas scale;
    private readonly TextBox watt, startupWatts;
    private readonly CheckBox registerStartup, autoPower;
    private List<Gpu> cards = new List<Gpu>();
    private string selectedUuid;
    private bool busy, reading, updating, draft, dragging, ready, checkedBroker, exiting, startupComplete;
    private int startupAttempts, tick;

    internal static void Run(bool trayStart, EventWaitHandle open, string preview = null, int dpi = 96, bool previewSettings = false)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow(trayStart, open, preview, dpi, previewSettings);
        app.Startup += (sender, args) => window.Boot();
        app.Run();
    }

    private MainWindow(bool trayStart, EventWaitHandle open, string preview, int dpi, bool previewSettings)
    {
        this.trayStart = trayStart;
        this.openSignal = open;
        this.preview = preview;
        this.previewDpi = dpi;
        Title = "Gary调音台";
        Width = 870;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 32);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = ColorBrush(23, 24, 23);
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mixer.xaml"))
            panel = (Border)XamlReader.Load(stream);
        Content = panel;
        model = Find<TextBlock>("Model");
        power = Find<TextBlock>("Power");
        range = Find<TextBlock>("Range");
        status = Find<TextBlock>("Status");
        selector = Find<ComboBox>("GpuList");
        slider = Find<Slider>("Fader");
        scale = Find<Canvas>("Scale");
        watt = Find<TextBox>("Watt");
        startupWatts = Find<TextBox>("AutoWatts");
        registerStartup = Find<CheckBox>("StartWindows");
        autoPower = Find<CheckBox>("AutoPower");
        apply = Find<Button>("Apply");
        restore = Find<Button>("Restore");
        enable = Find<Button>("Enable");
        save = Find<Button>("Save");
        presets = new[] { Find<Button>("Preset350"), Find<Button>("Preset450"), Find<Button>("Preset550") };

        var meters = Find<StackPanel>("Meters");
        string[] names = { "实时功耗", "GPU 占用", "显存占用", "风扇转速", "GPU 温度" };
        for (int index = 0; index < names.Length; index++)
        {
            ProgressBar bar;
            meters.Children.Add(Meter(names[index], index == 1 || index == 2, out metrics[index], out bar));
            if (bar != null) bars[index - 1] = bar;
        }

        Find<Border>("Header").MouseLeftButtonDown += (sender, args) =>
        {
            if (args.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        Find<Button>("Minimize").Click += (sender, args) => Hide();
        Find<Button>("Hide").Click += (sender, args) => Hide();
        var settings = Find<Border>("StartupPanel");
        if (previewSettings) settings.Visibility = Visibility.Visible;
        Find<Button>("Startup").Click += (sender, args) =>
            settings.Visibility = settings.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        registerStartup.IsChecked = Settings.StartupRegistered();
        registerStartup.Click += (sender, args) => SaveSetting(() => Settings.RegisterStartup(registerStartup.IsChecked == true));
        autoPower.Click += (sender, args) => UpdateControls();
        save.Click += (sender, args) => SaveStartup();
        startupWatts.KeyDown += (sender, args) => { if (args.Key == Key.Enter) { args.Handled = true; SaveStartup(); } };
        enable.Click += async (sender, args) => await EnableBroker();
        apply.Click += async (sender, args) => await ApplyInput();
        restore.Click += async (sender, args) => await ApplyPower(null);
        for (int index = 0; index < presets.Length; index++)
        {
            int target = presetWatts[index];
            presets[index].Click += async (sender, args) => await ApplyPower(target);
        }
        selector.SelectionChanged += (sender, args) =>
        {
            if (updating || selector.SelectedIndex < 0) return;
            SelectGpu(cards[selector.SelectedIndex]);
            SaveSetting(() => Settings.Write("SelectedGpu", selectedUuid));
        };
        scale.SizeChanged += (sender, args) => DrawScale();
        slider.ValueChanged += (sender, args) =>
        {
            if (updating) return;
            draft = true;
            SetInput(((int)slider.Value).ToString(Gpu.Invariant));
        };
        slider.PreviewMouseLeftButtonDown += (sender, args) => dragging = true;
        slider.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(async (sender, args) =>
        {
            if (!dragging) return;
            dragging = false;
            if (draft) await ApplyInput();
        }), true);
        slider.LostMouseCapture += (sender, args) =>
            Dispatcher.BeginInvoke(new Action(() => { if (Mouse.LeftButton == MouseButtonState.Released) dragging = false; }));
        watt.TextChanged += (sender, args) =>
        {
            if (updating) return;
            draft = true;
            int value;
            if (!ParseWatts(watt.Text, out value) || value < slider.Minimum || value > slider.Maximum) return;
            updating = true;
            slider.Value = value;
            updating = false;
        };
        watt.KeyDown += PowerEnter;
        slider.KeyDown += PowerEnter;

        trayMenu = new Tray.ContextMenuStrip();
        trayMenu.Items.Add("打开 Gary调音台", null, (sender, args) => Dispatcher.BeginInvoke((Action)RestoreWindow));
        trayMenu.Items.Add("退出", null, (sender, args) => Dispatcher.BeginInvoke(new Action(() => { exiting = true; Close(); })));
        tray = new Tray.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = Title, ContextMenuStrip = trayMenu, Visible = preview == null };
        tray.MouseClick += (sender, args) =>
        {
            if (args.Button == Tray.MouseButtons.Left) Dispatcher.BeginInvoke((Action)RestoreWindow);
        };
        Closing += (sender, args) =>
        {
            if (!exiting && preview == null) { args.Cancel = true; Hide(); }
        };
        Closed += (sender, args) =>
        {
            timer.Stop();
            tray.Dispose();
            trayMenu.Dispose();
            Application.Current.Shutdown();
        };
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += async (sender, args) =>
        {
            if (openSignal.WaitOne(0)) RestoreWindow();
            if (++tick % 8 == 0 && (IsVisible || !startupComplete && startupAttempts < 8)) await RefreshData();
        };
        UpdateControls();
    }

    private async void Boot()
    {
        if (preview == null && !trayStart) Show();
        if (preview == null) timer.Start();
        await RefreshData();
        if (preview == null) return;
        try { Capture(); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
        Close();
    }

    private void Capture()
    {
        if (previewDpi < 96 || previewDpi > 240) throw new ArgumentOutOfRangeException("dpi");
        double factor = previewDpi / 96.0;
        Content = null;
        VisualTreeHelper.SetRootDpi(panel, new DpiScale(factor, factor));
        panel.Measure(new Size(Width, SystemParameters.WorkArea.Height));
        var bounds = new Size(Width, panel.DesiredSize.Height);
        panel.Arrange(new Rect(bounds));
        panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * factor), (int)Math.Ceiling(bounds.Height * factor),
            previewDpi, previewDpi, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(preview)) png.Save(output);
        Console.WriteLine(status.Text);
    }

    private void RestoreWindow()
    {
        Show();
        Activate();
    }

    private T Find<T>(string name) where T : FrameworkElement
    {
        return (T)panel.FindName(name);
    }

    private static Brush ColorBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Border Meter(string label, bool gauge, out TextBlock value, out ProgressBar bar)
    {
        var rows = new StackPanel();
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.Children.Add(new TextBlock { Text = label, FontSize = 17, Foreground = Muted });
        value = new TextBlock {
            Text = "—", FontSize = 21, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(13, 0, 0, 0), FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI")
        };
        Grid.SetColumn(value, 1);
        line.Children.Add(value);
        rows.Children.Add(line);
        bar = gauge ? new ProgressBar { Margin = new Thickness(0, 5, 0, 0), Focusable = false } : null;
        if (bar != null) rows.Children.Add(bar);
        return new Border { BorderBrush = Rule, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8), Child = rows };
    }

    private Gpu Current { get { return cards.FirstOrDefault(gpu => gpu.Uuid == selectedUuid); } }

    private async Task RefreshData()
    {
        if (busy || reading || exiting) return;
        reading = true;
        try
        {
            var snapshot = await Task.Run(() => Gpu.Read().Where(gpu => gpu.Adjustable).ToList());
            if (exiting) return;
            if (snapshot.Count == 0) throw new InvalidOperationException("未检测到驱动允许调整功率上限的 NVIDIA 显卡。");
            if (!checkedBroker)
            {
                ready = await Task.Run(() => Broker.IsReady());
                checkedBroker = true;
                SetStatus(ready ? "功率控制已就绪" : "首次启用功率控制需要一次 Windows 管理员授权。");
            }
            else if (cards.Count == 0) SetStatus("已重新连接 NVIDIA 驱动。");
            Render(snapshot);
        }
        catch (Exception error)
        {
            startupAttempts++;
            cards.Clear();
            model.Visibility = Visibility.Visible;
            selector.Visibility = Visibility.Collapsed;
            model.Text = "未检测到可调显卡";
            power.Text = range.Text = "—";
            foreach (var metric in metrics) metric.Text = "—";
            foreach (var bar in bars) bar.Value = 0;
            DrawScale();
            UpdateControls();
            SetStatus(error.Message, true);
            if (trayStart && startupAttempts == 8) Notify(error.Message);
        }
        finally { reading = false; }

        if (Current != null && !startupComplete && preview == null)
        {
            startupComplete = true;
            int watts;
            if (ParseWatts(Settings.Read("Auto-" + Current.Uuid), out watts)) await ApplyPower(watts, true);
        }
    }

    private void Render(List<Gpu> snapshot)
    {
        bool changed = !cards.Select(gpu => gpu.Uuid).SequenceEqual(snapshot.Select(gpu => gpu.Uuid));
        cards = snapshot;
        string preference = selectedUuid ?? Settings.Read("SelectedGpu");
        Gpu selected = cards.FirstOrDefault(gpu => gpu.Uuid == preference) ?? cards[0];
        if (changed)
        {
            updating = true;
            selector.ItemsSource = cards.Select(gpu => gpu.Name).ToList();
            selector.SelectedIndex = cards.IndexOf(selected);
            selector.Visibility = cards.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            model.Visibility = cards.Count > 1 ? Visibility.Collapsed : Visibility.Visible;
            updating = false;
        }
        if (selectedUuid != selected.Uuid) SelectGpu(selected);
        else UpdateReadings(selected);
    }

    private void SelectGpu(Gpu gpu)
    {
        selectedUuid = gpu.Uuid;
        draft = false;
        string saved = Settings.Read("Auto-" + gpu.Uuid);
        autoPower.IsChecked = saved != null;
        startupWatts.Text = saved ?? decimal.Truncate(gpu.Limit.Value).ToString(Gpu.Invariant);
        UpdateReadings(gpu);
    }

    private void UpdateReadings(Gpu gpu)
    {
        const string edition = " Workstation Edition";
        model.Text = gpu.Name.EndsWith(edition, StringComparison.OrdinalIgnoreCase)
            ? gpu.Name.Substring(0, gpu.Name.Length - edition.Length) + "\n" + gpu.Name.Substring(gpu.Name.Length - edition.Length + 1)
            : gpu.Name;
        power.Text = Format(gpu.Limit, " W");
        range.Text = "默认 " + Format(gpu.Default, " W");
        if (gpu.Effective.HasValue && gpu.Effective != gpu.Limit)
            range.Text += "\n实际生效 " + Format(gpu.Effective, " W");
        metrics[0].Text = Format(gpu.Draw, " W");
        metrics[1].Text = Format(gpu.Utilization, "%");
        metrics[2].Text = gpu.Used.HasValue && gpu.Total.HasValue
            ? (gpu.Used.Value / 1024).ToString("0.0") + " / " + (gpu.Total.Value / 1024).ToString("0.0") + " GiB" : "—";
        metrics[3].Text = gpu.Fan == null ? Format(gpu.FanPercent, "%")
            : gpu.Fan + (gpu.FanPercent.HasValue ? " · " + Format(gpu.FanPercent, "%") : "");
        metrics[4].Text = Format(gpu.Temperature, " °C");
        bars[0].Value = (double)(gpu.Utilization ?? 0);
        bars[1].Value = gpu.Total > 0 ? (double)(100 * (gpu.Used ?? 0) / gpu.Total.Value) : 0;
        if (!dragging && !draft && Math.Ceiling(gpu.Minimum.Value) <= Math.Floor(gpu.Maximum.Value))
        {
            updating = true;
            slider.Maximum = (double)Math.Floor(gpu.Maximum.Value);
            slider.Minimum = (double)Math.Ceiling(gpu.Minimum.Value);
            slider.Value = (double)gpu.Limit.Value;
            watt.Text = ((int)slider.Value).ToString(Gpu.Invariant);
            updating = false;
        }
        tray.Text = Title + " · " + Format(gpu.Limit, " W");
        DrawScale();
        UpdateControls();
    }

    private void DrawScale()
    {
        scale.Children.Clear();
        Gpu gpu = Current;
        if (gpu == null || scale.ActualHeight <= 0) return;
        decimal minimum = gpu.Minimum.Value, maximum = gpu.Maximum.Value;
        for (int index = 0; index <= 18; index++)
        {
            var mark = new Border { Background = Rule, Width = 8, Height = 1 };
            Canvas.SetLeft(mark, 67);
            Canvas.SetTop(mark, scale.ActualHeight * index / 18);
            scale.Children.Add(mark);
        }
        var labels = new List<decimal> { minimum, maximum };
        foreach (var point in presetWatts)
            if (labels.All(existing => Math.Abs(existing - point) / (maximum - minimum) > 0.08m) && point > minimum && point < maximum)
                labels.Add(point);
        foreach (decimal point in labels)
        {
            double y = scale.ActualHeight * (double)((maximum - point) / (maximum - minimum));
            var mark = new Border { Background = Muted, Width = 16, Height = 1 };
            Canvas.SetLeft(mark, 59);
            Canvas.SetTop(mark, y);
            scale.Children.Add(mark);
            var label = new TextBlock { Text = Format(point, " W"), Foreground = Muted, FontSize = 13, TextAlignment = TextAlignment.Right, Width = 53 };
            Canvas.SetTop(label, y - 9);
            scale.Children.Add(label);
        }
    }

    private void UpdateControls()
    {
        Gpu gpu = Current;
        bool editable = gpu != null && !busy;
        bool integerRange = editable && Math.Ceiling(gpu.Minimum.Value) <= Math.Floor(gpu.Maximum.Value);
        selector.IsEnabled = !busy;
        slider.IsEnabled = watt.IsEnabled = apply.IsEnabled = integerRange;
        restore.IsEnabled = editable;
        for (int index = 0; index < presets.Length; index++)
        {
            int target = presetWatts[index];
            var button = presets[index];
            button.IsEnabled = editable && target >= gpu.Minimum && target <= gpu.Maximum;
            if (gpu != null && target == gpu.Limit)
            {
                button.BorderBrush = button.Foreground = Amber;
                button.Background = ColorBrush(54, 40, 23);
            }
            else
            {
                button.ClearValue(Control.ForegroundProperty);
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.BorderBrushProperty);
            }
        }
        autoPower.IsEnabled = save.IsEnabled = integerRange;
        startupWatts.IsEnabled = integerRange && autoPower.IsChecked == true;
        enable.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        enable.IsEnabled = !busy && gpu != null;
    }

    private async Task EnableBroker()
    {
        if (busy) return;
        busy = true;
        UpdateControls();
        SetStatus("等待首次管理员授权…");
        try
        {
            await Task.Run(() => Broker.Enable());
            ready = true;
            SetStatus("已启用免弹窗功率控制。");
        }
        catch (Exception error) { SetStatus(error.Message, true); }
        finally { busy = false; UpdateControls(); }
    }

    private async Task ApplyPower(int? watts, bool automatic = false)
    {
        Gpu gpu = Current;
        if (busy || gpu == null || preview != null) return;
        if (!ready)
        {
            const string message = "请先启用免弹窗设置。";
            SetStatus(message, true);
            if (automatic && !IsVisible) Notify(message);
            return;
        }
        busy = true;
        UpdateControls();
        SetStatus("正在设置并核对功率上限…");
        bool failed = false;
        try
        {
            decimal actual = await Task.Run(() => Broker.Apply(gpu, watts));
            draft = false;
            power.Text = Format(actual, " W");
            SetStatus("已设置并读回 " + Format(actual, " W") + "。");
        }
        catch (Exception error)
        {
            failed = true;
            SetStatus(error.Message, true);
            if (automatic && !IsVisible) Notify(error.Message);
        }
        finally { busy = false; UpdateControls(); }
        if (failed)
        {
            ready = await Task.Run(() => Broker.IsReady());
            UpdateControls();
        }
        await RefreshData();
    }

    private async Task ApplyInput()
    {
        int value;
        if (!ParseWatts(watt.Text, out value)) { SetStatus("请输入整数瓦数。", true); watt.Focus(); return; }
        await ApplyPower(value);
    }

    private async void PowerEnter(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            args.Handled = true;
            await ApplyInput();
        }
    }

    private void SaveStartup()
    {
        Gpu gpu = Current;
        if (gpu == null) return;
        int value = 0;
        bool enabled = autoPower.IsChecked == true;
        if (enabled && (!ParseWatts(startupWatts.Text, out value) || value < gpu.Minimum || value > gpu.Maximum))
        {
            SetStatus("启动功率必须是显卡允许范围内的整数瓦数。", true);
            startupWatts.Focus();
            return;
        }
        SaveSetting(() =>
        {
            Settings.Write("Auto-" + gpu.Uuid, enabled ? value.ToString(Gpu.Invariant) : null);
            SetStatus(enabled ? "下次启动 Gary调音台时自动应用 " + value + " W。" : "已关闭启动时自动应用功率。");
        });
    }

    private void SaveSetting(Action action)
    {
        try { action(); }
        catch (Exception error) { SetStatus("无法保存设置。 " + error.Message, true); }
    }

    private void SetInput(string text)
    {
        updating = true;
        watt.Text = text;
        updating = false;
    }

    private static bool ParseWatts(string text, out int watts)
    {
        return int.TryParse(text, System.Globalization.NumberStyles.None, Gpu.Invariant, out watts);
    }

    private static string Format(decimal? value, string unit)
    {
        return value.HasValue ? value.Value.ToString("0.##") + unit : "—";
    }

    private void SetStatus(string text, bool error = false)
    {
        status.Text = text;
        status.Foreground = error ? Amber : Muted;
    }

    private void Notify(string text) { tray.ShowBalloonTip(6000, Title, text, Tray.ToolTipIcon.Warning); }
}
