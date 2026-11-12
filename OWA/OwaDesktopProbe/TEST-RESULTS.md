# 테스트 결과 — 2026-10-19

## 수행 및 결과

| 시험 | 결과 | 범위 |
|---|---|---|
| 기존 ZIP 신규 폴더 압축 해제 | 통과 | artifacts/probe-20261019-134955.zip |
| ZIP 파일 SHA-256 | 17개 모두 일치 | 각 파일을 SHA256SUMS.txt와 비교 |
| 수정본 ZIP 신규 압축 해제 및 SHA-256 | 18개 모두 일치 | artifacts/probe-20261019-135329.zip; 포함된 probe.js와 테스트한 원본 해시 일치 |
| 검증 앱 JavaScript 계약 테스트 | 18/18 통과 | 아래 재현 명령, 네트워크 없음 |
| 기존 v1.1 코어 테스트 | 21/21 통과 | 기존 원본 수정 없음 |
| 최종 WPF Release 빌드 | 오류 0, 경고 0 | net8.0-windows |
| 실제 서버 재시험 | 통과 (사용자 직접 실행·제공 로그) | GetFolder/FindFolder/FindItem 모두 HTTP 200, SOAP NoError; 폴더 11개, 메타데이터 5건 |

## 발견 및 수정

FindItem 응답에서 RootFolder/Items 구조가 누락되어도 성공·0건으로 보고하던 오류를 재현했다.
회귀 테스트는 수정 전 실패했고, probe.js가 누락 구조를 INVALID_PAGE로 거부하도록 수정한 뒤 통과했다.
정상적인 빈 폴더(유효한 RootFolder/Items, 항목 0개)는 계속 성공·0건으로 처리한다.

## 새 테스트의 확인 범위

HTTP 401/403/440/500, 로그인 리디렉션, HTML 로그인 페이지 반환, 네트워크/제한시간 구분,
신뢰 출처와 로그인 화면 차단(요청 0회), XML 파서 오류 전달, HTTP 200의 SOAP 오류,
폴더 페이지 완료/중복 제거/페이지 반복 차단, 메일 요청 최대 5건·메타데이터 필드·XML 이스케이프,
잘못된 결과 구조를 성공으로 처리하지 않는 동작을 검사했다.

tests/probe.test.cjs는 실제 probe.js를 격리된 Node VM에서 실행하며 fetch 응답과 파싱된 DOM을 fixture로 제공한다.
실제 XML 파서, WebView2 UI, 회사 인증, 서버 응답을 검증하는 테스트가 아니다.
사서함 변경 요청을 실제 서버에 보내지 않았다. 기존 변경 기능의 테스트도 로컬 모의 시험이다.

## 재현

프로젝트 폴더에서:
```powershell
node --test .\tests\probe.test.cjs ..\Outlook_Mail_Cleaner_v1.1\tests\core.test.js
dotnet build .\OwaDesktopProbe.csproj -c Release --no-restore
.\Publish-Probe.ps1
```

## 실제 서버 재시험 증거

사용자가 제공한 2026-10-19 앱 로그 기준:

- 13:53:46 WebView2 Runtime 155.0.4283.62, https://mail.apsfamily.com/EWS/Exchange.asmx
- 13:53:58 및 13:54:08 WebView2 기본 추가 인증 요구. 두 번 표시된 원인은 로그만으로 특정하지 않는다.
- 13:54:17 GetFolder HTTP 200 (18613ms), SOAP NoError
- 13:54:17 FindFolder HTTP 200 (72ms), SOAP NoError, 폴더 11개 전체 페이지 완료
- 13:54:21 GetFolder HTTP 200 (75ms), SOAP NoError
- 13:54:21 FindFolder HTTP 200 (80ms), SOAP NoError, 폴더 11개 전체 페이지 완료
- 13:54:23 FindItem HTTP 200 (100ms), SOAP NoError, 메타데이터 5건

판정: 승인된 추가 인증 방식을 포함한 1단계 연결 검증 통과.
도구 직접 화면 재확인은 승인 검토 용량 문제로 실행하지 못했으나 사용자가 직접 실행한 앱 로그로 실제 서버 결과를 확인했다. 이번에 추가 서버 요청이나 사서함 변경 작업을 실행하지 않았다.

## 남은 실제 시험

- 강제 인증 만료 후 재로그인, 추가 인증 취소/실패 복구.
- 로그아웃 후 HTTP 인증 캐시까지 정리되는지, 다른 계정으로 전환 시 이전 사서함 정보가 남지 않는지.
- 팀원 PC 일반 권한 실행, .NET/WebView2 의존성 및 회사 보안 정책.
- 향후 본 앱의 검색/선택/진행률과 별도 승인 후 변경 기능 시험.

현재 결과는 연결 검증 앱의 통과이며 전체 클리너 완성을 뜻하지 않는다.
