using System;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace DeviceSimulator
{
    /// <summary>
    /// 실제 터보펌프 계측장비 대신 사용하는 콘솔 시뮬레이터.
    /// com0com 등으로 만든 가상 COM 포트 페어 중 하나(예: COM11)를 열고,
    /// SerialGuiApp(WPF, 예: COM10)이 보내는 명령에 응답하며,
    /// 주기적으로 비동기 텔레메트리를 스스로 송신한다.
    ///
    /// 사용법: DeviceSimulator.exe COM11 9600
    /// (인자를 생략하면 실행 중 직접 입력받는다)
    /// </summary>
    internal static class Program
    {
        private static readonly object PortLock = new();
        private static double _temperature = 23.0;
        private static double _pressure = 101.3;
        private static string _valveState = "CLOSE";
        private static readonly Random Rng = new();

        private static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== 터보펌프 장비 시뮬레이터 (DeviceSimulator) ===");

            string portName = args.Length > 0 ? args[0] : PromptFor("연결할 가상 COM 포트 이름을 입력하세요 (예: COM11): ");
            int baudRate = 9600;
            if (args.Length > 1 && int.TryParse(args[1], out var parsedBaud))
            {
                baudRate = parsedBaud;
            }

            using var port = new SerialPort(portName, baudRate)
            {
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                NewLine = "\n",
                ReadTimeout = SerialPort.InfiniteTimeout,
            };

            try
            {
                port.Open();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[오류] 포트 열기 실패: {ex.Message}");
                Console.WriteLine("가상 COM 포트 페어가 올바르게 설치되어 있는지, 포트 이름이 맞는지 확인하세요.");
                Console.WriteLine("Enter 키를 누르면 종료합니다...");
                Console.ReadLine();
                return;
            }

            Console.WriteLine($"[대기 중] {portName} @ {baudRate}bps 에서 명령을 기다리는 중...");
            Console.WriteLine("2초마다 비동기 텔레메트리(TELEMETRY:...)를 자동으로 전송합니다.");
            Console.WriteLine("종료하려면 Ctrl+C 를 누르세요.\n");

            // 비동기 텔레메트리 송신 타이머 (실제 실험설비의 주기적 상태 보고를 모사)
            using var telemetryTimer = new Timer(_ => SendTelemetry(port), null,
                dueTime: TimeSpan.FromSeconds(2), period: TimeSpan.FromSeconds(2));

            var receiveBuffer = new StringBuilder();

            port.DataReceived += (_, _) =>
            {
                string chunk;
                try
                {
                    chunk = port.ReadExisting();
                }
                catch
                {
                    return;
                }

                lock (PortLock)
                {
                    receiveBuffer.Append(chunk);
                    var lines = receiveBuffer.ToString().Split('\n');
                    receiveBuffer.Clear();
                    receiveBuffer.Append(lines[^1]);

                    for (int i = 0; i < lines.Length - 1; i++)
                    {
                        var line = lines[i].Trim('\r', '\n', ' ');
                        if (line.Length == 0) continue;
                        HandleCommand(port, line);
                    }
                }
            };

            // 프로세스가 종료되지 않도록 대기
            var exitSignal = new ManualResetEvent(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                exitSignal.Set();
            };
            exitSignal.WaitOne();
        }

        private static string PromptFor(string message)
        {
            Console.Write(message);
            return Console.ReadLine()?.Trim() ?? string.Empty;
        }

        private static void HandleCommand(SerialPort port, string command)
        {
            Console.WriteLine($"<< 수신: {command}");

            string? response = command switch
            {
                "GET:TEMP" => $"TEMP:{FormatValue(_temperature)}",
                "GET:PRESSURE" => $"PRESSURE:{FormatValue(_pressure)}",
                "SET:VALVE:OPEN" => SetValve("OPEN"),
                "SET:VALVE:CLOSE" => SetValve("CLOSE"),
                _ => null,
            };

            if (response == null)
            {
                Console.WriteLine($"   [경고] 알 수 없는 명령: {command}");
                return;
            }

            WriteLine(port, response);
        }

        private static string SetValve(string state)
        {
            _valveState = state;
            return $"ACK:VALVE:{state}";
        }

        private static void SendTelemetry(SerialPort port)
        {
            lock (PortLock)
            {
                if (!port.IsOpen) return;

                // 실측 데이터처럼 보이도록 약간의 랜덤 워크를 적용
                _temperature += (Rng.NextDouble() - 0.5) * 0.4;
                _pressure += (Rng.NextDouble() - 0.5) * 0.8;

                var payload = $"TELEMETRY:TEMP:{FormatValue(_temperature)},PRESSURE:{FormatValue(_pressure)},VALVE:{_valveState}";
                WriteLine(port, payload);
            }
        }

        private static void WriteLine(SerialPort port, string message)
        {
            try
            {
                port.WriteLine(message);
                Console.WriteLine($">> 송신: {message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[오류] 송신 실패: {ex.Message}");
            }
        }

        private static string FormatValue(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    }
}
