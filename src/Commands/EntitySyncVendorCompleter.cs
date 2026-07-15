using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace LISSTech.EntitySync.Commands;

public sealed class EntitySyncVendorCompleter : IArgumentCompleter
{
    private static readonly string[] Vendors = { "HaloPSA", "NetSuite", "NCentral", "AgentController" };

    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName,
        string parameterName,
        string wordToComplete,
        CommandAst commandAst,
        IDictionary fakeBoundParameters)
    {
        return Vendors
            .Where(vendor => vendor.StartsWith(wordToComplete ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .Select(vendor => new CompletionResult(vendor, vendor, CompletionResultType.ParameterValue, vendor));
    }
}
