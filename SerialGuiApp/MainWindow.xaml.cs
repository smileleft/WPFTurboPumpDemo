using System;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.IO;
using Microsoft.Win32;
using System.Diagnostics;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.Windows.Threading;

namespace SerialGuiApp
{
    /// <summary>
    /// 터보펌프 계측/제어 데모용 Serial 통신 GUI.
    /// 실제 하드웨어 대신 com0com 등으로 만든 가상 COM 포트 페어를 통해
    /// DeviceSimulator 콘솔 프로그램과 통신합니다.
    ///
    /// 통신 프로토콜 (줄바꿈으로 구분되는 텍스트 라인 기반):
    ///   요청(Telecommand) : "GET:TEMP", "GET:PRESSURE", "SET:VALVE:OPEN", "SET:VALVE:CLOSE"
    ///   응답              : "TEMP:23.45", "PRESSURE:101.32", "ACK:VALVE:OPEN"
    ///   비동기 텔레메트리 : "TELEMETRY:TEMP:23.45,PRESSURE:101.32,VALVE:OPEN"
    /// </summary>
    public partial class MainWindow : Window
    {
		public enum ConnectionState { Disconnected, Connected, Reconnecting }
        private SerialPort? _serialPort;
        private readonly StringBuilder _receiveBuffer = new();
        private StreamWriter? _autoLogWriter;
		private readonly PlotModel _plotModel = new() { Title = "터보펌프 텔레메트리" };
		private readonly LineSeries _tempSeries = new() { Title = "온도(°C)", YAxisKey = "TempAxis", Color = OxyColors.OrangeRed };
		private readonly LineSeries _pressureSeries = new() { Title = "압력(kPa)", YAxisKey = "PressureAxis", Color = OxyColors.SteelBlue };
		private readonly Stopwatch _plotStopwatch = new();
		private const int MaxPlotPoints = 300;
		
		private DispatcherTimer? _reconnectTimer;
		private DispatcherTimer? _heartbeatTimer;
		private DateTime _lastDataReceivedUtc;
		private string? _lastPortName;
		private int _lastBaudRate;
		private bool _isReconnecting;
		private int _reconnectAttempt;

		private const int HeartbeatTimeoutSeconds = 6;
		private const int ReconnectIntervalSeconds = 3;

        public MainWindow()
        {
            InitializeComponent();
            RefreshPorts();
			InitializePlot();
        }

        // ---------------------------------------------------------------
        // 포트 연결 / 해제
        // ---------------------------------------------------------------

        private void RefreshPortsButton_Click(object sender, RoutedEventArgs e) => RefreshPorts();

        private void RefreshPorts()
        {
            var currentSelection = PortComboBox.SelectedItem as string;
            PortComboBox.Items.Clear();

            foreach (var portName in SerialPort.GetPortNames())
            {
                PortComboBox.Items.Add(portName);
            }

            if (currentSelection != null && PortComboBox.Items.Contains(currentSelection))
            {
                PortComboBox.SelectedItem = currentSelection;
            }
            else if (PortComboBox.Items.Count > 0)
            {
                PortComboBox.SelectedIndex = 0;
            }
        }
		
		private void InitializePlot()
		{
			_plotModel.Axes.Add(new LinearAxis
			{
				Position = AxisPosition.Bottom,
				Title = "경과 시간(초)",
			});
			_plotModel.Axes.Add(new LinearAxis
			{
				Position = AxisPosition.Left,
				Key = "TempAxis",
				Title = "온도(°C)",
				TextColor = OxyColors.OrangeRed,
				TitleColor = OxyColors.OrangeRed,
			});
			_plotModel.Axes.Add(new LinearAxis
			{
				Position = AxisPosition.Right,
				Key = "PressureAxis",
				Title = "압력(kPa)",
				TextColor = OxyColors.SteelBlue,
				TitleColor = OxyColors.SteelBlue,
			});

			_plotModel.Series.Add(_tempSeries);
			_plotModel.Series.Add(_pressureSeries);

			TelemetryPlotView.Model = _plotModel;
			_plotStopwatch.Start();
		}

		private void AddPlotPoint(LineSeries series, double value)
		{
			double x = _plotStopwatch.Elapsed.TotalSeconds;
			series.Points.Add(new DataPoint(x, value));

			if (series.Points.Count > MaxPlotPoints)
			{
				series.Points.RemoveAt(0);
			}

			_plotModel.InvalidatePlot(true);
		}

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
		{
			if (_isReconnecting)
			{
				StopReconnectAttempts();
				return;
			}

			if (_serialPort is { IsOpen: true })
			{
				Disconnect(userInitiated: true);
				return;
			}

			TryOpenPort(fromUi: true);
		}

		private bool TryOpenPort(bool fromUi)
		{
			string? portName;
			int baudRate;

			if (fromUi)
			{
				if (PortComboBox.SelectedItem is not string selectedPort)
				{
					AppendLog("[오류] 포트를 선택하세요.");
					return false;
				}

				portName = selectedPort;
				var baudText = (BaudRateComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string;
				baudRate = int.TryParse(baudText, out var parsedBaud) ? parsedBaud : 9600;
			}
			else
			{
				portName = _lastPortName;
				baudRate = _lastBaudRate;
				if (portName == null) return false;
			}

			try
			{
				var port = new SerialPort(portName, baudRate)
				{
					Parity = Parity.None,
					DataBits = 8,
					StopBits = StopBits.One,
					Handshake = Handshake.None,
					NewLine = "\n",
					ReadTimeout = 2000,
					WriteTimeout = 2000,
				};
				port.DataReceived += SerialPort_DataReceived;
				port.Open();

				_serialPort = port;
				_lastPortName = portName;
				_lastBaudRate = baudRate;
				_reconnectAttempt = 0;

				StopReconnectTimer();
				SetUiState(ConnectionState.Connected);
				AppendLog(fromUi ? $"[연결됨] {portName} @ {baudRate}bps" : $"[재연결 성공] {portName} @ {baudRate}bps");

				StartHeartbeatMonitor();
				return true;
			}
			catch (Exception ex)
			{
				_serialPort = null;
				AppendLog(fromUi
					? $"[오류] 포트 열기 실패: {ex.Message}"
					: $"[재연결 실패 ({_reconnectAttempt}회)] {ex.Message}");
				return false;
			}
		}

        private void Disconnect(bool userInitiated)
		{
			StopHeartbeatMonitor();

			try
			{
				if (_serialPort != null)
				{
					_serialPort.DataReceived -= SerialPort_DataReceived;
					if (_serialPort.IsOpen) _serialPort.Close();
					_serialPort.Dispose();
				}
			}
			catch (Exception ex)
			{
				AppendLog($"[오류] 포트 닫기 실패: {ex.Message}");
			}
			finally
			{
				_serialPort = null;
			}

			if (userInitiated)
			{
				StopReconnectTimer();
				_lastPortName = null; // 사용자가 직접 끊으면 자동 재연결 대상에서 제외
				SetUiState(ConnectionState.Disconnected);
				AppendLog("[연결 해제됨]");
				return;
			}

			AppendLog("[연결 끊김 감지]");
			if (AutoReconnectCheckBox.IsChecked == true && _lastPortName != null)
			{
				StartReconnectTimer();
			}
			else
			{
				SetUiState(ConnectionState.Disconnected);
			}
		}

        private void SetUiState(ConnectionState state)
		{
			bool connected = state == ConnectionState.Connected;
			bool reconnecting = state == ConnectionState.Reconnecting;

			ConnectButton.Content = state switch
			{
				ConnectionState.Connected => "연결 해제",
				ConnectionState.Reconnecting => "재연결 취소",
				_ => "연결",
			};

			StatusText.Text = state switch
			{
				ConnectionState.Connected => "연결됨",
				ConnectionState.Reconnecting => $"재연결 시도 중 ({_reconnectAttempt}회)",
				_ => "연결 안 됨",
			};

			StatusLight.Fill = state switch
			{
				ConnectionState.Connected => Brushes.LimeGreen,
				ConnectionState.Reconnecting => Brushes.Orange,
				_ => Brushes.Gray,
			};

			GetTempButton.IsEnabled = connected;
			GetPressureButton.IsEnabled = connected;
			ValveOpenButton.IsEnabled = connected;
			ValveCloseButton.IsEnabled = connected;

			PortComboBox.IsEnabled = !connected && !reconnecting;
			BaudRateComboBox.IsEnabled = !connected && !reconnecting;
		}

        protected override void OnClosed(EventArgs e)
        {
            Disconnect(userInitiated: true);
			StopAutoSave();
			base.OnClosed(e);
        }

        // ---------------------------------------------------------------
        // 명령 전송 (Telecommand)
        // ---------------------------------------------------------------

        private void GetTempButton_Click(object sender, RoutedEventArgs e) => SendCommand("GET:TEMP");
        private void GetPressureButton_Click(object sender, RoutedEventArgs e) => SendCommand("GET:PRESSURE");
        private void ValveOpenButton_Click(object sender, RoutedEventArgs e) => SendCommand("SET:VALVE:OPEN");
        private void ValveCloseButton_Click(object sender, RoutedEventArgs e) => SendCommand("SET:VALVE:CLOSE");

        private void SendCommand(string command)
        {
            if (_serialPort is not { IsOpen: true })
            {
                AppendLog("[오류] 포트가 연결되어 있지 않습니다.");
                return;
            }

            try
            {
                _serialPort.WriteLine(command);
                AppendLog($">> {command}");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 전송 실패, 연결 끊김으로 처리합니다: {ex.Message}");
				Disconnect(userInitiated: false);
            }
        }

        // ---------------------------------------------------------------
        // 수신 처리 (Telemetry)
        // ---------------------------------------------------------------

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort is not { IsOpen: true }) return;

            string chunk;
			try
			{
				chunk = _serialPort.ReadExisting();
				_lastDataReceivedUtc = DateTime.UtcNow;
			}
			catch (Exception ex)
			{
				Dispatcher.Invoke(() =>
				{
					AppendLog($"[오류] 수신 중 예외 발생, 연결 끊김으로 처리합니다: {ex.Message}");
					Disconnect(userInitiated: false);
				});
				return;
			}

            // 백그라운드(수신) 스레드에서 호출되므로 버퍼 조작 후 UI 갱신은 Dispatcher로 넘긴다.
            _receiveBuffer.Append(chunk);
            string bufferedText = _receiveBuffer.ToString();
            string[] lines = bufferedText.Split('\n');

            // 마지막 조각은 아직 개행이 오지 않은 미완성 라인일 수 있으므로 버퍼에 남겨둔다.
            _receiveBuffer.Clear();
            _receiveBuffer.Append(lines[^1]);

            for (int i = 0; i < lines.Length - 1; i++)
            {
                string line = lines[i].Trim('\r', '\n', ' ');
                if (line.Length == 0) continue;

                Dispatcher.Invoke(() => ProcessIncomingLine(line));
            }
        }

        private void ProcessIncomingLine(string line)
        {
            AppendLog($"<< {line}");

            try
            {
                if (line.StartsWith("TEMP:", StringComparison.Ordinal))
                {
                    var value = line["TEMP:".Length..];
                    UpdateTemp(value);
                }
                else if (line.StartsWith("PRESSURE:", StringComparison.Ordinal))
                {
                    var value = line["PRESSURE:".Length..];
                    UpdatePressure(value);
                }
                else if (line.StartsWith("ACK:VALVE:", StringComparison.Ordinal))
                {
                    var state = line["ACK:VALVE:".Length..];
                    ValveStateText.Text = state;
                }
                else if (line.StartsWith("TELEMETRY:", StringComparison.Ordinal))
                {
                    // 예: TELEMETRY:TEMP:23.45,PRESSURE:101.32,VALVE:OPEN
                    var payload = line["TELEMETRY:".Length..];
                    var fields = payload.Split(',');
                    foreach (var field in fields)
                    {
                        var parts = field.Split(':');
                        if (parts.Length != 2) continue;

                        switch (parts[0])
                        {
                            case "TEMP":
                                UpdateTemp(parts[1]);
                                break;
                            case "PRESSURE":
                                UpdatePressure(parts[1]);
                                break;
                            case "VALVE":
                                ValveStateText.Text = parts[1];
                                break;
                        }
                    }
                }

                LastUpdateText.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                AppendLog($"[파싱 오류] '{line}' 처리 중 예외: {ex.Message}");
            }
        }

        private void UpdateTemp(string rawValue)
		{
			TempValueText.Text = rawValue;
			if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
			{
				AddPlotPoint(_tempSeries, value);
			}
		}
        private void UpdatePressure(string rawValue)
		{
			PressureValueText.Text = rawValue;
			if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
			{
				AddPlotPoint(_pressureSeries, value);
			}
		}

        // ---------------------------------------------------------------
        // 로그 UI
        // ---------------------------------------------------------------

        private void AppendLog(string message)
        {
            void Add()
            {
                var formatted = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                LogListBox.Items.Add(formatted);
                if (LogListBox.Items.Count > 0)
                {
                    LogListBox.ScrollIntoView(LogListBox.Items[^1]);
                }

                if (_autoLogWriter != null)
                {
                    try
                    {
                        _autoLogWriter.WriteLine(formatted);
                    }
                    catch (Exception ex)
                    {
                        LogListBox.Items.Add($"[{DateTime.Now:HH:mm:ss.fff}] [오류] 자동 저장 실패, 자동 저장을 중지합니다: {ex.Message}");
                        StopAutoSave();
                        AutoSaveCheckBox.IsChecked = false;
                    }
                }
            }

            if (Dispatcher.CheckAccess())
            {
                Add();
            }
            else
            {
                Dispatcher.Invoke(Add);
            }
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogListBox.Items.Clear();

        private void SaveLogButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "통신 로그 저장",
                Filter = "텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*",
                FileName = $"SerialLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                var lines = new string[LogListBox.Items.Count];
                for (int i = 0; i < LogListBox.Items.Count; i++)
                    lines[i] = LogListBox.Items[i]?.ToString() ?? string.Empty;

                File.WriteAllLines(dialog.FileName, lines, Encoding.UTF8);
                AppendLog($"[로그 저장 완료] {dialog.FileName}");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 로그 저장 실패: {ex.Message}");
            }
        }

        private void AutoSaveCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                var logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                Directory.CreateDirectory(logsDirectory);

                var filePath = Path.Combine(logsDirectory, $"SerialLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                _autoLogWriter = new StreamWriter(filePath, append: true, Encoding.UTF8) { AutoFlush = true };

                AutoSavePathText.Text = filePath;
                AppendLog($"[자동 저장 시작] {filePath}");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 자동 저장 시작 실패: {ex.Message}");
                AutoSaveCheckBox.IsChecked = false;
            }
        }

        private void AutoSaveCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            AppendLog("[자동 저장 중지]");
            StopAutoSave();
        }

        private void StopAutoSave()
        {
            _autoLogWriter?.Flush();
            _autoLogWriter?.Dispose();
            _autoLogWriter = null;
            AutoSavePathText.Text = string.Empty;
        }
		
		// ---------------------------------------------------------------
		// 자동 재연결
		// ---------------------------------------------------------------

		private void StartReconnectTimer()
		{
			_isReconnecting = true;
			_reconnectAttempt = 0;
			SetUiState(ConnectionState.Reconnecting);

			_reconnectTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(ReconnectIntervalSeconds) };
			_reconnectTimer.Tick -= ReconnectTimer_Tick;
			_reconnectTimer.Tick += ReconnectTimer_Tick;
			_reconnectTimer.Start();

			AppendLog($"[자동 재연결] {ReconnectIntervalSeconds}초 간격으로 {_lastPortName} 재연결을 시도합니다...");
		}

		private void ReconnectTimer_Tick(object? sender, EventArgs e)
		{
			_reconnectAttempt++;
			SetUiState(ConnectionState.Reconnecting); // 시도 횟수 텍스트 갱신
			TryOpenPort(fromUi: false); // 성공하면 내부에서 StopReconnectTimer() 호출됨
		}

		private void StopReconnectTimer()
		{
			if (_reconnectTimer != null)
			{
				_reconnectTimer.Stop();
				_reconnectTimer.Tick -= ReconnectTimer_Tick;
			}
			_isReconnecting = false;
		}

		private void StopReconnectAttempts()
		{
			StopReconnectTimer();
			_lastPortName = null;
			SetUiState(ConnectionState.Disconnected);
			AppendLog("[자동 재연결 취소됨]");
		}

		// ---------------------------------------------------------------
		// 하트비트(연결 끊김 감지)
		// ---------------------------------------------------------------

		private void StartHeartbeatMonitor()
		{
			_lastDataReceivedUtc = DateTime.UtcNow;

			_heartbeatTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
			_heartbeatTimer.Tick -= HeartbeatTimer_Tick;
			_heartbeatTimer.Tick += HeartbeatTimer_Tick;
			_heartbeatTimer.Start();
		}

		private void StopHeartbeatMonitor() => _heartbeatTimer?.Stop();

		private void HeartbeatTimer_Tick(object? sender, EventArgs e)
		{
			if (_serialPort is not { IsOpen: true }) return;

			var elapsed = DateTime.UtcNow - _lastDataReceivedUtc;
			if (elapsed.TotalSeconds > HeartbeatTimeoutSeconds)
			{
				AppendLog($"[경고] {HeartbeatTimeoutSeconds}초 이상 수신 데이터 없음 — 연결 끊김으로 판단합니다.");
				Disconnect(userInitiated: false);
			}
		}
    }
}
