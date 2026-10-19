# 데스크톱 클리너 구현 준비

작성: 2026-10-19. 대상 소스: 기존 Outlook_Mail_Cleaner_v1.1. 이번 범위는 검증 앱과 구현 준비이며, 전체 클리너나 사서함 변경 기능 구현 완료를 뜻하지 않는다.

## 1. 연결 검증 결론과 증거 수준

사용자가 WebView2 기본 추가 인증을 허용했고, 수정 앱에서 직접 인증한 뒤 “추가 인증 후 폴더·메일 조회 성공”을 확인했다. 이 사용자 실사용 확인을 근거로 구현 준비에 진입한다.

개발 도구가 직접 확인한 범위는 앱 빌드/실행, OWA 실제 로그인, EWS 추가 인증 요구 및 이를 취소했을 때의 HTTP 401이다. 추가 인증 허용 이후의 성공 HTTP/SOAP 응답과 조회 건수는 도구 승인 검토의 용량 문제로 직접 수집하지 못했다. 현재 앱 프로세스도 실행 중이지 않아 지난 결과를 복구할 수 없다. 폴더 수, 성공 시각, NoError 응답을 임의로 적지 않는다.

따라서 기록은 **사용자 실사용 성공 확인 / 성공 응답 원문 재확인 미완료**로 분리한다. 팀원 배포 승인 전에 시험 계정으로 동일 경로를 재현하고, 계정명·메일 제목 없이 작업명/HTTP/SOAP 코드/건수만 남긴다. 사용자가 이미 마친 인증을 문서 작성을 위해 다시 요구하지 않는다.

## 2. 유지할 인증·통신 경로

WPF 창 → 앱 전용 WebView2 프로필 → 실제 회사 OWA 로그인 → 사용자의 로그인 완료 확인 → 동일 OWA 문서에서 상대 경로 EWS fetch(credentials: include) → 필요 시 WebView2 기본 HTTP 인증 창 → SOAP 응답 파싱 → WPF DTO 표시.

- EWS 기본 인증 창은 OWA와 같은 계정으로 입력한다. OWA 계정과 EWS 계정의 일치가 코드로 자동 증명되는 것은 아니다. 첫 메일 목록을 사용자가 확인하고, 계정 전환 시 선택 목록/검색 결과/인증 상태를 모두 폐기한다. 대리 사서함 처리는 초기 범위에서 제외한다.
- 쿠키 추출, 로컬 HTML fetch, 별도 HttpClient 인증 경로로 갈아타지 않는다.
- 추가 인증은 진행 중인 사용자 조회, 신뢰 OWA 문서, 정확한 회사 HTTPS EWS URL에만 허용한다. Response.UserName/Password를 읽거나 설정하지 않는다. 요청 단위 인증 요구 횟수 제한을 유지한다.
- TLS/브라우저 보안 해제, 전역 Windows 인증 정책 변경, Outlook 의존성은 추가하지 않는다.
- WebView2 이벤트 이름 BasicAuthenticationRequested는 NTLM 등을 포함한다. Basic 평문 자격 증명 전송 방식으로 변경했다는 뜻이 아니다.
- 현재 인증 처리는 MainWindow, SOAP/파싱은 probe.js에 있다. 검증 앱은 기준선으로 보존하고 본 앱 프로젝트로 분리할 때 추출한다.

## 3. 앱 책임 분리

| 구성 | 책임 | 금지/주의 |
|---|---|---|
| Desktop Shell / ViewModels | 검색 조건, 목록, 선택, 진행률, 중단, 결과 | WebView에 임의 JavaScript/SOAP 문자열 전달 금지 |
| SessionCoordinator | OWA 화면, 기본 추가 인증, 만료·로그아웃, 계정 전환, 전용 프로필 | 비밀번호·쿠키·토큰 접근 및 로그 금지 |
| OwaWebViewTransport | 출처 재검사, 허용 operation, 요청 ID, 제한시간, 취소, navigation 세대 확인 | 범용 네이티브 메시지/호스트 객체 제공 금지 |
| EwsCore.js | XML 생성·파싱·날짜 경계·첨부 판단·결과 분류 | DOM UI 설치 코드와 분리, 본문/첨부 내용 다운로드 불필요 |
| CleanerService | 검색 스냅샷, 선택 항목 재확인, 순차 배치, 부분 성공/실패 | 변경 작업의 불명확한 응답 자동 재시도 금지 |
| ResultModel | item별 성공/실패/건너뜀/미확정/중단 전 완료 | 전부 성공으로 합치거나 추정 용량을 실측으로 표시 금지 |

필수 DTO: FolderRef(Id, ParentId, Path, Class), MessageRef(Id, ChangeKey, ParentId, Subject, Sender, ReceivedUtc, IsRead, Size?, Attachments), SearchCriteria, OperationResult. 비밀번호나 Cookie 필드는 만들지 않는다.

현재 검증 앱은 ExecuteScriptAsync로 요청하고 임의 요청 키의 결과를 polling한다. 본 앱도 이 확인된 경로를 우선 유지한다. transport에서 동시 요청 제한, 요청별 AbortController, 탐색 세대 검증, 결과 정리를 강화한다. 수천 메일은 페이지/배치 단위 DTO로 전달한다. 메시지 브리지가 필요해질 때만 출처·operation·요청 ID·스키마 검증을 추가하고 다시 시험한다.

## 4. 로그인 → 클리너 → 재로그인 흐름

1. 초기화: Runtime 확인, 전용 프로필 준비. 세션 보존은 선택 옵션이고 비밀번호 저장 기능이 아니다.
2. LoginRequired: 실제 회사 OWA 화면을 표시한다. 로그인 URL 도달만으로 성공 처리하지 않는다.
3. OwaConfirmed: 사용자가 메일 화면을 확인. EWS 상태는 아직 미확인.
4. EwsAuthenticating: GetFolder 실행. 필요 시 기본 인증 창 표시. 취소/401/403/440/리디렉션/HTML/네트워크/시간 초과를 구분한다.
5. Ready: EWS 성공 후 검색·선택 가능. EWS 계정이 의도한 사서함인지 사용자 확인.
6. Running: 조건과 선택을 고정하고 중복 실행 차단. 중단 요청은 다음 배치 시작을 막고 가능한 읽기 요청을 취소한다.
7. AuthRequired: 처리 중 인증 만료 시 중단, 이미 처리한 항목과 결과 불명 항목을 분리. 로그인 후 재검색/재확인한다. 변경 작업을 자동으로 이어 보내지 않는다.
8. Logout: 결과·선택 메모리 폐기, 전용 브라우징 데이터 정리. 서버 전체 SSO 로그아웃과 구분한다. HTTP 인증 캐시까지 지워졌는지는 새 프로필/프로세스 재시작 시험으로 확인한다. 확인 전 쿠키 정리만으로 계정 전환 안전을 보장하지 않는다.

## 5. 기존 JavaScript 재사용/수정 범위

| 소스 기능 | 처리 |
|---|---|
| xmlEscape, 날짜 검증, kstBoundaryUtc, chunk | 순수 함수 추출·재사용. 기존 경계값 테스트 이관 |
| buildGetFolderBody, buildFindFoldersBody, buildFindItemsBody, buildGetItemsBody | SOAP 생성기 재사용. 메타데이터 필드만 유지 |
| parseFolders, parseMessages, parseAttachments, pagingInfo, responseInfos, statusesFor | 재사용하되 namespace/누락 필드/부분 오류/페이지 반복 테스트 보강 |
| isNormalAttachment, buildFolderPaths, sortMessages, parseSize, totalSize | 재사용. null 크기·동명 폴더·인라인 첨부 보존 확인 |
| EwsClient.call | 동일 fetch 출처 유지, WPF 상태/취소/기본 인증 대기/오류 분류와 통합 |
| updateRead, moveToDeleted, deleteAttachments, emptyDeleted | 로직 후보만 유지. 별도 승인된 시험 전 production 활성화 금지 |
| installStyles, installDrag, createModal, installLauncher, HTML 행 생성, userscript 메타데이터 | WPF View/ViewModel로 대체. Tampermonkey 설치 로직 이식하지 않음 |
| 기존 C# Controls | 시각 컨트롤 후보. net8.0-windows 참조 및 키보드/DPI 시험 후 재사용 판단 |
| 기존 Demo | 샘플 데이터 UI. 실제 EWS 구현으로 재사용하지 않음 |

## 6. 기능 이식 순서와 합격 시험

| 순서 | 기능 | 검증 |
|---|---|---|
| 1 | 폴더·제목·기간 검색 | 실제 폴더 ID/경로, 페이지 전체 처리, 특수문자 제목, KST 시작 포함·종료 다음날 미포함, 폴더 변경 시 결과 초기화. 읽음 상태 변경 없는 메타데이터 조회 |
| 2 | 대상 목록·개별 선택 | 선택 ID를 기준으로 처리 대상 고정. 정렬 후 선택 유지, 재검색 후 이전 선택 제거, 0건/전체선택/일부선택. 실행 직전 대상 수·동작 표시 |
| 3 | 정렬·용량·진행률·중단·결과 | null 크기는 미확인으로 표시, 날짜/용량 정렬, 페이지 검색 진행률과 처리 진행률 분리, 중단 시 다음 배치 금지, 부분 성공·실패 합계 일치 |
| 4 | 읽음 처리 | 승인된 시험 메일에서 UpdateItem, SaveOnly, SuppressReadReceipts=true 확인. 이미 읽은 항목 처리, 부분 오류. 수신 측 읽음 확인 미발송 검증 |
| 5 | 삭제된 항목으로 이동 | 승인된 시험 메일의 부모 폴더 전후 확인, deleteditems 대상으로만 이동. 실패/중단 후 중복 이동 방지 |
| 6 | 일반 첨부 삭제·인라인 보존 | 승인된 시험용 복사본에 일반 파일+인라인 이미지+특수 첨부 혼합. IsInline/ContentId 판별, 최신 ChangeKey 확보, 실행 직전 첨부 재조회, 인라인 유지·일반 첨부만 제거·크기 변화 확인 |
| 7 | 휴지통 SoftDelete | 승인된 시험 항목의 현재 부모가 deleteditems이고 메일 클래스인지 재확인. DeleteType=SoftDelete, SendMeetingCancellations=SendToNone, HardDelete 배제. 이동된/이미 삭제된 항목 건너뜀 |

4~7은 이번에 수행하지 않는다. 사용자가 별도로 승인한 시험 계정·시험 메일·작업 범위를 정한 뒤 수행한다. 로컬 모의 테스트는 XML과 부분 실패 로직 검증용이며 실사서함 합격의 대체물이 아니다.

## 7. 배포 준비와 실제 확인 범위

- 이 PC: Windows Desktop Runtime 8.0.32 및 10.0.13 설치 확인. 검증 앱 타깃은 net8.0-windows, WebView2 SDK 1.0.3537.50. 최초 실제 실행 Runtime 155.0.4283.45.
- 현재 검증 앱은 framework-dependent portable 폴더로 publish한다. ZIP을 풀고 EXE를 실행하며 DLL/JS/runtimes를 함께 보존한다. .NET 8 Windows Desktop Runtime과 Evergreen WebView2 Runtime이 필요하다. .NET SDK나 Outlook/확장은 사용자의 실행 요건이 아니다.
- Publish-Probe.ps1은 기존 출력/배포본을 덮어쓰지 않는 타임스탬프 폴더, 필수 파일 점검, SHA-256 목록, ZIP을 만든다. 프로필/인증 정보는 포함하지 않는다. 설치 프로그램이나 업데이트 서비스는 만들지 않았다.
- 본 앱은 .NET 10 LTS + win-x64 self-contained 폴더 배포를 우선 후보로 둔다. .NET 8 지원은 2026-11-10 종료 예정이므로 장기 배포 기반으로 고정하지 않는다. 전환 후 동일 연결 시험을 다시 수행한다. 현재 검증 앱의 타깃은 기준선 보존을 위해 변경하지 않았다.
- self-contained는 .NET 별도 설치를 줄이지만 WebView2 Runtime을 포함하는 것은 아니다. Runtime 보안 업데이트와 앱에 포함된 .NET 업데이트 책임을 따로 관리한다.
- WebView2 Evergreen은 사용자별 설치가 가능하며, 관리자 실행 시 컴퓨터 전체 설치가 될 수 있다. 회사 정책이 사용자 설치를 막으면 IT 배포가 필요하다. 실제 팀원 PC에서 관리자 권한 불필요를 아직 확인하지 않았다.
- 앱은 사용자 쓰기 가능 폴더와 LocalAppData를 사용하고 관리자 권한 요청 manifest를 추가하지 않는다. 사내 코드 서명/SmartScreen/백신 허용 정책은 배포 전 IT 확인 대상이다. 보안 기능을 끄는 안내는 하지 않는다.
- 팀원 인수 시험: 일반 권한, 도메인 가입/미가입 조건, 사내망/VPN, Runtime 유무, 첫 로그인/추가 인증 취소/재로그인/세션 정리/다른 계정, 폴더·메일 5건. 팀원 인증 경로 차이가 있으면 출처 허용 범위를 무작정 넓히지 않는다.

공식 근거:
- https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- https://learn.microsoft.com/en-us/dotnet/core/deploying/
- https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/

## 8. 다음 구현 단위

새 본 앱 프로젝트에서 SessionCoordinator + OwaWebViewTransport와 읽기 전용 검색/목록만 먼저 추출한다. 기존 검증 앱은 연결 기준선으로 유지한다. 팀원 시험에서 동일 인증 경로와 의도한 사서함이 확인된 뒤 선택/진행률을 붙이고, 사용자 별도 승인 전에는 변경 명령을 비활성으로 둔다. 기능 비활성 패키지를 완성 클리너로 부르지 않는다.
