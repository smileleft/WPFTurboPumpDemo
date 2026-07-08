using System;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Media;

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
        private SerialPort? _serialPort;
        private readonly StringBuilder _receiveBuffer = new();

        public MainWindow()
        {
            InitializeComponent();
            RefreshPorts();
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

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_serialPort is { IsOpen: true })
            {
                Disconnect();
                return;
            }

            if (PortComboBox.SelectedItem is not string portName)
            {
                AppendLog("[오류] 포트를 선택하세요.");
                return;
            }

            var baudText = (BaudRateComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string;
            if (!int.TryParse(baudText, out var baudRate))
            {
                baudRate = 9600;
            }

            try
            {
                _serialPort = new SerialPort(portName, baudRate)
                {
                    Parity = Parity.None,
                    DataBits = 8,
                    StopBits = StopBits.One,
                    Handshake = Handshake.None,
                    NewLine = "\n",
                    ReadTimeout = 2000,
                    WriteTimeout = 2000,
                };
                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.Open();

                SetConnectedUiState(true);
                AppendLog($"[연결됨] {portName} @ {baudRate}bps");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 포트 열기 실패: {ex.Message}");
                _serialPort = null;
            }
        }

        private void Disconnect()
        {
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
                SetConnectedUiState(false);
                AppendLog("[연결 해제됨]");
            }
        }

        private void SetConnectedUiState(bool connected)
        {
            ConnectButton.Content = connected ? "연결 해제" : "연결";
            StatusText.Text = connected ? "연결됨" : "연결 안 됨";
            StatusLight.Fill = connected ? Brushes.LimeGreen : Brushes.Gray;

            GetTempButton.IsEnabled = connected;
            GetPressureButton.IsEnabled = connected;
            ValveOpenButton.IsEnabled = connected;
            ValveCloseButton.IsEnabled = connected;

            PortComboBox.IsEnabled = !connected;
            BaudRateComboBox.IsEnabled = !connected;
        }

        protected override void OnClosed(EventArgs e)
        {
            Disconnect();
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
                AppendLog($"[오류] 전송 실패: {ex.Message}");
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
            }
            catch (Exception)
            {
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

        private void UpdateTemp(string rawValue) => TempValueText.Text = rawValue;
        private void UpdatePressure(string rawValue) => PressureValueText.Text = rawValue;

        // ---------------------------------------------------------------
        // 로그 UI
        // ---------------------------------------------------------------

        private void AppendLog(string message)
        {
            void Add()
            {
                LogListBox.Items.Add($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
                if (LogListBox.Items.Count > 0)
                {
                    LogListBox.ScrollIntoView(LogListBox.Items[^1]);
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
    }
}
