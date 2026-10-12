# Ongi 개발 문서

## 구성

```
Ongi\
├─ src\               C# 소스(.NET Framework 4.8, C# 7.3)
│  ├─ Program.cs      진입점, 명령줄 옵션, 진단(--probe, --caches, --selftest), 미리보기 렌더
│  ├─ App.cs          수명 주기: 조회 일정·백오프, 캐시 스캔, 알림, 메뉴, 설정 적용
│  ├─ Providers.cs    Claude·Codex 사용량 조회, 샘플 데이터
│  ├─ CacheTracker.cs 대화 기록에서 채팅별 프롬프트 캐시 상태 추정
│  ├─ Render.cs       링 아이콘, 작업표시줄 위젯, 상세 패널, 주간 페이스 그리기
│  ├─ BarWindow.cs    작업표시줄 자식 레이어드 창(실패하면 떠 있는 창)
│  ├─ DetailPopup.cs  상세 패널 창
│  ├─ SettingsForm.cs 설정 창과 직접 그린 컨트롤(토글·분할 버튼·증감 버튼)
│  ├─ Core.cs         설정·캐시·로그·자동 실행·색 팔레트·공용 모델
│  └─ Native.cs       Win32 호출
├─ assets\Ongi.ico    빌드할 때 없으면 생성
├─ app.manifest       PerMonitorV2 DPI, Windows 8+ 호환(자식 레이어드 창에 필요)
├─ build.ps1          빌드 → bin\Ongi.exe
├─ package.ps1        빌드 → 셀프테스트 → dist\Ongi-<버전>.zip + SHA-256
├─ README.md          사용 설명서(배포 zip에 들어감)
└─ DEVELOP.md         이 문서
```

## 빌드와 패키징

Visual Studio 2022 또는 Build Tools가 필요합니다. `vswhere`로 Roslyn `csc.exe`를 찾습니다.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1     # bin\Ongi.exe (실행 중이었으면 빌드 후 다시 켬)
powershell -ExecutionPolicy Bypass -File package.ps1   # dist\Ongi-<버전>.zip, .sha256
```

- 버전은 `src\Program.cs`의 `AssemblyInformationalVersion`(표시용)과 `AssemblyFileVersion`에서 바꿉니다.
- 배포 zip에는 `Ongi.exe`와 `README.md`만 들어갑니다.
- `bin\Ongi.exe`는 git으로 받아서 바로 실행할 수 있게 리포에 커밋합니다(`bin\`의 다른 파일은 무시). 버전을 올릴 때는 `package.ps1`로 만든 exe를 소스와 같은 커밋에 넣고, 같은 exe를 zip으로 릴리스에 올립니다.

## 명령줄 옵션

| 옵션 | 설명 |
| --- | --- |
| (없음) | 위젯 실행. 이미 실행 중이면 그쪽에 위젯을 다시 붙이고 새로고침하라고 알립니다. |
| `--exit` | 실행 중인 Ongi 종료 |
| `--floating` | 이번 실행만 떠 있는 창 |
| `--probe` | 실제 자격 증명으로 한 번 조회해 결과 출력(토큰 값은 출력하지 않음) |
| `--caches` | 로컬 대화 기록만 읽어 채팅별 캐시 상태 출력 |
| `--selftest` | 내장 예시로 파서·표기·설정 창 검사. 종료 코드 = 실패 수 |
| `--mock` | 자격 증명 없이 샘플 데이터로 실행 |
| `--preview <png>` | 샘플 데이터로 위젯·패널을 `<png>`에, 설정 창을 `<png>-settings.png`에 렌더 |
| `--test-alert` | 샘플 캐시 만료 알림 표시(샘플 데이터 모드) |
| `--exit-after <초>` | 테스트용 자동 종료 |

출력은 콘솔과 `%LOCALAPPDATA%\Ongi\ongi.log`에 남습니다.

## 데이터 출처

| 대상 | 방법 |
| --- | --- |
| Claude 사용량 | `~/.claude/.credentials.json`의 `claudeAiOauth.accessToken`으로 `GET https://api.anthropic.com/api/oauth/usage`(`anthropic-beta: oauth-2025-04-20`). 응답은 `five_hour`/`seven_day`(+`seven_day_opus`/`_sonnet`)의 `utilization`, `resets_at`입니다. 토큰이 만료되었으면 조회하지 않습니다. |
| GPT 사용량 | `~/.codex/auth.json`의 `tokens.access_token`·`account_id`로 `GET https://chatgpt.com/backend-api/wham/usage`. 응답은 `rate_limit.primary_window`/`secondary_window`의 `used_percent`, `limit_window_seconds`, `reset_at`입니다. 실패하면 최근 rollout의 `rate_limits` 기록을 씁니다. 0%이면서 리셋이 "지금 + 창 길이"이면 시작 전으로 봅니다. |
| Claude 캐시 | `~/.claude/projects/*/<세션>.jsonl`(최근 65분 안에 수정된 파일, 끝 3MB). 마지막 요청의 첫 기록 시각 대신, 바로 앞 사용자 입력·도구 결과 시각(`"uuid":…,"timestamp":…`)을 씁니다. TTL은 `cache_creation.ephemeral_1h/5m_input_tokens`, 제목은 `ai-title`. 서브에이전트는 제외합니다. |
| GPT 캐시 | `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`의 마지막 `token_count` 시각과 `turn_context.model`. TTL은 GPT-5.6 이후 모델이면 30분, 그 이전이면 5분입니다(`codexCacheMinutes`로 덮어쓸 수 있음). 제목은 `session_index.jsonl`. |

`CLAUDE_CONFIG_DIR`, `CODEX_HOME` 환경 변수를 따릅니다.

## 조회 일정

- 기본 주기는 `refreshSeconds`(90초)입니다. Claude는 최소 3분입니다.
- 최근 10분 동안 그 서비스로 요청한 채팅이 없으면 10분으로 늘립니다. 다시 쓰기 시작하면 원래 주기로 당깁니다.
- 429를 받으면 5 → 10 → 20 → 30분 지수 백오프합니다. 다른 오류는 `Retry-After`와 기본 지연 중 큰 값을 씁니다.
- 리셋 시각 5초 뒤에 한 번 더 조회합니다.
- 재시작 직후 3분 안의 API 값이 있으면 바로 조회하지 않습니다.
- 캐시 스캔은 10초마다 백그라운드에서 하고, 바뀐 파일만 다시 읽습니다.

## 화면 구조

- **작업표시줄 위젯:** `Shell_TrayWnd`의 자식(`WS_CHILD | WS_EX_LAYERED`)으로 `TrayNotifyWnd` 왼쪽에 둡니다.
  - 설정의 `monitors`(`primary` | `all` | `custom` + `monitorList`의 `Screen.DeviceName`)에 따라 보조 작업표시줄(`Shell_SecondaryTrayWnd`)마다 하나씩 더 만듭니다. 고른 모니터가 하나도 연결되어 있지 않으면 주 모니터에 붙입니다.
  - 보조 작업표시줄에는 `TrayNotifyWnd`가 없고 시계가 XAML 안에 그려지므로, UI 자동화로 `AutomationId=SystemTrayIcon`의 위치를 백그라운드에서 30초마다 찾습니다. 못 찾으면 오른쪽 끝에서 90px 띄웁니다.
  - 2초마다 대상 작업표시줄 목록을 다시 확인해 모니터 연결·해제를 감지하면 위젯을 다시 만듭니다. 간격(`offset`)은 모든 위젯이 같이 씁니다.
  - 로그인 정보가 없다는 오류(`FetchException.SignedOut`)를 받은 서비스는 작업표시줄에서 빼고 상세 패널에만 원인을 보여 줍니다. 둘 다 없으면 둘 다 그립니다. 알파 1 배경으로 마우스를 받고, `UpdateLayeredWindow`로 픽셀 단위 알파를 그립니다. 2초마다 위치·Z 순서를 확인하고, `TaskbarCreated`를 받으면 다시 붙습니다.
- **상세 패널:** 활성화하지 않는 최상위 창입니다. 바깥을 클릭하면 닫히는데, 클릭은 `GetAsyncKeyState` 폴링으로 감지합니다.
- **설정 창:** 저장 버튼 없이 컨트롤의 `Changed`마다 `settings.json`을 저장하고 위젯에 적용합니다.
- **알림:** `NotifyIcon` 풍선 알림입니다. 알림 동안만 트레이 아이콘을 잠깐 켭니다.

## 검증

- 화면 캡처 없이 확인합니다: `--selftest`, `--preview`, `--caches`, `--mock --exit-after N`(로그에 위젯 위치·히트 테스트 기록).
- 실제 자격 증명 조회는 `--probe`로 확인합니다.
