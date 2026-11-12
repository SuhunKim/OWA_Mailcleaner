# 로컬 구자료 보관본 목록 (`_local/_archive`)

> 이 문서는 git에 올라가지만, 설명하는 압축본 자체는 **로컬 전용**(`_local/`은 `.gitignore` 제외)이다. 원격 저장소에는 압축본이 없다.
> 같은 내용의 사본이 `_local/_archive/INDEX.md`에 있다. 정리 기준일: 2026-11-12.

## 요약

| 압축본 | 내용 | 시점 | 새 저장소의 대응 위치 |
|---|---|---|---|
| `OwaDesktopCleaner_261019.zip` | WebView2 데스크톱 버전 스냅샷 | 2026-10-19 | `OWA/OwaDesktopCleaner` |
| `2026-10-23-local-cleanup/Outlook_Mail_Cleaner_Web.zip` | 구 브라우저 스크립트(v1.0.x) | 2026-10-23 정리 | `OWA/Outlook_Mail_Cleaner_Web` |
| `2026-10-23-local-cleanup/OwaDesktopCleaner.Wpf.zip` | 초기 WPF 프로젝트 | 2026-10-23 정리 | `OWA/OwaDesktopCleaner.Wpf` |
| `2026-10-23-local-cleanup/OwaDesktopProbe.zip` | 연결 진단 프로브 + 빌드·시험 산출물 | 2026-10-23 정리 | `OWA/OwaDesktopProbe` |
| `2026-10-23-local-cleanup/_backup.zip` | 과거 Outlook **VBA** 매크로 백업 | 2026-02 | **새 저장소에 없음(이 압축본이 유일)** |
| `loose-files/Outlook_Mail_Cleaner_Distribution_v1.1.0.zip` | 웹 v1.1 배포 패키지 | v1.1.0 | `OWA/Outlook_Mail_Cleaner_v1.1/build-distribution.ps1`로 재생성 가능 |

압축본 안의 소스는 현재 저장소보다 **옛 버전**이다. 현재 소스는 저장소의 `OWA/`를 쓰고, 이 압축본들은 옛 상태가 필요할 때만 푼다.

## 풀기 전 주의

- 저장소 폴더 안에 바로 풀지 않는다. 새 저장소와 같은 이름의 폴더가 생겨 혼동된다. 비어 있는 별도 폴더에 푼다.
- `OwaDesktopCleaner_261019.zip`만 **최상위 폴더 없이** 파일이 바로 들어 있다. 반드시 새 폴더를 만들어 그 안에 푼다.
- 풀기 예시(PowerShell):

```powershell
Expand-Archive "_local\_archive\2026-10-23-local-cleanup\OwaDesktopProbe.zip" -DestinationPath "C:\temp\restore"
Expand-Archive "_local\_archive\OwaDesktopCleaner_261019.zip" -DestinationPath "C:\temp\restore\OwaDesktopCleaner_261019"
```

## 압축본별 상세

### `OwaDesktopCleaner_261019.zip` (26.5MB, 1489개 파일)
- 최상위 폴더 없음. 풀면 `App.xaml`, `MainWindow.xaml(.cs)`, `EwsLoginWindow.xaml(.cs)`, `WindowsCredentialStore.cs`, `cleaner.js`, `OwaDesktopCleaner.csproj`, `README.md`가 바로 나온다.
- `assets/`: 아이콘(`owa-cleaner.ico`)과 아이콘 시안 `icon-concepts-20261019/`(PNG 4개, `prompts.md`).
- `tests/`: 시험 코드(`core.test.cjs`, `desktop.test.cjs`, `package*.json`)와 `node_modules`(약 1200개 파일).
- `bin/`, `obj/`, `.vs/`: 빌드 산출물. 필요 없으면 무시한다.

### `Outlook_Mail_Cleaner_Web.zip` (0.7MB, 210개 파일)
- 최상위 폴더 `Outlook_Mail_Cleaner_Web/`.
- 소스 32개: `Outlook_Mail_Cleaner.user.js`(Tampermonkey 스크립트), `distribution/`, `OwaMailCleaner.Controls*`(WPF 컨트롤 데모·시험), `build-distribution.ps1`, `package.json`, `README.md`.
- 현재 저장소에 없는 파일: `Outlook_Mail_Cleaner.user.zip`, `Outlook_Mail_Cleaner_Distribution_v1.0.0.zip`(v1.0.0 배포 패키지).
- 나머지(약 170개)는 `bin/`, `obj/`, `.vs/` 빌드 산출물이다.

### `OwaDesktopCleaner.Wpf.zip` (10.3MB, 211개 파일)
- 최상위 폴더 `OwaDesktopCleaner.Wpf/`.
- 소스 31개: `MailCleaner/`(앱: Services, ViewModels, Views, Assets), `ConnectionCheck/`, `Tests/`, `README.md`.
- 현재 저장소에 없는 파일: `build.ps1`, `dotnet-tools.json`, `global.json`.
- 나머지는 `bin/`, `obj/`, `.vs/` 빌드 산출물이다.

### `OwaDesktopProbe.zip` (5.0MB, 146개 파일)
- 최상위 폴더 `OwaDesktopProbe/`.
- 소스 11개: `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `probe.js`, `Publish-Probe.ps1`, `IMPLEMENTATION-PREP.md`, `README.md`, `.csproj`.
- 산출물: `artifacts/probe-20261019-134955`, `artifacts/probe-20261019-135329`(빌드 결과와 zip), `test-runs/`(압축 배포 시험 2회), `verified-build/`, `bin/`, `obj/`. 모두 현재 저장소에서는 `.gitignore`로 제외하는 종류다.

### `_backup.zip` (32KB, 25개 파일)
- 최상위 폴더 `_backup/`. **Outlook VBA 시절(2026-02)의 매크로 백업**이며 새 저장소에는 없다.
- `MailCleaner.bas`와 날짜별 `MailCleaner_260205_1636/1705`, `_260206`, `_260206_2`, `_260220.bas`.
- 폼 `frmMailCleaner.frm/.frx`와 날짜별 사본(260205~260220).
- `ModuleNewsAndDaily.bas/.zip`, `일보,뉴스클리핑 삭제.cls`, `Outlook VBA_260205.zip`, `Outlook VBA_260206.zip`.

### `loose-files/Outlook_Mail_Cleaner_Distribution_v1.1.0.zip` (34KB)
- 정리 전 `OutlookVBA`의 `Outlook_Mail_Cleaner_v1.1(Web)/`에만 있던 빌드 산출물. 저장소에는 이 zip이 없지만 `build-distribution.ps1`로 다시 만들 수 있어 참고용으로만 보관한다.

## 정리 기록·검증 파일 (`2026-10-23-local-cleanup/`)

| 파일 | 용도 |
|---|---|
| `cleanup-record.json` | 어떤 폴더를 어떤 압축본으로 만들었는지(원래 경로, 파일 수, 용량, SHA-256) |
| `*.manifest.json` | 압축본별 파일 단위 SHA-256 목록 |
| `preserved-before.json` | 정리 전 보존 대상 목록 |
| `preservation-verification.json` | 정리 후 보존 확인 결과 |

`cleanup-record.json`의 원래 경로(`D:\기타 프로그램\Outlook VBA\...`)는 저장소를 `MailCleaner`로 재구성하기 전의 옛 경로다. 압축본 해시는 2026-11-12에 `cleanup-record.json` 기록과 일치함을 확인했다.

## SHA-256 (2026-11-12 확인)

| 압축본 | SHA-256 |
|---|---|
| `Outlook_Mail_Cleaner_Web.zip` | `00E8F460769686E19D05A19FF3FD7C748506663AD5B2002A5EC31FEF15D6DC32` |
| `OwaDesktopCleaner.Wpf.zip` | `056DC9A3AAEFF45BCE970584D2BCCA9FFE128595F26FED7CDE9E73C976FEC962` |
| `OwaDesktopProbe.zip` | `F8B4B97D4D3A72E56F3460CDBB6D263BA4DA369ACB420C74D3A87348C51233B9` |
| `_backup.zip` | `5A5E315D84C4F7DBA8D6B97859CB78A469369BF6CE311513D9ADD955DE154784` |
| `OwaDesktopCleaner_261019.zip` | `720FC2BD6A1C1094BE4F7C895793B0BF3FA07BD42E3C9E6F5F5BCA39F1CE5BC5` |
| `Outlook_Mail_Cleaner_Distribution_v1.1.0.zip` | `5C1ACDCEC26CE0EA56CEA99ACF8D9BBC67B09545F8D91C549B186E0E7A78815B` |

`OwaDesktopCleaner_261019.zip`과 `Outlook_Mail_Cleaner_Distribution_v1.1.0.zip`은 기존 기록에 해시가 없어 이번에 처음 계산한 값이다.

## `_local`의 나머지

- `_local/MailCleanerBase.git`: 현재 작업 중인 저장소의 git 본체다(bare 아님, worktree 연결). **삭제 금지.**
