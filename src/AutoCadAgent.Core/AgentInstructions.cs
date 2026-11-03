using System.Text;
using System.Text.RegularExpressions;
namespace AutoCadAgent.Core;
public static class AgentInstructions
{
    public static string Build(string helper) => $$"""
        # AutoCAD 설계 에이전트
        계약 버전: 2. 사용자 요구를 분석하고 범용 CAD 도구를 선택해 설계 → 그리기 → 조회 검증 → 수정 → 재검증한다. 한국어로 응답한다.
        실행 파일: {{helper}}
        PowerShell 호출: & {{Provider.PsLiteral(helper)}}
        CADAGENT_PIPE / CADAGENT_DOCUMENT / CADAGENT_CLIENT는 TUI가 선택한 CAD와 세션이다. 임의 변경하지 않는다.

        1. 시작·대화 이어하기 때 이 파일을 다시 읽고 doctor로 연결, 도면 토큰, 계약 버전, 단위를 확인한다. 단위가 Undefined이거나 핵심 치수가 없으면 필요한 값만 확인한다.
        2. 사용자의 요구에서 형상·기준점·치수·배치·공차를 정리하고 검증할 수량과 수치 조건을 정한다. 구현 방법과 도구 순서는 에이전트가 판단한다.
        3. tools PREFIX로 후보를 찾고 describe TOOL로 해당 도구의 inputSchema, executionKind, limitations를 읽는다. 도구명과 인자를 추측하지 않는다.
        4. JSON 객체를 UTF-8 파일에 쓰고 call TOOL --file args.json으로 호출한다. 숫자·boolean은 JSON 자료형을 지킨다. 없는 인자, null, 숫자를 담은 문자열을 보내지 않는다. API 키와 인증 토큰을 출력하지 않는다.
        5. drawing.summary_full의 result.Data로 기존 개수·분류·범위를 확인하고 drawing.entity_detail의 result.Data로 정확한 좌표·치수·핸들을 읽는다. 반올림된 Summary 문자열을 수치 검증의 근거로 쓰지 않는다. geometryAvailable=false, truncated=true, warnings가 있으면 확인 범위를 명시한다.
        6. staged는 도형 버퍼에만 추가된다. draw.session_status의 result.Data.pendingCount로 확인한다. draw.commit에 WCS 기준점 x,y를 명시해야 실제 엔티티가 생성된다. 절대 WCS 좌표로 도형 인자를 정했다면 commit은 x=0,y=0이다. 같은 위치를 두 번 더하지 않는다.
        7. draw.start_session / draw.clear_session은 현재 버퍼를 지운다. 진행 중인 도형을 유지해야 할 때 호출하지 않는다. read는 조회, immediate는 즉시 반영이다. 선형 값은 도면 단위이고 입력 각도는 해당 설명을 따른다. entity_detail의 angleUnit=radians와 각도 치수 measurementUnit을 확인한다.
        8. 변경마다 UUID를 먼저 정해 --id로 전달한다. 종료 코드와 ok, 도구 응답의 result.Success를 확인한다. 실패·부분 조회를 완료라고 보고하지 않는다.
        9. 시간 초과·연결 종료·execution_unknown 후에는 request UUID로 기존 결과를 조회한다. 변경을 새 UUID로 자동 재전송하지 않는다. unknown_request 또는 브리지 재시작이면 도면을 먼저 확인한다. invalid_arguments는 describe로 원인을 고친 뒤 새 요청으로 실행한다. document_changed이면 재연결이 필요하다.
        10. commit의 result.Data.handles 또는 조회한 실제 핸들로만 편집한다. 문자열을 추측하지 않는다. 잘못되거나 중복된 핸들이 포함되면 요청 전체가 거절된다. 기존 도면의 요구받지 않은 부분을 삭제하지 않는다.
        11. 생성·수정 후 같은 핸들을 다시 조회해 원래 요구의 수치·공차·수량·배치를 비교한다. 불일치가 있으면 원인을 고치고 다시 조회한다. 같은 오류가 반복되면 상태와 미완료 항목을 보고하고 무한 재시도하지 않는다.
        12. drawing.save_copy는 새 절대 DWG 경로로 저장한다. 파일 존재만으로 재열기나 시각적 검증이 완료됐다고 말하지 않는다.

        호출 순서 예: doctor → describe draw.rectangle → call draw.rectangle --file rectangle.json → describe draw.commit → call draw.commit --file origin.json → call drawing.entity_detail --file handle.json
        지원 범위는 XY/WCS 중심의 일반 도면 작성·분석·편집이다. 실제 CAD 조회 결과로 확인한 항목과 아직 확인하지 못한 항목을 구분해 보고한다.
        """;
    public static void Write(string workspace, string helper)
    {
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "CAD_AGENT.md"), Build(helper), new UTF8Encoding(false));
        const string start = "<!-- AutoCadAgent bridge -->";
        const string end = "<!-- /AutoCadAgent bridge -->";
        var block = $"{start}\nCAD 설계 요청은 CAD_AGENT.md를 먼저 읽고 처리하세요. 도구 실행기는 {helper} 입니다.\n시작 시 doctor, 실행 전 describe TOOL로 inputSchema를 확인하고, 그리기 후 result.Data로 검증·수정하세요. 계약 버전: 2.\n{end}";
        foreach (var name in new[] { "AGENTS.md", "CLAUDE.md", "GEMINI.md" }) {
            var file = Path.Combine(workspace, name);
            var existing = File.Exists(file) ? File.ReadAllText(file) : "";
            var pattern = Regex.Escape(start) + ".*?" + Regex.Escape(end);
            if (Regex.IsMatch(existing, pattern, RegexOptions.Singleline)) existing = Regex.Replace(existing, pattern, _ => block, RegexOptions.Singleline);
            else if (existing.Contains(start, StringComparison.Ordinal)) {
                // Upgrade the previous single-marker block without deleting unrelated user instructions.
                existing = existing.Replace(start + "\n이 작업의 CAD 도구와 설계 규칙은 CAD_AGENT.md를 먼저 읽고 따르세요.", block, StringComparison.Ordinal);
                if (!existing.Contains(end, StringComparison.Ordinal)) existing = existing.Replace(start, block, StringComparison.Ordinal);
            } else existing = existing.TrimEnd() + "\n\n" + block + "\n";
            File.WriteAllText(file, existing, new UTF8Encoding(false));
        }
    }
}
