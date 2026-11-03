# NETLOAD 확인과 실제 CAD 검사

**현재 상태: SDK 빌드·로드 등록 메타데이터·통신 검사는 통과. 실제 AutoCAD 실행은 미검증.**

AutoCAD 설치 레지스트리, 실행 프로세스, 일반 설치 폴더에서 실행 파일을 찾지 못했고 `cad-agent discover`도 빈 배열을 반환했습니다. 상세 통신 검사 77개 및 소스 계약 검사는 `INTERFACE_VALIDATION.md`에 기록했습니다.

CAD DLL은 AutoCAD 2024 SDK / .NET Framework 4.8 / x64로 빌드됩니다. 초기화 등록은 `BridgePlugin`의 `IExtensionApplication`, 명령은 `CADAGENTSTART`, `CADAGENTSTOP`, `CADAGENTSTATUS`입니다. 최신 CLI와 TUI는 계약 버전 2를 요구합니다.

## AutoCAD에서 실행

1. AutoCAD 2024에서 모델 공간이 비어 있는 새 테스트 도면을 만듭니다.
2. `NETLOAD`로 빌드 결과의 `artifacts\app\cad\AutoCadAgent.AutoCAD.dll` 또는 릴리즈의 `cad\AutoCadAgent.AutoCAD.dll`을 로드합니다. 신뢰 경로가 필요하면 해당 `cad` 폴더만 추가합니다.
3. `CADAGENTSTATUS`에서 파이프와 pending을 확인합니다. 이미 이전 DLL을 로드한 세션이라면 AutoCAD를 재시작해야 합니다. STOP/START는 DLL 재로드 기능이 아닙니다.
4. AutoCAD와 같은 Windows 사용자로 프로젝트 루트 터미널에서 실행합니다.

```powershell
powershell -NoProfile -File scripts\verify-netload.ps1 -DrawTest
```

여러 인스턴스가 있으면 `-Pipe`를 지정합니다. `-DrawTest`를 생략하면 연결·카탈로그·도면 요약만 조회합니다.

## 검사 범위

빈 모델 공간을 먼저 확인하고 사각형·원·해치·치수를 버퍼에 넣습니다. commit 후 실제 핸들과 엔티티 수, 동일 요청의 중복 생성 방지, 반지름·면적·좌표·타입을 검사합니다. 잘못된 도면 토큰과 인자를 거절하는지도 확인합니다.

다른 CLI 세션의 버퍼 격리, 중복·무효 핸들이 섞인 이동 요청의 전체 거절, 정상 이동 후 소수 좌표를 JSON으로 재조회하는 검사도 포함합니다. 마지막으로 새 DWG 복사 저장과 기존 파일 덮어쓰기 거절을 검사합니다.

테스트 형상은 해당 빈 도면에 남습니다. 새 DWG와 `result.json`은 `artifacts\validation\netload-*`에 저장합니다. 기존 사용자 도형은 삭제하지 않습니다.

## 추가 확인할 항목

DWG를 직접 재열어 해치 채움과 치수 표시를 확인해야 합니다. 실제 CAD에서 도면 전환, STOP/START 후 재연결, UI 이벤트·잠금 동작도 확인해야 합니다. 자동 스크립트 성공이 이 모든 항목과 122개 전체 도구의 검증을 뜻하지는 않습니다.

메타데이터만 재검사하려면 Windows PowerShell에서 `scripts\verify-netload-metadata.ps1`을 실행합니다. 이 검사는 Autodesk 네이티브 코드를 실행하지 않습니다.
