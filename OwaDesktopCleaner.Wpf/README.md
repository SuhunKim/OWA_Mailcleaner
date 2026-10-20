# OWA MailCleaner · WPF

이 브랜치는 [이슈 #1](https://github.com/SuhunKim/OWA_Mailcleaner/issues/1)의 **기존 JavaScript UI와 유사한 WPF 화면**입니다. 기존 JavaScript 버전과 기존 WPF 작업 폴더를 보존하고 `codex/issue-1-wpf-js-ui` 브랜치의 별도 워크트리에서 수정했습니다. 현재 작업 위치는 `D:\기타 프로그램\Outlook VBA\.worktrees\wpf-js-ui`입니다.

기존 `cleaner.js`의 실제 CSS/HTML을 기준으로 흰색 둥근 패널, 파란 버튼, 둥근 입력창, 처리 방식 라디오 버튼, 상태 표시, 정렬 도구, 표 열 순서, 하단 선택 요약과 실행 버튼을 맞췄습니다. 기본 제목 `일보`와 최근 30일, 휴지통 모드의 폴더 전환·조건 복원도 원본 흐름을 따릅니다. WPF 통합 인증 연결 버튼은 작은 상단 행으로 유지했습니다. 달력·팝업·스크롤·창 테두리는 네이티브 WPF 제어이므로 브라우저 렌더링과 세부 모양에 차이가 있습니다.

## 이번 UI 수정 검증

- Debug/Release 빌드: 경고 0, 오류 0.
- 서비스·ViewModel 모의 테스트: 28개 통과. 기존 변경 안전장치와 정렬 중 선택 유지, 모드 전환 시 검색 무효화, 휴지통 조건 전환·복원을 확인했습니다.
- 연결 검증 모의 테스트: 20개 통과.
- 실제 WPF 컨트롤에 모의 EWS 응답을 연결하여 라디오 바인딩, 정렬 드롭다운, 체크박스 선택, 달력 팝업, 처리 결과 접힘/펼침, 최소 창 크기, 재검색 시 결과 숨김을 확인했습니다.
- 원본 JavaScript CSS/HTML도 네트워크를 차단한 별도 임시 브라우저로 렌더링하여 예시 데이터 화면과 비교했습니다.
- 이번 UI 작업에서는 실제 서버 로그인이나 사서함 변경을 시험하지 않았습니다.

새 솔루션을 Visual Studio 2026으로 열고 `MailCleaner`를 시작 프로젝트로 지정한 뒤 F5를 누르세요. 실제 사용에는 기본 실행파일을 사용하고, UI 모의 검사 실행기는 아래처럼 실행할 수 있습니다.

```powershell
dotnet run --project .\Tests -c Release -- --ui-smoke .\.artifacts\ui-review
```

`.artifacts\ui-review`에는 `javascript-results.png`, `wpf-results.png`, 초기 화면·처리 결과·최소 크기 화면이 있습니다. 모든 메일은 합성 예시이며 실사서함 데이터가 아닙니다. 이 폴더는 Git 추적에서 제외합니다. 원본 비교 화면을 다시 만들려면 Playwright와 Edge가 있는 환경에서 `node .\Tests\render-js-reference.cjs .\.artifacts\ui-review`를 실행하세요. WPF 앱 자체에는 Node나 Playwright가 필요하지 않습니다.

기존 HTML/JavaScript 검색 화면과 업무 로직을 사용하지 않는 .NET 10 WPF 버전입니다. C# HttpClient에서 회사 EWS에 Negotiate/NTLM 통합 인증으로 연결합니다. WebView2와 JavaScript 패키지 의존성이 없습니다.

## 위치와 실행

- 보존한 기존 버전: `D:\기타 프로그램\Outlook VBA\OwaDesktopCleaner`
- 기존 WPF 작업 폴더(보존): `D:\기타 프로그램\Outlook VBA\OwaDesktopCleaner.Wpf`
- 이번 UI 수정 솔루션: `D:\기타 프로그램\Outlook VBA\.worktrees\wpf-js-ui\OwaDesktopCleaner.Wpf\OwaDesktopCleaner.Wpf.sln`
- Release 실행파일: `MailCleaner\bin\Release\net10.0-windows\OwaMailCleaner.Wpf.exe`
- Debug 실행파일: `MailCleaner\bin\Debug\net10.0-windows\OwaMailCleaner.Wpf.exe`

설치 상태를 실제 확인한 환경은 Visual Studio Professional 2026 18.9.2, .NET 데스크톱 개발 워크로드, .NET SDK 10.0.402입니다. Visual Studio 2026에서 위 `.sln`을 열고 **MailCleaner를 시작 프로젝트로 설정**한 뒤 F5로 실행하세요. Visual Studio 2022도 설치되어 있지만 이 솔루션은 2026에서 여세요.

화면은 `MailCleaner\Views\MainWindow.xaml`, 연결·검색·선택·상태 바인딩은 `ViewModels\MainViewModel.cs`에서 수정합니다. 시작 시 서버에 자동 접속하지 않습니다. XAML의 DataContext는 런타임에만 만들며 디자이너 경로에서는 ViewModel 생성, 서버 호출, 자격 증명 조회를 하지 않습니다. 실제 Visual Studio XAML 디자이너를 조작하는 수동 검증은 아직 하지 않았습니다.

## 사용 순서

1. **현재 Windows 계정으로 연결**을 누릅니다. 이 PC에서 해당 방식으로 실제 연결·조회가 성공했습니다. OWA 브라우저 로그인이나 기존 앱 실행은 필요하지 않습니다.
2. 필요한 폴더, 제목 키워드, 시작일·종료일을 지정합니다. 날짜는 한국시간 기준이며 종료일 전체를 포함합니다. 날짜를 비우면 해당 날짜 제한이 없습니다.
3. 기본 방식은 **조회만**입니다. 변경할 때는 처리 방식을 먼저 고른 다음 검색하세요. 일반 첨부 삭제는 첨부 메타데이터를 상세 조회하고 삭제 가능한 일반 첨부가 있는 메일만 표시합니다.
4. 행의 체크박스나 전체 선택으로 대상을 고릅니다. 날짜·용량 열 제목을 클릭하면 정렬됩니다. 실행 버튼 옆에서 비활성 이유를 확인할 수 있습니다.
5. **선택 항목 실행…**을 누른 뒤 폴더·작업·건수·용량을 최종 확인합니다. 확인 창의 기본 선택은 ‘아니요’입니다.

최초 연결 성공 후 변경 작업 허용은 기본 체크됩니다. 사용자가 해제한 설정은 새 앱의 설정 파일에 보존되며 재연결로 다시 켜지지 않습니다. 인증 만료 시 현재 연결의 변경 권한을 차단합니다. 사용자가 작업 방식을 고르고 검색·선택·실행·최종 확인하기 전에는 사서함 변경을 보내지 않습니다.

휴지통 정리는 **지운 편지함에서 검색하여 선택한 메일**에 SoftDelete를 적용합니다. 서버 보존 정책의 영향을 받으며 HardDelete를 사용하지 않습니다. 검색 결과 밖의 메일이나 하위 폴더 전체를 일괄 제거하지 않습니다.

검색 및 실행은 비동기이며 중단 요청을 지원합니다. 검색·변경 전 재조회는 취소할 수 있습니다. 이미 전송한 변경 요청은 응답 또는 통신 제한시간(최대 약 3분)을 기다리고 다음 메일부터 멈춥니다. 통신 오류나 응답 구조 이상으로 변경 결과가 불명확하면 결과 미확인으로 표시하고 다음 메일을 처리하지 않습니다. 앱 차원의 자동 재시도는 없으며 통합 인증의 프로토콜 challenge 응답은 HttpClient가 수행합니다.

처리 후 성공·실패·건너뜀·결과 미확인·미처리를 구분합니다. 최근 처리 결과는 작은 접힘 영역에 표시되고, 새 검색 시작 시 숨겨집니다. 완료 후에는 다시 검색하여 실제 서버 상태를 확인하세요.

## 인증과 격리

- 기존 프로젝트·출력·설정·프로필·자격 증명은 수정하거나 삭제하지 않았습니다. 코드와 아이콘 파일만 읽어 새 폴더에 필요한 내용을 복사했습니다.
- 앱/실행파일: `OWA MailCleaner · WPF` / `OwaMailCleaner.Wpf.exe`
- 단일 실행 Mutex: `Local\APS.OwaMailCleaner.Wpf`
- 사용자 데이터: `%LOCALAPPDATA%\APS.OwaMailCleaner.Wpf`
- 일반 설정 파일: 위 폴더의 `settings.json` — 변경 허용 선호만 저장합니다. 암호·토큰·메일 내용은 기록하지 않습니다.
- 선택적 자격 증명 저장 대상: `APS.OwaMailCleaner.Wpf:EWS:https://mail.apsfamily.com`
- 기존 자격 증명 대상과 WebView2 프로필에는 접근하지 않습니다. WPF용 저장 계정도 사용자가 로그인 창의 ‘불러오기’를 눌러야 읽습니다.
- ‘다른 EWS 계정으로 연결’에서 계정을 직접 입력할 수 있습니다. 저장 체크박스는 기본 해제이며 체크할 때만 Windows 자격 증명 관리자에 저장합니다. 이 경로와 저장·불러오기·삭제는 실제 계정으로 시험하지 않았습니다.
- TLS 인증서 검증을 유지하고 리디렉션을 따라가지 않습니다. Basic 인증으로 자동 전환하거나 회사 브라우저 쿠키를 추출하지 않습니다. 다른 PC나 회사 인증 정책 변경 후의 연결을 보장하지 않습니다.

## 구조

단일 WPF 앱 프로젝트에 View/ViewModel/Model/Service를 분리했습니다. 외부 MVVM 프레임워크나 EWS NuGet 패키지를 추가하지 않았습니다.

| 경로 | 역할 |
| --- | --- |
| `MailCleaner\Views` | 메인 XAML, 계정 입력 PasswordBox, 최종 확인 창 연결 |
| `MailCleaner\ViewModels` | 명령, 검색 조건, 선택 무효화, 진행률, 실행 가능 이유, 결과 영역 |
| `MailCleaner\Models` | 메일·폴더·첨부·검색 조건·처리 결과 |
| `MailCleaner\Services\AuthenticationService.cs` | 엔드포인트에 한정한 통합 인증 클라이언트 생성 |
| `MailCleaner\Services\WindowsCredentialStore.cs` | WPF 전용 Windows 자격 증명 저장·조회·삭제 |
| `MailCleaner\Services\SettingsService.cs` | WPF 전용 사용자 선호 설정 |
| `MailCleaner\Services\EwsTransport.cs` | SOAP 전송, 응답 파싱, 인증 만료, 변경 권한 검사 |
| `MailCleaner\Services\EwsQueryService.cs` | 폴더 페이지 조회, KST 날짜·제목 검색, 메일 상세 조회 |
| `MailCleaner\Services\MailMutationService.cs` | 검색 결과 1회 사용, 최신 항목 검증, 변경 SOAP, 결과 분류 |
| `ConnectionCheck` | 최초 경로 검증용 읽기 전용 콘솔 프로그램 |
| `Tests` | 실제 서비스·ViewModel에 모의 HTTP 응답을 주입하는 테스트 실행기 |

변경 직전에 ID·부모 폴더·IPM.Note 항목 종류·최신 ChangeKey를 확인합니다. 일반 첨부는 FileAttachment 중 인라인/ContentId/ContentLocation이 없는 첨부만 허용하며, 검색·선택 때 보유한 첨부 ID와 직전 재조회한 첨부 ID의 교집합으로 제한합니다. 읽음 처리에는 `SuppressReadReceipts=true`를 적용하고 최신 ChangeKey의 충돌은 덮어쓰지 않습니다. 변경 요청에 사용한 검색 결과는 즉시 소모하여 중복 실행을 차단합니다.

## 기초 WPF 구현 당시의 검증 기록 — 2026-10-20 (한국시간)

- .NET 10 Debug/Release 솔루션 빌드: 경고 0, 오류 0.
- WPF 서비스/ViewModel 모의 테스트: 26개 통과. 검색 파싱·KST 경계·조건/모드 변경·선택 무효화·설정 유지·최종 확인·결과 숨김·취소·인증 거부·부분 실패·결과 미확인·중복/동시 변경 차단·일반 첨부 교집합 등을 검사했습니다.
- 별도 연결 검증 모의 테스트: 20개 통과.
- 실제 EWS 무인증 조회: HTTP 401, 서버 제시 인증 방식 Negotiate/NTLM.
- 현재 Windows 계정으로 실제 GetFolder/FindFolder/FindItem: HTTP 200 및 SOAP NoError.
- 완성한 WPF C# 조회 서비스: 폴더 10개, 당일 지운 편지함 조회 0건, 변경 권한 false 상태에서 검증.
- 실제 WPF 창 + ViewModel: Windows 통합 인증, 폴더 10개, 2026-10-20 받은편지함 검색 성공. 최종 Release 실행파일 검증에서는 3건을 조회했습니다. 조회만 모드로 수행했으며 변경 명령을 호출하지 않았습니다.
- 실제 Debug/Release 창 및 로그인 창 초기화, 아이콘 리소스 로딩, 메인 화면 레이아웃 렌더링 확인. 최종 Release EXE의 초기화 검사와 실제 조회 검사 모두 종료 코드 0입니다. EXE 아이콘 리소스 1개를 확인했고 복사한 ICO의 SHA-256이 원본과 일치합니다. 렌더링 결과는 `MailCleaner\bin\<구성>\net10.0-windows\Smoke\main-window.png`에 있습니다.
- 실제 사서함의 첨부 삭제·읽음 변경·휴지통 이동·SoftDelete는 **시험하지 않았습니다**. 사용자가 지정한 테스트 메일과 작업으로 별도 확인해야 합니다. 모의 성공이 실서버 변경 성공을 뜻하지 않습니다.

실행 명령 (새 솔루션 폴더에서):

```powershell
dotnet build .\OwaDesktopCleaner.Wpf.sln -c Debug -m:1
dotnet build .\OwaDesktopCleaner.Wpf.sln -c Release -m:1
dotnet run --project .\Tests -c Release
dotnet run --project .\ConnectionCheck -c Release -- --self-test
# 네트워크 없이 창·아이콘 초기화 검사 후 자동 종료
dotnet .\MailCleaner\bin\Release\net10.0-windows\OwaMailCleaner.Wpf.dll --smoke-test
```

실제 서버 조회 및 현재 Windows 계정 인증은 회사망 접근이 가능한 환경이 필요합니다. 이번 Codex 샌드박스 안에서는 DNS가 실패했으나 승인된 샌드박스 밖 실행에서는 같은 C# 경로로 성공했습니다. 일반 실행 중 문제가 있으면 회사망/VPN·계정·회사 EWS 정책을 먼저 확인하세요. 기존 앱 로그인 데이터를 복사할 필요가 없습니다.
