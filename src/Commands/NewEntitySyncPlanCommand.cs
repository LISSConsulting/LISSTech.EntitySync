using System.Management.Automation;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Runtime;

namespace LISSTech.EntitySync.Commands;

[Cmdlet(VerbsCommon.New, "EntitySyncPlan")]
[OutputType(typeof(EntitySyncPlan))]
public sealed class NewEntitySyncPlanCommand : PSCmdlet
{
    private readonly List<ExternalEntity> pipelineSources = new();

    [Parameter(ValueFromPipeline = true)]
    public ExternalEntity? InputObject { get; set; }

    [Parameter(Mandatory = true)]
    [ValidateSet("HaloPSA", "NetSuite")]
    public string SourceVendor { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public string SourceEntityType { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateSet("HaloPSA", "NetSuite")]
    public string TargetVendor { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public string TargetEntityType { get; set; } = string.Empty;

    [Parameter]
    public SwitchParameter IncludeInactive { get; set; }

    [Parameter]
    public SwitchParameter CreateMissing { get; set; }

    [Parameter]
    public int AutoLinkScore { get; set; } = 90;

    [Parameter]
    public int ReviewScore { get; set; } = 70;

    [Parameter]
    public string SourceExternalIdName { get; set; } = "NetSuiteInternalId";

    [Parameter]
    public string TargetCustomFieldName { get; set; } = "CFNetSuiteCustomerID";

    protected override void ProcessRecord()
    {
        if (InputObject != null) pipelineSources.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        try
        {
            var sourceAdapter = ConnectionRegistry.Get(SourceVendor);
            var targetAdapter = ConnectionRegistry.Get(TargetVendor);
            var sources = pipelineSources.Count > 0
                ? pipelineSources
                : sourceAdapter.GetEntitiesAsync(new EntityQuery { EntityType = SourceEntityType, IncludeInactive = IncludeInactive }, CancellationToken.None).GetAwaiter().GetResult().ToList();
            var targets = targetAdapter.GetEntitiesAsync(new EntityQuery { EntityType = TargetEntityType, IncludeInactive = true, FullObjects = true }, CancellationToken.None).GetAwaiter().GetResult();
            var options = new MatchOptions
            {
                SourceExternalIdName = SourceExternalIdName,
                TargetExternalIdName = SourceExternalIdName,
                TargetCustomFieldName = TargetCustomFieldName,
                CreateMissing = CreateMissing,
                AutoLinkScore = AutoLinkScore,
                ReviewScore = ReviewScore
            };
            var matcher = new WeightedEntityMatcher();
            var plan = new EntitySyncPlan { SourceVendor = SourceVendor, SourceEntityType = SourceEntityType, TargetVendor = TargetVendor, TargetEntityType = TargetEntityType };

            foreach (var source in sources)
            {
                var candidates = matcher.FindMatches(source, targets, options);
                var best = candidates.FirstOrDefault();
                if (best == null)
                {
                    plan.Items.Add(new EntitySyncPlanItem { Source = source, Action = CreateMissing ? "Create" : "Review", MatchType = "NoMatch", Reasons = { "No target candidate found" } });
                    continue;
                }

                var action = best.MatchType == "Linked" ? "None" : best.Score >= AutoLinkScore ? "Link" : best.Score >= ReviewScore ? "Review" : CreateMissing ? "Create" : "Review";
                plan.Items.Add(new EntitySyncPlanItem { Source = source, Target = best.Target, Action = action, MatchType = best.MatchType, Score = best.Score, Reasons = best.Reasons });
            }

            WriteObject(plan);
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "NewEntitySyncPlanFailed", ErrorCategory.InvalidOperation, null));
        }
    }
}
