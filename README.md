# OWA_Mailcleaner
For Groupware Mail Management

메일 사이트별 정리 도구 모음입니다. 하나의 저장소를 프로그램 종류별 폴더로 나눕니다.

## 폴더 구조

| 폴더 | 내용 |
|---|---|
| `OWA/` | Exchange OWA(`mail.apsfamily.com`)용 도구 |
| `OWA/Outlook_Mail_Cleaner_Web` | 브라우저 사용자 스크립트(구버전) |
| `OWA/Outlook_Mail_Cleaner_v1.1` | 브라우저 사용자 스크립트 v1.1 (휴지통 정리·정렬 포함) |
| `OWA/OwaDesktopCleaner` | WebView2 데스크톱 버전 |
| `OWA/OwaDesktopCleaner.Wpf` | 네이티브 WPF 버전(EWS 통합 인증) |
| `OWA/OwaDesktopProbe` | 연결 진단용 프로브 |
| `SCH/SchMailCleaner` | 순천향 그룹웨어(`hg.sch.ac.kr`)용 독립 EXE. 별개 프로그램 |
| `docs/` | 설계·인수인계 문서 |
| `_local/` | 로컬 전용(git 제외): `MailCleanerBase.git`(이 저장소의 git 본체, 삭제 금지), `_archive/`(구자료 압축본. 내용·복원 방법은 [docs/local-archive-index.md](docs/local-archive-index.md)) |

## 빌드

각 프로젝트 폴더의 `README.md`를 따릅니다. 빌드 산출물(`bin/`, `obj/`, `_bin/`, `publish/`)은 git에 올리지 않습니다.
배포용 exe·zip은 크기 때문에 저장소에 포함하지 않으며 별도로 보관합니다.
