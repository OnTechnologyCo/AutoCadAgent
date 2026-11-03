using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using AutoCadAgent.Cad;
internal static class ContractChecks
{
    internal sealed class CircleTool : ChatTool
    {
        public override string Name => "draw.circle";
        public override string Description => "Test contract fixture";
        public override IList<ToolParam> Parameters => new[] { new ToolParam { Name="cx",Type="number",Required=true }, new ToolParam { Name="cy",Type="number",Required=true }, new ToolParam { Name="r",Type="number",Required=true } };
        public override ToolResult Execute(IDictionary<string,object> args) => throw new NotSupportedException("CAD execution is not part of contract checks");
    }
    internal static void Run(Action<bool,string> check)
    {
        var tool = new CircleTool();
        var good = new Dictionary<string,object> {{"cx",1.123456789},{"cy",-0.987654321},{"r",20}};
        check(ToolContract.Validate(tool.Parameters,good)==null,"typed fractional coordinates accepted without rounding");
        var wrong = new Dictionary<string,object>(good) {{"radius",3}};
        check(ToolContract.Validate(tool.Parameters,wrong)!=null,"unknown argument typo rejected");
        wrong = new Dictionary<string,object>(good); wrong["r"]="20";
        check(ToolContract.Validate(tool.Parameters,wrong)!=null,"numeric string rejected before CAD dispatch");
        wrong["r"]=double.NaN;
        check(ToolContract.Validate(tool.Parameters,wrong)!=null,"nonfinite geometry rejected before CAD dispatch");
        var points = new[] {new ToolParam {Name="points",Type="array",Required=true}};
        check(ToolContract.Validate(points,new Dictionary<string,object>{{"points",new object[]{new object[]{1,2},new object[]{3,4}}}})==null,"XY point arrays accepted");
        check(ToolContract.Validate(points,new Dictionary<string,object>{{"points",new object[]{new object[]{1,2,3}}}})!=null,"unexpected Z coordinate cannot be silently discarded");
        check(ToolContract.Validate(points,new Dictionary<string,object>{{"points",new object[]{new object[]{1,null}}}})!=null,"null coordinate rejected");
        var handles = new[] {new ToolParam {Name="handles",Type="array",Required=true}};
        check(ToolContract.Validate(handles,new Dictionary<string,object>{{"handles",new object[]{"00aB","AB"}}})!=null,"equivalent duplicate hex handles rejected");
        check(ToolContract.Validate(handles,new Dictionary<string,object>{{"handles",new object[]{"XYZ"}}})!=null,"malformed entity handle rejected");
        check(ToolContract.Validate(handles,new Dictionary<string,object>{{"handles",new object[]{"AB","CD"}}})==null,"distinct entity handles accepted");
        var count = new[] {new ToolParam {Name="count",Type="number",Required=true}};
        check(ToolContract.Validate(count,new Dictionary<string,object>{{"count",2.7}})!=null,"fractional entity count rejected");
        var serialized=BridgeRuntime.Encode(ToolContract.Describe(tool));
        var descriptor=(Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(serialized);
        var schema=(Dictionary<string,object>)descriptor["inputSchema"];
        check((string)descriptor["executionKind"]=="staged" && (bool)schema["additionalProperties"]==false && ((object[])schema["required"]).Length==3,"LLM schema includes required fields and execution kind");
        check(ToolContract.Kind("drawing.entity_detail")=="read" && ToolContract.Kind("draw.move")=="immediate" && ToolContract.Kind("draw.clear_session")=="buffer","read buffer staged and immediate effects are distinct");
    }
}
