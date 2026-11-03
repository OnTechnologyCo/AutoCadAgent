using System.Collections.Generic;
namespace AutoCadAgent.Cad.Tools
{
    // Shared numeric and string argument adapters.
    internal static class _ToolHelp
    {
        public static double? AsDouble(IDictionary<string,object> args,string key)
            => DrawHelpers.TryGetDouble(args,key,out var value) ? value : (double?)null;
        public static string AsString(IDictionary<string,object> args,string key)
            => DrawHelpers.GetString(args,key);
    }
}

