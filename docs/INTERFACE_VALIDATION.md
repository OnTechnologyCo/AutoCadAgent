# CLI LLM–CAD 인터페이스 검증 — 2026-11-03

**결과: 전체 빌드와 자동 검사 77개 통과. 실제 AutoCAD 실행 및 제공자 모델의 설계 추론은 미검증.**

## 검증한 내용

| 검사 | 결과 | 범위 |
|---|---|---|
| 전체 솔루션 Release 빌드 | 오류 0 / 경고 0 | CAD 모듈, Core, CLI, TUI, 테스트 프로젝트 |
| .NET Framework 4.8 브리지·계약 | 53개 통과 | 실제 Windows 파이프, 큐, 종료·재시작, 중복 요청, 스키마, 자료형, 실제 cad-agent 프로세스 |
| Core·지침·클라이언트 | 24개 통과 | 제공자 인자, ConPTY, 분할 UTF-8, 도면 고정, 계약 버전, 요청 상태 ID 검증, 공통 지침 갱신 |
| 소스 계약 대조 | 통과 | 도구 122개의 이름·인자 선언, 버퍼를 쓰는 31개 도구와 staged 표시 일치 |
| PowerShell 검사 스크립트 | 구문 통과 | 빌드, 메타데이터 검사, 실제 CAD 검사 스크립트 |

브리지 검사에서는 CAD 작업을 검사 대리 함수로 대체합니다. `cad-agent.exe`, .NET 48 서버, 파일·파이프·JSON 계약은 실제 실행합니다. 이 결과는 AutoCAD 네이티브 API나 GPT·Claude·Gemini 모델의 설계 판단을 검증한 결과가 아닙니다.

## 동작 흐름

1. `doctor`가 활성 도면, 계약 버전 2, 입력 스키마 및 수치 조회 기능을 확인합니다. TUI도 같은 검사를 거쳐 연결합니다.
2. `describe TOOL`이 실제 `inputSchema`, 필수 필드, 실행 방식과 제한을 제공합니다. 에이전트는 호출 전에 이를 확인합니다.
3. 잘못된 인자 이름과 자료형, null 좌표, 중복 핸들, 정수가 아닌 수량을 거절합니다. 오류는 JSON 코드와 CLI 종료 코드로 전달합니다.
4. 조회 결과 `result.Data`에 정확한 수치를 제공합니다. 기본 형상, 폴리라인, 치수, 해치, 문자, 블록을 지원하며, 미지원 또는 부분 조회는 플래그와 경고로 알립니다.
5. 핸들 집합에 유효하지 않거나 삭제된 항목이 포함되면 일부만 수정하지 않고 전체 요청을 거절합니다. 복사·미러의 반환 핸들도 JSON 배열로 제공합니다.
6. commit 후 화면 갱신만 실패한 경우, 이미 생성된 형상을 실패로 오인해 다시 만들지 않도록 성공 결과에 표시 경고를 포함합니다.
7. 빈 과거 버퍼 세션을 정리해 완료 세션 누적 때문에 신규 세션이 막히지 않게 했습니다. 미반영 도형이 있는 버퍼는 자동으로 버리지 않습니다.
8. 세 제공자에게 공통 지침을 연결합니다. 지침은 요구 분석 → 도구 선택 → 그리기 → 수치 검증 → 수정 → 재검증 순서와 좌표·각도·오류 복구 규칙을 명시합니다. 기존 사용자 지침은 보존합니다.
9. TUI의 중복 시작 요청을 막고, 시작 중에 대상 선택이 바뀌어도 해당 CLI에는 검사했던 도면 값을 전달합니다.

## 실제 통신으로 재현한 사례

- 한국어·공백이 있는 JSON 파일 경로로 도구 호출.
- 좌표 `1.123456789`, `-2.987654321`이 .NET 10 CLI → net48 서버 → JSON 결과를 거쳐 1e-12 허용 오차 내에서 유지됨.
- `describe draw.circle`의 필수 필드와 staged 실행 표시 수신.
- 숫자 대신 문자열을 보낸 요청이 `invalid_arguments` 및 비정상 종료 코드로 반환됨.
- 관계없는 요청 ID로 반환된 상태 조회 응답 거절.
- 서로 다른 세 파이프 클라이언트의 요청 및 분할된 한국어 UTF-8 전달.

## 남은 실제 실행 검사

검증 환경에 AutoCAD 호스트가 없어 NETLOAD, UI 타이머·도면 잠금, 형상 생성, 수정 후 좌표, DWG 저장·재열기는 실제 CAD에서 확인하지 못했습니다. 제공자 계정에 로그인하여 모델 추론을 실행한 것도 아닙니다. 모델 오인식이 전혀 없다고 보장하지 않습니다.

AutoCAD 2024의 빈 테스트 도면에서 최신 DLL을 로드한 다음, 같은 Windows 계정으로 다음을 실행합니다.

```powershell
powershell -NoProfile -File scripts\verify-netload.ps1 -DrawTest
```

기본 형상 생성뿐 아니라 잘못된 핸들 집합의 부분 변경 방지, 소수 좌표 이동 후 정확한 JSON 재조회까지 검사하는 스크립트를 제공합니다. 실제 도면 검사 결과가 나오기 전에는 이 항목을 통과로 기록하지 않습니다. DWG 재열기와 해치·치수의 시각적 결과는 별도로 확인해야 합니다.

## 재현과 기록

```powershell
powershell -NoProfile -File scripts\build.ps1
powershell -NoProfile -File scripts\verify-netload-metadata.ps1
```

로그: `artifacts\validation\interface-build.log`, `interface-bridge-checks.log`, `interface-core-checks.log`, `interface-source-checks.log`.

실행 DLL: `artifacts\app\cad\AutoCadAgent.AutoCAD.dll`. CLI: `artifacts\app\agent\cad-agent.exe`. TUI: `artifacts\app\AutoCadAgent.Tui.exe`.
