# 터보펌프 계측 제어 GUI 실습 (WPF + Serial)

실물 하드웨어 없이 WPF 기반 Serial 장비 제어 GUI를 연습하기 위한 예제.
`SerialGuiApp`(WPF 클라이언트)와 `DeviceSimulator`(가상 장비 역할을 하는 콘솔 프로그램)
두 개의 프로젝트로 구성되어 있고, 가상 COM 포트 페어를 통해 서로 통신함.

```
[SerialGuiApp (WPF)]  <-- 가상 COM 포트 페어 -->  [DeviceSimulator (콘솔)]
     COM10                                              COM11
```

## 1. 개발 환경

- Windows 11
- Visual Studio 2022 Community + ".NET desktop development" 워크로드
- .NET 8 SDK (Visual Studio Installer에서 함께 설치됨)
- 가상 COM 포트 페어 생성 도구 (아래 중 하나)

## 2. 가상 COM 포트 페어 만들기 (Free Virtual Serial Ports 사용)

실물 장비가 없으므로, 서로 연결된 가상 COM 포트 한 쌍을 만들어야 함.
이 실습에서는 **"Free Virtual Serial Ports"** (freevirtualserialports.com) 를 사용.
이 도구는 사용자 모드(user-mode)로 동작하는 서명된 프로그램이라, com0com 같은
커널 모드 드라이버 방식보다 Windows 11에서 설치/서명 문제가 훨씬 적음.

> 참고: com0com은 2017년 이후 사실상 유지보수가 멈춘 프로젝트라, 최신 Windows 11
> 환경(특히 Secure Boot이 켜진 PC)에서는 드라이버 서명 문제(코드 52 오류)로 설치가
> 안 되는 경우가 종종 보고됨. 그래서 이번 실습은 사용자 모드 도구를 기본으로 사용.

### 설치 및 포트 페어 생성

1. `https://freevirtualserialports.com/` 에서 무료 버전을 다운로드해 설치.
   (관리자 권한이 필요.)
2. 설치 후 프로그램을 실행하면 가상 포트 페어를 관리하는 화면이 나옴(Local Brigdes).
   타 프로그램의 경우 보통 **"Add pair"** (또는 이와 유사한 이름의 버튼/메뉴)를 클릭하면 새 포트 페어를 만들 수 있슴.
3. 두 포트 이름을 지정. 이 README에서는 `COM2`(GUI용), `COM1`(시뮬레이터용)로
   가정하고 설명. 다른 이름을 써도 되며, 그 경우 아래 실행 단계에서 그 이름으로 바꿔 입력할 것.
4. 생성 버튼을 눌러 페어를 만들면, Windows 장치 관리자의 "포트(COM & LPT)" 항목에
   두 포트가 나타남. (버전에 따라 메뉴 명칭/위치가 조금 다를 수 있으니, 설치된
   프로그램의 도움말이나 "Pair 추가"류 메뉴를 참고.)
5. 무료 버전은 **같은 PC 안에서 포트 페어를 로컬로 연결하는 기능까지는 무료**이고,
   원격(다른 PC 간) 연결 등 고급 기능 또는 COM 포트명을 변경하는 것은 유료. 이 실습에서는 로컬 페어만 있으면 충분함.

### com0com을 대신 쓰고 싶다면 (참고용, 비권장)

com0com은 무료·오픈소스이고 여전히 동작하는 환경도 있지만, 커널 드라이버 특성상
설치가 안 되거나 코드 52 오류가 나는 사례가 보고됨. 그래도 시도해보고 싶다면
SourceForge 공식 프로젝트(`sourceforge.net/projects/com0com/` 또는
`sourceforge.net/projects/signed-drivers/files/com0com/`)에서만 받을 것.
문제가 생기면 위의 Free Virtual Serial Ports로 넘어가는 걸 추천.
(com0com을 검색하면 나오는 일부 제3자 사이트에서 "서명된 최신 빌드"를 광고하는데,
공식 배포처가 아니므로 드라이버 종류의 소프트웨어는 다운로드하지 않는 게 안전.)

## 3. 프로젝트 열기 및 빌드

1. `SerialTurboPumpDemo.sln`을 Visual Studio 2022로 오픈.
2. 솔루션 탐색기에서 두 프로젝트(`SerialGuiApp`, `DeviceSimulator`)가 보이는지 확인.
3. `빌드 > 솔루션 빌드` (Ctrl+Shift+B) 로 둘 다 빌드되는지 확인.
   - `System.IO.Ports` NuGet 패키지가 자동 복원됨. 인터넷 연결이 필요.

## 4. 실행 순서

1. **DeviceSimulator 먼저 실행**
   - `DeviceSimulator` 프로젝트를 우클릭 → 디버그 > 새 인스턴스 시작
   - 실행되면 포트 이름을 물어봄. 페어 중 하나(예: `COM1`)를 입력.
   - "[대기 중] COM1 @ 9600bps 에서 명령을 기다리는 중..." 메시지가 뜨면 정상.
   - 이후 2초마다 자동으로 `TELEMETRY:...` 메시지를 콘솔에 출력하며 자체 송신함.

2. **SerialGuiApp 실행**
   - `SerialGuiApp`을 시작 프로젝트로 설정하고 F5로 실행.
   - 상단에서 포트로 나머지 하나(예: `COM2`)를 선택하고 **연결** 클릭.
   - 연결 성공 시 상태 표시등이 초록색으로 바뀌고, 텔레메트리 값(온도/압력/밸브 상태)이
     2초 주기로 자동 갱신되는 걸 볼 수 있슴 (DeviceSimulator가 스스로 보내는 값).
   - "온도 조회", "압력 조회", "밸브 열기/닫기" 버튼을 눌러 요청-응답(Telecommand/Telemetry)
     흐름도 함께 확인할 수 있슴.
   - 하단 통신 로그에서 실제 주고받은 텍스트 프로토콜(`GET:TEMP`, `TEMP:23.45` 등)을
     그대로 확인할 수 있슴.

## 5. 통신 프로토콜 요약

| 방향 | 메시지 | 의미 |
|---|---|---|
| GUI → 장비 | `GET:TEMP` | 온도 조회 요청 |
| GUI → 장비 | `GET:PRESSURE` | 압력 조회 요청 |
| GUI → 장비 | `SET:VALVE:OPEN` / `SET:VALVE:CLOSE` | 밸브 개폐 명령 |
| 장비 → GUI | `TEMP:23.45` | 온도 조회 응답 |
| 장비 → GUI | `PRESSURE:101.32` | 압력 조회 응답 |
| 장비 → GUI | `ACK:VALVE:OPEN` | 밸브 명령 확인 응답 |
| 장비 → GUI | `TELEMETRY:TEMP:..,PRESSURE:..,VALVE:..` | 2초 주기 비동기 상태 보고 |

모두 줄바꿈(`\n`)으로 구분되는 단순 텍스트 프로토콜. 실무에서는 이 자리에
바이너리 프레이밍(예: IRIG 106 프레임)이나 JSON을 쓸 수도 있는데, 여기서는
Serial 통신/이벤트 처리/UI 스레드 동기화라는 핵심 구조를 익히는 데 집중하도록
가장 단순한 형태로 구성.

## 6. 코드에서 눈여겨볼 부분 (WPF/MFC 데스크톱 GUI 감각 익히기)

- `MainWindow.xaml.cs`의 `SerialPort.DataReceived` 핸들러: 백그라운드 스레드에서
  발생하므로, UI 요소를 직접 건드리지 않고 `Dispatcher.Invoke`로 넘겨서 갱신.
  이 부분이 WPF/MFC 계측 제어 GUI에서 가장 흔히 실수하는 지점(크로스 스레드 예외).
- 수신 데이터가 한 번에 완전한 줄 단위로 오지 않을 수 있으므로, `StringBuilder` 버퍼에
  누적했다가 개행 문자를 기준으로 잘라 처리함. 실제 계측 장비와 통신할 때도
  이 패턴이 거의 그대로 필요.
- `DeviceSimulator`는 `System.Threading.Timer`로 실제 장비처럼 비동기 텔레메트리를
  스스로 밀어 넣음 — 요청 없이도 상태가 계속 흘러 들어오는 상황을 만들어,
  Telemetry(비동기 보고)와 Telecommand(요청-응답)의 차이를 체감할 수 있게 함.

## 7. 확장 아이디어 (더 연습하고 싶다면)

- 통신 로그를 파일로 저장하는 기능 추가
- 온도/압력 값을 그래프(OxyPlot, LiveCharts 등)로 실시간 플로팅
- 프로토콜을 텍스트 라인 대신 JSON으로 바꿔보기 (React 웹 UI와 동일한 포맷으로 통일하는 연습)
- 재연결 로직 (연결이 끊어졌을 때 자동 재시도)
- QT로 동일한 기능을 다시 구현해보며 WPF와의 차이 비교
