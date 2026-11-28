# WorkHub Desktop

WorkHub 업무와 알림을 Windows 데스크톱에서 이어가는 WPF 클라이언트 포트폴리오입니다(앱 버전 1.0.28). The desktop client provides a native Windows shell, WebView2 content, real-time notifications and managed sign-in.

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white) ![WPF](https://img.shields.io/badge/WPF-Windows-0078D4) ![WebView2](https://img.shields.io/badge/WebView2-embedded%20web-0078D4) ![WebSocket](https://img.shields.io/badge/WebSocket-real--time-4b5563) ![Inno Setup](https://img.shields.io/badge/Inno%20Setup-installer-264b99)

[포트폴리오](https://beom.it/ko#projects) · [Portfolio in English](https://beom.it/en#projects) · [WorkHub 공개 웹 데모](https://work.beom.it) · [아키텍처 설계도](docs/architecture/Office_System_아키텍처_포트폴리오.pdf)

## 주요 기능 / Features

- WPF·WebView2 기반 업무 화면과 시스템 트레이
- 로그인 → OTP → 초기 비밀번호 강제 변경 순서를 지키는 초기화
- 기기 토큰과 자동 로그인. 로그아웃·온보딩 취소 때는 토큰을 폐기하고, 앱 종료 때는 유지
- 서버 재시작 등으로 세션만 끊기면 저장된 기기 토큰으로 조용히 다시 연결
- 출근·퇴근: 누르기 전에 판정(정상·지각·조퇴 등)을 미리 보여 주고, 서버가 거절하면 상태를 다시 받아 버튼에 반영
- 시간외 출근·퇴근(휴무일, 기준 퇴근시각 이후, 퇴근 후 재출근)과 신청하지 않은 시간외 근무 안내
- 조직도와 내 상태(온라인/자리비움) 실시간 표시
- 메시지·자료 전송, 알림 목록과 알림 팝업, 알림음 켜기·끄기
- WebSocket 재연결, 알림·근태 패널 자동 갱신
- 밝은·어두운 테마, 벡터 아이콘, 선택적 소프트웨어 렌더링과 시작 과정 기록
- 별도 업데이터와 설치기 소스(서버에 닿지 못하면 업데이트 확인을 나중에 다시 시도)

급여·시간외·근태·인사 발령 같은 웹 전용 화면은 SSO로 WorkHub 웹을 엽니다.

## 구조 / Layout

```text
.
├─ App.xaml(.cs)                 시작, 단일 실행, 렌더링 모드 결정
├─ MainWindow*.cs / .xaml        로그인·근태·조직도·메시지·알림·자료 전송 패널
├─ WorkSystemApiService.cs       WorkHub Desktop API·WebSocket 클라이언트
├─ AppSettingsService.cs         사용자 설정(기기 토큰은 DPAPI로 보호)
├─ RenderingPreferences.cs       소프트웨어 렌더링 선택
├─ StartupDiagnostics.cs         시작 과정 기록(옵션)
├─ Themes/ Icons/ Fonts/ Sounds/ 테마, 아이콘, 폰트, 알림음
├─ updater/                      업데이트 도우미(별도 실행 파일)
├─ installer/                    Inno Setup 정의와 빌드 스크립트
├─ tests/Regression/             Windows 회귀 검사
└─ docs/architecture/            아키텍처 설계도(PDF)
```

## 빌드와 테스트 / Build and test

Windows, .NET 10 SDK와 WebView2 Runtime이 필요합니다. NuGet 복원을 위해 인터넷을 사용할 수 있어야 합니다.

```powershell
dotnet build WorkSystemWindow.csproj -c Release
dotnet build updater/Portfolio.OfficeSystem.Updater.csproj -c Release
dotnet run --project tests/Regression/Regression.csproj -c Release
```

회귀 검사는 임시 로컬 HTTP·WebSocket 서버와 실제 WPF 컨트롤을 사용합니다. 계정·OTP 순서, 비밀번호 변경, 기기 토큰 폐기와 유지, 세션 만료 처리, 출퇴근 판정 미리보기, 미신청 시간외 건수, 화면 전환, 입력 초기화, 패널 갱신과 렌더링 설정 격리를 확인합니다. 실행 가능한 Windows 데스크톱 세션이 필요합니다.

`installer`에는 PowerShell 빌드 스크립트와 Inno Setup 정의가 있습니다. 설치 패키지는 포함하지 않습니다. 설치기를 만들려면 Inno Setup과 별도 릴리스 절차가 필요합니다.

## 연결과 설정 / Configuration

기본 서버 주소는 `http://localhost:18080`이며, 앱의 설정 화면에서 개발용 WorkHub 서버를 지정합니다. 설정은 `%APPDATA%\Portfolio.OfficeSystem`에 저장합니다. 저장되는 설정 이름은 `ServerUrl`, `UseSoftwareRendering`, `NotificationSound` 등이며 실제 주소·기기 토큰·사용자 설정 파일은 공개 소스에 포함하지 않습니다. 공개 웹 데모는 Desktop API를 차단하므로 클라이언트 검증에는 별도 개발 서버가 필요합니다.

렌더링 호환성 선택에는 `--software-rendering` 및 `--save-rendering-mode` 옵션이 있습니다. 앱의 개인 설정과 로그인 정보는 운영체제 사용자 영역에서 관리합니다.

## 출처와 라이선스

Pretendard 폰트의 원본 고지와 OFL은 [Fonts/OFL.txt](Fonts/OFL.txt)에 포함합니다. NuGet 의존성의 라이선스는 해당 패키지가 정한 조건을 따릅니다. 전체 목록은 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)를 참고하세요. 프로젝트 소스에 대한 새로운 포괄적 사용 허락은 이 저장소에서 부여하지 않습니다.
