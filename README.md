# AutoCadAgent

GPT/Codex, Claude Code, Gemini CLI를 연결해 AutoCAD 도면을 설계·작성·검증·수정하는 Windows 에이전트입니다. C#과 JavaScript로 구성된 TUI와 AutoCAD NETLOAD 모듈을 제공합니다.

## 주요 기능

- 세 제공자 CLI의 터미널, 모델 선택, 대화 이어하기
- AutoCAD 도면 연결과 상태 진단
- 그리기·치수·편집·레이어·블록·레이아웃·조회 도구 122개
- 도구별 JSON Schema 및 실행 방식 조회
- 도면·CLI 세션별 도형 버퍼와 명시적인 WCS 기준점
- 정확한 좌표·치수 JSON 조회를 통한 검증과 수정
- 요청 ID 기반 결과 조회 및 중복 실행 방지
- 새 DWG 복사 저장

## 실행 환경

- Windows x64
- AutoCAD 2024
- .NET 10 Desktop Runtime
- Microsoft Edge WebView2 Runtime
- 사용하려는 Codex, Claude Code 또는 Gemini CLI와 해당 계정

배포 ZIP은 .NET Runtime을 포함하지 않는 포터블 패키지입니다. 제공자 CLI는 별도로 설치합니다. AutoCAD 모듈은 .NET Framework 4.8 x64용이며, 다른 AutoCAD 버전에는 해당 SDK와 런타임에 맞는 빌드가 필요합니다.

## 릴리즈 실행

1. [Releases](https://github.com/OnTechnologyCo/AutoCadAgent/releases)에서 Windows x64 ZIP을 내려받아 압축을 풉니다.
2. AutoCAD 2024에서 새 도면을 열고 `NETLOAD`로 압축을 푼 폴더의 `cad\AutoCadAgent.AutoCAD.dll`을 로드합니다. 신뢰 경로가 필요하면 해당 `cad` 폴더를 추가합니다.
3. `Start-AutoCadAgent.cmd`를 실행합니다.
4. CAD 새로고침 → 도면 연결 → 제공자·모델 선택 → 새 대화를 시작합니다.
5. CLI의 로그인 안내에 따라 로그인하고 도면 요구 사항을 입력합니다.

AutoCAD와 TUI는 같은 Windows 사용자로 실행합니다. CLI 경로를 자동으로 찾지 못하면 실행 파일을 지정합니다. 모델명을 비우면 제공자 CLI의 기본 모델을 사용합니다.

## 설계 흐름

에이전트는 요구 사항과 도면 단위를 확인하고 도구를 선택합니다. 도형 생성 후 실제 엔티티 핸들을 조회해 좌표·치수·수량을 비교하고, 필요한 부분을 수정한 뒤 다시 검증합니다.

작업 폴더의 `CAD_AGENT.md`에 공통 절차를 기록하며 `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`에서 이를 참조합니다. 사용자 지침은 유지합니다.

```powershell
# 압축을 푼 폴더에서 실행
$cad = Join-Path $PWD 'agent\cad-agent.exe'
& $cad discover
& $cad doctor
& $cad tools draw.
& $cad describe draw.rectangle
```

TUI가 시작한 CLI에는 `CADAGENT_PIPE`, `CADAGENT_DOCUMENT`, `CADAGENT_CLIENT`가 전달됩니다. 외부 터미널에서는 `--pipe`, `--document`, `--client`를 지정합니다. 같은 작업에서 client UUID를 유지합니다.

```powershell
& $cad call draw.rectangle --file examples\rectangle.json
& $cad call draw.commit --file examples\origin.json
& $cad call drawing.summary_full
```

- `describe TOOL`의 `inputSchema`에 맞춰 UTF-8 JSON 파일로 인자를 전달합니다.
- `staged`는 버퍼에만 누적합니다. `draw.commit` 성공 후 실제 도형이 생성됩니다.
- 도형 인자가 절대 WCS 좌표이면 commit의 기준점은 `x=0,y=0`입니다.
- `read`는 조회, `buffer`는 버퍼 관리, `immediate`는 즉시 반영되는 작업입니다.
- 수치 검증에는 `drawing.entity_detail`의 `result.Data`를 사용합니다. `Summary`는 사람이 읽는 요약입니다.
- 응답을 잃으면 `request UUID`로 결과를 확인합니다. 변경 요청을 새 ID로 자동 재전송하지 않습니다.
- 도면을 전환하면 TUI에서 다시 연결하고 터미널을 새로 엽니다.
- `drawing.save_copy`는 새 DWG 경로에 저장합니다.

## 소스 빌드

필요 항목: .NET 10 SDK, .NET Framework 4.8 참조 어셈블리, AutoCAD 2024 managed SDK, Node.js.

SDK의 `AcMgd.dll`, `AcDbMgd.dll`, `AcCoreMgd.dll`을 `.sdk\AutoCAD2024`에 준비하거나 SDK 폴더를 지정합니다.

```powershell
powershell -NoProfile -File scripts\build.ps1 -AutoCADSdkDir 'C:\SDK\AutoCAD2024'
```

`AUTOCAD_SDK_DIR` 환경 변수도 지원합니다. 빌드 결과는 `artifacts\app`에 저장되며, 프로젝트 루트의 `Start-AutoCadAgent.cmd`로 실행합니다.

## 검증 상태

자동 검사 77개와 도구 122개의 소스 계약 대조를 통과했습니다. 브리지 검사는 실제 Windows Named Pipe와 `cad-agent.exe`를 사용하며 CAD 실행 부분은 검사 대리 함수로 처리합니다.

실제 AutoCAD NETLOAD, 네이티브 형상 생성·수정, DWG 재열기와 제공자 모델의 설계 추론은 별도 검증이 필요합니다. 지원 형상은 XY/WCS 중심의 일반 도면입니다. [검증 범위](docs/VALIDATION.md)와 [CAD 실행 검사](docs/NETLOAD_VALIDATION.md)를 확인하세요.

## 구성과 고지

| 프로젝트 | 런타임 | 역할 |
|---|---|---|
| AutoCadAgent.AutoCAD | .NET Framework 4.8 x64 | AutoCAD 도구와 파이프 서버 |
| AutoCadAgent.Core | .NET 10 | CLI 실행·터미널·도구 클라이언트 |
| AutoCadAgent.Tui | .NET 10 Windows | WPF·WebView2·xterm.js 인터페이스 |
| AutoCadAgent.Cli | .NET 10 | 공통 CAD 명령 |

[구조](docs/ANALYSIS.md) · [인터페이스](docs/INTERFACE_VALIDATION.md) · [제3자 고지](THIRD_PARTY_NOTICES.md)
