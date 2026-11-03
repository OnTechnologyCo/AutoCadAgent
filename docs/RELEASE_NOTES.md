# AutoCadAgent v0.1.0 Preview

AutoCadAgent는 GPT/Codex, Claude Code, Gemini CLI를 AutoCAD와 연결하는 Windows CAD 에이전트입니다.

- WPF·WebView2·xterm.js 기반 CLI 터미널
- AutoCAD 2024 NETLOAD 모듈과 범용 도구 122개
- JSON Schema, 연결 진단, 정확한 수치 조회
- 도면 작성·검증·수정 지침과 요청 ID 기반 중복 실행 방지

Windows x64, AutoCAD 2024, .NET 10 Desktop Runtime, WebView2 Runtime이 필요합니다. 제공자 CLI와 계정은 별도로 준비합니다. ZIP을 풀고 AutoCAD에서 `cad\AutoCadAgent.AutoCAD.dll`을 NETLOAD한 다음 `Start-AutoCadAgent.cmd`를 실행하세요.

자동 검사 77개와 소스 계약 검사를 통과했습니다. 실제 AutoCAD 형상 생성·수정, DWG 재열기 및 제공자 모델의 설계 추론은 별도 검증이 필요한 Preview 릴리즈입니다.
