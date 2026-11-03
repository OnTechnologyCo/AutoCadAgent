# AutoCadAgent 구조와 설계 흐름

프로젝트: `AutoCadAgent`

## 구성

| 구성 요소 | 역할 |
|---|---|
| AutoCadAgent.Tui | GPT/Codex, Claude Code, Gemini CLI 터미널과 CAD 도면 연결 |
| AutoCadAgent.Core | ConPTY, 제공자 실행, 연결 검증, 공통 에이전트 지침 |
| cad-agent CLI | doctor, status, tools, describe, call, request 명령 |
| AutoCadAgent.AutoCAD | AutoCAD 2024 NETLOAD 모듈, 122개 범용 CAD 도구 |
| BridgeRuntime | 같은 Windows 계정의 로컬 파이프, 요청 큐, 중복 실행 방지 |
| ToolContract | JSON Schema, 자료형·인자 검사, 실행 방식 명세 |
| GeometrySnapshot | 도면 엔티티의 정확한 수치와 확인 한계를 JSON으로 전달 |

앱과 CLI는 .NET 10, CAD 모듈은 .NET Framework 4.8 x64입니다. 빌드 SDK는 `AUTOCAD_SDK_DIR`, `-AutoCADSdkDir`, 또는 프로젝트의 `.sdk\AutoCAD2024`에서 선택합니다. SDK 어셈블리는 실행 결과물에 복사하지 않습니다.

## 에이전트 동작

제공자 CLI가 사용자의 요구를 해석하고 형상·기준점·치수·검증 조건을 정합니다. `doctor`로 연결과 단위를 확인하고, `tools`와 `describe`에서 실제 사용할 도구의 계약을 읽습니다. 숫자와 배열은 JSON 자료형을 유지해 UTF-8 파일로 전달합니다.

`staged` 도구는 현재 도면과 CLI 세션의 버퍼에 도형을 넣습니다. `draw.commit`이 명시적인 WCS 기준점에 실제 엔티티를 생성하고 핸들을 반환합니다. `read`는 조회, `buffer`는 버퍼 관리, `immediate`는 즉시 반영되는 동작입니다. 입력 좌표가 이미 절대 WCS 좌표라면 commit의 기준점은 0,0입니다.

그리기 후 `drawing.summary_full`과 `drawing.entity_detail`의 `result.Data`를 요구 조건과 비교합니다. 좌표·반지름·면적·치수값은 반올림하지 않은 숫자입니다. 수정할 때는 조회된 핸들을 사용하고, 변경 후 같은 핸들을 다시 조회해 공차와 수량을 검사합니다. `geometryAvailable`, `truncated`, `warnings`는 확인 범위의 한계를 나타냅니다.

## 인터페이스와 복구

- 계약 버전 2. TUI 연결과 CLI `doctor`가 버전 및 기능을 확인합니다.
- `describe TOOL`은 `inputSchema`, 필수 인자, 실행 방식, 좌표 규칙과 제한을 반환합니다.
- 오타 인자, 잘못된 JSON 자료형, null 좌표, 숫자 아닌 값, 중복 핸들을 실행 전에 거절합니다.
- 유효하지 않거나 이미 지워진 엔티티 핸들이 섞여 있으면 해당 핸들 집합을 사용하는 변경 전체를 거절합니다.
- 변경 요청은 UUID를 먼저 정합니다. 응답이 없으면 `request UUID`로 조회하며 새 ID로 자동 재실행하지 않습니다.
- 도면 토큰이 달라지면 `document_changed`로 거절합니다. 기존 터미널은 새 도면으로 자동 전환하지 않습니다.
- `CAD_AGENT.md`에 공통 절차를 기록하고 AGENTS.md, CLAUDE.md, GEMINI.md에 관리 블록을 갱신합니다. 기존 사용자 지침은 유지합니다.

## 터미널 구현

AgentCLIHub의 ConPTY 방식을 참고했습니다. 부모 표준 출력이 터미널을 우회하지 않도록 처리하고, 분할 UTF-8 바이트를 그대로 전달합니다. 제공자별 모델 지정과 이어하기 인자를 구분하며 각 CLI의 기본 승인 정책을 유지합니다. 앱이 계정 인증 정보를 별도로 보관하지 않습니다.

## 범위와 한계

XY/WCS 중심의 일반 도면 설계·작성·조회·편집을 대상으로 합니다. `draw.tolerance`는 MText 표현이고, 일부 고급 도형은 별도 형상 검사가 필요합니다. 122개 도구의 실제 CAD 동작을 모두 검증한 상태는 아닙니다.

AutoCAD 실행 환경이 없으면 통신·계약 검사는 가능하지만 실제 NETLOAD, 잠금·UI 타이머, 형상 생성과 DWG 재열기는 확인할 수 없습니다. 제공자 CLI의 실제 모델 판단 역시 소프트웨어 계약 검사만으로 오류가 없다고 보장할 수 없습니다. 세부 결과는 VALIDATION.md와 INTERFACE_VALIDATION.md에 기록합니다.
