using System.Collections.Generic;

namespace AutoCadAgent.Cad
{
    // CAD 도구의 이름, 입력 계약과 실행 결과.
    // 모든 도구는 단독 호출 가능해야 함 (UI 결합 금지) — 같은 엔진을 LLM 도구 + UI 양쪽에서 호출.
    public abstract class ChatTool
    {
        public abstract string Name { get; }                   // "<group>.<name>", 예: "debug.draw_line"
        public abstract string Description { get; }
        public abstract IList<ToolParam> Parameters { get; }   // 시스템 프롬프트 카탈로그 생성용

        // 실제 실행. UI 클릭이 필요한 작업은 도구가 아니라 액션 버튼으로 처리해야 하므로
        // 도구는 즉시 완결 가능한 작업만 다룬다.
        public abstract ToolResult Execute(IDictionary<string, object> args);
    }

    public class ToolParam
    {
        public string Name;
        public string Type;        // "number" / "string" / "boolean" / "object" / "array"
        public string Description;
        public bool Required;
        public object DefaultValue;
    }

    public class ToolResult
    {
        public bool Success;
        public string Summary;     // 사람이 읽는 한국어 요약 (채팅에 표시)
        public object Data;        // 추가 데이터 (엔티티 ID 등)
        public string Code;
        public string Error;       // 실패 시

        public static ToolResult Ok(string summary, object data = null)
            => new ToolResult { Success = true, Summary = summary, Data = data };

        public static ToolResult Fail(string error, string code = "tool_failed")
            => new ToolResult { Success = false, Error = error, Code = code };
    }
}


