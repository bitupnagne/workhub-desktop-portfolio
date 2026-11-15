# WorkHub Desktop

WorkHub 업무와 알림을 Windows 데스크톱에서 이어가는 WPF 클라이언트 포트폴리오입니다. The desktop client provides a native Windows shell, WebView2 content, real-time notifications and managed sign-in.

[포트폴리오](https://beom.it/ko#projects) · [Portfolio in English](https://beom.it/en#projects) · [WorkHub 공개 웹 데모](https://work.beom.it)

## 주요 기능 / Features

- WPF·WebView2 기반 업무 화면과 시스템 트레이
- 로그인 → OTP → 필수 비밀번호 변경 순서를 지키는 초기화
- 기기 토큰과 자동 로그인, 로그아웃·온보딩 취소 시 토큰 폐기
- WebSocket 재연결, 알림·근태 패널 갱신
- 밝은·어두운 테마, 고해상도 아이콘과 선택적 소프트웨어 렌더링
- 별도 업데이터와 설치기 소스

## 빌드와 테스트 / Build and test

Windows, .NET 10 SDK와 WebView2 Runtime이 필요합니다. NuGet 복원을 위해 인터넷을 사용할 수 있어야 합니다.

```powershell
dotnet build WorkSystemWindow.csproj -c Release
dotnet build updater/Portfolio.OfficeSystem.Updater.csproj -c Release
dotnet run --project tests/Regression/Regression.csproj -c Release
```

회귀 검사는 임시 로컬 HTTP·WebSocket 서버와 실제 WPF 컨트롤을 사용합니다. 계정·OTP 순서, 비밀번호 변경, 기기 토큰 폐기, 화면 전환, 입력 초기화, 패널 갱신과 렌더링 설정 격리를 확인합니다. 실행 가능한 Windows 데스크톱 세션이 필요합니다.

`installer`에는 PowerShell 빌드 스크립트와 Inno Setup 정의가 있습니다. 설치 패키지는 포함하지 않습니다. 설치기를 만들려면 Inno Setup과 별도 릴리스 절차가 필요합니다.

## 연결과 설정 / Configuration

앱의 설정 화면에서 개발용 WorkHub 서버를 지정합니다. 저장되는 설정 이름은 `ServerUrl`, `UseSoftwareRendering` 등이며 실제 주소·기기 토큰·사용자 설정 파일은 공개 소스에 포함하지 않습니다. 공개 웹 데모는 Desktop API를 차단하므로 클라이언트 검증에는 별도 개발 서버가 필요합니다.

렌더링 호환성 선택에는 `--software-rendering` 및 `--save-rendering-mode` 옵션이 있습니다. 앱의 개인 설정과 로그인 정보는 운영체제 사용자 영역에서 관리합니다.

## 출처와 라이선스

Pretendard 폰트의 원본 고지와 OFL은 [Fonts/OFL.txt](Fonts/OFL.txt)에 포함합니다. NuGet 의존성의 라이선스는 해당 패키지가 정한 조건을 따릅니다. 전체 목록은 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)를 참고하세요. 프로젝트 소스에 대한 새로운 포괄적 사용 허락은 이 저장소에서 부여하지 않습니다.
