# AI Usage Tracker

이 프로젝트는 `small-app-harness` 스킬의 공통 규칙을 따른다. 아래는 이 프로젝트에만 해당하는 내용이다.
개인 정보(계정, 로컬 경로)는 `CLAUDE.local.md`, 진행 상태는 `PROGRESS.md`에 있다 (둘 다 gitignore).

## 무엇인가

Claude Code / Codex 플랜 사용량과 오늘의 Antigravity 활동을 보여주는 Windows 플로팅 위젯. 포터블 exe 하나(약 90 KB).

## 명령

| 할 일 | 명령 |
|---|---|
| 빌드 | `build.cmd` → `dist\AIUsageTracker.exe` (Windows 내장 csc, .NET Framework 4.8) |
| 진단 | `dist\AIUsageTracker.exe --diagnose \| Out-String` (PowerShell, 위젯과 같은 코드로 한 번 조회) |
| statusLine 모드 | `--statusline` (Claude Code가 호출) |

테스트 프레임워크가 없다. 변경 후에는 빌드 + `--diagnose` + 실제 위젯 실행으로 확인한다.

## 구조

- `src/Program.cs`: 진입점, 명령행 인자, 아이콘 생성
- `src/Claude.cs`, `src/Providers.cs`: 서비스별 데이터 수집
- `src/Common.cs`: 경로(`data\`는 exe 옆), 프로세스 실행, JSON 등 공용
- `src/WidgetForm.cs`, `src/SettingsForm.cs`: UI
- `src/Lang.cs`: UI 문자열. **한국어 문자열이 키**이고 en/ja/zh 번역을 찾는다. 기본 언어는 Windows 표시 언어. 새 문자열은 4개 언어 모두 넣고 README 4종(en/ko/ja/zh-CN)도 같이 고친다.
- C# 5 문법까지만 (내장 csc 한계: `$""`, `?.`, `nameof` 불가).

## 불변 조건 (정책)

- 각 도구의 **공식 CLI와 공개 로컬 파일만** 쓴다. 서비스 서버에 직접 요청하지 않는다. 데이터를 어디로도 보내지 않는다.
- 자격증명 파일(`~/.claude/.credentials.json`, `~/.codex/auth.json` 등)은 절대 읽지 않는다.
- Antigravity: `agy`를 실행하거나 서비스와 통신하지 않는다 (약관 §6, FAQ). 파일 시각과 프로세스 목록만 본다. opt-in으로도 되살리지 않는다.
- Claude `/usage`가 로컬 명령이 아니라 프롬프트로 처리되면(사용량 소모) 호출을 멈추는 안전장치를 유지한다.
- `--diagnose` 출력에 이메일·계정 ID를 넣지 않는다.
- statusLine 연결은 `~/.claude/settings.json`을 백업(`settings.json.aiusagetracker.bak`)하고 바꾸며, 해제 시 원래 값을 복원한다.

## 하지 말 것

- `dist/`, `data/`, `*.tmp`는 커밋하지 않는다.
- 공개 전환, 라이선스 선택은 사용자 결정 전에는 하지 않는다.
