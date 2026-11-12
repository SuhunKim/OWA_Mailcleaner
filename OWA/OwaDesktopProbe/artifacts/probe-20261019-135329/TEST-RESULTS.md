# 테스트 결과 — 2026-10-19

## 수행 및 결과

| 시험 | 결과 | 범위 |
|---|---|---|
| 기존 ZIP 신규 폴더 압축 해제 | 통과 | artifacts/probe-20261019-134955.zip |
| ZIP 파일 SHA-256 | 17개 모두 일치 | 각 파일을 SHA256SUMS.txt와 비교 |
| 검증 앱 JavaScript 계약 테스트 | 18/18 통과 | 아래 재현 명령, 네트워크 없음 |
| 기존 v1.1 코어 테스트 | 21/21 통과 | 기존 원본 수정 없음 |
| 최종 WPF Release 빌드 | 오류 0, 경고 0 | net8.0-windows |
| 실제 UI 실행 및 서버 재시험 | 미실행 | 화면 도구 자동 승인 검토의 모델 용량 부족으로 실행되지 않음 |

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

## 남은 실제 시험

추가 인증 후 폴더·메일 조회 성공은 이전 사용자 확인으로 보유하고 있지만,
이번 수정본의 실제 HTTP/SOAP 코드·건수 및 UI 동작은 아직 재확인하지 않았다.
사용자에게 기본 인증과 읽기 전용 조회 후 민감 정보를 제외한 코드·건수만 요청했다.
현재 결과로 실제 서버 재시험까지 통과했다고 주장하지 않는다.
